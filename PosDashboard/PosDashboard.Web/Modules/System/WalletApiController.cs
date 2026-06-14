using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Serenity.Data;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WalletDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/wallet")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WalletApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IConfiguration _configuration;
        public WalletApiController(ISqlConnections sqlConnections, IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            _configuration = configuration;
        }

        // ── Resolve current user id from JWT ──
        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(claim, out var id) ? id : 0;
        }

        // =============================================
        // GET /api/wallet/types
        // Load SUBS_TYPE master list
        // =============================================
        [HttpGet("types")]
        public ActionResult<ApiResult<List<SubsTypeDto>>> GetTypes()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var list = conn.Query<SubsTypeDto>(@"
                SELECT
                    ID              AS Id,
                    NAME            AS Name,
                    CAST(VALUE AS float) AS Value,
                    DAYS_COUNT      AS DaysCount,
                    DiscountValue   AS DiscountValue,
                    CAST([Count] AS float) AS [Count],
                    Type            AS Type,
                    DiscountType    AS DiscountType
                FROM dbo.SUBS_TYPE
                ORDER BY ID")
                .ToList();

            return Ok(new ApiResult<List<SubsTypeDto>>(true, null, list));
        }

        // =============================================
        // GET /api/wallet/subscriptions?branchId=1&customerId=&page=1
        // =============================================
        // =============================================
        // GET /api/wallet/subscriptions?branchId=1&customerId=&page=1&month=6&year=2025
        // =============================================
        [HttpGet("subscriptions")]
        public ActionResult<ApiResult<List<SubscriptionDto>>> GetSubscriptions(
            [FromQuery] int? branchId = null,
            [FromQuery] int? customerId = null,
            [FromQuery] int? month = null,
            [FromQuery] int? year = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var offset = (page - 1) * pageSize;

            var list = conn.Query<dynamic>(@"
            SELECT
                s.Id,
                s.GUID,
                c.CUSTOMER_ID       AS CustomerId,
                c.CUSTOMER_NAME     AS CustomerName,
                c.CUSTOMER_PHONE1   AS CustomerPhone,
                s.SubTypeId,
                st.NAME             AS SubTypeName,
                ISNULL(s.Value, 0)  AS Value,
                s.DiscountType,
                s.DiscountValue,
                ISNULL(s.Net, 0)    AS Net,
                s.[Count]           AS [Count],
                s.StartDate,
                s.EndDate,
                s.DaysCount,
                s.BranchId,
                ISNULL(s.IsPaid, 0) AS IsPaid,
                s.AddedDate,
                s.PayerCustomerId,
                pc.CUSTOMER_NAME    AS PayerCustomerName,
                s.PayerNote,
                ISNULL((
                    SELECT TOP 1 sh.Balance
                    FROM dbo.SubscriptionsHistory sh
                    WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                    ORDER BY sh.Id DESC
                ), 0) AS CurrentBalance,
                ISNULL((
                    SELECT SUM(sh.Amount)
                    FROM dbo.SubscriptionsHistory sh
                    WHERE sh.SubscriptionId = s.Id
                      AND sh.Deleted = 0
                      AND sh.Amount > 0
                ), 0) AS TotalCredit,
                ISNULL((
                    SELECT SUM(sp.PAYMENT_AMOUNT)
                    FROM dbo.SubscriptionPayment sp
                    WHERE sp.SubscriptionId = s.Id AND sp.DELETED = 0
                ), 0) AS TotalPaid,
                ISNULL((
                    SELECT TOP 1 sp.ActionType
                    FROM dbo.SubscriptionPayment sp
                    WHERE sp.SubscriptionId = s.Id AND sp.DELETED = 0
                    ORDER BY sp.Id DESC
                ), 'CREATE') AS LastActionType
            FROM dbo.Subscriptions s
            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
            INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
            LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
            WHERE s.Deleted = 0
              AND (@BranchId IS NULL OR s.BranchId = @BranchId)
              AND (@CustomerId IS NULL OR c.CUSTOMER_ID = @CustomerId)
              AND (@Month IS NULL OR MONTH(s.AddedDate) = @Month)
              AND (@Year IS NULL OR YEAR(s.AddedDate) = @Year)
            ORDER BY s.AddedDate DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
            new { BranchId = branchId, CustomerId = customerId, Month = month, Year = year, Offset = offset, PageSize = pageSize })
            .ToList();

            var now = DateTime.UtcNow;
            var result = list.Select(r => (SubscriptionDto)MapToSubscriptionDto(r, now)).ToList();

            return Ok(new ApiResult<List<SubscriptionDto>>(true, null, result));
        }

        // =============================================
        // GET /api/wallet/subscriptions/{id}
        // Full wallet detail with payments + history
        // =============================================
        [HttpGet("subscriptions/{id:int}")]
        public ActionResult<ApiResult<WalletDetailDto>> GetSubscriptionDetail(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var sub = conn.Query<dynamic>(@"
            SELECT
                s.Id, s.GUID,
                c.CUSTOMER_ID       AS CustomerId,
                c.CUSTOMER_NAME     AS CustomerName,
                c.CUSTOMER_PHONE1   AS CustomerPhone,
                s.SubTypeId,
                st.NAME             AS SubTypeName,
                ISNULL(s.Value, 0)  AS Value,
                s.DiscountType,
                s.DiscountValue,
                ISNULL(s.Net, 0)    AS Net,
                s.[Count]           AS [Count],
                s.StartDate,
                s.EndDate,
                s.DaysCount,
                s.BranchId,
                ISNULL(s.IsPaid, 0) AS IsPaid,
                s.AddedDate,
                s.PayerCustomerId,
                pc.CUSTOMER_NAME    AS PayerCustomerName,
                s.PayerNote,
                ISNULL((
                    SELECT TOP 1 sh.Balance
                    FROM dbo.SubscriptionsHistory sh
                    WHERE sh.SubscriptionId = s.Id
                      AND sh.Deleted = 0
                    ORDER BY sh.Id DESC
                ), 0) AS CurrentBalance
            FROM dbo.Subscriptions s
            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
            INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
            LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
            WHERE s.Id = @Id AND s.Deleted = 0",
            new { Id = id }).FirstOrDefault();

            if (sub == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Subscription not found", null));

            var now = DateTime.UtcNow;
            var subDto = MapToSubscriptionDto(sub, now);

            var payments = conn.Query<dynamic>(@"
                SELECT
                sp.Id,
                sp.SubscriptionId,
                sp.PAYMENT_TYPE_ID  AS PaymentTypeId,
                ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '') AS PaymentTypeName,
                ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME2, '') AS PaymentTypeNameAr,
                sp.PAYMENT_AMOUNT   AS PaymentAmount,
                sp.PAYMENT_DATE     AS PaymentDate,
                sp.Notes,
                ISNULL(sp.ActionType, 'CREATE') AS ActionType,
                sp.PreviousSubTypeId,
                pst.NAME            AS PreviousSubTypeName
            FROM dbo.SubscriptionPayment sp
            LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt
                ON pt.INVOICE_PAYMENT_TYPE_ID = sp.PAYMENT_TYPE_ID
            LEFT JOIN dbo.SUBS_TYPE pst
                ON pst.ID = sp.PreviousSubTypeId
            WHERE sp.SubscriptionId = @Id AND sp.DELETED = 0
            ORDER BY sp.PAYMENT_DATE",
                new { Id = id })
                .Select(p => new SubscriptionPaymentDto(
                    Id: (int)p.Id,
                    SubscriptionId: (int)p.SubscriptionId,
                    PaymentTypeId: (int)p.PaymentTypeId,
                    PaymentTypeName: (string)(p.PaymentTypeName ?? ""),
                    PaymentTypeNameAr: (string)(p.PaymentTypeNameAr ?? ""),
                    PaymentAmount: (decimal)p.PaymentAmount,
                    PaymentDate: (DateTime)p.PaymentDate,
                    Notes: (string?)p.Notes,
                    ActionType: (string)p.ActionType,
                    PreviousSubTypeId: (int?)p.PreviousSubTypeId,
                    PreviousSubTypeName: (string?)p.PreviousSubTypeName
                )).ToList();

            var history = conn.Query<dynamic>(@"
                SELECT
                    sh.Id,
                    sh.SubscriptionId,
                    sh.RefType,
                    sh.Amount,
                    sh.Balance,
                    sh.AddedDate,
                    sh.InvoiceId
                FROM dbo.SubscriptionsHistory sh
                WHERE sh.SubscriptionId = @Id
                  AND sh.Deleted = 0
                ORDER BY sh.Id",
                new { Id = id })
                .Select(h => new SubscriptionHistoryDto(
                    Id: (int)h.Id,
                    SubscriptionId: (int?)h.SubscriptionId,
                    RefType: (int)h.RefType,
                    RefTypeLabel: MapRefType((int)h.RefType),
                    Amount: (decimal)h.Amount,
                    Balance: (decimal)h.Balance,
                    AddedDate: (DateTime)h.AddedDate,
                    InvoiceId: (int?)h.InvoiceId
                )).ToList();

            var detail = new WalletDetailDto(subDto, payments, history);
            return Ok(new ApiResult<WalletDetailDto>(true, null, detail));
        }

        // =============================================
        // GET /api/wallet/customer-summary?customerId=1
        // Quick balance check for appointment drawer
        // =============================================
        [HttpGet("customer-summary")]
        public ActionResult<ApiResult<CustomerWalletSummaryDto>> GetCustomerSummary(
            [FromQuery] int customerId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var customer = conn.Query<dynamic>(
                "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<CustomerWalletSummaryDto>(false, "Customer not found", null));

            Guid refGuide = (Guid)customer.RefGuide;
            var now = DateTime.UtcNow;

            var activeSub = conn.Query<dynamic>(@"
                SELECT TOP 1
                    s.Id,
                    st.NAME AS SubTypeName,
                    s.EndDate,
                    ISNULL((
                        SELECT TOP 1 sh.Balance
                        FROM dbo.SubscriptionsHistory sh
                        WHERE sh.SubscriptionId = s.Id
                          AND sh.Deleted = 0
                        ORDER BY sh.Id DESC
                    ), 0) AS CurrentBalance
                FROM dbo.Subscriptions s
                INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                WHERE s.CustomerRef = @Ref
                  AND s.Deleted = 0
                  AND ISNULL(s.IsPaid, 0) = 1
                  AND s.EndDate >= @Now
                ORDER BY s.EndDate DESC",
                new { Ref = refGuide, Now = now }).FirstOrDefault();

            if (activeSub == null || (decimal)activeSub.CurrentBalance <= 0)
            {
                return Ok(new ApiResult<CustomerWalletSummaryDto>(true, null,
                    new CustomerWalletSummaryDto(false, 0, null, null, null)));
            }

            var summary = new CustomerWalletSummaryDto(
                HasActiveWallet: true,
                CurrentBalance: (decimal)activeSub.CurrentBalance,
                SubscriptionId: (int)activeSub.Id,
                SubTypeName: (string?)activeSub.SubTypeName,
                EndDate: (DateTime?)activeSub.EndDate
            );

            return Ok(new ApiResult<CustomerWalletSummaryDto>(true, null, summary));
        }

        // =============================================
        // POST /api/wallet/subscriptions
        // Create wallet / subscription purchase
        // =============================================
        [HttpPost("subscriptions")]
        public ActionResult<ApiResult<WalletDetailDto>> CreateSubscription(
            [FromBody] CreateSubscriptionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Could not resolve current user", null));

                // Validate wallet owner
                var customer = conn.Query<dynamic>(
                    @"SELECT CUSTOMER_ID, CUSTOMER_REF_GUIDE AS RefGuide, CUSTOMER_NAME, CUSTOMER_PHONE1
              FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();

                if (customer == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Customer not found", null));

                // ── PHASE 2: enforce one-wallet-per-customer ──
                var existingWallet = conn.Query<dynamic>(@"
                SELECT Id FROM dbo.Subscriptions
                WHERE CustomerRef = @Ref AND Deleted = 0",
                    new { Ref = (Guid)customer.RefGuide }).FirstOrDefault();

                if (existingWallet != null)
                {
                    return Ok(new ApiResult<WalletDetailDto>(false,
                        $"This customer already has a wallet (#{(int)existingWallet.Id}). Use Renew or Upgrade instead.",
                        null));
                }
                // Validate payer customer if provided (must exist; can equal owner)
                string? payerCustomerName = null;
                if (request.PayerCustomerId.HasValue)
                {
                    var payer = conn.Query<dynamic>(
                        @"SELECT CUSTOMER_ID, CUSTOMER_NAME FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                        new { Id = request.PayerCustomerId.Value }).FirstOrDefault();

                    if (payer == null)
                        return Ok(new ApiResult<WalletDetailDto>(false, "Payer customer not found", null));

                    payerCustomerName = (string)payer.CUSTOMER_NAME;
                }

                var branch = conn.Query<dynamic>(
                    "SELECT BRANCH_ID FROM dbo.BRANCH WHERE BRANCH_ID = @Id AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                    new { Id = request.BranchId }).FirstOrDefault();

                if (branch == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Branch not found or inactive", null));

                var paymentType = conn.Query<dynamic>(
                    "SELECT INVOICE_PAYMENT_TYPE_ID, INVOICE_PAYMENT_TYPE_NAME1 FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = request.PaymentTypeId }).FirstOrDefault();

                if (paymentType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Payment type not found", null));

                var subsType = conn.Query<dynamic>(
                    "SELECT ID, NAME, VALUE, DAYS_COUNT, DiscountValue, [Count], Type, DiscountType FROM dbo.SUBS_TYPE WHERE ID = @Id",
                    new { Id = request.SubTypeId }).FirstOrDefault();

                if (subsType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Subscription type not found", null));

                decimal rawValue = request.CustomValue.HasValue
                    ? request.CustomValue.Value
                    : Convert.ToDecimal(subsType.VALUE ?? 0);

                decimal discountValue = subsType.DiscountValue != null
                    ? Convert.ToDecimal(subsType.DiscountValue)
                    : 0m;

                int? discountType = subsType.DiscountType != null
                    ? Convert.ToInt32(subsType.DiscountType)
                    : null;

                decimal net = request.CustomNet.HasValue
                    ? request.CustomNet.Value
                    : CalculateNet(rawValue, discountValue, discountType);

                int daysCount = subsType.DAYS_COUNT != null
                    ? Convert.ToInt32(subsType.DAYS_COUNT)
                    : 365;

                DateTime startDate = request.StartDate.Date;
                DateTime endDate = startDate.AddDays(daysCount);

                decimal count = subsType.Count != null
                    ? Convert.ToDecimal(subsType.Count)
                    : 0m;

                Guid refGuide = (Guid)customer.RefGuide;
                Guid subGuid = Guid.NewGuid();
                var now = DateTime.UtcNow;

                // Normalize payer note
                string? payerNote = string.IsNullOrWhiteSpace(request.PayerNote)
                    ? null
                    : request.PayerNote.Trim();

                var subId = SqlMapper.Query<int>(conn, @"
            INSERT INTO dbo.Subscriptions (
                GUID, CustomerRef, SubTypeId, Value, DiscountType, DiscountValue,
                Net, [Count], StartDate, EndDate, DaysCount,
                BranchId, AddedBy, AddedDate, Deleted, IsPaid,
                SHIFT_ID, ActiveOnline, Source,
                PayerCustomerId, PayerNote
            )
            OUTPUT INSERTED.Id
            VALUES (
                @Guid, @CustomerRef, @SubTypeId, @Value, @DiscountType, @DiscountValue,
                @Net, @Count, @StartDate, @EndDate, @DaysCount,
                @BranchId, @AddedBy, @AddedDate, 0, 1,
                0, 0, 0,
                @PayerCustomerId, @PayerNote
            )",
                    new
                    {
                        Guid = subGuid,
                        CustomerRef = refGuide,
                        SubTypeId = request.SubTypeId,
                        Value = rawValue,
                        DiscountType = discountType,
                        DiscountValue = discountValue,
                        Net = net,
                        Count = count,
                        StartDate = startDate,
                        EndDate = endDate,
                        DaysCount = (decimal)daysCount,
                        BranchId = request.BranchId,
                        AddedBy = userId,
                        AddedDate = now,
                        PayerCustomerId = request.PayerCustomerId,
                        PayerNote = payerNote
                    }).FirstOrDefault();

                    SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.SubscriptionPayment (
                        SubscriptionPaymentGuid, PAYMENT_TYPE_ID, SubscriptionId,
                        PAYMENT_AMOUNT, PAYMENT_DATE, Notes,
                        DELETED, AddedBy, AddedDate, ShiftId, isCollected,
                        ActionType, PreviousSubTypeId
                    )
                    VALUES (
                        NEWID(), @PaymentTypeId, @SubscriptionId,
                        @PaymentAmount, @PaymentDate, @Notes,
                        0, @AddedBy, @AddedDate, 0, 0,
                        'CREATE', NULL
                    )",
                    new
                    {
                        PaymentTypeId = request.PaymentTypeId,
                        SubscriptionId = subId,
                        PaymentAmount = net,
                        PaymentDate = now,
                        Notes = request.Notes,
                        AddedBy = userId,
                        AddedDate = now
                    });

                // Wallet credit = Count (not Net)
                SqlMapper.Execute(conn, @"
            INSERT INTO dbo.SubscriptionsHistory (
                CustomerRef, RefType, InvoiceId, SubscriptionId,
                Amount, Balance, AddedBy, AddedDate, Deleted
            )
            VALUES (
                @CustomerRef, 0, NULL, @SubscriptionId,
                @Amount, @Balance, @AddedBy, @AddedDate, 0
            )",
                    new
                    {
                        CustomerRef = refGuide,
                        SubscriptionId = subId,
                        Amount = count,
                        Balance = count,
                        AddedBy = userId,
                        AddedDate = now
                    });

                using var conn2 = sqlConnections.NewByKey("Default");
                var detailResult = GetSubscriptionDetailInternal(conn2, subId);

                return Ok(new ApiResult<WalletDetailDto>(true, null, detailResult));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletDetailDto>(false, $"Failed to create subscription: {ex.Message}", null));
            }
        }

        // =============================================
        // POST /api/wallet/subscriptions/{id}/renew
        // Top up the same wallet (same SubTypeId).
        // =============================================
        [HttpPost("subscriptions/{id:int}/renew")]
        public ActionResult<ApiResult<WalletDetailDto>> RenewSubscription(
            int id, [FromBody] RenewSubscriptionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Could not resolve current user", null));

                var sub = conn.Query<dynamic>(@"
            SELECT s.Id, s.CustomerRef, s.SubTypeId, s.EndDate,
                   ISNULL(s.Deleted, 0) AS Deleted
            FROM dbo.Subscriptions s
            WHERE s.Id = @Id",
                    new { Id = id }).FirstOrDefault();

                if (sub == null || (int)sub.Deleted == 1)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Wallet not found", null));

                var subsType = conn.Query<dynamic>(
                    "SELECT ID, VALUE, DAYS_COUNT, DiscountValue, [Count], DiscountType FROM dbo.SUBS_TYPE WHERE ID = @Id",
                    new { Id = (int)sub.SubTypeId }).FirstOrDefault();

                if (subsType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Wallet type not found", null));

                var paymentType = conn.Query<dynamic>(
                    "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = request.PaymentTypeId }).FirstOrDefault();
                if (paymentType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Payment type not found", null));

                // Validate payer if provided
                if (request.PayerCustomerId.HasValue)
                {
                    var payer = conn.Query<dynamic>(
                        "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                        new { Id = request.PayerCustomerId.Value }).FirstOrDefault();
                    if (payer == null)
                        return Ok(new ApiResult<WalletDetailDto>(false, "Payer customer not found", null));
                }

                decimal rawValue = request.CustomValue ?? Convert.ToDecimal(subsType.VALUE ?? 0);
                decimal discountValue = subsType.DiscountValue != null ? Convert.ToDecimal(subsType.DiscountValue) : 0m;
                int? discountType = subsType.DiscountType != null ? Convert.ToInt32(subsType.DiscountType) : null;
                decimal net = request.CustomNet ?? CalculateNet(rawValue, discountValue, discountType);
                decimal creditGranted = subsType.Count != null ? Convert.ToDecimal(subsType.Count) : 0m;
                int daysCount = subsType.DAYS_COUNT != null ? Convert.ToInt32(subsType.DAYS_COUNT) : 365;

                DateTime startDate = (request.StartDate ?? DateTime.UtcNow).Date;
                // Renewal extends from MAX(current EndDate, requested StartDate) by daysCount
                DateTime baseDate = ((DateTime)sub.EndDate) > startDate ? (DateTime)sub.EndDate : startDate;
                DateTime newEndDate = baseDate.AddDays(daysCount);

                var now = DateTime.UtcNow;

                // Current running balance
                var currentBalance = conn.Query<decimal?>(@"
            SELECT TOP 1 Balance FROM dbo.SubscriptionsHistory
            WHERE SubscriptionId = @Id AND Deleted = 0
            ORDER BY Id DESC",
                    new { Id = id }).FirstOrDefault() ?? 0m;

                // 1) Extend wallet end date and mark as paid
                SqlMapper.Execute(conn, @"
            UPDATE dbo.Subscriptions SET
                EndDate = @EndDate,
                IsPaid = 1,
                PayerCustomerId = COALESCE(@PayerCustomerId, PayerCustomerId),
                PayerNote = COALESCE(@PayerNote, PayerNote)
            WHERE Id = @Id",
                    new
                    {
                        Id = id,
                        EndDate = newEndDate,
                        PayerCustomerId = request.PayerCustomerId,
                        PayerNote = string.IsNullOrWhiteSpace(request.PayerNote) ? null : request.PayerNote.Trim()
                    });

                // 2) Insert renewal payment row
                SqlMapper.Execute(conn, @"
            INSERT INTO dbo.SubscriptionPayment (
                SubscriptionPaymentGuid, PAYMENT_TYPE_ID, SubscriptionId,
                PAYMENT_AMOUNT, PAYMENT_DATE, Notes,
                DELETED, AddedBy, AddedDate, ShiftId, isCollected,
                ActionType, PreviousSubTypeId
            )
            VALUES (
                NEWID(), @PaymentTypeId, @SubscriptionId,
                @PaymentAmount, @PaymentDate, @Notes,
                0, @AddedBy, @AddedDate, 0, 0,
                'RENEW', NULL
            )",
                    new
                    {
                        PaymentTypeId = request.PaymentTypeId,
                        SubscriptionId = id,
                        PaymentAmount = net,
                        PaymentDate = now,
                        Notes = request.Notes,
                        AddedBy = userId,
                        AddedDate = now
                    });

                // 3) Insert ledger credit row (RefType=0)
                SqlMapper.Execute(conn, @"
            INSERT INTO dbo.SubscriptionsHistory (
                CustomerRef, RefType, InvoiceId, SubscriptionId,
                Amount, Balance, AddedBy, AddedDate, Deleted
            )
            VALUES (
                @CustomerRef, 0, NULL, @SubscriptionId,
                @Amount, @Balance, @AddedBy, @AddedDate, 0
            )",
                    new
                    {
                        CustomerRef = (Guid)sub.CustomerRef,
                        SubscriptionId = id,
                        Amount = creditGranted,
                        Balance = currentBalance + creditGranted,
                        AddedBy = userId,
                        AddedDate = now
                    });

                using var conn2 = sqlConnections.NewByKey("Default");
                var detail = GetSubscriptionDetailInternal(conn2, id);
                return Ok(new ApiResult<WalletDetailDto>(true, null, detail));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletDetailDto>(false, $"Failed to renew wallet: {ex.Message}", null));
            }
        }

        // =============================================
        // POST /api/wallet/subscriptions/{id}/upgrade
        // Change wallet type on the same wallet record.
        // =============================================
        [HttpPost("subscriptions/{id:int}/upgrade")]
        public ActionResult<ApiResult<WalletDetailDto>> UpgradeSubscription(
            int id, [FromBody] UpgradeSubscriptionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Could not resolve current user", null));

                var sub = conn.Query<dynamic>(@"
            SELECT s.Id, s.CustomerRef, s.SubTypeId, s.EndDate,
                   ISNULL(s.Deleted, 0) AS Deleted
            FROM dbo.Subscriptions s
            WHERE s.Id = @Id",
                    new { Id = id }).FirstOrDefault();

                if (sub == null || (int)sub.Deleted == 1)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Wallet not found", null));

                int previousSubTypeId = (int)sub.SubTypeId;
                if (previousSubTypeId == request.NewSubTypeId)
                    return Ok(new ApiResult<WalletDetailDto>(false,
                        "New wallet type must be different from the current type. Use Renew instead.", null));

                var newType = conn.Query<dynamic>(
                    "SELECT ID, VALUE, DAYS_COUNT, DiscountValue, [Count], DiscountType FROM dbo.SUBS_TYPE WHERE ID = @Id",
                    new { Id = request.NewSubTypeId }).FirstOrDefault();

                if (newType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "New wallet type not found", null));

                var paymentType = conn.Query<dynamic>(
                    "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = request.PaymentTypeId }).FirstOrDefault();
                if (paymentType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Payment type not found", null));

                if (request.PayerCustomerId.HasValue)
                {
                    var payer = conn.Query<dynamic>(
                        "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                        new { Id = request.PayerCustomerId.Value }).FirstOrDefault();
                    if (payer == null)
                        return Ok(new ApiResult<WalletDetailDto>(false, "Payer customer not found", null));
                }

                decimal rawValue = request.CustomValue ?? Convert.ToDecimal(newType.VALUE ?? 0);
                decimal discountValue = newType.DiscountValue != null ? Convert.ToDecimal(newType.DiscountValue) : 0m;
                int? discountType = newType.DiscountType != null ? Convert.ToInt32(newType.DiscountType) : null;
                decimal net = request.CustomNet ?? CalculateNet(rawValue, discountValue, discountType);
                decimal creditGranted = newType.Count != null ? Convert.ToDecimal(newType.Count) : 0m;
                int daysCount = newType.DAYS_COUNT != null ? Convert.ToInt32(newType.DAYS_COUNT) : 365;

                DateTime startDate = (request.StartDate ?? DateTime.UtcNow).Date;
                DateTime baseDate = ((DateTime)sub.EndDate) > startDate ? (DateTime)sub.EndDate : startDate;
                DateTime newEndDate = baseDate.AddDays(daysCount);

                var now = DateTime.UtcNow;

                var currentBalance = conn.Query<decimal?>(@"
            SELECT TOP 1 Balance FROM dbo.SubscriptionsHistory
            WHERE SubscriptionId = @Id AND Deleted = 0
            ORDER BY Id DESC",
                    new { Id = id }).FirstOrDefault() ?? 0m;

                // 1) Change wallet type + extend end date
                SqlMapper.Execute(conn, @"
            UPDATE dbo.Subscriptions SET
                SubTypeId = @NewSubTypeId,
                EndDate = @EndDate,
                DaysCount = @DaysCount,
                IsPaid = 1,
                PayerCustomerId = COALESCE(@PayerCustomerId, PayerCustomerId),
                PayerNote = COALESCE(@PayerNote, PayerNote)
            WHERE Id = @Id",
                    new
                    {
                        Id = id,
                        NewSubTypeId = request.NewSubTypeId,
                        EndDate = newEndDate,
                        DaysCount = (decimal)daysCount,
                        PayerCustomerId = request.PayerCustomerId,
                        PayerNote = string.IsNullOrWhiteSpace(request.PayerNote) ? null : request.PayerNote.Trim()
                    });

                // 2) Insert upgrade payment row with PreviousSubTypeId audit trail
                SqlMapper.Execute(conn, @"
            INSERT INTO dbo.SubscriptionPayment (
                SubscriptionPaymentGuid, PAYMENT_TYPE_ID, SubscriptionId,
                PAYMENT_AMOUNT, PAYMENT_DATE, Notes,
                DELETED, AddedBy, AddedDate, ShiftId, isCollected,
                ActionType, PreviousSubTypeId
            )
            VALUES (
                NEWID(), @PaymentTypeId, @SubscriptionId,
                @PaymentAmount, @PaymentDate, @Notes,
                0, @AddedBy, @AddedDate, 0, 0,
                'UPGRADE', @PreviousSubTypeId
            )",
                    new
                    {
                        PaymentTypeId = request.PaymentTypeId,
                        SubscriptionId = id,
                        PaymentAmount = net,
                        PaymentDate = now,
                        Notes = request.Notes,
                        AddedBy = userId,
                        AddedDate = now,
                        PreviousSubTypeId = previousSubTypeId
                    });

                // 3) Insert ledger credit row
                SqlMapper.Execute(conn, @"
            INSERT INTO dbo.SubscriptionsHistory (
                CustomerRef, RefType, InvoiceId, SubscriptionId,
                Amount, Balance, AddedBy, AddedDate, Deleted
            )
            VALUES (
                @CustomerRef, 0, NULL, @SubscriptionId,
                @Amount, @Balance, @AddedBy, @AddedDate, 0
            )",
                    new
                    {
                        CustomerRef = (Guid)sub.CustomerRef,
                        SubscriptionId = id,
                        Amount = creditGranted,
                        Balance = currentBalance + creditGranted,
                        AddedBy = userId,
                        AddedDate = now
                    });

                using var conn2 = sqlConnections.NewByKey("Default");
                var detail = GetSubscriptionDetailInternal(conn2, id);
                return Ok(new ApiResult<WalletDetailDto>(true, null, detail));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletDetailDto>(false, $"Failed to upgrade wallet: {ex.Message}", null));
            }
        }

        // =============================================
        // POST /api/wallet/deduct
        // Deduct wallet balance for an appointment payment
        // =============================================
        [HttpPost("deduct")]
        public ActionResult<ApiResult<DeductWalletResponse>> DeductWallet(
            [FromBody] DeductWalletRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<DeductWalletResponse>(false, "Request body is required", null));

            if (request.Amount <= 0)
                return Ok(new ApiResult<DeductWalletResponse>(false, "Amount must be greater than 0", null));

            using var conn = sqlConnections.NewByKey("Default");

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Could not resolve current user", null));

                var now = DateTime.UtcNow;

                var apt = conn.Query<dynamic>(
                    "SELECT Id, TotalPrice, PaidAmount, PaymentStatus, CheckoutStatus, CustomerId FROM dbo.AppointmentData WHERE Id = @Id",
                    new { Id = request.AppointmentId }).FirstOrDefault();

                if (apt == null)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Appointment not found", null));

                if ((string)apt.CheckoutStatus == "cancelled")
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Cannot apply payment to cancelled appointment", null));

                decimal totalPrice = (decimal)apt.TotalPrice;
                decimal currentPaid = (decimal)apt.PaidAmount;
                decimal remaining = totalPrice - currentPaid;

                if (remaining <= 0)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Appointment is already fully paid", null));

                if (request.Amount > remaining)
                    return Ok(new ApiResult<DeductWalletResponse>(false, $"Amount exceeds remaining balance of {remaining}", null));

                var sub = conn.Query<dynamic>(@"
                    SELECT s.Id, s.CustomerRef, s.EndDate,
                           ISNULL(s.Deleted, 0) AS Deleted,
                           ISNULL(s.IsPaid, 0) AS IsPaid,
                           ISNULL((
                               SELECT TOP 1 sh.Balance
                               FROM dbo.SubscriptionsHistory sh
                               WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                               ORDER BY sh.Id DESC
                           ), 0) AS CurrentBalance
                    FROM dbo.Subscriptions s
                    WHERE s.Id = @Id",
                    new { Id = request.SubscriptionId }).FirstOrDefault();

                if (sub == null || (int)sub.Deleted == 1)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription not found", null));

                if ((int)sub.IsPaid != 1)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription is not paid", null));

                if ((DateTime)sub.EndDate < now)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription has expired", null));

                decimal currentBalance = (decimal)sub.CurrentBalance;

                if (currentBalance < request.Amount)
                    return Ok(new ApiResult<DeductWalletResponse>(false, $"Insufficient wallet balance. Available: {currentBalance}", null));

                var customer = conn.Query<dynamic>(
                    "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = (int)apt.CustomerId }).FirstOrDefault();

                if (customer == null || (Guid)customer.RefGuide != (Guid)sub.CustomerRef)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription does not belong to this appointment's customer", null));

                var ptExists = conn.Query<dynamic>(
                    "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = request.PaymentTypeId }).FirstOrDefault();

                if (ptExists == null)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Payment type not found", null));

                decimal newBalance = currentBalance - request.Amount;

                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.AppointmentPayments
                        (AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt, IsWalletPayment)
                    VALUES
                        (@AppointmentId, @Amount, @PaymentTypeId,
                         @PaymentAs, NULL, SYSUTCDATETIME(), 1)",
                    new
                    {
                        AppointmentId = request.AppointmentId,
                        Amount = request.Amount,
                        PaymentTypeId = request.PaymentTypeId,
                        PaymentAs = request.Amount >= remaining ? "FULL" : "DEPOSIT"
                    });


                decimal newPaid = currentPaid + request.Amount;
                decimal newRemaining = totalPrice - newPaid;
                string newPaymentStatus = newRemaining <= 0 ? "FULL"
                    : newPaid > 0 ? "DEPOSIT"
                    : "NONE";
                decimal depositAmount = newPaymentStatus == "DEPOSIT" ? newPaid : 0;

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.AppointmentData SET
                        PaidAmount = @PaidAmount,
                        PaymentStatus = @PaymentStatus,
                        DepositAmount = @DepositAmount,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE Id = @Id",
                    new
                    {
                        Id = request.AppointmentId,
                        PaidAmount = newPaid,
                        PaymentStatus = newPaymentStatus,
                        DepositAmount = depositAmount
                    });

                if ((string)apt.CheckoutStatus == "checked_out")
                {
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentInvoices SET
                            PaidAmount = @PaidAmount,
                            RemainingAmount = @RemainingAmount,
                            PaymentStatus = @PaymentStatus
                        WHERE AppointmentId = @AppointmentId",
                        new
                        {
                            AppointmentId = request.AppointmentId,
                            PaidAmount = newPaid,
                            RemainingAmount = Math.Max(0, newRemaining),
                            PaymentStatus = newPaymentStatus
                        });
                }

                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.SubscriptionsHistory (
                        CustomerRef, RefType, InvoiceId, SubscriptionId,
                        Amount, Balance, AddedBy, AddedDate, Deleted
                    )
                    VALUES (
                        @CustomerRef, 1, NULL, @SubscriptionId,
                        @Amount, @Balance, @AddedBy, @AddedDate, 0
                    )",
                    new
                    {
                        CustomerRef = (Guid)sub.CustomerRef,
                        SubscriptionId = request.SubscriptionId,
                        Amount = -request.Amount,
                        Balance = newBalance,
                        AddedBy = userId,
                        AddedDate = now
                    });

                var response = new DeductWalletResponse(
                    AppointmentId: request.AppointmentId,
                    SubscriptionId: request.SubscriptionId,
                    DeductedAmount: request.Amount,
                    RemainingWalletBalance: newBalance,
                    AppointmentPaidAmount: newPaid,
                    AppointmentRemainingAmount: Math.Max(0, newRemaining),
                    AppointmentPaymentStatus: newPaymentStatus
                );

                return Ok(new ApiResult<DeductWalletResponse>(true, null, response));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeductWalletResponse>(false, $"Failed to deduct wallet: {ex.Message}", null));
            }
        }

        // =============================================
        // POST /api/wallet/send-wallet-whatsapp
        // Send WhatsApp notification after wallet purchase
        // =============================================
        [HttpPost("send-wallet-whatsapp")]
        public async Task<ActionResult<ApiResult<object>>> SendWalletWhatsApp(
            [FromBody] int subscriptionId,
            [FromServices] IHttpClientFactory httpClientFactory)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var config = conn.Query<dynamic>(@"
                SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
                FROM dbo.WHATSAPP_CONFIG
                ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
                return Ok(new ApiResult<object>(true, null, new { Sent = false, Reason = "WhatsApp disabled" }));

            var sub = conn.Query<dynamic>(@"
            SELECT
                s.Id, ISNULL(s.Net, 0) AS Net, s.StartDate, s.EndDate,
                ISNULL(s.[Count], 0) AS [Count],
                st.NAME AS SubTypeName,
                ISNULL((SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                        WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                        ORDER BY sh.Id DESC), 0) AS CurrentBalance,
                c.CUSTOMER_NAME AS CustomerName,
                c.CUSTOMER_PHONE1 AS CustomerPhone,
                ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
                b.ArabicCurrencyName, b.EnglishCurrencyName,
                s.PayerCustomerId,
                pc.CUSTOMER_NAME AS PayerCustomerName,
                s.PayerNote
            FROM dbo.Subscriptions s
            INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
            LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = s.BranchId
            LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
            WHERE s.Id = @Id AND s.Deleted = 0",
            new { Id = subscriptionId }).FirstOrDefault();

            if (sub == null)
                return Ok(new ApiResult<object>(false, "Subscription not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string lang = (string)sub.CustomerLang;
            string currency = lang == "en"
                ? ((string?)sub.EnglishCurrencyName ?? "KWD")
                : ((string?)sub.ArabicCurrencyName ?? "د.ك");

            decimal net = (decimal)sub.Net;
            decimal balance = (decimal)sub.CurrentBalance;
            string typeName = (string)sub.SubTypeName;
            string customerName = (string)sub.CustomerName;
            string phone = NormalizePhone((string)sub.CustomerPhone);
            DateTime endDate = (DateTime)sub.EndDate;

            // Fetch Count (wallet credit) from the subscription record for the WhatsApp message.
            // Net = what the customer paid; Count = wallet credit they received; balance = current ledger balance.
            decimal walletCredit = (decimal)sub.Count;

            // Payer info
            string? payerName = (string?)sub.PayerCustomerName;
            string? payerNote = (string?)sub.PayerNote;
            bool hasPayer = !string.IsNullOrWhiteSpace(payerName);

            string message;
            if (lang == "en")
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
                sb.AppendLine("💳 *Wallet Activated Successfully*");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine();
                sb.AppendLine($"👤 *Client:* {customerName}");
                if (hasPayer)
                {
                    sb.AppendLine($"🎁 *Gift from:* {payerName}");
                    if (!string.IsNullOrWhiteSpace(payerNote))
                        sb.AppendLine($"💬 *Message:* {payerNote}");
                }
                sb.AppendLine($"💼 *Wallet Type:* {typeName}");
                sb.AppendLine($"💳 *Amount Paid:* {currency} {net:F2}");
                sb.AppendLine($"🎁 *Wallet Credit:* {currency} {walletCredit:F2}");
                sb.AppendLine($"💵 *Current Balance:* {currency} {balance:F2}");
                sb.AppendLine($"📅 *Valid Until:* {endDate:dd MMM yyyy}");
                sb.AppendLine();
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("Thank you for your purchase! 🙏");
                if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
                message = sb.ToString();
            }
            else
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
                sb.AppendLine("💳 *تم تفعيل المحفظة بنجاح*");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine();
                sb.AppendLine($"👤 *العميل:* {customerName}");
                if (hasPayer)
                {
                    sb.AppendLine($"🎁 *هدية من:* {payerName}");
                    if (!string.IsNullOrWhiteSpace(payerNote))
                        sb.AppendLine($"💬 *رسالة:* {payerNote}");
                }
                sb.AppendLine($"💼 *نوع المحفظة:* {typeName}");
                sb.AppendLine($"💳 *المبلغ المدفوع:* {net:F2} {currency}");
                sb.AppendLine($"🎁 *رصيد المحفظة الممنوح:* {walletCredit:F2} {currency}");
                sb.AppendLine($"💵 *الرصيد الحالي:* {balance:F2} {currency}");
                sb.AppendLine($"📅 *صالحة حتى:* {endDate:dd MMM yyyy}");
                sb.AppendLine();
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("شكراً لكم! 🙏");
                if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
                message = sb.ToString();
            }

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer",
                        _configuration["WhatsApp:ApiKey"] ?? "");

                var payload = new { instance_id = instanceId, message, number = phone };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await client.PostAsync("https://business.enjazatik.com/api/v1/send-message", content);

                return Ok(new ApiResult<object>(true, null, new { Sent = true }));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<object>(true, null, new { Sent = false, Error = ex.Message }));
            }
        }

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var cleaned = new string(phone.Where(char.IsDigit).ToArray());
            if (cleaned.StartsWith("0")) cleaned = "965" + cleaned.Substring(1);
            if (cleaned.Length == 8) cleaned = "965" + cleaned;
            return cleaned;
        }

        #region Private Helpers

        private static decimal CalculateNet(decimal value, decimal discountValue, int? discountType)
        {
            if (discountValue <= 0 || discountType == null) return value;
            if (discountType == 1)
                return value - (value * discountValue / 100);
            if (discountType == 2)
                return Math.Max(0, value - discountValue);
            return value;
        }

        private static string MapRefType(int refType) => refType switch
        {
            0 => "Subscription",
            1 => "Invoice",
            2 => "Adjustment",
            3 => "Return",
            _ => $"Type {refType}"
        };

        private static SubscriptionDto MapToSubscriptionDto(dynamic r, DateTime now)
        {
            var endDate = (DateTime)r.EndDate;
            var isExpired = endDate < now;
            var currentBalance = (decimal)r.CurrentBalance;
            var isActive = !isExpired && currentBalance > 0 && (int)r.IsPaid == 1;

            int? payerCustomerId = null;
            string? payerCustomerName = null;
            string? payerNote = null;
            try { payerCustomerId = (int?)r.PayerCustomerId; } catch { }
            try { payerCustomerName = (string?)r.PayerCustomerName; } catch { }
            try { payerNote = (string?)r.PayerNote; } catch { }

            decimal totalCredit = 0;
            decimal totalPaid = 0;
            string lastAction = "CREATE";
            try { totalCredit = (decimal)(r.TotalCredit ?? 0m); } catch { }
            try { totalPaid = (decimal)(r.TotalPaid ?? 0m); } catch { }
            try { lastAction = (string?)r.LastActionType ?? "CREATE"; } catch { }

            return new SubscriptionDto(
                Id: (int)r.Id,
                Guid: (Guid)r.GUID,
                CustomerId: (int)r.CustomerId,
                CustomerName: (string)(r.CustomerName ?? ""),
                CustomerPhone: (string)(r.CustomerPhone ?? ""),
                SubTypeId: (int)r.SubTypeId,
                SubTypeName: (string)(r.SubTypeName ?? ""),
                Value: (decimal)r.Value,
                DiscountType: (int?)r.DiscountType,
                DiscountValue: (decimal?)r.DiscountValue,
                Net: (decimal)r.Net,
                Count: (decimal?)r.Count,
                StartDate: (DateTime)r.StartDate,
                EndDate: endDate,
                DaysCount: (decimal?)r.DaysCount,
                BranchId: (int)r.BranchId,
                IsPaid: (int)r.IsPaid,
                AddedDate: (DateTime)r.AddedDate,
                CurrentBalance: currentBalance,
                IsExpired: isExpired,
                IsActive: isActive,
                PayerCustomerId: payerCustomerId,
                PayerCustomerName: payerCustomerName,
                PayerNote: payerNote,
                TotalCredit: totalCredit,
                TotalPaid: totalPaid,
                LastActionType: lastAction
            );
        }

        private WalletDetailDto? GetSubscriptionDetailInternal(IDbConnection conn, int subId)
        {
            var sub = conn.Query<dynamic>(@"
            SELECT s.Id, s.GUID, c.CUSTOMER_ID AS CustomerId, c.CUSTOMER_NAME AS CustomerName,
                   c.CUSTOMER_PHONE1 AS CustomerPhone, s.SubTypeId, st.NAME AS SubTypeName,
                   ISNULL(s.Value, 0) AS Value, s.DiscountType, s.DiscountValue,
                   ISNULL(s.Net, 0) AS Net, s.[Count] AS [Count], s.StartDate, s.EndDate, s.DaysCount,
                   s.BranchId, ISNULL(s.IsPaid, 0) AS IsPaid, s.AddedDate,
                   s.PayerCustomerId,
                   pc.CUSTOMER_NAME AS PayerCustomerName,
                   s.PayerNote,
                   ISNULL((SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                           WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                           ORDER BY sh.Id DESC), 0) AS CurrentBalance
            FROM dbo.Subscriptions s
            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
            INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
            LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
            WHERE s.Id = @Id AND s.Deleted = 0",
            new { Id = subId }).FirstOrDefault();

            if (sub == null) return null;
            var now = DateTime.UtcNow;
            var subDto = MapToSubscriptionDto(sub, now);

            var payments = conn.Query<dynamic>(@"
                SELECT
                sp.Id,
                sp.SubscriptionId,
                sp.PAYMENT_TYPE_ID  AS PaymentTypeId,
                ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '') AS PaymentTypeName,
                ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME2, '') AS PaymentTypeNameAr,
                sp.PAYMENT_AMOUNT   AS PaymentAmount,
                sp.PAYMENT_DATE     AS PaymentDate,
                sp.Notes,
                ISNULL(sp.ActionType, 'CREATE') AS ActionType,
                sp.PreviousSubTypeId,
                pst.NAME            AS PreviousSubTypeName
            FROM dbo.SubscriptionPayment sp
            LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt
                ON pt.INVOICE_PAYMENT_TYPE_ID = sp.PAYMENT_TYPE_ID
            LEFT JOIN dbo.SUBS_TYPE pst
                ON pst.ID = sp.PreviousSubTypeId
            WHERE sp.SubscriptionId = @Id AND sp.DELETED = 0
            ORDER BY sp.PAYMENT_DATE",
                new { Id = subId })
                .Select(p => new SubscriptionPaymentDto(
                    Id: (int)p.Id,
                    SubscriptionId: (int)p.SubscriptionId,
                    PaymentTypeId: (int)p.PaymentTypeId,
                    PaymentTypeName: (string)(p.PaymentTypeName ?? ""),
                    PaymentTypeNameAr: (string)(p.PaymentTypeNameAr ?? ""),
                    PaymentAmount: (decimal)p.PaymentAmount,
                    PaymentDate: (DateTime)p.PaymentDate,
                    Notes: (string?)p.Notes,
                    ActionType: (string)p.ActionType,
                    PreviousSubTypeId: (int?)p.PreviousSubTypeId,
                    PreviousSubTypeName: (string?)p.PreviousSubTypeName
                )).ToList();

            var history = conn.Query<dynamic>(@"
                SELECT sh.Id, sh.SubscriptionId, sh.RefType, sh.Amount, sh.Balance, sh.AddedDate, sh.InvoiceId
                FROM dbo.SubscriptionsHistory sh
                WHERE sh.SubscriptionId = @Id AND sh.Deleted = 0
                ORDER BY sh.Id",
                new { Id = subId })
                .Select(h => new SubscriptionHistoryDto(
                    Id: (int)h.Id,
                    SubscriptionId: (int?)h.SubscriptionId,
                    RefType: (int)h.RefType,
                    RefTypeLabel: MapRefType((int)h.RefType),
                    Amount: (decimal)h.Amount,
                    Balance: (decimal)h.Balance,
                    AddedDate: (DateTime)h.AddedDate,
                    InvoiceId: (int?)h.InvoiceId)).ToList();

            return new WalletDetailDto(subDto, payments, history);
        }

        #endregion
    }
}