// Modules/System/Controllers/AppointmentGroupCheckoutApiController.cs
//
// Consolidated checkout for multi-service appointments.
//
//   POST /api/appointments/{id}/group-checkout
//     Checks out EVERY appointment that shares the given appointment's
//     SaleGroupId and produces ONE invoice covering all of them (instead of one
//     invoice per service). A single-service appointment (no group) is treated
//     as a group of one, so this is safe to call for any appointment.
//
//   GET /api/appointments/{id}/group-info
//     Lightweight summary the checkout drawer uses to show the whole visit
//     (all services + combined totals) before confirming.
//
// The consolidated invoice follows the exact same storage shape the rest of the
// app already understands: one AppointmentInvoices row anchored on the entry
// appointment, plus one AppointmentInvoiceLines row per service (and per
// checkout extra). The calendar's invoice resolution already maps non-anchor
// rows to the invoice via AppointmentInvoiceLines, so nothing else changes.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using PosDashboard.Web.Modules.System.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;   // InvoiceDto, CheckoutRequest
using GroupDtos = PosDashboard.Web.Modules.System.Models.AppointmentGroupDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/appointments")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AppointmentGroupCheckoutApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public AppointmentGroupCheckoutApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // =====================================================================
        // GET /api/appointments/{id}/group-info
        // =====================================================================
        [HttpGet("{id:int}/group-info")]
        public ActionResult<GroupDtos.ApiResult<GroupDtos.GroupInfoDto>> GroupInfo(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var anchor = SqlMapper.Query(conn,
                @"SELECT Id, BranchId, SaleGroupId
                  FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (anchor == null)
                return Ok(new GroupDtos.ApiResult<GroupDtos.GroupInfoDto>(false, "Appointment not found", null));

            object? saleGroupId = anchor.SaleGroupId;
            var memberFilter = saleGroupId != null
                ? "a.SaleGroupId = @G AND a.Status != 'cancelled'"
                : "a.Id = @Id";

            var services = SqlMapper.Query(conn, $@"
                SELECT
                    a.Id,
                    i.ITEM_NAME1                                    AS ItemName,
                    s.EnglishName                                   AS StaffName,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5)  AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5)  AS EndTime,
                    a.TotalPrice                                    AS Price,
                    a.PaidAmount                                    AS Paid
                FROM dbo.AppointmentData a
                INNER JOIN dbo.ITEM  i ON i.ITEM_ID = a.ItemId
                INNER JOIN dbo.STAFF s ON s.Id      = a.StaffId
                WHERE {memberFilter}
                ORDER BY a.StartTime, a.Id",
                new { G = saleGroupId, Id = id }).ToList();

            var currency = SqlMapper.Query<string>(conn,
                "SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                new { Id = (int)anchor.BranchId }).FirstOrDefault() ?? "KWD";

            decimal total = services.Sum(x => (decimal)x.Price);
            decimal paid = services.Sum(x => (decimal)x.Paid);

            var list = services.Select(x => new GroupDtos.GroupServiceDto(
                AppointmentId: (int)x.Id,
                ItemName: (string)(x.ItemName ?? ""),
                StaffName: (string)(x.StaffName ?? ""),
                StartTime: (string)x.StartTime,
                EndTime: (string)x.EndTime,
                Price: (decimal)x.Price)).ToList();

            var dto = new GroupDtos.GroupInfoDto(
                IsGroup: services.Count > 1,
                Count: services.Count,
                Total: total,
                Paid: paid,
                Remaining: total - paid,
                Currency: currency,
                Services: list);

            return Ok(new GroupDtos.ApiResult<GroupDtos.GroupInfoDto>(true, null, dto));
        }

        // =====================================================================
        // POST /api/appointments/{id}/group-checkout
        // =====================================================================
        [HttpPost("{id:int}/group-checkout")]
        public ActionResult<GroupDtos.ApiResult<GroupDtos.GroupCheckoutResponse>> GroupCheckout(
            int id, [FromBody] CheckoutRequest? request)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var anchor = SqlMapper.Query(conn,
                @"SELECT Id, BranchId, CustomerId, CheckoutStatus, SaleGroupId
                  FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (anchor == null)
                return Fail("Appointment not found");
            if ((string)anchor.CheckoutStatus == "checked_out")
                return Fail("Appointment is already checked out");

            // -------- Resolve group members (or just this one) --------
            object? saleGroupId = anchor.SaleGroupId;
            List<int> memberIds;
            if (saleGroupId != null)
            {
                memberIds = SqlMapper.Query<int>(conn,
                    @"SELECT Id FROM dbo.AppointmentData
                      WHERE SaleGroupId = @G
                        AND Status != 'cancelled'
                        AND CheckoutStatus != 'checked_out'",
                    new { G = saleGroupId }).ToList();
                if (!memberIds.Contains(id)) memberIds.Add(id);
            }
            else
            {
                memberIds = new List<int> { id };
            }

            using var uow = new UnitOfWork(conn);
            try
            {
                // Recalc each row so per-row checkout extras are included.
                foreach (var mid in memberIds)
                    RecalcAppointmentTotal(uow.Connection, mid);

                var rows = SqlMapper.Query(uow.Connection,
                    @"SELECT Id, TotalPrice, PaidAmount
                      FROM dbo.AppointmentData WHERE Id IN @Ids",
                    new { Ids = memberIds }).ToList();

                decimal groupTotal = rows.Sum(r => (decimal)r.TotalPrice);
                decimal groupPaid = rows.Sum(r => (decimal)r.PaidAmount);
                decimal remaining = groupTotal - groupPaid;

                string paymentStatus =
                    remaining <= 0.0001m ? "FULL" :
                    groupPaid > 0.0001m ? "DEPOSIT" : "NONE";

                // Mark the whole group checked out.
                SqlMapper.Execute(uow.Connection,
                    @"UPDATE dbo.AppointmentData
                      SET CheckoutStatus = 'checked_out', Status = 'completed',
                          UpdatedAt = SYSUTCDATETIME()
                      WHERE Id IN @Ids",
                    new { Ids = memberIds });

                var currency = SqlMapper.Query<string>(uow.Connection,
                    "SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                    new { Id = (int)anchor.BranchId }).FirstOrDefault() ?? "KWD";

                var lastPt = SqlMapper.Query<int?>(uow.Connection,
                    @"SELECT TOP 1 PaymentTypeId FROM dbo.AppointmentPayments
                      WHERE AppointmentId IN @Ids AND IsWalletPayment = 0
                      ORDER BY PaidAt DESC",
                    new { Ids = memberIds }).FirstOrDefault();

                // ONE consolidated invoice anchored on the entry appointment.
                var invoiceNumber = InvoiceNumberService.Next(uow.Connection, InvoiceNumberService.PrefixInvoice);
                var invoiceId = SqlMapper.Query<int>(uow.Connection, @"
                    INSERT INTO dbo.AppointmentInvoices (
                        InvoiceNumber, AppointmentId, BranchId, CustomerId,
                        TotalAmount, PaidAmount, RemainingAmount, Currency,
                        PaymentTypeId, PaymentStatus, CreatedAt
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        @InvoiceNumber, @AppointmentId, @BranchId, @CustomerId,
                        @TotalAmount, @PaidAmount, @RemainingAmount, @Currency,
                        @PaymentTypeId, @PaymentStatus, SYSUTCDATETIME()
                    )",
                    new
                    {
                        InvoiceNumber = invoiceNumber,
                        AppointmentId = id,
                        BranchId = (int)anchor.BranchId,
                        CustomerId = (int)anchor.CustomerId,
                        TotalAmount = groupTotal,
                        PaidAmount = groupPaid,
                        RemainingAmount = remaining,
                        Currency = currency,
                        PaymentTypeId = lastPt ?? request?.PaymentTypeId,
                        PaymentStatus = paymentStatus
                    }).First();

                // One invoice line per member service + per member's checkout extras.
                foreach (var mid in memberIds)
                {
                    SqlMapper.Execute(uow.Connection, @"
                        INSERT INTO dbo.AppointmentInvoiceLines
                            (InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                             UnitPrice, DiscountedUnitPrice, TotalPrice,
                             AppointmentDate, StartTime, EndTime, DurationMinutes, Notes)
                        SELECT
                            @InvoiceId, a.Id, a.ItemId, a.UnitId, a.StaffId,
                            a.DiscountedUnitPrice, a.DiscountedUnitPrice, a.DiscountedUnitPrice,
                            a.AppointmentDate,
                            ISNULL(a.StartTime, '00:00:00'),
                            ISNULL(a.EndTime,   '00:00:00'),
                            0, NULL
                        FROM dbo.AppointmentData a
                        WHERE a.Id = @Mid",
                        new { InvoiceId = invoiceId, Mid = mid });

                    SqlMapper.Execute(uow.Connection, @"
                        INSERT INTO dbo.AppointmentInvoiceLines
                            (InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                             UnitPrice, DiscountedUnitPrice, TotalPrice,
                             AppointmentDate, StartTime, EndTime, DurationMinutes, Notes)
                        SELECT
                            @InvoiceId, ci.AppointmentId, ci.ItemId, ci.UnitId, ci.StaffId,
                            ci.DiscountedUnitPrice, ci.DiscountedUnitPrice, ci.TotalPrice,
                            a.AppointmentDate,
                            ISNULL(a.StartTime, '00:00:00'),
                            ISNULL(a.EndTime,   '00:00:00'),
                            0, NULL
                        FROM dbo.AppointmentCheckoutItems ci
                        INNER JOIN dbo.AppointmentData a ON a.Id = ci.AppointmentId
                        WHERE ci.AppointmentId = @Mid",
                        new { InvoiceId = invoiceId, Mid = mid });
                }

                uow.Commit();

                var invoice = new InvoiceDto(
                    Id: invoiceId,
                    InvoiceNumber: invoiceNumber,
                    AppointmentId: id,
                    TotalAmount: groupTotal,
                    PaidAmount: groupPaid,
                    RemainingAmount: remaining,
                    Currency: currency,
                    PaymentTypeId: lastPt ?? request?.PaymentTypeId,
                    PaymentStatus: paymentStatus,
                    CreatedAt: DateTime.UtcNow);

                return Ok(new GroupDtos.ApiResult<GroupDtos.GroupCheckoutResponse>(
                    true, null, new GroupDtos.GroupCheckoutResponse(memberIds, invoice)));
            }
            catch (Exception ex)
            {
                return Ok(new GroupDtos.ApiResult<GroupDtos.GroupCheckoutResponse>(
                    false, $"Group checkout failed: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/appointments/{id}/group-payment
        //   Collect a payment against the WHOLE group's pooled balance. The
        //   amount is distributed greedily across the members (fill each
        //   member's own remaining), every member that receives money gets an
        //   AppointmentPayments row, and the consolidated invoice (if the group
        //   is already checked out) is kept in sync.
        // =====================================================================
        [HttpPost("{id:int}/group-payment")]
        public ActionResult<GroupDtos.ApiResult<GroupDtos.GroupPaymentResponse>> GroupPayment(
            int id, [FromBody] GroupDtos.GroupPaymentRequest request)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var anchor = SqlMapper.Query(conn,
                @"SELECT Id, BranchId, CheckoutStatus, SaleGroupId
                  FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();
            if (anchor == null)
                return FailPay("Appointment not found");
            if (request == null || request.Amount <= 0m)
                return FailPay("Payment amount must be greater than 0");

            var pt = SqlMapper.Query(conn,
                @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE
                  WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                new { Id = request.PaymentTypeId }).FirstOrDefault();
            if (pt == null) return FailPay("Payment type not found");

            object? saleGroupId = anchor.SaleGroupId;
            List<int> memberIds = saleGroupId != null
                ? SqlMapper.Query<int>(conn,
                    @"SELECT Id FROM dbo.AppointmentData
                      WHERE SaleGroupId = @G AND Status != 'cancelled'",
                    new { G = saleGroupId }).ToList()
                : new List<int> { id };
            if (!memberIds.Contains(id)) memberIds.Add(id);

            using var uow = new UnitOfWork(conn);
            try
            {
                foreach (var mid in memberIds)
                    RecalcAppointmentTotal(uow.Connection, mid);

                var members = SqlMapper.Query(uow.Connection,
                    @"SELECT Id, TotalPrice, PaidAmount
                      FROM dbo.AppointmentData WHERE Id IN @Ids
                      ORDER BY StartTime, Id",
                    new { Ids = memberIds })
                    .Select(r => new { Id = (int)r.Id, Total = (decimal)r.TotalPrice, Paid = (decimal)r.PaidAmount })
                    .ToList();

                decimal groupTotal = members.Sum(m => m.Total);
                decimal groupPaidBefore = members.Sum(m => m.Paid);
                decimal groupRemaining = groupTotal - groupPaidBefore;

                if (request.Amount - groupRemaining > 0.0001m)
                    return FailPay($"Amount ({request.Amount:F3}) exceeds the group balance ({groupRemaining:F3}).");

                var voucher = string.IsNullOrWhiteSpace(request.VoucherCode) ? null : request.VoucherCode.Trim();

                // Distribute greedily across members.
                decimal left = request.Amount;
                foreach (var m in members)
                {
                    if (left <= 0.0001m) break;
                    var memberRemaining = m.Total - m.Paid;
                    if (memberRemaining <= 0.0001m) continue;

                    var take = Math.Min(left, memberRemaining);
                    var newPaid = m.Paid + take;
                    var newStatus = (m.Total - newPaid) <= 0.0001m ? "FULL" : (newPaid > 0 ? "DEPOSIT" : "NONE");

                    SqlMapper.Execute(uow.Connection,
                        @"UPDATE dbo.AppointmentData SET
                              PaidAmount = @PaidAmount,
                              PaymentStatus = @PaymentStatus,
                              DepositAmount = @DepositAmount,
                              VoucherCode = COALESCE(@VoucherCode, VoucherCode),
                              UpdatedAt = SYSUTCDATETIME()
                          WHERE Id = @Id",
                        new
                        {
                            Id = m.Id,
                            PaidAmount = newPaid,
                            PaymentStatus = newStatus,
                            DepositAmount = newStatus == "DEPOSIT" ? newPaid : 0m,
                            VoucherCode = voucher
                        });

                    SqlMapper.Execute(uow.Connection,
                        @"INSERT INTO dbo.AppointmentPayments
                              (AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt, IsWalletPayment)
                          VALUES
                              (@AppointmentId, @Amount, @PaymentTypeId, @PaymentAs, @VoucherCode, SYSUTCDATETIME(), @IsWalletPayment)",
                        new
                        {
                            AppointmentId = m.Id,
                            Amount = take,
                            request.PaymentTypeId,
                            PaymentAs = newStatus == "FULL" ? "FULL" : "DEPOSIT",
                            VoucherCode = voucher,
                            request.IsWalletPayment
                        });

                    left -= take;
                }

                decimal groupPaidAfter = groupPaidBefore + request.Amount;
                decimal groupRemainingAfter = groupTotal - groupPaidAfter;
                string groupStatus = groupRemainingAfter <= 0.0001m ? "FULL" : (groupPaidAfter > 0 ? "DEPOSIT" : "NONE");

                // Keep the consolidated invoice in sync (only exists once checked out).
                if ((string)anchor.CheckoutStatus == "checked_out" && saleGroupId != null)
                {
                    var invId = SqlMapper.Query<int?>(uow.Connection, @"
                        SELECT TOP 1 il.InvoiceId
                        FROM dbo.AppointmentInvoiceLines il
                        INNER JOIN dbo.AppointmentData a ON a.Id = il.AppointmentId
                        WHERE a.SaleGroupId = @G
                        ORDER BY il.InvoiceId",
                        new { G = saleGroupId }).FirstOrDefault();

                    if (invId.HasValue)
                    {
                        SqlMapper.Execute(uow.Connection,
                            @"UPDATE dbo.AppointmentInvoices SET
                                  PaidAmount = @PaidAmount,
                                  RemainingAmount = @RemainingAmount,
                                  PaymentStatus = @PaymentStatus
                              WHERE Id = @Id",
                            new
                            {
                                Id = invId.Value,
                                PaidAmount = groupPaidAfter,
                                RemainingAmount = groupRemainingAfter,
                                PaymentStatus = groupStatus
                            });
                    }
                }

                uow.Commit();

                var currency = SqlMapper.Query<string>(conn,
                    "SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                    new { Id = (int)anchor.BranchId }).FirstOrDefault() ?? "KWD";

                var states = SqlMapper.Query(conn,
                    @"SELECT Id, TotalPrice, PaidAmount, PaymentStatus
                      FROM dbo.AppointmentData WHERE Id IN @Ids ORDER BY StartTime, Id",
                    new { Ids = memberIds })
                    .Select(r => new GroupDtos.GroupMemberPayState(
                        (int)r.Id, (decimal)r.TotalPrice, (decimal)r.PaidAmount,
                        (decimal)r.TotalPrice - (decimal)r.PaidAmount, (string)r.PaymentStatus))
                    .ToList();

                return Ok(new GroupDtos.ApiResult<GroupDtos.GroupPaymentResponse>(true, null,
                    new GroupDtos.GroupPaymentResponse(
                        groupTotal, groupPaidAfter, groupRemainingAfter, groupStatus, currency, states)));
            }
            catch (Exception ex)
            {
                return Ok(new GroupDtos.ApiResult<GroupDtos.GroupPaymentResponse>(
                    false, $"Group payment failed: {ex.Message}", null));
            }
        }

        // ===== helpers (duplicated minimally from AppointmentsApiController) =====

        private ActionResult<GroupDtos.ApiResult<GroupDtos.GroupCheckoutResponse>> Fail(string error) =>
            Ok(new GroupDtos.ApiResult<GroupDtos.GroupCheckoutResponse>(false, error, null));

        private ActionResult<GroupDtos.ApiResult<GroupDtos.GroupPaymentResponse>> FailPay(string error) =>
            Ok(new GroupDtos.ApiResult<GroupDtos.GroupPaymentResponse>(false, error, null));

        private void RecalcAppointmentTotal(IDbConnection conn, int appointmentId)
        {
            var originalPrice = SqlMapper.Query<decimal>(conn,
                "SELECT DiscountedUnitPrice FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = appointmentId }).FirstOrDefault();

            var extrasTotal = SqlMapper.Query<decimal?>(conn,
                "SELECT SUM(TotalPrice) FROM dbo.AppointmentCheckoutItems WHERE AppointmentId = @Id",
                new { Id = appointmentId }).FirstOrDefault() ?? 0;

            SqlMapper.Execute(conn,
                @"UPDATE dbo.AppointmentData
                  SET TotalPrice = @GrandTotal, UpdatedAt = SYSUTCDATETIME()
                  WHERE Id = @Id",
                new { Id = appointmentId, GrandTotal = originalPrice + extrasTotal });
        }


    }
}