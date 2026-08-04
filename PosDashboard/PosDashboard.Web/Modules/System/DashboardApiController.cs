// File: Modules/System/Controllers/DashboardApiController.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using static PosDashboard.Web.Modules.System.Models.DashboardDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/dashboard")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class DashboardApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        /// <summary>
        /// Safety net: the default ADO.NET command timeout is 30s, which is what produced
        /// "SqlException: Execution Timeout Expired" in production. After the query fixes
        /// below the endpoint should finish in well under a second, so this ceiling should
        /// never be reached — it only exists so a slow month never turns into a hard error.
        /// </summary>
        private const int CmdTimeoutSeconds = 120;

        public DashboardApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // GET /api/dashboard/summary?branchId=1&fromDate=2026-05-01&toDate=2026-05-07&staffId=
        // Backward compatible: ?date=2026-05-01 still works (treated as a single-day range).
        [HttpGet("summary")]
        public ActionResult<ApiResult<DashboardSummaryDto>> Summary(
            [FromQuery] int branchId,
            [FromQuery] string? fromDate = null,
            [FromQuery] string? toDate = null,
            [FromQuery] string? date = null,
            [FromQuery] int? staffId = null,
            [FromQuery] string? lang = null)
        {
            // Language for display names: 'ar' uses NAME2/ArabicName columns, anything else 'en'.
            var langCode = string.Equals((lang ?? "en").Trim(), "ar",
                StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
            try
            {
                if (branchId <= 0)
                    return Ok(new ApiResult<DashboardSummaryDto>(false, "branchId is required", null));

                // Resolve the requested period. Priority:
                //   1) fromDate/toDate (range mode)
                //   2) legacy single 'date' (single-day range)
                // If only one of fromDate/toDate is supplied, the other falls back to it.
                var rawFrom = !string.IsNullOrWhiteSpace(fromDate) ? fromDate
                            : (!string.IsNullOrWhiteSpace(date) ? date : toDate);
                var rawTo = !string.IsNullOrWhiteSpace(toDate) ? toDate
                            : (!string.IsNullOrWhiteSpace(date) ? date : fromDate);

                if (string.IsNullOrWhiteSpace(rawFrom) || string.IsNullOrWhiteSpace(rawTo))
                    return Ok(new ApiResult<DashboardSummaryDto>(false,
                        "fromDate and toDate (or legacy date) are required", null));

                if (!DateTime.TryParseExact(rawFrom, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDateOnly))
                    return Ok(new ApiResult<DashboardSummaryDto>(false,
                        "fromDate must be yyyy-MM-dd", null));

                if (!DateTime.TryParseExact(rawTo, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDateOnly))
                    return Ok(new ApiResult<DashboardSummaryDto>(false,
                        "toDate must be yyyy-MM-dd", null));

                // Tolerate a reversed range (user picked end before start).
                if (toDateOnly < fromDateOnly)
                    (fromDateOnly, toDateOnly) = (toDateOnly, fromDateOnly);

                using var conn = sqlConnections.NewByKey("Default");

                // Meta query first, so we can understand tzOffset
                var metaP = new { BranchId = branchId };

                var meta = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'calendarStartHour') AS StartHour,
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'calendarEndHour')   AS EndHour,
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'timeZoneOffset')    AS TzOffset,
                    (SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @BranchId)                                AS Currency,
                    (SELECT ArabicCurrencyName  FROM dbo.BRANCH WHERE BRANCH_ID = @BranchId)                                AS CurrencyAr",
                                metaP, commandTimeout: CmdTimeoutSeconds).FirstOrDefault();

                int startHour = meta?.StartHour != null ? (int)meta.StartHour : 10;
                int endHour = meta?.EndHour != null ? (int)meta.EndHour : 22;
                string currency = langCode == "ar"
                    ? (meta?.CurrencyAr != null ? (string)meta.CurrencyAr
                        : (meta?.Currency != null ? (string)meta.Currency : "د.ك"))
                    : (meta?.Currency != null ? (string)meta.Currency : "KWD");
                int workdayMinutes = Math.Max(1, (endHour - startHour) * 60);
                int tzOffset = meta?.TzOffset != null ? (int)meta.TzOffset : 3;


                // Range window (half-open in UTC-stored terms, offset-adjusted):
                //   start = local-midnight of fromDate  - tzOffset
                //   end   = local-midnight of (toDate+1) - tzOffset  (exclusive)
                var dateStart = fromDateOnly.Date.AddHours(-tzOffset);
                var dateEnd = toDateOnly.Date.AddDays(1).AddHours(-tzOffset);

                var p = new
                {
                    BranchId = branchId,
                    DateStart = dateStart,
                    DateEnd = dateEnd,
                    // DATE-typed comparisons use the local dates (without offset), inclusive on both ends.
                    FromDateOnly = fromDateOnly.Date,
                    ToDateOnly = toDateOnly.Date,
                    StaffId = staffId,
                    Lang = langCode,
                    // Transaction timestamps (CreatedAt/PaidAt/ProcessedAt/...) are stored in UTC.
                    // Add the branch tz offset so the displayed [Time] matches local wall-clock.
                    TzOffset = tzOffset
                };

                // ---------- 2A: Revenue KPIs ----------
                var kpi = SqlMapper.Query<dynamic>(conn, @"
                    ;WITH
                    CheckoutToday AS (
                        SELECT ISNULL(SUM(ap.Amount), 0) AS TotalInvoicePaid
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        INNER JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                        -- PERF: الأصل كان (inv.AppointmentId = ... OR inv.Id IN (...)) وهو شرط
                        -- لا يستطيع SQL Server تنفيذه بـ index seek، فكان يمسح جدول
                        -- AppointmentInvoices لكل صف payment. صيغة الـ UNION التالية مكافئة
                        -- منطقياً (نفس الصفوف، نفس TOP 1 حسب Id) لكن قابلة للـ seek.
                        CROSS APPLY (
                            SELECT TOP 1 x.CreatedAt, x.IsVoid
                            FROM (
                                SELECT inv.Id, inv.CreatedAt, ISNULL(inv.IsVoid, 0) AS IsVoid
                                FROM dbo.AppointmentInvoices inv
                                WHERE inv.AppointmentId = ap.AppointmentId
                                UNION
                                SELECT inv2.Id, inv2.CreatedAt, ISNULL(inv2.IsVoid, 0) AS IsVoid
                                FROM dbo.AppointmentInvoiceLines ail
                                INNER JOIN dbo.AppointmentInvoices inv2 ON inv2.Id = ail.InvoiceId
                                WHERE ail.AppointmentId = ap.AppointmentId
                            ) x
                            ORDER BY x.Id
                        ) ri
                        WHERE a.BranchId = @BranchId
                          AND ri.CreatedAt >= @DateStart AND ri.CreatedAt < @DateEnd
                          AND ap.IsWalletPayment = 0
                          AND ap.PaymentAs = 'FULL'
                          AND ISNULL(pt.OnlinePayment, 0) = 0
                          AND ri.IsVoid = 0
                          AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    ),
                    DepositsToday AS (
                        SELECT ISNULL(SUM(ap.Amount), 0) AS TodayDepositRevenue
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        WHERE a.BranchId = @BranchId
                          AND ap.PaymentAs = 'DEPOSIT'
                          AND ap.IsWalletPayment = 0
                          AND ap.PaidAt >= @DateStart AND ap.PaidAt < @DateEnd
                          AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    ),
                    PendingDeposits AS (
                        SELECT ISNULL(SUM(a.TotalPrice - a.PaidAmount), 0) AS PendingFromDeposits
                        FROM dbo.AppointmentData a
                        WHERE a.BranchId = @BranchId
                          AND a.CreatedAt >= @DateStart AND a.CreatedAt < @DateEnd
                          AND a.CheckoutStatus = 'open'
                          AND a.PaidAmount > 0
                          AND (a.TotalPrice - a.PaidAmount) > 0
                          AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    ),
                    WalletToday AS (
                        -- Wallet income = packages sold + overdrafts collected back.
                        -- A settlement collection is money that came in the till today
                        -- against a wallet, so it belongs here and therefore flows
                        -- into TotalEffectiveRevenue below. A waiver (SettledAmount = 0)
                        -- moved no money and is excluded.
                        SELECT ISNULL(SUM(w.Amount), 0) AS WalletRevenue
                        FROM (
                            SELECT sp.PAYMENT_AMOUNT AS Amount
                            FROM dbo.SubscriptionPayment sp
                            INNER JOIN dbo.Subscriptions s ON s.Id = sp.SubscriptionId
                            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                            WHERE c.BRANCH_ID = @BranchId
                              AND sp.DELETED = 0
                              AND sp.PAYMENT_DATE >= @DateStart AND sp.PAYMENT_DATE < @DateEnd

                            UNION ALL

                            SELECT wa.SettledAmount
                            FROM dbo.WalletAdjustments wa
                            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                            WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                              AND wa.Deleted = 0
                              AND wa.AdjustType = 'COLLECT'
                              AND wa.SettledAmount > 0
                              AND wa.AddedDate >= @DateStart AND wa.AddedDate < @DateEnd
                        ) w
                    ),
                    PackagesToday AS (
                        SELECT ISNULL(SUM(pp.PaymentAmount), 0) AS PackagesRevenue
                        FROM dbo.CustomerPackagePayments pp
                        INNER JOIN dbo.CustomerPackages cp ON cp.Id = pp.CustomerPackageId
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                        WHERE c.BRANCH_ID = @BranchId
                          AND ISNULL(pp.Deleted, 0) = 0
                          AND pp.AddedDate >= @DateStart AND pp.AddedDate < @DateEnd
                    ),
                    OnlineFullToday AS (
                        SELECT ISNULL(SUM(ap.Amount), 0) AS OnlineFullRevenue
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        INNER JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                        -- PERF: نفس رواية الـ CROSS APPLY أعلاه — OR اتحوّل لـ UNION.
                        CROSS APPLY (
                            SELECT TOP 1 x.CreatedAt, x.IsVoid
                            FROM (
                                SELECT inv.Id, inv.CreatedAt, ISNULL(inv.IsVoid, 0) AS IsVoid
                                FROM dbo.AppointmentInvoices inv
                                WHERE inv.AppointmentId = ap.AppointmentId
                                UNION
                                SELECT inv2.Id, inv2.CreatedAt, ISNULL(inv2.IsVoid, 0) AS IsVoid
                                FROM dbo.AppointmentInvoiceLines ail
                                INNER JOIN dbo.AppointmentInvoices inv2 ON inv2.Id = ail.InvoiceId
                                WHERE ail.AppointmentId = ap.AppointmentId
                            ) x
                            ORDER BY x.Id
                        ) ri
                        WHERE a.BranchId = @BranchId
                          AND ri.CreatedAt >= @DateStart AND ri.CreatedAt < @DateEnd
                          AND ap.IsWalletPayment = 0
                          AND ap.PaymentAs = 'FULL'
                          AND ISNULL(pt.OnlinePayment, 0) = 1
                          AND ri.IsVoid = 0
                          AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    ),
                    -- Deduct cash refunds processed today from checkout revenue
                    -- Deduct cash refunds processed today from checkout revenue.
                    -- Invoice refunds ONLY: this feeds the Sales card, and a wallet
                    -- payout reverses no sale.
                    CashRefundsToday AS (
                        SELECT ISNULL(SUM(rt.RefundAmount), 0) AS TotalCashRefunded
                        FROM dbo.RefundTransactions rt
                        WHERE rt.BranchId  = @BranchId
                          AND rt.RefundType = 'CASH'
                          AND rt.ProcessedAt >= @DateStart AND rt.ProcessedAt < @DateEnd
                          AND rt.Deleted = 0
                    ),
                    -- Money paid back out of a wallet (Adjust → REFUND). It reverses
                    -- wallet income, so it comes off Total Revenue — but never off
                    -- Sales, because no sale was reversed.
                    --
                    -- Unlike the invoice-refund rule above, BOTH rails count here.
                    -- WalletToday records wallet income whatever rail it arrived on,
                    -- so the reversal has to match, or a LINK payout would leave
                    -- income on the books that no longer exists.
                    --
                    -- The window is @DateStart/@DateEnd — the offset-adjusted UTC
                    -- window every other KPI uses — so this figure and the Refunds
                    -- card describe the same set of settlements.
                    WalletRefundsToday AS (
                        SELECT ISNULL(SUM(wa.SettledAmount), 0) AS TotalWalletRefunded
                        FROM dbo.WalletAdjustments wa
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                        WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                          AND wa.Deleted = 0
                          AND wa.AdjustType = 'REFUND'
                          AND wa.SettledAmount > 0
                          AND wa.AddedDate >= @DateStart AND wa.AddedDate < @DateEnd
                    )
                    SELECT
                        c.TotalInvoicePaid        AS TotalCheckoutRevenue,
                        d.TodayDepositRevenue,
                        p.PendingFromDeposits,
                        wal.WalletRevenue,
                        pk.PackagesRevenue,
                        onl.OnlineFullRevenue,
                        cr.TotalCashRefunded,
                        -- Checkout revenue net of cash refunds (can go negative — by design)
                        (c.TotalInvoicePaid - cr.TotalCashRefunded) AS NetCheckoutRevenue,
                        wr.TotalWalletRefunded,
                        ((c.TotalInvoicePaid - cr.TotalCashRefunded) + d.TodayDepositRevenue
                         + wal.WalletRevenue + pk.PackagesRevenue + onl.OnlineFullRevenue
                         - wr.TotalWalletRefunded) AS TotalEffectiveRevenue 
                    FROM CheckoutToday c
                    CROSS JOIN DepositsToday d
                    CROSS JOIN PendingDeposits p
                    CROSS JOIN WalletToday wal
                    CROSS JOIN PackagesToday pk
                    CROSS JOIN OnlineFullToday onl
                    CROSS JOIN CashRefundsToday cr
                    CROSS JOIN WalletRefundsToday wr
                    OPTION (RECOMPILE);",
                        p, commandTimeout: CmdTimeoutSeconds).FirstOrDefault();

                decimal totalCheckout = kpi != null ? (decimal)kpi.NetCheckoutRevenue : 0m;
                decimal todayDeposit = kpi != null ? (decimal)kpi.TodayDepositRevenue : 0m;
                decimal pendingDeposit = kpi != null ? (decimal)kpi.PendingFromDeposits : 0m;
                decimal walletRev = kpi != null ? (decimal)kpi.WalletRevenue : 0m;
                decimal packagesRev = kpi != null ? (decimal)kpi.PackagesRevenue : 0m;
                decimal onlineFullRev = kpi != null ? (decimal)kpi.OnlineFullRevenue : 0m;
                decimal totalEffective = kpi != null ? (decimal)kpi.TotalEffectiveRevenue : 0m;

                // ---------- 2H: Refund Summary ----------
                var refundSummaryRow = SqlMapper.Query<dynamic>(conn, @"
                                     ;WITH AllRefunds AS (
                                         SELECT rt.RefundAmount AS RefundAmount, rt.RefundType AS RefundType
                                         FROM dbo.RefundTransactions rt
                                         WHERE rt.BranchId = @BranchId
                                           -- PERF: CAST(col AS DATE) يمنع الـ index seek؛
                                           -- النطاق النصف-مفتوح يرجّع نفس الصفوف بالظبط.
                                           AND rt.ProcessedAt >= @FromDateOnly
                                           AND rt.ProcessedAt <  DATEADD(DAY, 1, @ToDateOnly)
                                           AND rt.Deleted = 0

                                         UNION ALL

                                         -- Money paid back out of a wallet settlement is a
                                         -- refund by every meaning the card has: it leaves
                                         -- the till and reverses earlier income. RefundMethod
                                         -- already uses the same CASH/LINK vocabulary, so the
                                         -- per-type counters keep working unchanged.
                                         SELECT wa.SettledAmount, wa.RefundMethod
                                         FROM dbo.WalletAdjustments wa
                                         INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                                         WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                                           AND wa.Deleted = 0
                                           AND wa.AdjustType = 'REFUND'
                                           AND wa.SettledAmount > 0
                                           AND wa.AddedDate >= @DateStart
                                           AND wa.AddedDate <  @DateEnd
                                     )
                                     SELECT COUNT(*) AS TotalRefunds,
                                            ISNULL(SUM(RefundAmount), 0)                          AS TotalRefundAmount,
                                            SUM(CASE WHEN RefundType = 'CASH'   THEN 1 ELSE 0 END) AS CashRefunds,
                                            SUM(CASE WHEN RefundType = 'LINK'   THEN 1 ELSE 0 END) AS LinkRefunds,
                                            SUM(CASE WHEN RefundType = 'WALLET' THEN 1 ELSE 0 END) AS WalletRefunds
                                     FROM AllRefunds
                                     OPTION (RECOMPILE)",
                    p, commandTimeout: CmdTimeoutSeconds).FirstOrDefault();

                var refundSummary = refundSummaryRow != null
                    ? new RefundSummaryDto(
                        TotalRefunds: (int)(refundSummaryRow.TotalRefunds ?? 0),
                        TotalRefundAmount: (decimal)(refundSummaryRow.TotalRefundAmount ?? 0m),
                        CashRefunds: (int)(refundSummaryRow.CashRefunds ?? 0),
                        LinkRefunds: (int)(refundSummaryRow.LinkRefunds ?? 0),
                        WalletRefunds: (int)(refundSummaryRow.WalletRefunds ?? 0))
                    : null;
                // ---------- 2B: Payment Type Breakdown ----------
                var paymentBreakdown = SqlMapper.Query<dynamic>(conn, @"
                    ;WITH AllPayments AS (
                        SELECT ap.PaymentTypeId, ap.Amount
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        -- PERF: شرط الـ OR اتحوّل لـ UNION قابل للـ seek (نفس مجموعة الصفوف).
                        OUTER APPLY (
                            SELECT TOP 1 x.InvoiceId, x.IsVoid
                            FROM (
                                SELECT inv.Id AS InvoiceId, ISNULL(inv.IsVoid, 0) AS IsVoid
                                FROM dbo.AppointmentInvoices inv
                                WHERE inv.AppointmentId = ap.AppointmentId
                                UNION
                                SELECT inv2.Id, ISNULL(inv2.IsVoid, 0)
                                FROM dbo.AppointmentInvoiceLines ail
                                INNER JOIN dbo.AppointmentInvoices inv2 ON inv2.Id = ail.InvoiceId
                                WHERE ail.AppointmentId = ap.AppointmentId
                            ) x
                            ORDER BY x.InvoiceId
                        ) ri
                        WHERE a.BranchId = @BranchId
                          AND ap.PaidAt >= @DateStart AND ap.PaidAt < @DateEnd
                          AND ap.IsWalletPayment = 0
                          AND ISNULL(ri.IsVoid, 0) = 0
                          AND (ri.InvoiceId IS NOT NULL OR ap.PaymentAs = 'DEPOSIT')
                          AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                        UNION ALL
                        SELECT sp.PAYMENT_TYPE_ID, sp.PAYMENT_AMOUNT
                        FROM dbo.SubscriptionPayment sp
                        INNER JOIN dbo.Subscriptions s ON s.Id = sp.SubscriptionId
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                        WHERE c.BRANCH_ID = @BranchId
                          AND sp.DELETED = 0
                          AND sp.PAYMENT_DATE >= @DateStart AND sp.PAYMENT_DATE < @DateEnd
                        UNION ALL
                        SELECT pp.PaymentTypeId, pp.PaymentAmount
                        FROM dbo.CustomerPackagePayments pp
                        INNER JOIN dbo.CustomerPackages cp ON cp.Id = pp.CustomerPackageId
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                        WHERE c.BRANCH_ID = @BranchId
                          AND ISNULL(pp.Deleted, 0) = 0
                          AND pp.AddedDate >= @DateStart AND pp.AddedDate < @DateEnd
                        UNION ALL
                        -- Wallet settlement collections. The cashier picked a real
                        -- payment type when taking the money, so it lands on that row
                        -- exactly like any other payment.
                        SELECT wa.PaymentTypeId, wa.SettledAmount
                        FROM dbo.WalletAdjustments wa
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                        WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                          AND wa.Deleted = 0
                          AND wa.AdjustType = 'COLLECT'
                          AND wa.PaymentTypeId IS NOT NULL
                          AND wa.SettledAmount > 0
                          AND wa.AddedDate >= @DateStart AND wa.AddedDate < @DateEnd
                    ),
                    -- Cash refunds processed today — subtract from whichever payment type is 'Cash'
                    CashRefundsByType AS (
                        SELECT
                            pt.INVOICE_PAYMENT_TYPE_ID AS PaymentTypeId,
                            -ISNULL(SUM(r.Amount), 0) AS Amount
                        FROM (
                            SELECT rt.RefundAmount AS Amount
                            FROM dbo.RefundTransactions rt
                            WHERE rt.BranchId   = @BranchId
                              AND rt.RefundType  = 'CASH'
                              AND rt.ProcessedAt >= @DateStart AND rt.ProcessedAt < @DateEnd
                              AND rt.Deleted = 0

                            UNION ALL

                            -- Cash paid back to close a wallet leaves the drawer just
                            -- like an invoice refund, so it reduces the Cash row too.
                            -- A LINK payout never touches the drawer and is excluded.
                            SELECT wa.SettledAmount
                            FROM dbo.WalletAdjustments wa
                            INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                            WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                              AND wa.Deleted = 0
                              AND wa.AdjustType   = 'REFUND'
                              AND wa.RefundMethod = 'CASH'
                              AND wa.SettledAmount > 0
                              AND wa.AddedDate >= @DateStart AND wa.AddedDate < @DateEnd
                        ) r
                        -- Map cash payouts to the cash payment type row
                        INNER JOIN dbo.INVOICE_PAYMENT_TYPE pt
                            ON UPPER(pt.INVOICE_PAYMENT_TYPE_NAME1) LIKE '%CASH%'
                            OR UPPER(pt.DocumentName) LIKE '%CASH%'
                        GROUP BY pt.INVOICE_PAYMENT_TYPE_ID
                    ),
                    Combined AS (
                        SELECT PaymentTypeId, Amount FROM AllPayments
                        UNION ALL
                        SELECT PaymentTypeId, Amount FROM CashRefundsByType
                    )
                    SELECT
                        pt.INVOICE_PAYMENT_TYPE_ID    AS PaymentTypeId,
                        CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2
                             ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END AS PaymentTypeName,
                        pt.DocumentName               AS DocumentName,
                        SUM(ap.Amount)                AS Amount
                    FROM Combined ap
                    INNER JOIN dbo.INVOICE_PAYMENT_TYPE pt
                        ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                    GROUP BY pt.INVOICE_PAYMENT_TYPE_ID, pt.INVOICE_PAYMENT_TYPE_NAME1, pt.INVOICE_PAYMENT_TYPE_NAME2, pt.DocumentName
                    HAVING SUM(ap.Amount) <> 0   -- include negative balances (refund > income)
                    ORDER BY SUM(ap.Amount) DESC
                    OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds)
                    .Select(r => new PaymentTypeBreakdownDto(
                        PaymentTypeId: (int)r.PaymentTypeId,
                        PaymentTypeName: (string)(r.PaymentTypeName ?? ""),
                        Amount: (decimal)r.Amount,
                        DocumentName: (string?)r.DocumentName
                    )).ToList();


                // ---------- 2C: Transactions ----------
                var transactions = SqlMapper.Query<dynamic>(conn, @"
                ;WITH
                -- الـ invoices الأساسية — الأساس اللي كل الـ CTEs بعده بتتقيّد بيه
                InvBase AS (
                    SELECT inv.Id          AS InvoiceId,
                           inv.InvoiceNumber,
                           inv.AppointmentId,
                           inv.CreatedAt
                    FROM dbo.AppointmentInvoices inv
                    INNER JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
                    WHERE a.BranchId = @BranchId
                      AND inv.CreatedAt >= @DateStart AND inv.CreatedAt < @DateEnd
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                ),
                -- PERF: كل زوج (invoice, appointment) مرة واحدة. هذا بديل الـ
                --   INNER JOIN AppointmentPayments ON ap.AppointmentId = inv.AppointmentId
                --                                  OR ap.AppointmentId IN (...)
                -- الذي كان يمنع أي index seek على AppointmentPayments. الـ UNION يزيل
                -- التكرار فيُحسب كل payment مرة واحدة لكل invoice — نفس سلوك الـ OR.
                InvApptMap AS (
                    SELECT ib.InvoiceId, ib.AppointmentId
                    FROM InvBase ib
                    UNION
                    SELECT ib.InvoiceId, ail.AppointmentId
                    FROM InvBase ib
                    INNER JOIN dbo.AppointmentInvoiceLines ail ON ail.InvoiceId = ib.InvoiceId
                ),
                -- الـ FULL non-wallet payments لكل invoice
                InvFullPaid AS (
                    SELECT m.InvoiceId,
                           ISNULL(SUM(ap.Amount), 0) AS NonDepositNonWalletPaid,
                           MAX(ap.PaidAt)            AS LastFullPaidAt
                    FROM InvApptMap m
                    INNER JOIN dbo.AppointmentPayments ap ON ap.AppointmentId = m.AppointmentId
                    WHERE ap.IsWalletPayment = 0
                      AND ap.PaymentAs = 'FULL'
                    GROUP BY m.InvoiceId
                ),
                -- آخر non-wallet FULL payment type لكل appointment
                -- PERF: كان يعمل GROUP BY على جدول AppointmentPayments بالكامل (كل التواريخ من
                -- بداية التشغيل) ثم يستخدم صفوف الفترة فقط. التقييد على InvBase نفس النتيجة
                -- تماماً لأن الـ CTE ده مستخدم في LEFT JOIN على AppointmentId من InvBase.
                InvLastPayType AS (
                SELECT ap.AppointmentId,
                       STRING_AGG(CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2 ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END, ' + ')
                           WITHIN GROUP (ORDER BY ap.PaidAt ASC) AS LastPaymentTypeName,
                       -- JSON array للـ breakdown
                       '[' + STRING_AGG(
                           '{""n"":""' + REPLACE(ISNULL(CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2 ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END, '-'), '""', '') +
                           '"",""a"":' + CAST(ap.Amount AS varchar(20)) + '}',
                           ','
                       ) WITHIN GROUP (ORDER BY ap.PaidAt ASC) + ']' AS PaymentBreakdownJson
                FROM dbo.AppointmentPayments ap
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt
                    ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                WHERE ap.IsWalletPayment = 0
                  AND ap.PaymentAs = 'FULL'
                  AND ap.AppointmentId IN (SELECT ib.AppointmentId FROM InvBase ib)
                GROUP BY ap.AppointmentId
                ),
                -- أسماء الـ services لكل invoice (New Sale = متعددة)
                -- PERF: كان يقرأ AppointmentInvoiceLines بالكامل؛ الآن على invoices الفترة فقط.
                -- (ملاحظة: الـ CTE القديم InvWallet كان معرَّفاً وغير مُستخدم — تم حذفه.)
                InvServices AS (
                    SELECT ail.InvoiceId,
                           STRING_AGG(CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END, ' + ') AS AllServicesName
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = ail.ItemId
                    WHERE ail.InvoiceId IN (SELECT ib.InvoiceId FROM InvBase ib)
                    GROUP BY ail.InvoiceId
                ),
                InvAmounts AS (
                    SELECT
                        ib.InvoiceId,
                        ib.InvoiceNumber,
                        ib.AppointmentId,
                        ib.CreatedAt,
                        ISNULL(fp.NonDepositNonWalletPaid, 0) AS NonDepositNonWalletPaid,
                        ISNULL(lp.LastPaymentTypeName, '-')   AS LastPaymentTypeName,
                        ISNULL(lp.PaymentBreakdownJson, '[]') AS PaymentBreakdownJson,
                        ISNULL(svc.AllServicesName, (
                            SELECT TOP 1 CASE WHEN @Lang = 'ar' THEN i3.ITEM_NAME2 ELSE i3.ITEM_NAME1 END
                            FROM dbo.AppointmentData a3
                            INNER JOIN dbo.ITEM i3 ON i3.ITEM_ID = a3.ItemId
                            WHERE a3.Id = ib.AppointmentId
                        ))                                    AS AllServicesName
                    FROM InvBase ib
                    LEFT JOIN InvFullPaid  fp  ON fp.InvoiceId  = ib.InvoiceId
                    LEFT JOIN InvLastPayType lp ON lp.AppointmentId = ib.AppointmentId
                    LEFT JOIN InvServices  svc ON svc.InvoiceId     = ib.InvoiceId
                ),
                Tx AS (
                    SELECT
                        'CHK-' + CAST(ia.InvoiceId AS varchar(20)) AS TransactionId,
                        'CHECKOUT'                AS TransactionType,
                        ia.InvoiceNumber,
                        c.CUSTOMER_NAME           AS CustomerName,
                        CASE WHEN @Lang = 'ar' THEN s.ArabicName ELSE s.EnglishName END AS StaffName,
                        ia.AllServicesName        AS ServiceName,
                        ia.NonDepositNonWalletPaid AS Amount,
                        ia.LastPaymentTypeName    AS PaymentTypeName,
                        ia.PaymentBreakdownJson   AS PaymentBreakdownJson,
                        ia.AppointmentId          AS AppointmentId,
                        ia.CreatedAt              AS TxAt,
                        'completed'               AS Status,
                        ai.PackageOfferId,
                        ai.PackageOfferName,
                        ai.PackageOfferPrice,
                        ISNULL(ai.IsFullyRefunded, 0) AS IsFullyRefunded,
                        ISNULL(ai.IsVoid, 0)          AS IsVoid,
                        CASE WHEN @Lang = 'ar' THEN dt.NameAr ELSE dt.NameEn END AS DeliveryTypeName,
                        dt.IsDelivery                 AS IsDelivery,
                        ai.DeliveryDate               AS DeliveryDate,
                        ISNULL(ai.DeliveryCharge, 0)  AS DeliveryCharge,
                        CAST(NULL AS NVARCHAR(20))    AS WalletAdjustType,
                        CAST(0 AS DECIMAL(18,3))      AS WalletWaivedAmount,
                        CAST(NULL AS INT)             AS WalletSubscriptionId,
                        CAST(0 AS BIT)                AS WalletClosed
                    FROM InvAmounts ia
                    INNER JOIN dbo.AppointmentData a ON a.Id = ia.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
                    LEFT  JOIN dbo.STAFF s           ON s.Id = a.StaffId
                    LEFT JOIN dbo.AppointmentInvoices ai ON ai.AppointmentId = ia.AppointmentId
                    LEFT JOIN dbo.DeliveryType dt    ON dt.Id = ai.DeliveryTypeId
                    WHERE ia.NonDepositNonWalletPaid > 0

                    UNION ALL

                    SELECT
                        'DEP-' + CAST(ap.Id AS varchar(20)),
                        'DEPOSIT', NULL,
                        c.CUSTOMER_NAME,
                        CASE WHEN @Lang = 'ar' THEN s.ArabicName ELSE s.EnglishName END,
                        CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END,
                        ap.Amount,
                        ISNULL(CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2 ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END, '-'),
                        NULL,
                        NULL,
                        ap.PaidAt,
                        CASE WHEN a.CheckoutStatus = 'checked_out' THEN 'completed' ELSE 'pending' END,
                        CAST(NULL AS INT)            AS PackageOfferId,
                        CAST(NULL AS NVARCHAR(255))  AS PackageOfferName,
                        CAST(NULL AS DECIMAL(18,3))  AS PackageOfferPrice,
                        CAST(0 AS BIT)              AS IsFullyRefunded,
                        CAST(0 AS BIT)              AS IsVoid,
                        CAST(NULL AS NVARCHAR(100))  AS DeliveryTypeName,
                        CAST(NULL AS BIT)            AS IsDelivery,
                        CAST(NULL AS DATETIME2(0))   AS DeliveryDate,
                        CAST(0 AS DECIMAL(18,3))     AS DeliveryCharge,
                        CAST(NULL AS NVARCHAR(20))   AS WalletAdjustType,
                        CAST(0 AS DECIMAL(18,3))     AS WalletWaivedAmount,
                        CAST(NULL AS INT)            AS WalletSubscriptionId,
                        CAST(0 AS BIT)               AS WalletClosed
                    FROM dbo.AppointmentPayments ap
                    INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
                    LEFT  JOIN dbo.STAFF s           ON s.Id = a.StaffId
                    INNER JOIN dbo.ITEM i            ON i.ITEM_ID = a.ItemId
                    LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                    WHERE a.BranchId = @BranchId
                      AND ap.PaymentAs = 'DEPOSIT'
                      AND ap.IsWalletPayment = 0
                      AND ap.PaidAt >= @DateStart AND ap.PaidAt < @DateEnd
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)

                    UNION ALL

                    SELECT
                        'WAL-' + CAST(sp.Id AS varchar(20)),
                        'WALLET_LOAD', NULL,
                        c.CUSTOMER_NAME, NULL, st.NAME,
                        sp.PAYMENT_AMOUNT,
                        ISNULL(CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2 ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END, '-'),
                        NULL,
                        NULL,
                        sp.PAYMENT_DATE,
                        'completed',
                        CAST(NULL AS INT)            AS PackageOfferId,
                        CAST(NULL AS NVARCHAR(255))  AS PackageOfferName,
                        CAST(NULL AS DECIMAL(18,3))  AS PackageOfferPrice,
                        CAST(0 AS BIT)              AS IsFullyRefunded,
                        CAST(0 AS BIT)              AS IsVoid,
                        CAST(NULL AS NVARCHAR(100))  AS DeliveryTypeName,
                        CAST(NULL AS BIT)            AS IsDelivery,
                        CAST(NULL AS DATETIME2(0))   AS DeliveryDate,
                        CAST(0 AS DECIMAL(18,3))     AS DeliveryCharge,
                        CAST(NULL AS NVARCHAR(20))   AS WalletAdjustType,
                        CAST(0 AS DECIMAL(18,3))     AS WalletWaivedAmount,
                        CAST(NULL AS INT)            AS WalletSubscriptionId,
                        CAST(0 AS BIT)               AS WalletClosed
                    FROM dbo.SubscriptionPayment sp
                    INNER JOIN dbo.Subscriptions s ON s.Id = sp.SubscriptionId
                    INNER JOIN dbo.CUSTOMER c      ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                    INNER JOIN dbo.SUBS_TYPE st    ON st.ID = s.SubTypeId
                    LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = sp.PAYMENT_TYPE_ID
                    WHERE c.BRANCH_ID = @BranchId
                      AND sp.DELETED = 0
                      AND sp.PAYMENT_DATE >= @DateStart AND sp.PAYMENT_DATE < @DateEnd

                    UNION ALL

                    SELECT
                        COALESCE(cp.InvoiceNumber, 'PKG-' + CAST(pp.Id AS varchar(20))),
                        'PACKAGE_SALE', NULL,
                        c.CUSTOMER_NAME, NULL, CASE WHEN @Lang = 'ar' THEN pkg.ArabicName ELSE pkg.EnglishName END,
                        pp.PaymentAmount,
                        ISNULL(CASE WHEN @Lang = 'ar' THEN pt.INVOICE_PAYMENT_TYPE_NAME2 ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END, '-'),
                        NULL,
                        NULL,
                        pp.AddedDate,
                        'completed',
                        CAST(NULL AS INT)            AS PackageOfferId,
                        CAST(NULL AS NVARCHAR(255))  AS PackageOfferName,
                        CAST(NULL AS DECIMAL(18,3))  AS PackageOfferPrice,
                        CAST(0 AS BIT)              AS IsFullyRefunded,
                        CAST(0 AS BIT)              AS IsVoid,
                        CAST(NULL AS NVARCHAR(100))  AS DeliveryTypeName,
                        CAST(NULL AS BIT)            AS IsDelivery,
                        CAST(NULL AS DATETIME2(0))   AS DeliveryDate,
                        CAST(0 AS DECIMAL(18,3))     AS DeliveryCharge,
                        CAST(NULL AS NVARCHAR(20))   AS WalletAdjustType,
                        CAST(0 AS DECIMAL(18,3))     AS WalletWaivedAmount,
                        CAST(NULL AS INT)            AS WalletSubscriptionId,
                        CAST(0 AS BIT)               AS WalletClosed
                    FROM dbo.CustomerPackagePayments pp
                    INNER JOIN dbo.CustomerPackages cp  ON cp.Id = pp.CustomerPackageId
                    INNER JOIN dbo.Packages pkg         ON pkg.Id = cp.PackageId
                    INNER JOIN dbo.CUSTOMER c           ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                    LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = pp.PaymentTypeId
                    WHERE c.BRANCH_ID = @BranchId
                      AND ISNULL(pp.Deleted, 0) = 0
                      AND pp.AddedDate >= @DateStart AND pp.AddedDate < @DateEnd

                    UNION ALL

                    SELECT
                        'RFD-' + CAST(rt.Id AS varchar(20)),
                        'REFUND',
                        ai.InvoiceNumber,
                        c.CUSTOMER_NAME,
                        NULL,                    -- StaffName
                        NULL,                    -- ServiceName
                        -rt.RefundAmount,        -- negative amount
                        rt.RefundType,           -- used as PaymentTypeName for REFUND rows
                        NULL,                    -- PaymentBreakdownJson
                        NULL,                    -- AppointmentId
                        rt.ProcessedAt,
                        'refunded',               -- Status
                        CAST(NULL AS INT)            AS PackageOfferId,
                        CAST(NULL AS NVARCHAR(255))  AS PackageOfferName,
                        CAST(NULL AS DECIMAL(18,3))  AS PackageOfferPrice,
                        CAST(0 AS BIT)              AS IsFullyRefunded,
                        CAST(0 AS BIT)              AS IsVoid,
                        CAST(NULL AS NVARCHAR(100))  AS DeliveryTypeName,
                        CAST(NULL AS BIT)            AS IsDelivery,
                        CAST(NULL AS DATETIME2(0))   AS DeliveryDate,
                        CAST(0 AS DECIMAL(18,3))     AS DeliveryCharge,
                        CAST(NULL AS NVARCHAR(20))   AS WalletAdjustType,
                        CAST(0 AS DECIMAL(18,3))     AS WalletWaivedAmount,
                        CAST(NULL AS INT)            AS WalletSubscriptionId,
                        CAST(0 AS BIT)               AS WalletClosed
                    FROM dbo.RefundTransactions rt
                    INNER JOIN dbo.AppointmentInvoices ai ON ai.Id = rt.InvoiceId
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = rt.CustomerId
                    WHERE rt.BranchId = @BranchId
                      AND rt.ProcessedAt >= @DateStart AND rt.ProcessedAt < @DateEnd
                      AND rt.Deleted = 0

                    UNION ALL

                    -- ── WALLET SETTLEMENT (Adjust) ────────────────────────────
                    -- COLLECT: an overdrawn wallet was paid off  → positive amount.
                    -- REFUND : leftover credit was handed back   → negative amount,
                    --          which is what makes the row render red client-side.
                    -- A pure waiver (SettledAmount = 0) still produces a row: the
                    -- write-off is a real event the day's log has to account for.
                    SELECT
                        'WADJ-' + CAST(wa.Id AS varchar(20)),
                        'WALLET_ADJUST',
                        NULL,
                        c.CUSTOMER_NAME,
                        NULL,                                  -- StaffName
                        st.NAME,                               -- ServiceName = wallet type
                        CASE WHEN wa.AdjustType = 'REFUND'
                             THEN -wa.SettledAmount
                             ELSE  wa.SettledAmount END,
                        ISNULL(
                            CASE WHEN wa.AdjustType = 'REFUND'
                                 THEN wa.RefundMethod
                                 ELSE CASE WHEN @Lang = 'ar'
                                           THEN pt.INVOICE_PAYMENT_TYPE_NAME2
                                           ELSE pt.INVOICE_PAYMENT_TYPE_NAME1 END
                            END, '-'),
                        NULL,                                  -- PaymentBreakdownJson
                        NULL,                                  -- AppointmentId
                        wa.AddedDate,
                        CASE WHEN wa.AdjustType = 'REFUND' THEN 'refunded' ELSE 'completed' END,
                        CAST(NULL AS INT)            AS PackageOfferId,
                        CAST(NULL AS NVARCHAR(255))  AS PackageOfferName,
                        CAST(NULL AS DECIMAL(18,3))  AS PackageOfferPrice,
                        CAST(0 AS BIT)               AS IsFullyRefunded,
                        CAST(0 AS BIT)               AS IsVoid,
                        CAST(NULL AS NVARCHAR(100))  AS DeliveryTypeName,
                        CAST(NULL AS BIT)            AS IsDelivery,
                        CAST(NULL AS DATETIME2(0))   AS DeliveryDate,
                        CAST(0 AS DECIMAL(18,3))     AS DeliveryCharge,
                        wa.AdjustType                AS WalletAdjustType,
                        ISNULL(wa.WaivedAmount, 0)   AS WalletWaivedAmount,
                        wa.SubscriptionId            AS WalletSubscriptionId,
                        ISNULL(wa.ClosedWallet, 0)   AS WalletClosed
                    FROM dbo.WalletAdjustments wa
                    INNER JOIN dbo.Subscriptions s2 ON s2.Id = wa.SubscriptionId
                    INNER JOIN dbo.SUBS_TYPE st     ON st.ID = s2.SubTypeId
                    INNER JOIN dbo.CUSTOMER c       ON c.CUSTOMER_REF_GUIDE = wa.CustomerRef
                    LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = wa.PaymentTypeId
                    WHERE ISNULL(wa.BranchId, c.BRANCH_ID) = @BranchId
                      AND wa.Deleted = 0
                      AND wa.AddedDate >= @DateStart AND wa.AddedDate < @DateEnd
                )
                SELECT
                    TransactionId, TransactionType, InvoiceNumber, CustomerName,
                    StaffName, ServiceName, Amount, PaymentTypeName,
                    PaymentBreakdownJson,
                    AppointmentId,
                    CONVERT(varchar(5), DATEADD(HOUR, @TzOffset, TxAt), 108) AS [Time],
                    Status,
                    PackageOfferId,
                    PackageOfferName,
                    PackageOfferPrice,
                    IsFullyRefunded,
                    IsVoid,
                    DeliveryTypeName,
                    IsDelivery,
                    -- DeliveryDate is stored branch-local already (POS writes branch-local);
                    -- pass through untouched.
                    DeliveryDate,
                    DeliveryCharge,
                    WalletAdjustType,
                    WalletWaivedAmount,
                    WalletSubscriptionId,
                    WalletClosed
                FROM Tx
                ORDER BY TxAt DESC
                OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds)
                    .Select(r => {
                        var breakdown = new List<TransactionPaymentBreakdownDto>();
                        try
                        {
                            var json = (string?)r.PaymentBreakdownJson;
                            if (!string.IsNullOrEmpty(json) && json != "[]")
                            {
                                // parse manually أو استخدم System.Text.Json
                                var doc = JsonDocument.Parse(json);
                                foreach (var el in doc.RootElement.EnumerateArray())
                                {
                                    breakdown.Add(new TransactionPaymentBreakdownDto(
                                        PaymentTypeName: el.GetProperty("n").GetString() ?? "-",
                                        Amount: el.GetProperty("a").GetDecimal()
                                    ));
                                }
                            }
                        }
                        catch { }

                        return new DashboardTransactionDto(
                            TransactionId: (string)r.TransactionId,
                            TransactionType: (string)r.TransactionType,
                            InvoiceNumber: (string?)r.InvoiceNumber,
                            CustomerName: (string)(r.CustomerName ?? ""),
                            StaffName: (string?)r.StaffName,
                            ServiceName: (string?)r.ServiceName,
                            Amount: (decimal)r.Amount,
                            PaymentTypeName: (string)(r.PaymentTypeName ?? "-"),
                            Time: (string)(r.Time ?? "00:00"),
                            Status: (string)r.Status,
                            PaymentBreakdown: breakdown,
                            AppointmentId: r.AppointmentId != null ? (int?)r.AppointmentId : null,
                            RefundType: (string)r.TransactionType == "REFUND"
                                      ? (string)r.PaymentTypeName   // for REFUND rows, PaymentTypeName carries RefundType
                                      : null,
                            PackageOfferId: r.PackageOfferId is DBNull || r.PackageOfferId == null
                                ? (int?)null
                                : (int?)Convert.ToInt32(r.PackageOfferId),

                            PackageOfferName: r.PackageOfferName is DBNull || r.PackageOfferName == null
                                ? (string?)null
                                : (string?)r.PackageOfferName,

                            PackageOfferPrice: r.PackageOfferPrice is DBNull || r.PackageOfferPrice == null
                                ? (decimal?)null
                                : (decimal?)Convert.ToDecimal(r.PackageOfferPrice),
                            IsFullyRefunded: r.IsFullyRefunded != null && (bool)r.IsFullyRefunded,
                            IsVoid: r.IsVoid != null && (bool)r.IsVoid,
                            DeliveryTypeName: r.DeliveryTypeName is DBNull || r.DeliveryTypeName == null
                                ? (string?)null : (string?)r.DeliveryTypeName,
                            IsDelivery: r.IsDelivery is DBNull || r.IsDelivery == null
                                ? (bool?)null : (bool?)r.IsDelivery,
                            DeliveryDate: r.DeliveryDate is DBNull || r.DeliveryDate == null
                                ? (DateTime?)null : (DateTime?)r.DeliveryDate,
                            DeliveryCharge: r.DeliveryCharge is DBNull || r.DeliveryCharge == null
                                ? 0m : Convert.ToDecimal(r.DeliveryCharge),
                            // ── Wallet settlement ──
                            WalletAdjustType: r.WalletAdjustType is DBNull || r.WalletAdjustType == null
                                ? (string?)null : (string?)r.WalletAdjustType,
                            WalletWaivedAmount: r.WalletWaivedAmount is DBNull || r.WalletWaivedAmount == null
                                ? 0m : Convert.ToDecimal(r.WalletWaivedAmount),
                            WalletSubscriptionId: r.WalletSubscriptionId is DBNull || r.WalletSubscriptionId == null
                                ? (int?)null : Convert.ToInt32(r.WalletSubscriptionId),
                            WalletClosed: r.WalletClosed != null && r.WalletClosed is not DBNull
                                && Convert.ToInt32(r.WalletClosed) == 1
                        );
                    }).ToList();

                // ---------- 2D: Staff Performance + per-staff clients ----------


                var staffRows = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    s.Id            AS StaffId,
                    CASE WHEN @Lang = 'ar' THEN s.ArabicName ELSE s.EnglishName END   AS StaffName,
                    COUNT(DISTINCT a.Id)                                                    AS AppointmentCount,
                    SUM(CASE WHEN a.Status = 'completed' THEN 1 ELSE 0 END)                AS CompletedCount,
                    SUM(CASE WHEN a.Status = 'cancelled' THEN 1 ELSE 0 END)                AS CancelledCount,
                    SUM(CASE WHEN a.Status = 'no-show'   THEN 1 ELSE 0 END)                AS NoShowCount,
                    ISNULL(SUM(
                    CASE WHEN a.StartTime IS NOT NULL AND a.EndTime IS NOT NULL
                              AND a.Status != 'cancelled'
                         THEN DATEDIFF(MINUTE, 
                              CAST(a.StartTime AS time), 
                              CAST(a.EndTime AS time))
                         ELSE 0
                    END), 0)  AS TotalWorkMinutes,
                    ISNULL(SUM(
                        CASE WHEN a.CheckoutStatus = 'checked_out'
                             THEN a.DiscountedUnitPrice
                             ELSE 0
                        END), 0)                                                            AS TotalRevenue
                FROM dbo.STAFF s
                INNER JOIN (
                    -- الـ appointments الأصلية (excluding fully-refunded lines)
                    -- PERF: فلاتر الفرع (BranchId + المدى الزمني) كانت في شرط الـ JOIN بالخارج،
                    -- فكان الـ engine يحسب الـ correlated subqueries لكل صف في الجدول كله.
                    -- نزّلناها جوه كل فرع — نفس النتيجة تماماً لأنه INNER JOIN على نفس الأعمدة.
                    SELECT
                        a.Id,
                        a.StaffId,
                        a.BranchId,
                        a.AppointmentDate,
                        a.Status,
                        a.CheckoutStatus,
                        a.StartTime,
                        a.EndTime,
                        -- Use the line's DiscountedUnitPrice if available (reflects refund reductions),
                        -- else use the appointment's own value
                        ISNULL((
                            SELECT TOP 1 ail.DiscountedUnitPrice
                            FROM dbo.AppointmentInvoiceLines ail
                            WHERE ail.AppointmentId = a.Id
                              AND ISNULL(ail.IsRefunded, 0) = 0
                            ORDER BY ail.Id
                        ), a.DiscountedUnitPrice) AS DiscountedUnitPrice
                    FROM dbo.AppointmentData a
                    WHERE a.CheckoutStatus = 'checked_out'   -- only actually-paid appointments count
                      AND a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                      AND (NOT EXISTS (
                        -- Exclude appointment if ALL its invoice lines are refunded
                        SELECT 1 FROM dbo.AppointmentInvoiceLines ail2
                        WHERE ail2.AppointmentId = a.Id
                    ) OR EXISTS (
                        -- Include only if at least one non-refunded line remains
                        SELECT 1 FROM dbo.AppointmentInvoiceLines ail3
                        WHERE ail3.AppointmentId = a.Id
                          AND ISNULL(ail3.IsRefunded, 0) = 0
                    ))

                    UNION ALL

                    -- الـ checkout items الإضافية (خدمات أضيفت وقت الـ checkout)
                    SELECT
                        aci.Id,
                        aci.StaffId,
                        a.BranchId,
                        a.AppointmentDate,
                        a.Status,
                        a.CheckoutStatus,
                        NULL            AS StartTime,
                        NULL            AS EndTime,
                        aci.DiscountedUnitPrice
                    FROM dbo.AppointmentCheckoutItems aci
                    INNER JOIN dbo.AppointmentData a ON a.Id = aci.AppointmentId
                    WHERE ISNULL(aci.IsRefunded, 0) = 0
                      AND a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly

                    UNION ALL

                    -- الـ package sessions المقدّمة مباشرة (بدون appointment)
                    SELECT
                        cps.Id,
                        cps.StaffId,
                        c2.BRANCH_ID                    AS BranchId,
                        CAST(cps.ServedDate AS DATE)    AS AppointmentDate,
                        'completed'                     AS Status,
                        'checked_out'                   AS CheckoutStatus,
                        NULL                            AS StartTime,
                        NULL                            AS EndTime,
                        cps.ItemPriceInPackage          AS DiscountedUnitPrice
                    FROM dbo.CustomerPackageSessions cps
                    INNER JOIN dbo.CustomerPackages  cp  ON cp.Id = cps.CustomerPackageId
                    INNER JOIN dbo.CUSTOMER          c2  ON c2.CUSTOMER_REF_GUIDE = cp.CustomerRef
                    WHERE cps.StaffId IS NOT NULL
                      AND ISNULL(cps.Served, 0) = 1
                      AND cps.AppointmentId IS NULL
                      AND ISNULL(cps.Deleted, 0) = 0
                      AND c2.BRANCH_ID = @BranchId
                      AND cps.ServedDate >= @FromDateOnly
                      AND cps.ServedDate <  DATEADD(DAY, 1, @ToDateOnly)

                ) a ON a.StaffId = s.Id
                   AND a.BranchId = @BranchId
                   AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                WHERE s.Deleted = 0
                  AND s.Active = 1
                  AND (s.BranchId IS NULL OR s.BranchId = @BranchId)
                  AND (@StaffId IS NULL OR s.Id = @StaffId)
                GROUP BY s.Id, s.EnglishName, s.ArabicName
                HAVING COUNT(DISTINCT a.Id) > 0
                ORDER BY TotalRevenue DESC
                OPTION (RECOMPILE);",
                p, commandTimeout: CmdTimeoutSeconds).ToList();

                var clientRows = SqlMapper.Query<dynamic>(conn, @"
                -- الـ appointments الأصلية
                SELECT
                    a.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END                            AS ServiceName,
                    CASE
                        -- OFFER package: حساب النصيب النسبي
                        WHEN ai.PackageOfferId IS NOT NULL
                             AND ai.PackageOfferPrice IS NOT NULL
                             AND ai.PackageOfferPrice > 0
                             AND pkgTotal.OriginalTotal > 0
                        THEN ROUND(
                            (iu.ITEM_UNIT_PRICE / pkgTotal.OriginalTotal) * ai.PackageOfferPrice,
                            3)
                        WHEN a.DiscountedUnitPrice = 0
                             AND EXISTS (
                                 SELECT 1 FROM dbo.CustomerPackageSessions cps2
                                 WHERE cps2.AppointmentId = a.Id
                                   AND ISNULL(cps2.Deleted, 0) = 0
                             )
                        THEN ISNULL((
                            SELECT TOP 1 cps3.ItemPriceInPackage
                            FROM dbo.CustomerPackageSessions cps3
                            WHERE cps3.AppointmentId = a.Id
                              AND ISNULL(cps3.Deleted, 0) = 0
                            ORDER BY cps3.Id
                        ), iu.ITEM_UNIT_PRICE)
                        ELSE a.DiscountedUnitPrice
                    END AS Amount,
                    CONVERT(varchar(5), a.StartTime, 108)   AS [Time],
                    ai.InvoiceNumber                        AS InvoiceNumber
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM     i ON i.ITEM_ID     = a.ItemId
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_ID = a.ItemId AND iu.UNIT_ID = a.UnitId
                -- ربط الـ invoice للـ OFFER
                OUTER APPLY (
                    -- PERF: شرط الـ OR اتحول لـ UNION قابل للـ seek — نفس الصفوف ونفس TOP 1 by Id DESC.
                    SELECT TOP 1 x.Id AS InvoiceId, x.PackageOfferId, x.PackageOfferPrice, x.InvoiceNumber
                    FROM (
                        SELECT inv.Id, inv.PackageOfferId, inv.PackageOfferPrice, inv.InvoiceNumber
                        FROM dbo.AppointmentInvoices inv
                        WHERE inv.AppointmentId = a.Id
                        UNION
                        SELECT inv2.Id, inv2.PackageOfferId, inv2.PackageOfferPrice, inv2.InvoiceNumber
                        FROM dbo.AppointmentInvoiceLines ail
                        INNER JOIN dbo.AppointmentInvoices inv2 ON inv2.Id = ail.InvoiceId
                        WHERE ail.AppointmentId = a.Id
                    ) x
                    ORDER BY x.Id DESC
                ) ai
                -- مجموع الأسعار الأصلية لكل services في نفس الـ invoice
                -- PERF: كان يكرر نفس بحث الـ invoice داخل subquery ثانٍ؛ الآن يستخدم ai.InvoiceId مباشرة.
                OUTER APPLY (
                    SELECT ISNULL(SUM(iu2.ITEM_UNIT_PRICE), 0) AS OriginalTotal
                    FROM dbo.AppointmentInvoiceLines ail2
                    INNER JOIN dbo.AppointmentData a2 ON a2.Id = ail2.AppointmentId
                    INNER JOIN dbo.ITEM_UNIT iu2 ON iu2.ITEM_ID = a2.ItemId AND iu2.UNIT_ID = a2.UnitId
                    WHERE ail2.InvoiceId = ai.InvoiceId
                ) pkgTotal
                WHERE a.BranchId        = @BranchId
                  AND a.AppointmentDate  BETWEEN @FromDateOnly AND @ToDateOnly
                  AND a.StaffId IS NOT NULL          -- un-staffed POS sales have no staff to attribute to
                  AND a.CheckoutStatus = 'checked_out'   -- only actually-paid appointments count
                  AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                  -- Exclude appointment if it has invoice lines and ALL of them are refunded
                  AND (
                      NOT EXISTS (
                          SELECT 1 FROM dbo.AppointmentInvoiceLines ail_chk
                          WHERE ail_chk.AppointmentId = a.Id
                      )
                      OR EXISTS (
                          SELECT 1 FROM dbo.AppointmentInvoiceLines ail_chk2
                          WHERE ail_chk2.AppointmentId = a.Id
                            AND ISNULL(ail_chk2.IsRefunded, 0) = 0
                      )
                  )

                UNION ALL

                -- الـ checkout items الإضافية
                SELECT
                    aci.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END                            AS ServiceName,
                    aci.DiscountedUnitPrice                 AS Amount,
                    NULL                                    AS [Time],
                    (SELECT TOP 1 inv_ci.InvoiceNumber
                     FROM dbo.AppointmentInvoices inv_ci
                     WHERE inv_ci.AppointmentId = a.Id
                     ORDER BY inv_ci.Id DESC)               AS InvoiceNumber
                FROM dbo.AppointmentCheckoutItems aci
                INNER JOIN dbo.AppointmentData a ON a.Id  = aci.AppointmentId
                INNER JOIN dbo.CUSTOMER        c ON c.CUSTOMER_ID = aci.CustomerId
                INNER JOIN dbo.ITEM            i ON i.ITEM_ID     = aci.ItemId
                WHERE a.BranchId        = @BranchId
                  AND a.AppointmentDate  BETWEEN @FromDateOnly AND @ToDateOnly
                  AND ISNULL(aci.IsRefunded, 0) = 0
                  AND aci.StaffId IS NOT NULL        -- skip un-staffed checkout items
                  AND (@StaffId IS NULL OR aci.StaffId = @StaffId)

                UNION ALL

                -- الـ package sessions المقدّمة مباشرة (بدون appointment)
                SELECT
                    cps.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END                            AS ServiceName,
                    CASE
                        WHEN ISNULL(cps.ItemPriceInPackage, 0) > 0
                        THEN cps.ItemPriceInPackage
                        ELSE iu.ITEM_UNIT_PRICE
                    END AS Amount,
                    NULL                                    AS [Time],
                    CAST(NULL AS NVARCHAR(50))              AS InvoiceNumber
                FROM dbo.CustomerPackageSessions cps
                INNER JOIN dbo.CustomerPackages  cp  ON cp.Id               = cps.CustomerPackageId
                INNER JOIN dbo.CUSTOMER          c   ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                INNER JOIN dbo.PackageItems      pi  ON pi.Id               = cps.PackageItemId
                INNER JOIN dbo.ITEM_UNIT         iu  ON iu.ITEM_UNIT_ID      = pi.ItemUnitId
                INNER JOIN dbo.ITEM              i   ON i.ITEM_ID            = iu.ITEM_ID
                WHERE cps.StaffId IS NOT NULL
                  AND ISNULL(cps.Served, 0) = 1
                  AND cps.AppointmentId IS NULL
                  AND ISNULL(cps.Deleted, 0) = 0
                  AND c.BRANCH_ID             = @BranchId
                  -- PERF: CAST(col AS DATE) يمنع الـ index seek؛ النطاق النصف-مفتوح يعطي نفس الصفوف
                  AND cps.ServedDate >= @FromDateOnly
                  AND cps.ServedDate <  DATEADD(DAY, 1, @ToDateOnly)
                  AND (@StaffId IS NULL OR cps.StaffId = @StaffId)

                ORDER BY StaffId, [Time]
                OPTION (RECOMPILE);",
                                p, commandTimeout: CmdTimeoutSeconds).ToList();

                var clientsByStaff = clientRows
                    .Where(r => r.StaffId != null)          // defensive: never group a null staff id
                    .GroupBy(r => (int)r.StaffId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => new StaffClientDto(
                            CustomerName: (string)(r.CustomerName ?? ""),
                            ServiceName: (string)(r.ServiceName ?? ""),
                            Amount: (decimal)r.Amount,
                            Time: (string)(r.Time ?? "00:00"),
                            InvoiceNumber: r.InvoiceNumber is DBNull || r.InvoiceNumber == null
                                ? (string?)null : (string?)r.InvoiceNumber
                        )).ToList()
                    );

                // Utilization is measured against the total available working minutes
                // across the whole selected period (workday * number of days).
                int numDays = Math.Max(1, (int)(toDateOnly.Date - fromDateOnly.Date).TotalDays + 1);
                int periodWorkMinutes = workdayMinutes * numDays;

                var staffPerformance = staffRows.Select(r =>
                {
                    int sId = (int)r.StaffId;
                    int totalWork = (int)r.TotalWorkMinutes;
                    decimal util = periodWorkMinutes > 0
                        ? Math.Round((decimal)totalWork * 100m / periodWorkMinutes, 1)
                        : 0m;
                    if (util > 100m) util = 100m;

                    var staffClients = clientsByStaff.TryGetValue(sId, out var csVal)
                    ? csVal
                    : new List<StaffClientDto>();

                    return new StaffPerformanceDto(
                        StaffId: sId,
                        StaffName: (string)(r.StaffName ?? ""),
                        StaffColor: null,
                        AppointmentCount: (int)r.AppointmentCount,
                        CompletedCount: (int)r.CompletedCount,
                        CancelledCount: (int)r.CancelledCount,
                        NoShowCount: (int)r.NoShowCount,
                        TotalWorkMinutes: totalWork,
                        TotalRevenue: staffClients.Count > 0
                            ? staffClients.Sum(c => c.Amount)
                            : (decimal)r.TotalRevenue,
                        Utilization: util,
                        Clients: staffClients
                    );
                }).ToList();

                // ---------- 2E: Appointment Stats + Hourly ----------
                var stats = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    COUNT(*)                                              AS TotalAppointments,
                    SUM(CASE WHEN Status = 'completed' THEN 1 ELSE 0 END) AS CompletedCount,
                    SUM(CASE WHEN Status = 'cancelled' THEN 1 ELSE 0 END) AS CancelledCount,
                    SUM(CASE WHEN Status = 'no-show'   THEN 1 ELSE 0 END) AS NoShowCount,
                    SUM(CASE WHEN Status = 'scheduled' THEN 1 ELSE 0 END) AS ScheduledCount,
                    SUM(CASE WHEN IsOnlineBooking = 1  THEN 1 ELSE 0 END) AS OnlineBookingCount,
                    SUM(CASE WHEN ServiceType = 'SALON' THEN 1 ELSE 0 END) AS SalonCount,
                    SUM(CASE WHEN ServiceType = 'HOME'  THEN 1 ELSE 0 END) AS HomeCount
                FROM dbo.AppointmentData
                WHERE BranchId = @BranchId
                  AND AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                  AND (@StaffId IS NULL OR StaffId = @StaffId)
                OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds).FirstOrDefault();

                var hourlyRows = SqlMapper.Query<dynamic>(conn, @"
                ;WITH HourBuckets AS (
                    SELECT
                        DATEPART(HOUR, a.StartTime) AS Hour,
                        CASE WHEN @Lang = 'ar' THEN i.ITEM_NAME2 ELSE i.ITEM_NAME1 END                AS ServiceName,
                        COUNT(*)                    AS Cnt
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = a.ItemId
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    GROUP BY DATEPART(HOUR, a.StartTime), i.ITEM_NAME1, i.ITEM_NAME2
                ),
                HourTotals AS (
                    SELECT Hour, SUM(Cnt) AS Total FROM HourBuckets GROUP BY Hour
                ),
                TopPerHour AS (
                    SELECT Hour, ServiceName,
                           ROW_NUMBER() OVER (PARTITION BY Hour ORDER BY Cnt DESC) AS rn
                    FROM HourBuckets
                )
                SELECT ht.Hour, ht.Total AS Count, tp.ServiceName AS TopService
                FROM HourTotals ht
                LEFT JOIN TopPerHour tp ON tp.Hour = ht.Hour AND tp.rn = 1
                ORDER BY ht.Hour
                OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds).ToList();

                // Build full 0-23 hour grid (fills missing hours with 0)
                var hourMap = hourlyRows
                    .Where(r => r.Hour != null)            // skip rows with no start time (NULL hour)
                    .ToDictionary(r => (int)r.Hour, r => r);
                var hourly = new List<HourlyDistributionDto>();
                for (int h = 0; h < 24; h++)
                {
                    if (hourMap.TryGetValue(h, out var row))
                    {
                        hourly.Add(new HourlyDistributionDto(
                            Hour: h,
                            Count: (int)row.Count,
                            TopService: (string?)row.TopService
                        ));
                    }
                    else
                    {
                        hourly.Add(new HourlyDistributionDto(h, 0, null));
                    }
                }

                var apptStats = new AppointmentStatsDto(
                    TotalAppointments: stats != null ? (int)stats.TotalAppointments : 0,
                    CompletedCount: stats != null ? (int)(stats.CompletedCount ?? 0) : 0,
                    CancelledCount: stats != null ? (int)(stats.CancelledCount ?? 0) : 0,
                    NoShowCount: stats != null ? (int)(stats.NoShowCount ?? 0) : 0,
                    ScheduledCount: stats != null ? (int)(stats.ScheduledCount ?? 0) : 0,
                    OnlineBookingCount: stats != null ? (int)(stats.OnlineBookingCount ?? 0) : 0,
                    ByServiceType: new ServiceTypeCountDto(
                        SALON: stats != null ? (int)(stats.SalonCount ?? 0) : 0,
                        HOME: stats != null ? (int)(stats.HomeCount ?? 0) : 0
                    ),
                    HourlyDistribution: hourly
                );

                // ---------- 2F: Service Categories ----------
                var categories = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    CASE WHEN @Lang = 'ar' THEN ac.ArabicName ELSE ac.EnglishName END              AS CategoryName,
                    COUNT(a.Id)                                 AS AppointmentCount,
                    ISNULL(SUM(a.DiscountedUnitPrice), 0)       AS Revenue
                FROM dbo.AppointmentData a
                INNER JOIN dbo.ITEM                  i   ON i.ITEM_ID = a.ItemId
                INNER JOIN dbo.AppointmentCategories ac  ON ac.Id = i.AppointmentCategoryId
                WHERE a.BranchId = @BranchId
                  AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                  AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                  AND ISNULL(ac.Deleted, 0) = 0
                GROUP BY ac.EnglishName, ac.ArabicName
                ORDER BY Revenue DESC
                OPTION (RECOMPILE);",
                p, commandTimeout: CmdTimeoutSeconds)
                .Select(r => new ServiceCategoryBreakdownDto(
                    CategoryName: (string)(r.CategoryName ?? ""),
                    AppointmentCount: (int)r.AppointmentCount,
                    Revenue: (decimal)r.Revenue
                )).ToList();

                // ---------- 2G: Client Insights ----------
                var insightsHeader = SqlMapper.Query<dynamic>(conn, @"
                ;WITH TodayCustomers AS (
                    SELECT DISTINCT a.CustomerId
                    FROM dbo.AppointmentData a
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                )
                SELECT
                    (SELECT COUNT(*) FROM dbo.CUSTOMER c
                        WHERE c.BRANCH_ID = @BranchId
                          AND c.CUSTOMER_CREATED_DATE >= @DateStart
                          AND c.CUSTOMER_CREATED_DATE <  @DateEnd) AS NewCustomersToday,
                    (SELECT COUNT(*) FROM TodayCustomers tc
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = tc.CustomerId
                        WHERE c.CUSTOMER_CREATED_DATE < @DateStart) AS ReturningCustomers,
                    (SELECT COUNT(*) FROM TodayCustomers tc
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = tc.CustomerId
                        WHERE ISNULL(c.LoyaltyBalance, 0) > 0
                           -- PERF: نفس الشرط بالظبط (>= 5) لكنه يتوقف بعد أول 5 صفوف
                           -- بدلاً من عدّ كل مواعيد العميل من بداية التاريخ.
                           OR (SELECT COUNT(*) FROM (
                                   SELECT TOP 5 1 AS one
                                   FROM dbo.AppointmentData a2
                                   WHERE a2.CustomerId = c.CUSTOMER_ID AND a2.BranchId = @BranchId
                               ) t5) >= 5
                    ) AS VIPCustomers
                OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds).FirstOrDefault();

                var topClients = SqlMapper.Query<dynamic>(conn, @"
                ;WITH ApptInvoices AS (
                    -- ربط كل appointment بالـ invoice بتاعها
                    SELECT a.Id AS AppointmentId, a.CustomerId, a.DiscountedUnitPrice,
                           inv.Id AS InvoiceId
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = a.Id
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly

                    UNION

                    SELECT a.Id, a.CustomerId, a.DiscountedUnitPrice,
                           inv.Id AS InvoiceId
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.AppointmentInvoiceLines ail ON ail.AppointmentId = a.Id
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.Id = ail.InvoiceId
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate BETWEEN @FromDateOnly AND @ToDateOnly
                )
                SELECT TOP 5
                    c.CUSTOMER_NAME            AS CustomerName,
                    SUM(ai.DiscountedUnitPrice) AS TotalSpent,
                    COUNT(DISTINCT ai.InvoiceId) AS VisitCount
                FROM ApptInvoices ai
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = ai.CustomerId
                GROUP BY c.CUSTOMER_NAME
                ORDER BY TotalSpent DESC
                OPTION (RECOMPILE);",
                    p, commandTimeout: CmdTimeoutSeconds)
                    .Select(r => new TopClientDto(
                        CustomerName: (string)(r.CustomerName ?? ""),
                        TotalSpent: (decimal)r.TotalSpent,
                        VisitCount: (int)r.VisitCount
                    )).ToList();

                var clientInsights = new ClientInsightsDto(
                    NewCustomersToday: insightsHeader != null ? (int)(insightsHeader.NewCustomersToday ?? 0) : 0,
                    ReturningCustomers: insightsHeader != null ? (int)(insightsHeader.ReturningCustomers ?? 0) : 0,
                    VIPCustomers: insightsHeader != null ? (int)(insightsHeader.VIPCustomers ?? 0) : 0,
                    TopClients: topClients
                );

                var dto = new DashboardSummaryDto(
                    TotalCheckoutRevenue: totalCheckout,
                    TodayDepositRevenue: todayDeposit,
                    PendingFromDeposits: pendingDeposit,
                    WalletRevenue: walletRev,
                    PackagesRevenue: packagesRev,
                    OnlineFullRevenue: onlineFullRev,
                    TotalEffectiveRevenue: totalEffective,
                    PaymentTypeBreakdown: paymentBreakdown,
                    Transactions: transactions,
                    StaffPerformance: staffPerformance,
                    AppointmentStats: apptStats,
                    ServiceCategories: categories,
                    ClientInsights: clientInsights,
                    RefundSummary: refundSummary,
                    Currency: currency,
                    WorkdayMinutes: workdayMinutes,
                    TzOffset: tzOffset,
                    GeneratedAt: DateTime.UtcNow
                );

                return Ok(new ApiResult<DashboardSummaryDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DashboardSummaryDto>(
                    false,
                    $"{ex.GetType().Name}: {ex.Message} | {ex.InnerException?.Message}",
                    null));
            }
        }
    }
}