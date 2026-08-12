// Modules/System/WalletApiController.cs
//
// UPDATE WALLET FLOW
// ─────────────────────────────────────────────────────────────────────────────
// Part 1  Wallet-type CRUD (SUBS_TYPE) + overdraft config (AllowOverdraft / MaxCount)
// Part 2  Adjust (settlement): collect what the customer owes, or refund what is
//         left over, optionally waiving part of it, then close the wallet.
// Part 3  Renew / upgrade balance rules:
//           · an EXPIRED wallet drops leftover credit instead of stacking it
//           · debt always carries forward, expired or not
// Part 4  Wallet block for the invoice + WhatsApp receipt (LoadInvoiceWalletInfo)
//
// LEDGER (dbo.SubscriptionsHistory.RefType)
//   0 = Subscription credit   1 = Invoice spend   2 = Adjustment (legacy)
//   3 = Return                4 = ADJUST reset    5 = EXPIRY reset
//
// SIGN CONVENTION: Balance is what the customer HAS. A negative balance means
// the customer overdrew and owes the salon that amount.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PosDashboard.Web.Modules.System.Models;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
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
using static PosDashboard.Web.Modules.System.Models.WhatsAppProviderDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/wallet")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WalletApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IConfiguration _configuration;

        // The transport is a setting now, not a decision made here.
        private readonly IWhatsAppSender sender;

        public WalletApiController(ISqlConnections sqlConnections, IConfiguration configuration,
            IWhatsAppSender sender)
        {
            this.sqlConnections = sqlConnections;
            _configuration = configuration;
            this.sender = sender;
        }

        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("sub")?.Value;
            return int.TryParse(claim, out var id) ? id : 0;
        }

        // ═════════════════════════════════════════════════════════════════
        // PART 1 — WALLET TYPE CRUD  (dbo.SUBS_TYPE)
        // ═════════════════════════════════════════════════════════════════

        // GET /api/wallet/types
        [HttpGet("types")]
        public ActionResult<ApiResult<List<SubsTypeDto>>> GetTypes()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var rows = conn.Query<dynamic>(@"
                SELECT
                    st.ID                        AS Id,
                    st.NAME                      AS Name,
                    CAST(st.VALUE AS float)      AS Value,
                    st.DAYS_COUNT                AS DaysCount,
                    st.DiscountValue             AS DiscountValue,
                    CAST(st.[Count] AS float)    AS [Count],
                    st.Type                      AS Type,
                    st.DiscountType              AS DiscountType,
                    ISNULL(st.AllowOverdraft, 0) AS AllowOverdraft,
                    st.MaxCount                  AS MaxCount,
                    ISNULL((
                        SELECT COUNT(1) FROM dbo.Subscriptions s
                        WHERE s.SubTypeId = st.ID AND ISNULL(s.Deleted, 0) = 0
                    ), 0)                        AS WalletsInUse
                FROM dbo.SUBS_TYPE st
                ORDER BY st.ID").ToList();

            var list = rows.Select(MapSubsType).ToList();
            return Ok(new ApiResult<List<SubsTypeDto>>(true, null, list));
        }

        // GET /api/wallet/types/{id}
        [HttpGet("types/{id:int}")]
        public ActionResult<ApiResult<SubsTypeDto>> GetType(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var row = QuerySubsType(conn, id);
            if (row == null)
                return Ok(new ApiResult<SubsTypeDto>(false, "Wallet type not found", null));

            return Ok(new ApiResult<SubsTypeDto>(true, null, (SubsTypeDto)MapSubsType(row)));
        }

        // POST /api/wallet/types
        [HttpPost("types")]
        public ActionResult<ApiResult<SubsTypeDto>> CreateType([FromBody] SubsTypeSaveRequest request)
        {
            var error = ValidateType(request);
            if (error != null)
                return Ok(new ApiResult<SubsTypeDto>(false, error, null));

            using var conn = sqlConnections.NewByKey("Default");

            var duplicate = conn.Query<int>(
                "SELECT TOP 1 ID FROM dbo.SUBS_TYPE WHERE LTRIM(RTRIM(NAME)) = @Name",
                new { Name = request.Name.Trim() }).FirstOrDefault();

            if (duplicate != 0)
                return Ok(new ApiResult<SubsTypeDto>(false,
                    $"A wallet type named '{request.Name.Trim()}' already exists", null));

            try
            {
                var newId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.SUBS_TYPE
                        (NAME, VALUE, DAYS_COUNT, DiscountValue, [Count], Type, DiscountType,
                         AllowOverdraft, MaxCount)
                    OUTPUT INSERTED.ID
                    VALUES
                        (@Name, @Value, @DaysCount, @DiscountValue, @Count, @Type, @DiscountType,
                         @AllowOverdraft, @MaxCount)",
                    new
                    {
                        Name = request.Name.Trim(),
                        Value = request.Value ?? 0d,
                        DaysCount = request.DaysCount ?? 365,
                        DiscountValue = request.DiscountValue ?? 0m,
                        Count = request.Count ?? 0d,
                        Type = request.Type,
                        DiscountType = request.DiscountType,
                        AllowOverdraft = request.AllowOverdraft,
                        MaxCount = request.AllowOverdraft ? request.MaxCount : (int?)null
                    }).FirstOrDefault();

                var row = QuerySubsType(conn, newId);
                return Ok(new ApiResult<SubsTypeDto>(true, null, (SubsTypeDto)MapSubsType(row!)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SubsTypeDto>(false, $"Failed to create wallet type: {ex.Message}", null));
            }
        }

        // POST /api/wallet/types/update/{id}
        [HttpPost("types/update/{id:int}")]
        public ActionResult<ApiResult<SubsTypeDto>> UpdateType(int id, [FromBody] SubsTypeSaveRequest request)
        {
            var error = ValidateType(request);
            if (error != null)
                return Ok(new ApiResult<SubsTypeDto>(false, error, null));

            using var conn = sqlConnections.NewByKey("Default");

            var existing = QuerySubsType(conn, id);
            if (existing == null)
                return Ok(new ApiResult<SubsTypeDto>(false, "Wallet type not found", null));

            var duplicate = conn.Query<int>(
                "SELECT TOP 1 ID FROM dbo.SUBS_TYPE WHERE LTRIM(RTRIM(NAME)) = @Name AND ID <> @Id",
                new { Name = request.Name.Trim(), Id = id }).FirstOrDefault();

            if (duplicate != 0)
                return Ok(new ApiResult<SubsTypeDto>(false,
                    $"A wallet type named '{request.Name.Trim()}' already exists", null));

            try
            {
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.SUBS_TYPE SET
                        NAME           = @Name,
                        VALUE          = @Value,
                        DAYS_COUNT     = @DaysCount,
                        DiscountValue  = @DiscountValue,
                        [Count]        = @Count,
                        Type           = @Type,
                        DiscountType   = @DiscountType,
                        AllowOverdraft = @AllowOverdraft,
                        MaxCount       = @MaxCount
                    WHERE ID = @Id",
                    new
                    {
                        Id = id,
                        Name = request.Name.Trim(),
                        Value = request.Value ?? 0d,
                        DaysCount = request.DaysCount ?? 365,
                        DiscountValue = request.DiscountValue ?? 0m,
                        Count = request.Count ?? 0d,
                        Type = request.Type,
                        DiscountType = request.DiscountType,
                        AllowOverdraft = request.AllowOverdraft,
                        MaxCount = request.AllowOverdraft ? request.MaxCount : (int?)null
                    });

                // Wallets sold BEFORE this edit keep the rules they were sold under.
                // Only wallets that are still open AND still on this type inherit
                // the change, and only where the rules actually differ.
                SqlMapper.Execute(conn, @"
                    UPDATE s SET
                        s.AllowOverdraft = @AllowOverdraft,
                        s.MaxCount       = @MaxCount
                    FROM dbo.Subscriptions s
                    WHERE s.SubTypeId = @Id
                      AND ISNULL(s.Deleted, 0)  = 0
                      AND ISNULL(s.IsClosed, 0) = 0",
                    new
                    {
                        Id = id,
                        AllowOverdraft = request.AllowOverdraft,
                        MaxCount = request.AllowOverdraft ? (decimal?)request.MaxCount : null
                    });

                var row = QuerySubsType(conn, id);
                return Ok(new ApiResult<SubsTypeDto>(true, null, (SubsTypeDto)MapSubsType(row!)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SubsTypeDto>(false, $"Failed to update wallet type: {ex.Message}", null));
            }
        }

        // POST /api/wallet/types/delete/{id}
        [HttpPost("types/delete/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteType(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var existing = QuerySubsType(conn, id);
            if (existing == null)
                return Ok(new ApiResult<bool>(false, "Wallet type not found", false));

            int inUse = Convert.ToInt32(existing.WalletsInUse);
            if (inUse > 0)
                return Ok(new ApiResult<bool>(false,
                    $"This wallet type is used by {inUse} wallet(s) and cannot be deleted.", false));

            try
            {
                SqlMapper.Execute(conn, "DELETE FROM dbo.SUBS_TYPE WHERE ID = @Id", new { Id = id });
                return Ok(new ApiResult<bool>(true, null, true));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<bool>(false, $"Failed to delete wallet type: {ex.Message}", false));
            }
        }

        private static string? ValidateType(SubsTypeSaveRequest? r)
        {
            if (r == null) return "Request body is required";
            if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required";
            if (r.Name.Trim().Length > 50) return "Name must be 50 characters or fewer";
            if (r.Value is < 0) return "Value cannot be negative";
            if (r.Count is < 0) return "Count cannot be negative";
            if (r.DaysCount is <= 0) return "Days count must be greater than 0";
            if (r.DiscountValue is < 0) return "Discount value cannot be negative";
            if (r.DiscountType == 1 && r.DiscountValue > 100)
                return "A percentage discount cannot exceed 100%";

            if (r.AllowOverdraft)
            {
                if (r.MaxCount == null)
                    return "Max count is required when overdraft is enabled";
                if (r.MaxCount <= (r.Count ?? 0))
                    return "Max count must be greater than count";
            }
            return null;
        }

        private static dynamic? QuerySubsType(IDbConnection conn, int id) =>
            conn.Query<dynamic>(@"
                SELECT
                    st.ID                        AS Id,
                    st.NAME                      AS Name,
                    CAST(st.VALUE AS float)      AS Value,
                    st.DAYS_COUNT                AS DaysCount,
                    st.DiscountValue             AS DiscountValue,
                    CAST(st.[Count] AS float)    AS [Count],
                    st.Type                      AS Type,
                    st.DiscountType              AS DiscountType,
                    ISNULL(st.AllowOverdraft, 0) AS AllowOverdraft,
                    st.MaxCount                  AS MaxCount,
                    ISNULL((
                        SELECT COUNT(1) FROM dbo.Subscriptions s
                        WHERE s.SubTypeId = st.ID AND ISNULL(s.Deleted, 0) = 0
                    ), 0)                        AS WalletsInUse
                FROM dbo.SUBS_TYPE st
                WHERE st.ID = @Id", new { Id = id }).FirstOrDefault();

        private static SubsTypeDto MapSubsType(dynamic r)
        {
            bool allowOverdraft = Convert.ToInt32(r.AllowOverdraft ?? 0) == 1;
            int? maxCount = r.MaxCount == null ? (int?)null : Convert.ToInt32(r.MaxCount);
            decimal count = r.Count == null ? 0m : Convert.ToDecimal(r.Count);
            decimal value = r.Value == null ? 0m : Convert.ToDecimal(r.Value);
            decimal discountValue = r.DiscountValue == null ? 0m : Convert.ToDecimal(r.DiscountValue);
            int? discountType = r.DiscountType == null ? (int?)null : Convert.ToInt32(r.DiscountType);

            return new SubsTypeDto(
                Id: Convert.ToInt32(r.Id),
                Name: (string)(r.Name ?? ""),
                Value: r.Value == null ? (double?)null : Convert.ToDouble(r.Value),
                DaysCount: r.DaysCount == null ? (int?)null : Convert.ToInt32(r.DaysCount),
                DiscountValue: r.DiscountValue == null ? (decimal?)null : discountValue,
                Count: r.Count == null ? (double?)null : Convert.ToDouble(r.Count),
                Type: r.Type == null ? (int?)null : Convert.ToInt32(r.Type),
                DiscountType: discountType,
                AllowOverdraft: allowOverdraft,
                MaxCount: maxCount,
                OverdraftLimit: CalcOverdraftLimit(allowOverdraft, count, maxCount),
                NetValue: CalculateNet(value, discountValue, discountType),
                WalletsInUse: Convert.ToInt32(r.WalletsInUse ?? 0)
            );
        }

        /// <summary>
        /// Count = 30, MaxCount = 40 → the customer may draw 10 past the balance,
        /// i.e. the balance floor is -10. Overdraft off (or a MaxCount that is not
        /// actually bigger than Count) means a floor of 0.
        /// </summary>
        private static decimal CalcOverdraftLimit(bool allowOverdraft, decimal count, decimal? maxCount)
        {
            if (!allowOverdraft || maxCount == null) return 0m;
            var limit = maxCount.Value - count;
            return limit > 0 ? limit : 0m;
        }

        // ═════════════════════════════════════════════════════════════════
        // WALLET LIST / DETAIL
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Column list shared by the list and detail queries. Kept in one place so
        /// the two can never drift apart — a bug this file has had before.
        /// </summary>
        private const string SubscriptionSelect = @"
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
            ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) AS AllowOverdraft,
            ISNULL(s.MaxCount, st.MaxCount)                        AS MaxCount,
            ISNULL(s.IsClosed, 0) AS IsClosed,
            s.ClosedAt,
            s.ClosedReason,
            ISNULL((
                SELECT TOP 1 sh.Balance
                FROM dbo.SubscriptionsHistory sh
                WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                ORDER BY sh.Id DESC
            ), 0) AS CurrentBalance,
            ISNULL((
                SELECT SUM(sh.Amount)
                FROM dbo.SubscriptionsHistory sh
                WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0 AND sh.Amount > 0
            ), 0) AS TotalCredit,
            ISNULL((
                SELECT SUM(sp.PAYMENT_AMOUNT)
                FROM dbo.SubscriptionPayment sp
                WHERE sp.SubscriptionId = s.Id AND sp.DELETED = 0
            ), 0) AS TotalPaid,
            CASE
                WHEN EXISTS (
                    SELECT 1 FROM dbo.WalletAdjustments wa
                    WHERE wa.SubscriptionId = s.Id AND wa.Deleted = 0
                      AND wa.AddedDate >= ISNULL((
                            SELECT MAX(sp2.PAYMENT_DATE) FROM dbo.SubscriptionPayment sp2
                            WHERE sp2.SubscriptionId = s.Id AND sp2.DELETED = 0), '1900-01-01')
                ) THEN 'ADJUST'
                ELSE ISNULL((
                    SELECT TOP 1 sp.ActionType
                    FROM dbo.SubscriptionPayment sp
                    WHERE sp.SubscriptionId = s.Id AND sp.DELETED = 0
                    ORDER BY sp.Id DESC
                ), 'CREATE')
            END AS LastActionType";

        // GET /api/wallet/subscriptions
        [HttpGet("subscriptions")]
        public ActionResult<ApiResult<List<SubscriptionDto>>> GetSubscriptions(
            [FromQuery] int? branchId = null,
            [FromQuery] int? customerId = null,
            [FromQuery] int? month = null,
            [FromQuery] int? year = null,
            // true = only overdraft-enabled types, false = only plain types, null = both
            [FromQuery] bool? hasMaxCount = null,
            // true = only closed wallets, false = only open, null = both
            [FromQuery] bool? closed = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var offset = (page - 1) * pageSize;

            var list = conn.Query<dynamic>($@"
            SELECT {SubscriptionSelect}
            FROM dbo.Subscriptions s
            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
            INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
            LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
            WHERE ISNULL(s.Deleted, 0) = 0
              AND (@BranchId IS NULL OR s.BranchId = @BranchId)
              AND (@CustomerId IS NULL OR c.CUSTOMER_ID = @CustomerId)
              AND (@Month IS NULL OR MONTH(s.AddedDate) = @Month)
              AND (@Year IS NULL OR YEAR(s.AddedDate) = @Year)
              AND (@HasMaxCount IS NULL
                   OR (@HasMaxCount = 1 AND ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) = 1
                       AND ISNULL(s.MaxCount, st.MaxCount) IS NOT NULL)
                   OR (@HasMaxCount = 0 AND (ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) = 0
                       OR ISNULL(s.MaxCount, st.MaxCount) IS NULL)))
              AND (@Closed IS NULL OR ISNULL(s.IsClosed, 0) = @Closed)
            ORDER BY s.AddedDate DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
            new
            {
                BranchId = branchId,
                CustomerId = customerId,
                Month = month,
                Year = year,
                HasMaxCount = hasMaxCount.HasValue ? (hasMaxCount.Value ? 1 : 0) : (int?)null,
                Closed = closed.HasValue ? (closed.Value ? 1 : 0) : (int?)null,
                Offset = offset,
                PageSize = pageSize
            })
            .ToList();

            var now = DateTime.UtcNow;
            var result = list.Select(r => (SubscriptionDto)MapToSubscriptionDto(r, now)).ToList();

            return Ok(new ApiResult<List<SubscriptionDto>>(true, null, result));
        }

        // GET /api/wallet/subscriptions/{id}
        [HttpGet("subscriptions/{id:int}")]
        public ActionResult<ApiResult<WalletDetailDto>> GetSubscriptionDetail(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var detail = GetSubscriptionDetailInternal(conn, id);

            if (detail == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Subscription not found", null));

            return Ok(new ApiResult<WalletDetailDto>(true, null, detail));
        }

        // GET /api/wallet/customer-summary?customerId=1
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

            var empty = new CustomerWalletSummaryDto(
                false, 0, null, null, null, false, 0, 0, 0, false, 0, null);

            Guid refGuide = (Guid)customer.RefGuide;
            var now = DateTime.UtcNow;

            var activeSub = conn.Query<dynamic>(@"
                SELECT TOP 1
                    s.Id,
                    st.NAME AS SubTypeName,
                    s.EndDate,
                    s.[Count] AS [Count],
                    ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) AS AllowOverdraft,
                    ISNULL(s.MaxCount, st.MaxCount) AS MaxCount,
                    ISNULL(s.IsClosed, 0) AS IsClosed,
                    ISNULL((
                        SELECT TOP 1 sh.Balance
                        FROM dbo.SubscriptionsHistory sh
                        WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                        ORDER BY sh.Id DESC
                    ), 0) AS CurrentBalance
                FROM dbo.Subscriptions s
                INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                WHERE s.CustomerRef = @Ref
                  AND ISNULL(s.Deleted, 0) = 0
                  AND ISNULL(s.IsPaid, 0) = 1
                  AND ISNULL(s.IsClosed, 0) = 0
                  AND s.EndDate >= @Now
                ORDER BY s.EndDate DESC",
                new { Ref = refGuide, Now = now }).FirstOrDefault();

            if (activeSub == null)
                return Ok(new ApiResult<CustomerWalletSummaryDto>(true, null, empty));

            decimal balance = Convert.ToDecimal(activeSub.CurrentBalance);
            decimal count = activeSub.Count == null ? 0m : Convert.ToDecimal(activeSub.Count);
            bool allowOverdraft = Convert.ToInt32(activeSub.AllowOverdraft ?? 0) == 1;
            decimal? maxCount = activeSub.MaxCount == null ? (decimal?)null : Convert.ToDecimal(activeSub.MaxCount);
            decimal overdraftLimit = CalcOverdraftLimit(allowOverdraft, count, maxCount);

            // Spendable = what's left plus whatever overdraft head-room remains.
            // Without overdraft this collapses to max(balance, 0), which is the
            // old behaviour, so existing callers keep working unchanged.
            decimal available = Math.Max(0m, balance + overdraftLimit);

            if (available <= 0)
                return Ok(new ApiResult<CustomerWalletSummaryDto>(true, null, empty));

            var summary = new CustomerWalletSummaryDto(
                HasActiveWallet: true,
                CurrentBalance: balance,
                SubscriptionId: Convert.ToInt32(activeSub.Id),
                SubTypeName: (string?)activeSub.SubTypeName,
                EndDate: (DateTime?)activeSub.EndDate,
                AllowOverdraft: allowOverdraft,
                OverdraftLimit: overdraftLimit,
                AvailableToSpend: available,
                AmountOwed: balance < 0 ? -balance : 0m,
                IsClosed: false,
                Count: count,
                MaxCount: allowOverdraft ? maxCount : null
            );

            return Ok(new ApiResult<CustomerWalletSummaryDto>(true, null, summary));
        }

        // ═════════════════════════════════════════════════════════════════
        // CREATE
        // ═════════════════════════════════════════════════════════════════

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

                var customer = conn.Query<dynamic>(
                    @"SELECT CUSTOMER_ID, CUSTOMER_REF_GUIDE AS RefGuide, CUSTOMER_NAME, CUSTOMER_PHONE1
                      FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();

                if (customer == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Customer not found", null));

                // One wallet per customer — but a CLOSED wallet no longer counts.
                // Once a wallet has been settled and shut, the customer is free to
                // buy a fresh one instead of being told to renew a dead record.
                var existingWallet = conn.Query<dynamic>(@"
                    SELECT Id, ISNULL(IsClosed, 0) AS IsClosed
                    FROM dbo.Subscriptions
                    WHERE CustomerRef = @Ref AND ISNULL(Deleted, 0) = 0 AND ISNULL(IsClosed, 0) = 0",
                    new { Ref = (Guid)customer.RefGuide }).FirstOrDefault();

                if (existingWallet != null)
                {
                    return Ok(new ApiResult<WalletDetailDto>(false,
                        $"This customer already has an open wallet (#{(int)existingWallet.Id}). Use Renew or Upgrade instead.",
                        null));
                }

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
                    "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = request.PaymentTypeId }).FirstOrDefault();

                if (paymentType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Payment type not found", null));

                var subsType = conn.Query<dynamic>(
                    @"SELECT ID, NAME, VALUE, DAYS_COUNT, DiscountValue, [Count], Type, DiscountType,
                             ISNULL(AllowOverdraft, 0) AS AllowOverdraft, MaxCount
                      FROM dbo.SUBS_TYPE WHERE ID = @Id",
                    new { Id = request.SubTypeId }).FirstOrDefault();

                if (subsType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Subscription type not found", null));

                decimal rawValue = request.CustomValue ?? Convert.ToDecimal(subsType.VALUE ?? 0);
                decimal discountValue = subsType.DiscountValue != null ? Convert.ToDecimal(subsType.DiscountValue) : 0m;
                int? discountType = subsType.DiscountType != null ? Convert.ToInt32(subsType.DiscountType) : null;
                decimal net = request.CustomNet ?? CalculateNet(rawValue, discountValue, discountType);
                int daysCount = subsType.DAYS_COUNT != null ? Convert.ToInt32(subsType.DAYS_COUNT) : 365;
                decimal count = subsType.Count != null ? Convert.ToDecimal(subsType.Count) : 0m;

                bool allowOverdraft = Convert.ToInt32(subsType.AllowOverdraft ?? 0) == 1;
                decimal? maxCount = subsType.MaxCount == null ? (decimal?)null : Convert.ToDecimal(subsType.MaxCount);

                DateTime startDate = request.StartDate.Date;
                DateTime endDate = startDate.AddDays(daysCount);

                Guid refGuide = (Guid)customer.RefGuide;
                Guid subGuid = Guid.NewGuid();
                var now = DateTime.UtcNow;

                string? payerNote = string.IsNullOrWhiteSpace(request.PayerNote) ? null : request.PayerNote.Trim();

                var subId = SqlMapper.Query<int>(conn, @"
            INSERT INTO dbo.Subscriptions (
                GUID, CustomerRef, SubTypeId, Value, DiscountType, DiscountValue,
                Net, [Count], StartDate, EndDate, DaysCount,
                BranchId, AddedBy, AddedDate, Deleted, IsPaid,
                SHIFT_ID, ActiveOnline, Source,
                PayerCustomerId, PayerNote,
                AllowOverdraft, MaxCount, IsClosed
            )
            OUTPUT INSERTED.Id
            VALUES (
                @Guid, @CustomerRef, @SubTypeId, @Value, @DiscountType, @DiscountValue,
                @Net, @Count, @StartDate, @EndDate, @DaysCount,
                @BranchId, @AddedBy, @AddedDate, 0, 1,
                0, 0, 0,
                @PayerCustomerId, @PayerNote,
                @AllowOverdraft, @MaxCount, 0
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
                        PayerNote = payerNote,
                        AllowOverdraft = allowOverdraft,
                        MaxCount = maxCount
                    }).FirstOrDefault();

                InsertPayment(conn, subId, request.PaymentTypeId, net, now, request.Notes, userId, "CREATE", null);
                InsertLedger(conn, refGuide, subId, RefTypeSubscription, count, count, userId, now);

                var detailResult = GetSubscriptionDetailInternal(conn, subId);
                return Ok(new ApiResult<WalletDetailDto>(true, null, detailResult));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletDetailDto>(false, $"Failed to create subscription: {ex.Message}", null));
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PART 3 — RENEW / UPGRADE with the new balance rules
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /api/wallet/subscriptions/{id}/renew-preview?subTypeId=
        /// Dry-run of the balance maths so the dialog can show it before charging.
        /// Omit subTypeId for a renew; pass the target type for an upgrade.
        /// </summary>
        [HttpGet("subscriptions/{id:int}/renew-preview")]
        public ActionResult<ApiResult<RenewPreviewDto>> RenewPreview(
            int id, [FromQuery] int? subTypeId = null, [FromQuery] DateTime? startDate = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var sub = LoadWalletRow(conn, id);
            if (sub == null)
                return Ok(new ApiResult<RenewPreviewDto>(false, "Wallet not found", null));

            int targetTypeId = subTypeId ?? (int)sub.SubTypeId;
            var type = conn.Query<dynamic>(
                "SELECT ID, VALUE, DAYS_COUNT, DiscountValue, [Count], DiscountType FROM dbo.SUBS_TYPE WHERE ID = @Id",
                new { Id = targetTypeId }).FirstOrDefault();

            if (type == null)
                return Ok(new ApiResult<RenewPreviewDto>(false, "Wallet type not found", null));

            decimal creditGranted = type.Count != null ? Convert.ToDecimal(type.Count) : 0m;
            int daysCount = type.DAYS_COUNT != null ? Convert.ToInt32(type.DAYS_COUNT) : 365;
            decimal rawValue = Convert.ToDecimal(type.VALUE ?? 0);
            decimal discountValue = type.DiscountValue != null ? Convert.ToDecimal(type.DiscountValue) : 0m;
            int? discountType = type.DiscountType != null ? Convert.ToInt32(type.DiscountType) : null;

            var now = DateTime.UtcNow;
            var start = (startDate ?? now).Date;
            bool previewIsClosed = Convert.ToInt32(sub.IsClosed ?? 0) == 1;
            var carry = ComputeCarry(conn, id, (DateTime)sub.EndDate, previewIsClosed, now);

            DateTime baseDate = ((DateTime)sub.EndDate) > start ? (DateTime)sub.EndDate : start;
            DateTime newEndDate = baseDate.AddDays(daysCount);

            var preview = new RenewPreviewDto(
                CurrentBalance: carry.PreviousBalance,
                IsExpired: carry.IsExpired,
                IsClosed: carry.IsClosed,
                CarriedBalance: carry.CarriedBalance,
                DroppedCredit: carry.DroppedCredit,
                CarriedDebt: carry.CarriedDebt,
                CreditGranted: creditGranted,
                ResultingBalance: carry.CarriedBalance + creditGranted,
                Net: CalculateNet(rawValue, discountValue, discountType),
                NewEndDate: newEndDate
            );

            return Ok(new ApiResult<RenewPreviewDto>(true, null, preview));
        }

        // POST /api/wallet/subscriptions/{id}/renew
        [HttpPost("subscriptions/{id:int}/renew")]
        public ActionResult<ApiResult<WalletDetailDto>> RenewSubscription(
            int id, [FromBody] RenewSubscriptionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Request body is required", null));

            return ApplyRenewOrUpgrade(id, null, request.PaymentTypeId, request.StartDate,
                request.CustomValue, request.CustomNet, request.Notes,
                request.PayerCustomerId, request.PayerNote);
        }

        // POST /api/wallet/subscriptions/{id}/upgrade
        [HttpPost("subscriptions/{id:int}/upgrade")]
        public ActionResult<ApiResult<WalletDetailDto>> UpgradeSubscription(
            int id, [FromBody] UpgradeSubscriptionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletDetailDto>(false, "Request body is required", null));

            return ApplyRenewOrUpgrade(id, request.NewSubTypeId, request.PaymentTypeId, request.StartDate,
                request.CustomValue, request.CustomNet, request.Notes,
                request.PayerCustomerId, request.PayerNote);
        }

        /// <summary>
        /// Renew and upgrade differ only in whether the wallet type changes, so
        /// they share one implementation. Splitting them meant the Part 3 balance
        /// rules had to be written — and kept correct — twice.
        /// </summary>
        private ActionResult<ApiResult<WalletDetailDto>> ApplyRenewOrUpgrade(
            int id, int? newSubTypeId, int paymentTypeId, DateTime? startDateReq,
            decimal? customValue, decimal? customNet, string? notes,
            int? payerCustomerId, string? payerNote)
        {
            using var conn = sqlConnections.NewByKey("Default");

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Could not resolve current user", null));

                var sub = LoadWalletRow(conn, id);
                if (sub == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Wallet not found", null));

                int previousSubTypeId = (int)sub.SubTypeId;
                bool isUpgrade = newSubTypeId.HasValue;

                if (isUpgrade && previousSubTypeId == newSubTypeId!.Value)
                    return Ok(new ApiResult<WalletDetailDto>(false,
                        "New wallet type must be different from the current type. Use Renew instead.", null));

                int targetTypeId = newSubTypeId ?? previousSubTypeId;

                var type = conn.Query<dynamic>(
                    @"SELECT ID, VALUE, DAYS_COUNT, DiscountValue, [Count], DiscountType,
                             ISNULL(AllowOverdraft, 0) AS AllowOverdraft, MaxCount
                      FROM dbo.SUBS_TYPE WHERE ID = @Id",
                    new { Id = targetTypeId }).FirstOrDefault();

                if (type == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Wallet type not found", null));

                var paymentType = conn.Query<dynamic>(
                    "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                    new { Id = paymentTypeId }).FirstOrDefault();
                if (paymentType == null)
                    return Ok(new ApiResult<WalletDetailDto>(false, "Payment type not found", null));

                if (payerCustomerId.HasValue)
                {
                    var payer = conn.Query<dynamic>(
                        "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                        new { Id = payerCustomerId.Value }).FirstOrDefault();
                    if (payer == null)
                        return Ok(new ApiResult<WalletDetailDto>(false, "Payer customer not found", null));
                }

                decimal rawValue = customValue ?? Convert.ToDecimal(type.VALUE ?? 0);
                decimal discountValue = type.DiscountValue != null ? Convert.ToDecimal(type.DiscountValue) : 0m;
                int? discountType = type.DiscountType != null ? Convert.ToInt32(type.DiscountType) : null;
                decimal net = customNet ?? CalculateNet(rawValue, discountValue, discountType);
                decimal creditGranted = type.Count != null ? Convert.ToDecimal(type.Count) : 0m;
                int daysCount = type.DAYS_COUNT != null ? Convert.ToInt32(type.DAYS_COUNT) : 365;

                bool allowOverdraft = Convert.ToInt32(type.AllowOverdraft ?? 0) == 1;
                decimal? maxCount = type.MaxCount == null ? (decimal?)null : Convert.ToDecimal(type.MaxCount);

                var now = DateTime.UtcNow;
                DateTime startDate = (startDateReq ?? now).Date;
                DateTime currentEnd = (DateTime)sub.EndDate;
                DateTime baseDate = currentEnd > startDate ? currentEnd : startDate;
                DateTime newEndDate = baseDate.AddDays(daysCount);

                bool wasClosed = Convert.ToInt32(sub.IsClosed ?? 0) == 1;
                Guid customerRef = (Guid)sub.CustomerRef;

                // ── PART 3: the balance rules ──────────────────────────────
                //   · expired wallet  → leftover CREDIT is dropped, not stacked
                //   · debt            → always carried forward and netted off the
                //                       new credit, expired or not
                //   · closed wallet   → already settled; nothing carries over
                var carry = ComputeCarry(conn, id, currentEnd, wasClosed, now);

                // 1) Extend / retype the wallet. Renewing also reopens it: buying
                //    more credit is an explicit statement that the wallet is live
                //    again, so leaving IsClosed set would strand the balance.
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.Subscriptions SET
                        SubTypeId       = @SubTypeId,
                        EndDate         = @EndDate,
                        DaysCount       = @DaysCount,
                        IsPaid          = 1,
                        AllowOverdraft  = @AllowOverdraft,
                        MaxCount        = @MaxCount,
                        [Count]         = @Count,
                        IsClosed        = 0,
                        ClosedAt        = NULL,
                        ClosedBy        = NULL,
                        ClosedReason    = NULL,
                        PayerCustomerId = COALESCE(@PayerCustomerId, PayerCustomerId),
                        PayerNote       = COALESCE(@PayerNote, PayerNote)
                    WHERE Id = @Id",
                    new
                    {
                        Id = id,
                        SubTypeId = targetTypeId,
                        EndDate = newEndDate,
                        DaysCount = (decimal)daysCount,
                        AllowOverdraft = allowOverdraft,
                        MaxCount = maxCount,
                        Count = creditGranted,
                        PayerCustomerId = payerCustomerId,
                        PayerNote = string.IsNullOrWhiteSpace(payerNote) ? null : payerNote.Trim()
                    });

                // 2) Payment row (audit trail keeps the previous type on upgrades)
                InsertPayment(conn, id, paymentTypeId, net, now, notes, userId,
                    isUpgrade ? "UPGRADE" : "RENEW", isUpgrade ? previousSubTypeId : (int?)null);

                // 3) Ledger. When expired credit is dropped it gets its own row so
                //    the customer can see exactly what was written off and when —
                //    folding it into the credit row would hide it.
                if (carry.DroppedCredit > 0)
                    InsertLedger(conn, customerRef, id, RefTypeExpiryReset,
                        -carry.DroppedCredit, 0m, userId, now);

                decimal resultingBalance = carry.CarriedBalance + creditGranted;
                InsertLedger(conn, customerRef, id, RefTypeSubscription,
                    creditGranted, resultingBalance, userId, now);

                var detail = GetSubscriptionDetailInternal(conn, id);
                return Ok(new ApiResult<WalletDetailDto>(true, null, detail));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletDetailDto>(false,
                    $"Failed to {(newSubTypeId.HasValue ? "upgrade" : "renew")} wallet: {ex.Message}", null));
            }
        }

        private record CarryResult(
            decimal PreviousBalance, bool IsExpired, bool IsClosed,
            decimal CarriedBalance, decimal DroppedCredit, decimal CarriedDebt);

        /// <summary>
        /// Part 3 in one place. Expired wallets forfeit leftover credit but never
        /// forfeit debt — otherwise letting a wallet lapse would be a free way to
        /// walk away from an overdraft.
        /// </summary>
        private static CarryResult ComputeCarry(IDbConnection conn, int subId, DateTime endDate, bool isClosed, DateTime now)
        {
            decimal prev = conn.Query<decimal?>(@"
                SELECT TOP 1 Balance FROM dbo.SubscriptionsHistory
                WHERE SubscriptionId = @Id AND Deleted = 0
                ORDER BY Id DESC", new { Id = subId }).FirstOrDefault() ?? 0m;

            bool isExpired = endDate < now;

            decimal carried;
            decimal dropped = 0m;

            if (isClosed)
            {
                // Settled already — the ledger was zeroed by the Adjust flow.
                carried = 0m;
                if (prev > 0) dropped = prev;
            }
            else if (isExpired)
            {
                carried = prev < 0 ? prev : 0m;   // keep debt, drop credit
                if (prev > 0) dropped = prev;
            }
            else
            {
                carried = prev;                    // normal top-up: stack it
            }

            return new CarryResult(
                PreviousBalance: prev,
                IsExpired: isExpired,
                IsClosed: isClosed,
                CarriedBalance: carried,
                DroppedCredit: dropped,
                CarriedDebt: carried < 0 ? -carried : 0m);
        }

        // ═════════════════════════════════════════════════════════════════
        // PART 2 — ADJUST (settlement)
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /api/wallet/subscriptions/{id}/adjust-preview
        /// Tells the dialog which way the money flows before it renders a form.
        /// </summary>
        [HttpGet("subscriptions/{id:int}/adjust-preview")]
        public ActionResult<ApiResult<WalletAdjustPreviewDto>> AdjustPreview(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var row = conn.Query<dynamic>($@"
                SELECT {SubscriptionSelect}
                FROM dbo.Subscriptions s
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
                WHERE s.Id = @Id AND ISNULL(s.Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault();

            if (row == null)
                return Ok(new ApiResult<WalletAdjustPreviewDto>(false, "Wallet not found", null));

            SubscriptionDto dto = (SubscriptionDto)MapToSubscriptionDto(row, DateTime.UtcNow);

            string direction = dto.CurrentBalance < 0 ? "COLLECT"
                             : dto.CurrentBalance > 0 ? "REFUND"
                             : "NONE";

            var preview = new WalletAdjustPreviewDto(
                SubscriptionId: dto.Id,
                CustomerId: dto.CustomerId,
                CustomerName: dto.CustomerName,
                SubTypeName: dto.SubTypeName,
                CurrentBalance: dto.CurrentBalance,
                Direction: direction,
                DueAmount: Math.Abs(dto.CurrentBalance),
                Count: dto.Count ?? 0m,
                MaxCount: dto.MaxCount,
                OverdraftLimit: dto.OverdraftLimit,
                IsExpired: dto.IsExpired,
                IsClosed: dto.IsClosed,
                EndDate: dto.EndDate
            );

            return Ok(new ApiResult<WalletAdjustPreviewDto>(true, null, preview));
        }

        /// <summary>
        /// POST /api/wallet/subscriptions/{id}/adjust
        ///
        /// COLLECT — the customer overdrew. They pay some or all of it; the rest
        ///           can be waived. The balance resets to zero either way.
        /// REFUND  — unused credit is handed back as cash or a payment link
        ///           (never as wallet credit: the wallet is being closed). The
        ///           amount can be less than owed (waiver) or more (goodwill).
        ///
        /// Both directions zero the ledger and, by default, close the wallet.
        /// </summary>
        [HttpPost("subscriptions/{id:int}/adjust")]
        public ActionResult<ApiResult<WalletAdjustResponse>> AdjustWallet(
            int id, [FromBody] WalletAdjustRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WalletAdjustResponse>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            using var uow = new UnitOfWork(conn);

            try
            {
                int userId = GetCurrentUserId();
                if (userId == 0)
                    return Ok(new ApiResult<WalletAdjustResponse>(false, "Could not resolve current user", null));

                var sub = conn.Query<dynamic>(@"
                    SELECT s.Id, s.CustomerRef, s.BranchId, s.EndDate,
                           ISNULL(s.Deleted, 0)  AS Deleted,
                           ISNULL(s.IsClosed, 0) AS IsClosed,
                           c.CUSTOMER_ID         AS CustomerId,
                           ISNULL((
                               SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                               WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                               ORDER BY sh.Id DESC), 0) AS CurrentBalance
                    FROM dbo.Subscriptions s
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                    WHERE s.Id = @Id",
                    new { Id = id }).FirstOrDefault();

                if (sub == null || Convert.ToInt32(sub.Deleted) == 1)
                    return Ok(new ApiResult<WalletAdjustResponse>(false, "Wallet not found", null));

                if (Convert.ToInt32(sub.IsClosed) == 1)
                    return Ok(new ApiResult<WalletAdjustResponse>(false,
                        "This wallet is already closed. Renew or upgrade it to reopen.", null));

                decimal balanceBefore = Convert.ToDecimal(sub.CurrentBalance);
                if (balanceBefore == 0)
                    return Ok(new ApiResult<WalletAdjustResponse>(false,
                        "Nothing to settle — the wallet balance is already zero.", null));

                string adjustType = (request.AdjustType ?? "").Trim().ToUpperInvariant();
                string expected = balanceBefore < 0 ? "COLLECT" : "REFUND";

                if (adjustType != expected)
                    return Ok(new ApiResult<WalletAdjustResponse>(false,
                        $"This wallet requires a '{expected}' adjustment, not '{adjustType}'.", null));

                decimal due = Math.Abs(balanceBefore);
                decimal settled = Math.Round(request.SettledAmount, 3);
                decimal waived;

                if (settled < 0)
                    return Ok(new ApiResult<WalletAdjustResponse>(false, "Amount cannot be negative", null));

                int? paymentTypeId = null;
                string? refundMethod = null;
                string? refundLink = null;

                if (adjustType == "COLLECT")
                {
                    if (settled > due)
                        return Ok(new ApiResult<WalletAdjustResponse>(false,
                            $"Collected amount cannot exceed the {due:F3} owed.", null));

                    // Whatever is not collected is written off, by definition. Deriving
                    // it rather than trusting the client keeps due = settled + waived
                    // true in the ledger no matter what the UI sends.
                    waived = due - settled;

                    if (settled > 0)
                    {
                        if (request.PaymentTypeId == null)
                            return Ok(new ApiResult<WalletAdjustResponse>(false,
                                "PaymentTypeId is required when collecting an amount", null));

                        var pt = conn.Query<dynamic>(
                            "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                            new { Id = request.PaymentTypeId.Value }).FirstOrDefault();
                        if (pt == null)
                            return Ok(new ApiResult<WalletAdjustResponse>(false, "Payment type not found", null));

                        paymentTypeId = request.PaymentTypeId.Value;
                    }
                }
                else // REFUND
                {
                    refundMethod = (request.RefundMethod ?? "").Trim().ToUpperInvariant();

                    if (refundMethod == "WALLET")
                        return Ok(new ApiResult<WalletAdjustResponse>(false,
                            "Wallet credit is not a valid refund method here — the wallet is being closed.", null));

                    if (settled > 0 && refundMethod != "CASH" && refundMethod != "LINK")
                        return Ok(new ApiResult<WalletAdjustResponse>(false,
                            "RefundMethod must be 'CASH' or 'LINK'", null));

                    if (refundMethod == "LINK" && string.IsNullOrWhiteSpace(request.RefundLink))
                        return Ok(new ApiResult<WalletAdjustResponse>(false,
                            "RefundLink is required for a LINK refund", null));

                    refundLink = refundMethod == "LINK" ? request.RefundLink!.Trim() : null;

                    // Refunding MORE than the leftover balance is allowed on purpose —
                    // goodwill top-ups happen — so only a shortfall counts as waived.
                    waived = settled < due ? due - settled : 0m;

                    if (settled == 0) refundMethod = null;
                }

                var now = DateTime.UtcNow;
                Guid customerRef = (Guid)sub.CustomerRef;
                int customerId = Convert.ToInt32(sub.CustomerId);
                int branchId = request.BranchId ?? Convert.ToInt32(sub.BranchId ?? 0);
                bool closeWallet = request.CloseWallet;

                // 1) Zero the ledger. The movement is the mirror image of whatever
                //    the balance was, so the running balance lands exactly on 0.
                InsertLedger(conn, customerRef, id, RefTypeAdjust, -balanceBefore, 0m, userId, now);

                // 2) Settlement record — the source of truth for the dashboard row.
                var adjustmentId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.WalletAdjustments (
                        AdjustGuid, SubscriptionId, CustomerRef, CustomerId, BranchId,
                        AdjustType, DueAmount, SettledAmount, WaivedAmount,
                        PaymentTypeId, RefundMethod, RefundLink,
                        BalanceBefore, BalanceAfter, ClosedWallet,
                        Notes, AddedBy, AddedDate, Deleted
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        NEWID(), @SubscriptionId, @CustomerRef, @CustomerId, @BranchId,
                        @AdjustType, @DueAmount, @SettledAmount, @WaivedAmount,
                        @PaymentTypeId, @RefundMethod, @RefundLink,
                        @BalanceBefore, 0, @ClosedWallet,
                        @Notes, @AddedBy, @AddedDate, 0
                    )",
                    new
                    {
                        SubscriptionId = id,
                        CustomerRef = customerRef,
                        CustomerId = customerId,
                        BranchId = branchId == 0 ? (int?)null : branchId,
                        AdjustType = adjustType,
                        DueAmount = due,
                        SettledAmount = settled,
                        WaivedAmount = waived,
                        PaymentTypeId = paymentTypeId,
                        RefundMethod = refundMethod,
                        RefundLink = refundLink,
                        BalanceBefore = balanceBefore,
                        ClosedWallet = closeWallet,
                        Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                        AddedBy = userId,
                        AddedDate = now
                    }).FirstOrDefault();

                // 3) Close the wallet unless the cashier explicitly kept it open.
                if (closeWallet)
                {
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.Subscriptions SET
                            IsClosed     = 1,
                            ClosedAt     = @Now,
                            ClosedBy     = @UserId,
                            ClosedReason = @Reason
                        WHERE Id = @Id",
                        new
                        {
                            Id = id,
                            Now = now,
                            UserId = userId,
                            Reason = adjustType == "COLLECT"
                                ? $"Settled: collected {settled:F3}, waived {waived:F3}"
                                : $"Settled: refunded {settled:F3}" + (waived > 0 ? $", waived {waived:F3}" : "")
                        });
                }

                uow.Commit();

                using var conn2 = sqlConnections.NewByKey("Default");
                var detail = GetSubscriptionDetailInternal(conn2, id);

                var response = new WalletAdjustResponse(
                    AdjustmentId: adjustmentId,
                    SubscriptionId: id,
                    AdjustType: adjustType,
                    DueAmount: due,
                    SettledAmount: settled,
                    WaivedAmount: waived,
                    BalanceBefore: balanceBefore,
                    BalanceAfter: 0m,
                    WalletClosed: closeWallet,
                    Wallet: detail
                );

                return Ok(new ApiResult<WalletAdjustResponse>(true, null, response));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<WalletAdjustResponse>(false,
                    $"Failed to adjust wallet: {ex.Message}", null));
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // DEDUCT (appointment payment) — now overdraft-aware
        // ═════════════════════════════════════════════════════════════════

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
                    SELECT s.Id, s.CustomerRef, s.EndDate, s.[Count] AS [Count],
                           ISNULL(s.Deleted, 0)  AS Deleted,
                           ISNULL(s.IsPaid, 0)   AS IsPaid,
                           ISNULL(s.IsClosed, 0) AS IsClosed,
                           ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) AS AllowOverdraft,
                           ISNULL(s.MaxCount, st.MaxCount) AS MaxCount,
                           ISNULL((
                               SELECT TOP 1 sh.Balance
                               FROM dbo.SubscriptionsHistory sh
                               WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                               ORDER BY sh.Id DESC
                           ), 0) AS CurrentBalance
                    FROM dbo.Subscriptions s
                    INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                    WHERE s.Id = @Id",
                    new { Id = request.SubscriptionId }).FirstOrDefault();

                if (sub == null || Convert.ToInt32(sub.Deleted) == 1)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription not found", null));

                if (Convert.ToInt32(sub.IsPaid) != 1)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription is not paid", null));

                if (Convert.ToInt32(sub.IsClosed) == 1)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "This wallet is closed", null));

                if ((DateTime)sub.EndDate < now)
                    return Ok(new ApiResult<DeductWalletResponse>(false, "Subscription has expired", null));

                decimal currentBalance = Convert.ToDecimal(sub.CurrentBalance);
                decimal count = sub.Count == null ? 0m : Convert.ToDecimal(sub.Count);
                bool allowOverdraft = Convert.ToInt32(sub.AllowOverdraft ?? 0) == 1;
                decimal? maxCount = sub.MaxCount == null ? (decimal?)null : Convert.ToDecimal(sub.MaxCount);
                decimal overdraftLimit = CalcOverdraftLimit(allowOverdraft, count, maxCount);

                // Overdraft lowers the floor from 0 to -(MaxCount - Count). With
                // overdraft off, overdraftLimit is 0 and this is the old check.
                decimal spendable = currentBalance + overdraftLimit;

                if (spendable < request.Amount)
                    return Ok(new ApiResult<DeductWalletResponse>(false,
                        overdraftLimit > 0
                            ? $"Insufficient wallet balance. Available (incl. overdraft): {spendable:F3}"
                            : $"Insufficient wallet balance. Available: {currentBalance:F3}", null));

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

                InsertLedger(conn, (Guid)sub.CustomerRef, request.SubscriptionId,
                    RefTypeInvoice, -request.Amount, newBalance, userId, now);

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

        // ═════════════════════════════════════════════════════════════════
        // PART 4 — wallet block for invoices + WhatsApp
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// The customer's current wallet, shaped for a receipt. Returns null when
        /// the customer has no wallet at all. Static so PosApiController and
        /// AppointmentsApiController can reuse it without a second round trip —
        /// the same pattern DebtApiController.GetCustomerOpenDebt follows.
        ///
        /// The most recently ending wallet wins, and an open wallet always beats
        /// a closed one, so a customer who settled an old wallet and bought a new
        /// one sees the new one on the receipt.
        /// </summary>
        public static InvoiceWalletInfoDto? LoadInvoiceWalletInfo(IDbConnection conn, int customerId)
        {
            try
            {
                var row = conn.Query<dynamic>(@"
                    SELECT TOP 1
                        s.Id,
                        st.NAME    AS SubTypeName,
                        s.EndDate,
                        s.[Count]  AS [Count],
                        ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) AS AllowOverdraft,
                        ISNULL(s.MaxCount, st.MaxCount) AS MaxCount,
                        ISNULL(s.IsClosed, 0) AS IsClosed,
                        ISNULL((
                            SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                            WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                            ORDER BY sh.Id DESC
                        ), 0) AS CurrentBalance
                    FROM dbo.Subscriptions s
                    INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                    INNER JOIN dbo.CUSTOMER c   ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                    WHERE c.CUSTOMER_ID = @CustomerId
                      AND ISNULL(s.Deleted, 0) = 0
                      AND ISNULL(s.IsPaid, 0) = 1
                    ORDER BY ISNULL(s.IsClosed, 0) ASC, s.EndDate DESC, s.Id DESC",
                    new { CustomerId = customerId }).FirstOrDefault();

                if (row == null) return null;

                decimal balance = Convert.ToDecimal(row.CurrentBalance);
                decimal count = row.Count == null ? 0m : Convert.ToDecimal(row.Count);
                bool allowOverdraft = Convert.ToInt32(row.AllowOverdraft ?? 0) == 1;
                decimal? maxCount = row.MaxCount == null ? (decimal?)null : Convert.ToDecimal(row.MaxCount);
                DateTime endDate = (DateTime)row.EndDate;

                return new InvoiceWalletInfoDto(
                    SubscriptionId: Convert.ToInt32(row.Id),
                    SubTypeName: (string?)row.SubTypeName ?? "",
                    CurrentBalance: balance,
                    EndDate: endDate,
                    IsExpired: endDate < DateTime.UtcNow,
                    IsClosed: Convert.ToInt32(row.IsClosed ?? 0) == 1,
                    AllowOverdraft: allowOverdraft,
                    MaxCount: maxCount,
                    OverdraftLimit: CalcOverdraftLimit(allowOverdraft, count, maxCount),
                    AmountOwed: balance < 0 ? -balance : 0m
                );
            }
            catch
            {
                // A receipt must still print if the wallet lookup fails.
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // WHATSAPP — wallet purchase notification
        // ═════════════════════════════════════════════════════════════════

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
                ISNULL(s.AllowOverdraft, ISNULL(st.AllowOverdraft, 0)) AS AllowOverdraft,
                ISNULL(s.MaxCount, st.MaxCount) AS MaxCount,
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
            WHERE s.Id = @Id AND ISNULL(s.Deleted, 0) = 0",
            new { Id = subscriptionId }).FirstOrDefault();

            if (sub == null)
                return Ok(new ApiResult<object>(false, "Subscription not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string lang = (string)sub.CustomerLang;
            string currency = lang == "en"
                ? ((string?)sub.EnglishCurrencyName ?? "KWD")
                : ((string?)sub.ArabicCurrencyName ?? "د.ك");

            decimal net = Convert.ToDecimal(sub.Net);
            decimal balance = Convert.ToDecimal(sub.CurrentBalance);
            decimal walletCredit = Convert.ToDecimal(sub.Count);
            string typeName = (string)sub.SubTypeName;
            string customerName = (string)sub.CustomerName;
            string phone = NormalizePhone((string)sub.CustomerPhone);
            DateTime endDate = (DateTime)sub.EndDate;

            bool allowOverdraft = Convert.ToInt32(sub.AllowOverdraft ?? 0) == 1;
            decimal? maxCount = sub.MaxCount == null ? (decimal?)null : Convert.ToDecimal(sub.MaxCount);
            decimal overdraftLimit = CalcOverdraftLimit(allowOverdraft, walletCredit, maxCount);

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
                if (overdraftLimit > 0)
                    sb.AppendLine($"➕ *Extra Allowance:* {currency} {overdraftLimit:F2} (up to {currency} {maxCount:F2})");
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
                if (overdraftLimit > 0)
                    sb.AppendLine($"➕ *حد السحب الإضافي:* {overdraftLimit:F2} {currency} (حتى {maxCount:F2} {currency})");
                sb.AppendLine($"📅 *صالحة حتى:* {endDate:dd MMM yyyy}");
                sb.AppendLine();
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("شكراً لكم! 🙏");
                if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
                message = sb.ToString();
            }

            // Raw number in — the service applies the configured country code.
            var result = await sender.SendAsync(conn, (string)sub.CustomerPhone, message,
                new WhatsAppContext(
                    MessageType: WhatsAppMessageTypes.WalletCreated,
                    ReferenceId: subscriptionId.ToString(),
                    CustomerName: customerName,
                    Lang: lang));

            // Queued counts as sent: the message is on its way, through a person.
            return Ok(new ApiResult<object>(true, null, new
            {
                Sent = result.Sent,
                Error = result.Error,
                Queued = result.AwaitingManualSend,
                Link = result.WaLink
            }));
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

        private const int RefTypeSubscription = 0;
        private const int RefTypeInvoice = 1;
        private const int RefTypeAdjustLegacy = 2;
        private const int RefTypeReturn = 3;
        private const int RefTypeAdjust = 4;
        private const int RefTypeExpiryReset = 5;

        private static dynamic? LoadWalletRow(IDbConnection conn, int id) =>
            conn.Query<dynamic>(@"
                SELECT s.Id, s.CustomerRef, s.SubTypeId, s.EndDate, s.BranchId,
                       ISNULL(s.Deleted, 0)  AS Deleted,
                       ISNULL(s.IsClosed, 0) AS IsClosed
                FROM dbo.Subscriptions s
                WHERE s.Id = @Id AND ISNULL(s.Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault();

        private static void InsertPayment(IDbConnection conn, int subId, int paymentTypeId,
            decimal amount, DateTime when, string? notes, int userId,
            string actionType, int? previousSubTypeId)
        {
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
                    @ActionType, @PreviousSubTypeId
                )",
                new
                {
                    PaymentTypeId = paymentTypeId,
                    SubscriptionId = subId,
                    PaymentAmount = amount,
                    PaymentDate = when,
                    Notes = notes,
                    AddedBy = userId,
                    AddedDate = when,
                    ActionType = actionType,
                    PreviousSubTypeId = previousSubTypeId
                });
        }

        private static void InsertLedger(IDbConnection conn, Guid customerRef, int subId,
            int refType, decimal amount, decimal balance, int userId, DateTime when)
        {
            SqlMapper.Execute(conn, @"
                INSERT INTO dbo.SubscriptionsHistory (
                    CustomerRef, RefType, InvoiceId, SubscriptionId,
                    Amount, Balance, AddedBy, AddedDate, Deleted
                )
                VALUES (
                    @CustomerRef, @RefType, NULL, @SubscriptionId,
                    @Amount, @Balance, @AddedBy, @AddedDate, 0
                )",
                new
                {
                    CustomerRef = customerRef,
                    RefType = refType,
                    SubscriptionId = subId,
                    Amount = amount,
                    Balance = balance,
                    AddedBy = userId,
                    AddedDate = when
                });
        }

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
            RefTypeSubscription => "Subscription",
            RefTypeInvoice => "Invoice",
            RefTypeAdjustLegacy => "Adjustment",
            RefTypeReturn => "Return",
            RefTypeAdjust => "Settlement",
            RefTypeExpiryReset => "Expired credit reset",
            _ => $"Type {refType}"
        };

        private static SubscriptionDto MapToSubscriptionDto(dynamic r, DateTime now)
        {
            var endDate = (DateTime)r.EndDate;
            var isExpired = endDate < now;
            decimal currentBalance = Convert.ToDecimal(r.CurrentBalance);

            bool isClosed = false;
            DateTime? closedAt = null;
            string? closedReason = null;
            try { isClosed = Convert.ToInt32(r.IsClosed ?? 0) == 1; } catch { }
            try { closedAt = (DateTime?)r.ClosedAt; } catch { }
            try { closedReason = (string?)r.ClosedReason; } catch { }

            decimal count = 0m;
            try { count = r.Count == null ? 0m : Convert.ToDecimal(r.Count); } catch { }

            bool allowOverdraft = false;
            decimal? maxCount = null;
            try { allowOverdraft = Convert.ToInt32(r.AllowOverdraft ?? 0) == 1; } catch { }
            try { maxCount = r.MaxCount == null ? (decimal?)null : Convert.ToDecimal(r.MaxCount); } catch { }

            decimal overdraftLimit = CalcOverdraftLimit(allowOverdraft, count, maxCount);

            // A closed wallet is never "active" no matter what the dates say — it
            // has been settled, and offering to spend from it would be a bug.
            bool isActive = !isClosed && !isExpired
                        && (currentBalance + overdraftLimit) > 0
                        && Convert.ToInt32(r.IsPaid) == 1;

            int? payerCustomerId = null;
            string? payerCustomerName = null;
            string? payerNote = null;
            try { payerCustomerId = (int?)r.PayerCustomerId; } catch { }
            try { payerCustomerName = (string?)r.PayerCustomerName; } catch { }
            try { payerNote = (string?)r.PayerNote; } catch { }

            decimal totalCredit = 0;
            decimal totalPaid = 0;
            string lastAction = "CREATE";
            try { totalCredit = Convert.ToDecimal(r.TotalCredit ?? 0m); } catch { }
            try { totalPaid = Convert.ToDecimal(r.TotalPaid ?? 0m); } catch { }
            try { lastAction = (string?)r.LastActionType ?? "CREATE"; } catch { }

            return new SubscriptionDto(
                Id: (int)r.Id,
                Guid: (Guid)r.GUID,
                CustomerId: (int)r.CustomerId,
                CustomerName: (string)(r.CustomerName ?? ""),
                CustomerPhone: (string)(r.CustomerPhone ?? ""),
                SubTypeId: (int)r.SubTypeId,
                SubTypeName: (string)(r.SubTypeName ?? ""),
                Value: Convert.ToDecimal(r.Value),
                DiscountType: (int?)r.DiscountType,
                DiscountValue: (decimal?)r.DiscountValue,
                Net: Convert.ToDecimal(r.Net),
                Count: r.Count == null ? (decimal?)null : count,
                StartDate: (DateTime)r.StartDate,
                EndDate: endDate,
                DaysCount: (decimal?)r.DaysCount,
                BranchId: (int)r.BranchId,
                IsPaid: Convert.ToInt32(r.IsPaid),
                AddedDate: (DateTime)r.AddedDate,
                CurrentBalance: currentBalance,
                IsExpired: isExpired,
                IsActive: isActive,
                PayerCustomerId: payerCustomerId,
                PayerCustomerName: payerCustomerName,
                PayerNote: payerNote,
                TotalCredit: totalCredit,
                TotalPaid: totalPaid,
                LastActionType: lastAction,
                AllowOverdraft: allowOverdraft,
                MaxCount: maxCount,
                OverdraftLimit: overdraftLimit,
                AmountOwed: currentBalance < 0 ? -currentBalance : 0m,
                AvailableToSpend: Math.Max(0m, currentBalance + overdraftLimit),
                IsClosed: isClosed,
                ClosedAt: closedAt,
                ClosedReason: closedReason,
                IsOverdrawn: currentBalance < 0
            );
        }

        private WalletDetailDto? GetSubscriptionDetailInternal(IDbConnection conn, int subId)
        {
            var sub = conn.Query<dynamic>($@"
                SELECT {SubscriptionSelect}
                FROM dbo.Subscriptions s
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                INNER JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                LEFT JOIN dbo.CUSTOMER pc ON pc.CUSTOMER_ID = s.PayerCustomerId
                WHERE s.Id = @Id AND ISNULL(s.Deleted, 0) = 0",
            new { Id = subId }).FirstOrDefault();

            if (sub == null) return null;

            SubscriptionDto subDto = (SubscriptionDto)MapToSubscriptionDto(sub, DateTime.UtcNow);

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
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = sp.PAYMENT_TYPE_ID
                LEFT JOIN dbo.SUBS_TYPE pst ON pst.ID = sp.PreviousSubTypeId
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

            var adjustments = LoadAdjustments(conn, subId);

            return new WalletDetailDto(subDto, payments, history, adjustments);
        }

        private static List<WalletAdjustmentDto> LoadAdjustments(IDbConnection conn, int subId)
        {
            try
            {
                return conn.Query<dynamic>(@"
                    SELECT
                        wa.Id, wa.SubscriptionId, wa.AdjustType,
                        wa.DueAmount, wa.SettledAmount, wa.WaivedAmount,
                        wa.PaymentTypeId,
                        pt.INVOICE_PAYMENT_TYPE_NAME1 AS PaymentTypeName,
                        pt.INVOICE_PAYMENT_TYPE_NAME2 AS PaymentTypeNameAr,
                        wa.RefundMethod, wa.RefundLink,
                        wa.BalanceBefore, wa.BalanceAfter, wa.ClosedWallet,
                        wa.Notes, wa.AddedDate
                    FROM dbo.WalletAdjustments wa
                    LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = wa.PaymentTypeId
                    WHERE wa.SubscriptionId = @Id AND wa.Deleted = 0
                    ORDER BY wa.Id",
                    new { Id = subId })
                    .Select(a => new WalletAdjustmentDto(
                        Id: (int)a.Id,
                        SubscriptionId: (int)a.SubscriptionId,
                        AdjustType: (string)a.AdjustType,
                        DueAmount: (decimal)a.DueAmount,
                        SettledAmount: (decimal)a.SettledAmount,
                        WaivedAmount: (decimal)a.WaivedAmount,
                        PaymentTypeId: (int?)a.PaymentTypeId,
                        PaymentTypeName: (string?)a.PaymentTypeName,
                        PaymentTypeNameAr: (string?)a.PaymentTypeNameAr,
                        RefundMethod: (string?)a.RefundMethod,
                        RefundLink: (string?)a.RefundLink,
                        BalanceBefore: (decimal)a.BalanceBefore,
                        BalanceAfter: (decimal)a.BalanceAfter,
                        ClosedWallet: Convert.ToInt32((object)a.ClosedWallet) == 1,
                        Notes: (string?)a.Notes,
                        AddedDate: (DateTime)a.AddedDate
                    )).ToList();
            }
            catch
            {
                // WalletAdjustments may not exist yet if the migration has not run.
                return new List<WalletAdjustmentDto>();
            }
        }

        #endregion
    }
}
