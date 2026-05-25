// Modules/System/Controllers/RefundApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using static PosDashboard.Web.Modules.System.Models.RefundDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/refunds")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class RefundApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public RefundApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        private static readonly string[] ValidRefundTypes = { "CASH", "LINK", "WALLET" };

        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value;
            return int.TryParse(claim, out var id) ? id : 0;
        }

        // =============================================
        // POST /api/refunds — Process a Refund
        // =============================================
        [HttpPost]
        public ActionResult<ApiResult<RefundResponse>> ProcessRefund(
            [FromBody] RefundRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<RefundResponse>(false, "Request body is required", null));

            if (!ValidRefundTypes.Contains(request.RefundType?.ToUpperInvariant()))
                return Ok(new ApiResult<RefundResponse>(false,
                    "RefundType must be 'CASH', 'LINK', or 'WALLET'", null));

            if (string.IsNullOrWhiteSpace(request.CancellationReason))
                return Ok(new ApiResult<RefundResponse>(false,
                    "CancellationReason is required", null));

            if (request.RefundType.Equals("LINK", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(request.RefundLink))
                return Ok(new ApiResult<RefundResponse>(false,
                    "RefundLink is required for LINK refund type", null));

            using var conn = sqlConnections.NewByKey("Default");

            using var uow = new UnitOfWork(conn);
            try
            {
                int userId = GetCurrentUserId();
                var refundType = request.RefundType.ToUpperInvariant();

                // ── STEP A: Load & validate the invoice ──────────────
                var invoice = SqlMapper.Query(conn, @"
                    SELECT Id, InvoiceNumber, AppointmentId, BranchId, CustomerId,
                           TotalAmount, PaidAmount, RemainingAmount, Currency,
                           ISNULL(IsFullyRefunded, 0) AS IsFullyRefunded,
                           ISNULL(TotalRefunded, 0)   AS TotalRefunded
                    FROM dbo.AppointmentInvoices
                    WHERE Id = @InvoiceId AND BranchId = @BranchId",
                    new { InvoiceId = request.InvoiceId, BranchId = request.BranchId }).FirstOrDefault();

                if (invoice == null)
                    return Ok(new ApiResult<RefundResponse>(false,
                        "Invoice not found or does not belong to this branch", null));

                if ((bool)invoice.IsFullyRefunded)
                    return Ok(new ApiResult<RefundResponse>(false,
                        "Invoice has already been fully refunded", null));

                int customerId = (int)invoice.CustomerId;
                decimal invoiceTotal = (decimal)invoice.TotalAmount;
                decimal alreadyRefunded = (decimal)invoice.TotalRefunded;
                string invoiceNumber = (string)invoice.InvoiceNumber;
                int leadAppointmentId = (int)invoice.AppointmentId;

                // Load customer ref guide for wallet ops
                var customerRefRow = SqlMapper.Query(conn,
                    "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = customerId }).FirstOrDefault();

                if (customerRefRow == null)
                    return Ok(new ApiResult<RefundResponse>(false, "Customer not found", null));

                Guid customerRef = (Guid)customerRefRow.RefGuide;

                // Load invoice lines (New Sale style)
                var invoiceLines = SqlMapper.Query(conn, @"
                    SELECT ail.Id AS InvoiceLineId, ail.AppointmentId, ail.ItemId, ail.StaffId,
                           i.ITEM_NAME1 AS ItemName, s.EnglishName AS StaffName,
                           ail.TotalPrice AS LineAmount,
                           ISNULL(ail.IsRefunded, 0) AS IsRefunded,
                           ISNULL(ail.RefundedAmount, 0) AS RefundedAmount
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.ITEM i  ON i.ITEM_ID = ail.ItemId
                    INNER JOIN dbo.STAFF s ON s.Id = ail.StaffId
                    WHERE ail.InvoiceId = @InvoiceId",
                    new { InvoiceId = request.InvoiceId }).ToList();

                bool isNewSaleInvoice = invoiceLines.Count > 0;

                // ── STEP B: Calculate refund amount ──────────────────
                decimal refundAmount;
                List<(int? InvoiceLineId, int AppointmentId, int ItemId, int StaffId, decimal Amount, string ItemName, string StaffName)> refundLines;

                if (request.Lines != null && request.Lines.Count > 0)
                {
                    // Partial refund — validate each requested line
                    foreach (var rl in request.Lines)
                    {
                        if (rl.RefundAmount <= 0)
                            return Ok(new ApiResult<RefundResponse>(false,
                                $"RefundAmount must be > 0 for line AppointmentId={rl.AppointmentId}", null));

                        if (isNewSaleInvoice && rl.InvoiceLineId.HasValue)
                        {
                            var matchedLine = invoiceLines.FirstOrDefault(
                                l => (int)l.InvoiceLineId == rl.InvoiceLineId.Value);

                            if (matchedLine == null)
                                return Ok(new ApiResult<RefundResponse>(false,
                                    $"InvoiceLineId={rl.InvoiceLineId} not found in this invoice", null));

                            if ((bool)matchedLine.IsRefunded)
                                return Ok(new ApiResult<RefundResponse>(false,
                                    $"Line '{matchedLine.ItemName}' has already been refunded", null));
                        }
                    }

                    refundAmount = request.Lines.Sum(l => l.RefundAmount);
                    refundLines = request.Lines.Select(l =>
                    {
                        string itemName = l.ItemId.ToString();
                        string staffName = l.StaffId.ToString();
                        if (isNewSaleInvoice && l.InvoiceLineId.HasValue)
                        {
                            var match = invoiceLines.FirstOrDefault(
                                x => (int)x.InvoiceLineId == l.InvoiceLineId.Value);
                            if (match != null)
                            {
                                itemName = (string)match.ItemName;
                                staffName = (string)match.StaffName;
                            }
                        }
                        return (l.InvoiceLineId, l.AppointmentId, l.ItemId, l.StaffId,
                                l.RefundAmount, itemName, staffName);
                    }).ToList();
                }
                else
                {
                    // Full invoice refund (all remaining non-refunded lines)
                    if (isNewSaleInvoice)
                    {
                        // Use the actual unrefunded line amounts — immune to TotalAmount corruption
                        var unrefundedLines = invoiceLines
                            .Where(l => !(bool)l.IsRefunded)
                            .ToList();

                        if (unrefundedLines.Count == 0)
                            return Ok(new ApiResult<RefundResponse>(false,
                                "Nothing left to refund on this invoice", null));

                        refundAmount = unrefundedLines.Sum(l => (decimal)l.LineAmount);

                        refundLines = unrefundedLines
                            .Select(l => ((int?)((int)l.InvoiceLineId), (int)l.AppointmentId,
                                          (int)l.ItemId, (int)l.StaffId,
                                          (decimal)l.LineAmount,
                                          (string)l.ItemName, (string)l.StaffName))
                            .ToList();
                    }
                    else
                    {
                        // Legacy invoice — fall back to TotalAmount - TotalRefunded
                        refundAmount = invoiceTotal - alreadyRefunded;

                        if (refundAmount <= 0)
                            return Ok(new ApiResult<RefundResponse>(false,
                                "Nothing left to refund on this invoice", null));

                        var legacyAppt = SqlMapper.Query(conn, @"
                            SELECT a.Id, a.ItemId, a.StaffId,
                                   i.ITEM_NAME1 AS ItemName, s.EnglishName AS StaffName
                            FROM dbo.AppointmentData a
                            INNER JOIN dbo.ITEM i  ON i.ITEM_ID = a.ItemId
                            INNER JOIN dbo.STAFF s ON s.Id = a.StaffId
                            WHERE a.Id = @Id",
                            new { Id = leadAppointmentId }).FirstOrDefault();

                        if (legacyAppt == null)
                            return Ok(new ApiResult<RefundResponse>(false,
                                "Lead appointment not found", null));

                        refundLines = new List<(int?, int, int, int, decimal, string, string)>
                        {
                            (null, leadAppointmentId, (int)legacyAppt.ItemId,
                             (int)legacyAppt.StaffId, refundAmount,
                             (string)legacyAppt.ItemName, (string)legacyAppt.StaffName)
                        };
                    }
                }

                // Over-refund guard: for line-based invoices use the sum of all line amounts
                // (immune to TotalAmount corruption); for legacy use invoiceTotal.
                decimal originalInvoiceTotal = isNewSaleInvoice
                    ? invoiceLines.Sum(l => (decimal)l.LineAmount)
                    : invoiceTotal;
                decimal alreadyRefundedAmount = invoiceLines.Count > 0
                    ? invoiceLines.Where(l => (bool)l.IsRefunded).Sum(l => (decimal)l.LineAmount)
                    : alreadyRefunded;

                if (refundAmount + alreadyRefundedAmount > originalInvoiceTotal + 0.001m)
                    return Ok(new ApiResult<RefundResponse>(false,
                        $"Refund amount ({refundAmount:F3}) exceeds refundable balance " +
                        $"({originalInvoiceTotal - alreadyRefundedAmount:F3})", null));

                // Collect affected appointment IDs
                var affectedAppointmentIds = refundLines
                    .Select(l => l.AppointmentId)
                    .Distinct()
                    .ToList();

                // ── STEP C: Insert RefundTransactions row ─────────────
                int refundTxId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.RefundTransactions (
                        InvoiceId, BranchId, CustomerId,
                        RefundType, RefundAmount,
                        CancellationReason, CustomerComment, RefundLink,
                        Status, ProcessedBy, ProcessedAt, Deleted
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        @InvoiceId, @BranchId, @CustomerId,
                        @RefundType, @RefundAmount,
                        @CancellationReason, @CustomerComment, @RefundLink,
                        'COMPLETED', @ProcessedBy, SYSUTCDATETIME(), 0
                    )",
                    new
                    {
                        InvoiceId = request.InvoiceId,
                        BranchId = request.BranchId,
                        CustomerId = customerId,
                        RefundType = refundType,
                        RefundAmount = refundAmount,
                        CancellationReason = request.CancellationReason?.Trim(),
                        CustomerComment = string.IsNullOrWhiteSpace(request.CustomerComment)
                            ? null : request.CustomerComment.Trim(),
                        RefundLink = string.IsNullOrWhiteSpace(request.RefundLink)
                            ? null : request.RefundLink.Trim(),
                        ProcessedBy = userId
                    }).FirstOrDefault();

                // ── STEP D: Insert RefundTransactionLines ─────────────
                foreach (var rl in refundLines)
                {
                    SqlMapper.Execute(conn, @"
                        INSERT INTO dbo.RefundTransactionLines (
                            RefundTransactionId, InvoiceLineId, AppointmentId,
                            ItemId, StaffId, RefundedAmount, IsFullyRefunded
                        )
                        VALUES (
                            @RefundTxId, @InvoiceLineId, @AppointmentId,
                            @ItemId, @StaffId, @RefundedAmount, 1
                        )",
                        new
                        {
                            RefundTxId = refundTxId,
                            InvoiceLineId = rl.InvoiceLineId,
                            rl.AppointmentId,
                            rl.ItemId,
                            rl.StaffId,
                            RefundedAmount = rl.Amount
                        });
                }

                // ── STEP E: Mark invoice lines as refunded ────────────
                if (isNewSaleInvoice)
                {
                    foreach (var rl in refundLines)
                    {
                        if (rl.InvoiceLineId.HasValue)
                        {
                            SqlMapper.Execute(conn, @"
                                UPDATE dbo.AppointmentInvoiceLines
                                SET IsRefunded = 1, RefundedAmount = @Amount
                                WHERE Id = @Id",
                                new { Id = rl.InvoiceLineId.Value, Amount = rl.Amount });
                        }
                    }

                    // Check if ALL lines are now refunded
                    int totalLines = invoiceLines.Count;
                    int nowRefundedLines = SqlMapper.Query<int>(conn, @"
                        SELECT COUNT(*) FROM dbo.AppointmentInvoiceLines
                        WHERE InvoiceId = @InvoiceId AND ISNULL(IsRefunded, 0) = 1",
                        new { InvoiceId = request.InvoiceId }).FirstOrDefault();

                    bool allRefunded = totalLines > 0 && nowRefundedLines >= totalLines;

                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentInvoices SET
                            TotalRefunded       = ISNULL(TotalRefunded, 0) + @RefundAmount,
                            IsFullyRefunded     = @IsFullyRefunded,
                            IsPartiallyRefunded = @IsPartiallyRefunded
                        WHERE Id = @InvoiceId",
                        new
                        {
                            InvoiceId = request.InvoiceId,
                            RefundAmount = refundAmount,
                            IsFullyRefunded = allRefunded ? 1 : 0,
                            IsPartiallyRefunded = !allRefunded ? 1 : 0
                        });
                }
                else
                {
                    // Full legacy refund
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentInvoices SET
                            TotalRefunded       = TotalAmount,
                            IsFullyRefunded     = 1,
                            IsPartiallyRefunded = 0
                        WHERE Id = @InvoiceId",
                        new { InvoiceId = request.InvoiceId });
                }

                // ── STEP G: Cancel fully-refunded appointments ────────
                // Re-query the DB AFTER STEP E updates so IsRefunded flags are accurate.
                // An appointment is fully refunded only when ALL its invoice lines are
                // now marked IsRefunded = 1 in the DB.
                bool apptsCancelled = false;
                foreach (var apptId in affectedAppointmentIds)
                {
                    bool fullyRefunded;

                    if (!isNewSaleInvoice)
                    {
                        // Legacy single-appointment invoice — always fully refunded here
                        fullyRefunded = true;
                    }
                    else
                    {
                        // Count how many lines for this appointment still have IsRefunded = 0
                        int remainingLines = SqlMapper.Query<int>(conn, @"
                            SELECT COUNT(*)
                            FROM dbo.AppointmentInvoiceLines
                            WHERE InvoiceId      = @InvoiceId
                              AND AppointmentId  = @ApptId
                              AND ISNULL(IsRefunded, 0) = 0",
                            new { InvoiceId = request.InvoiceId, ApptId = apptId })
                            .FirstOrDefault();

                        fullyRefunded = remainingLines == 0;
                    }

                    if (fullyRefunded)
                    {
                        // Only update Status — CheckoutStatus has a CHECK constraint
                        // that only allows 'open' and 'checked_out'
                        SqlMapper.Execute(conn, @"
                            UPDATE dbo.AppointmentData
                            SET Status = 'cancelled',
                                UpdatedAt = SYSUTCDATETIME()
                            WHERE Id = @Id",
                            new { Id = apptId });
                        apptsCancelled = true;
                    }
                }

                // ── STEP H: Adjust staff revenue (DiscountedUnitPrice) ─
                foreach (var rl in refundLines)
                {
                    // Reduce the original appointment's DiscountedUnitPrice.
                    // For the lead appointment, this covers the main service.
                    // For checkout-drawer extras that now have AppointmentInvoiceLines rows,
                    // the AppointmentId points back to the same single appointment —
                    // we reduce checkout item price separately below.
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentData
                        SET DiscountedUnitPrice = CASE
                                WHEN DiscountedUnitPrice >= @Amount
                                THEN DiscountedUnitPrice - @Amount
                                ELSE 0
                            END,
                            UpdatedAt = SYSUTCDATETIME()
                        WHERE Id = @AppointmentId",
                        new { AppointmentId = rl.AppointmentId, Amount = rl.Amount });

                    // Also reduce the matching AppointmentCheckoutItems row if the
                    // refunded line maps to a checkout extra (identified by InvoiceLineId →
                    // AppointmentInvoiceLines.ItemId + StaffId cross-reference).
                    if (rl.InvoiceLineId.HasValue)
                    {
                        SqlMapper.Execute(conn, @"
                            UPDATE dbo.AppointmentCheckoutItems
                            SET IsRefunded = 1,
                                DiscountedUnitPrice = 0,
                                TotalPrice          = 0
                            WHERE AppointmentId = @AppointmentId
                              AND ItemId  = (SELECT ItemId  FROM dbo.AppointmentInvoiceLines WHERE Id = @LineId)
                              AND StaffId = (SELECT StaffId FROM dbo.AppointmentInvoiceLines WHERE Id = @LineId)",
                            new { AppointmentId = rl.AppointmentId, Amount = rl.Amount, LineId = rl.InvoiceLineId.Value });
                    }
                }

                // ── STEP I: Wallet credit (if RefundType='WALLET') ────
                int? walletSubId = null;
                bool walletCredited = false;

                if (refundType == "WALLET")
                {
                    // UX_Subscriptions_CustomerRef_Active is unique on CustomerRef alone —
                    // each customer can have at most ONE subscription row.
                    // Always search by CustomerRef only, never filter by SubTypeId.
                    var existingSub = SqlMapper.Query(conn, @"
                        SELECT TOP 1 s.Id, s.SubTypeId,
                               ISNULL((
                                   SELECT TOP 1 sh.Balance
                                   FROM dbo.SubscriptionsHistory sh
                                   WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                   ORDER BY sh.Id DESC
                               ), 0) AS CurrentBalance
                        FROM dbo.Subscriptions s
                        WHERE s.CustomerRef = @CustomerRef
                          AND s.Deleted = 0
                        ORDER BY s.Id DESC",
                        new { CustomerRef = customerRef })
                        .FirstOrDefault();

                    if (existingSub != null)
                    {
                        // Credit the existing subscription — just add a history row
                        walletSubId = (int)existingSub.Id;
                        decimal oldBalance = (decimal)existingSub.CurrentBalance;

                        SqlMapper.Execute(conn, @"
                            INSERT INTO dbo.SubscriptionsHistory (
                                CustomerRef, RefType, InvoiceId, SubscriptionId,
                                Amount, Balance, AddedBy, AddedDate, Deleted
                            )
                            VALUES (
                                @CustomerRef, 3, @InvoiceId, @SubscriptionId,
                                @Amount, @Balance, @AddedBy, SYSUTCDATETIME(), 0
                            )",
                            new
                            {
                                CustomerRef = customerRef,
                                InvoiceId = (int?)null,
                                SubscriptionId = walletSubId.Value,
                                Amount = refundAmount,
                                Balance = oldBalance + refundAmount,
                                AddedBy = userId
                            });

                        // Update Value/Net and extend EndDate if expired
                        SqlMapper.Execute(conn, @"
                            UPDATE dbo.Subscriptions
                            SET Value   = Value + @Amount,
                                Net     = Net   + @Amount,
                                IsPaid  = 1,
                                EndDate = CASE
                                    WHEN EndDate < SYSUTCDATETIME()
                                    THEN DATEADD(YEAR, 1, SYSUTCDATETIME())
                                    ELSE EndDate
                                END
                            WHERE Id = @Id",
                            new { Id = walletSubId.Value, Amount = refundAmount });
                    }
                    else
                    {
                        // No subscription exists yet — safe to INSERT
                        int refundSubTypeId = Convert.ToInt32(
                            SqlMapper.Query<dynamic>(conn, @"
                                SELECT TOP 1 ID FROM dbo.SUBS_TYPE
                                WHERE NAME LIKE '%Refund%' OR NAME LIKE '%refund%'
                                ORDER BY ID").FirstOrDefault()?.ID ?? 0);

                        if (refundSubTypeId == 0)
                            return Ok(new ApiResult<RefundResponse>(false,
                                "Refund Balance subscription type not found in SUBS_TYPE. Run migration 1F first.", null));

                        int newSubId = SqlMapper.Query<int>(conn, @"
                            INSERT INTO dbo.Subscriptions (
                                GUID, CustomerRef, SubTypeId, Value, Net, Count,
                                StartDate, EndDate, DaysCount,
                                BranchId, AddedBy, AddedDate, Deleted, IsPaid,
                                SHIFT_ID, ActiveOnline, Source
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                NEWID(), @CustomerRef, @SubTypeId,
                                @Value, @Value, 1,
                                SYSUTCDATETIME(), DATEADD(YEAR, 1, SYSUTCDATETIME()), 365,
                                @BranchId, @AddedBy, SYSUTCDATETIME(), 0, 1,
                                0, 0, 0
                            )",
                            new
                            {
                                CustomerRef = customerRef,
                                SubTypeId = refundSubTypeId,
                                Value = refundAmount,
                                BranchId = request.BranchId,
                                AddedBy = userId
                            }).FirstOrDefault();

                        SqlMapper.Execute(conn, @"
                            INSERT INTO dbo.SubscriptionsHistory (
                                CustomerRef, RefType, InvoiceId, SubscriptionId,
                                Amount, Balance, AddedBy, AddedDate, Deleted
                            )
                            VALUES (
                                @CustomerRef, 3, @InvoiceId, @SubscriptionId,
                                @Amount, @Balance, @AddedBy, SYSUTCDATETIME(), 0
                            )",
                            new
                            {
                                CustomerRef = customerRef,
                                InvoiceId = (int?)null,
                                SubscriptionId = newSubId,
                                Amount = refundAmount,
                                Balance = refundAmount,
                                AddedBy = userId
                            });

                        walletSubId = newSubId;
                    }

                    walletCredited = true;
                }

                // ── STEP J: Update CUSTOMER refund tracking ───────────
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.CUSTOMER SET
                        HasRefundHistory  = 1,
                        LastRefundDate    = SYSUTCDATETIME(),
                        TotalRefundAmount = ISNULL(TotalRefundAmount, 0) + @RefundAmount
                    WHERE CUSTOMER_ID = @CustomerId",
                    new { CustomerId = customerId, RefundAmount = refundAmount });

                // ── STEP K: Commit ────────────────────────────────────
                uow.Commit();

                return Ok(new ApiResult<RefundResponse>(true, null,
                    new RefundResponse(
                        RefundTransactionId: refundTxId,
                        RefundType: refundType,
                        RefundAmount: refundAmount,
                        InvoiceNumber: invoiceNumber,
                        AppointmentsCancelled: apptsCancelled,
                        WalletCredited: walletCredited,
                        WalletSubscriptionId: walletSubId
                    )));
            }
            catch (Exception ex)
            {
                // UnitOfWork rolls back automatically when disposed without Commit()
                return Ok(new ApiResult<RefundResponse>(false,
                    $"Refund failed (rolled back): {ex.Message}", null));
            }
        }

        // =============================================
        // GET /api/refunds/invoice/{invoiceId}
        // =============================================
        [HttpGet("invoice/{invoiceId:int}")]
        public ActionResult<ApiResult<List<RefundTransactionDto>>> GetInvoiceRefundHistory(
            int invoiceId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var refunds = SqlMapper.Query(conn, @"
                SELECT rt.Id, rt.InvoiceId, ai.InvoiceNumber,
                       rt.BranchId, rt.CustomerId,
                       c.CUSTOMER_NAME AS CustomerName,
                       rt.RefundType, rt.RefundAmount,
                       rt.CancellationReason, rt.CustomerComment, rt.RefundLink,
                       rt.Status, rt.ProcessedAt
                FROM dbo.RefundTransactions rt
                INNER JOIN dbo.AppointmentInvoices ai ON ai.Id = rt.InvoiceId
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = rt.CustomerId
                WHERE rt.InvoiceId = @InvoiceId AND rt.Deleted = 0
                ORDER BY rt.ProcessedAt DESC",
                new { InvoiceId = invoiceId }).ToList();

            var result = new List<RefundTransactionDto>();

            foreach (var r in refunds)
            {
                var lines = SqlMapper.Query(conn, @"
                    SELECT rtl.Id, rtl.InvoiceLineId, rtl.AppointmentId,
                           rtl.ItemId, i.ITEM_NAME1 AS ItemName,
                           rtl.StaffId, s.EnglishName AS StaffName,
                           rtl.RefundedAmount, rtl.IsFullyRefunded
                    FROM dbo.RefundTransactionLines rtl
                    INNER JOIN dbo.ITEM i  ON i.ITEM_ID = rtl.ItemId
                    INNER JOIN dbo.STAFF s ON s.Id = rtl.StaffId
                    WHERE rtl.RefundTransactionId = @RefundTxId
                    ORDER BY rtl.Id",
                    new { RefundTxId = (int)r.Id })
                    .Select(l => new RefundTransactionLineDto(
                        Id: (int)l.Id,
                        InvoiceLineId: (int?)l.InvoiceLineId,
                        AppointmentId: (int)l.AppointmentId,
                        ItemId: (int)l.ItemId,
                        ItemName: (string)(l.ItemName ?? ""),
                        StaffId: (int)l.StaffId,
                        StaffName: (string)(l.StaffName ?? ""),
                        RefundedAmount: (decimal)l.RefundedAmount,
                        IsFullyRefunded: (bool)l.IsFullyRefunded
                    )).ToList();

                result.Add(new RefundTransactionDto(
                    Id: (int)r.Id,
                    InvoiceId: (int)r.InvoiceId,
                    InvoiceNumber: (string)r.InvoiceNumber,
                    BranchId: (int)r.BranchId,
                    CustomerId: (int)r.CustomerId,
                    CustomerName: (string)(r.CustomerName ?? ""),
                    RefundType: (string)r.RefundType,
                    RefundAmount: (decimal)r.RefundAmount,
                    CancellationReason: (string?)r.CancellationReason,
                    CustomerComment: (string?)r.CustomerComment,
                    RefundLink: (string?)r.RefundLink,
                    Status: (string)r.Status,
                    ProcessedAt: (DateTime)r.ProcessedAt,
                    Lines: lines
                ));
            }

            return Ok(new ApiResult<List<RefundTransactionDto>>(true, null, result));
        }

        // =============================================
        // GET /api/refunds/customer/{customerId}
        // =============================================
        [HttpGet("customer/{customerId:int}")]
        public ActionResult<ApiResult<List<CustomerRefundHistoryDto>>> GetCustomerRefundHistory(
            int customerId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var history = SqlMapper.Query(conn, @"
                SELECT rt.Id      AS RefundTransactionId,
                       ai.InvoiceNumber,
                       rt.ProcessedAt,
                       rt.RefundType,
                       rt.RefundAmount,
                       rt.CancellationReason,
                       rt.CustomerComment
                FROM dbo.RefundTransactions rt
                INNER JOIN dbo.AppointmentInvoices ai ON ai.Id = rt.InvoiceId
                WHERE rt.CustomerId = @CustomerId AND rt.Deleted = 0
                ORDER BY rt.ProcessedAt DESC",
                new { CustomerId = customerId })
                .Select(r => new CustomerRefundHistoryDto(
                    RefundTransactionId: (int)r.RefundTransactionId,
                    InvoiceNumber: (string)r.InvoiceNumber,
                    ProcessedAt: (DateTime)r.ProcessedAt,
                    RefundType: (string)r.RefundType,
                    RefundAmount: (decimal)r.RefundAmount,
                    CancellationReason: (string?)r.CancellationReason,
                    CustomerComment: (string?)r.CustomerComment
                )).ToList();

            return Ok(new ApiResult<List<CustomerRefundHistoryDto>>(true, null, history));
        }

        // =============================================
        // GET /api/refunds/summary?branchId={id}&date={date}
        // =============================================
        [HttpGet("summary")]
        public ActionResult<ApiResult<RefundSummaryDto>> GetRefundSummary(
            [FromQuery] int branchId,
            [FromQuery] string date)
        {
            if (!DateTime.TryParse(date, out var parsedDate))
                return Ok(new ApiResult<RefundSummaryDto>(false, "Invalid date format", null));

            using var conn = sqlConnections.NewByKey("Default");

            var summary = SqlMapper.Query(conn, @"
                SELECT
                    COUNT(*)                                              AS TotalRefunds,
                    ISNULL(SUM(RefundAmount), 0)                         AS TotalRefundAmount,
                    SUM(CASE WHEN RefundType = 'CASH'   THEN 1 ELSE 0 END) AS CashRefunds,
                    SUM(CASE WHEN RefundType = 'LINK'   THEN 1 ELSE 0 END) AS LinkRefunds,
                    SUM(CASE WHEN RefundType = 'WALLET' THEN 1 ELSE 0 END) AS WalletRefunds
                FROM dbo.RefundTransactions
                WHERE BranchId = @BranchId
                  AND CAST(ProcessedAt AS DATE) = @Date
                  AND Deleted = 0",
                new { BranchId = branchId, Date = parsedDate.Date }).FirstOrDefault();

            if (summary == null)
                return Ok(new ApiResult<RefundSummaryDto>(true, null,
                    new RefundSummaryDto(0, 0, 0, 0, 0)));

            return Ok(new ApiResult<RefundSummaryDto>(true, null,
                new RefundSummaryDto(
                    TotalRefunds: (int)(summary.TotalRefunds ?? 0),
                    TotalRefundAmount: (decimal)(summary.TotalRefundAmount ?? 0),
                    CashRefunds: (int)(summary.CashRefunds ?? 0),
                    LinkRefunds: (int)(summary.LinkRefunds ?? 0),
                    WalletRefunds: (int)(summary.WalletRefunds ?? 0)
                )));
        }
    }
}