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

        public DashboardApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // GET /api/dashboard/summary?branchId=1&date=2026-05-01&staffId=
        [HttpGet("summary")]
        public ActionResult<ApiResult<DashboardSummaryDto>> Summary(
            [FromQuery] int branchId,
            [FromQuery] string date,
            [FromQuery] int? staffId = null)
        {
            try
            {
                if (branchId <= 0)
                    return Ok(new ApiResult<DashboardSummaryDto>(false, "branchId is required", null));

                if (!DateTime.TryParseExact(date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
                                return Ok(new ApiResult<DashboardSummaryDto>(false,
                                    "date must be yyyy-MM-dd", null));

                using var conn = sqlConnections.NewByKey("Default");

                // Meta query first, so we can understand tzOffset
                var metaP = new { BranchId = branchId };

                var meta = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'calendarStartHour') AS StartHour,
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'calendarEndHour')   AS EndHour,
                    (SELECT TRY_CAST(SETTING_VALUE AS int) FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'timeZoneOffset')    AS TzOffset,
                    (SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @BranchId)                                AS Currency",
                                metaP).FirstOrDefault();

                int startHour = meta?.StartHour != null ? (int)meta.StartHour : 10;
                int endHour = meta?.EndHour != null ? (int)meta.EndHour : 22;
                string currency = meta?.Currency != null ? (string)meta.Currency : "KWD";
                int workdayMinutes = Math.Max(1, (endHour - startHour) * 60);
                int tzOffset = meta?.TzOffset != null ? (int)meta.TzOffset : 3;

                
                var dateStart = dateOnly.Date.AddHours(-tzOffset);
                var dateEnd = dateStart.AddDays(1);

                var p = new
                {
                    BranchId = branchId,
                    DateStart = dateStart,
                    DateEnd = dateEnd,
                    DateOnly = dateOnly.Date,   // ← Must prefer local date (without offset)
                    StaffId = staffId
                };

                // ---------- 2A: Revenue KPIs ----------
                var kpi = SqlMapper.Query<dynamic>(conn, @"
                    ;WITH
                    CheckoutToday AS (
                        -- بس الـ FULL payments غير الـ Wallet (مش الـ Deposits)
                        SELECT ISNULL(SUM(ap.Amount), 0) AS TotalInvoicePaid
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = ap.AppointmentId
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        WHERE a.BranchId = @BranchId
                          AND inv.CreatedAt >= @DateStart AND inv.CreatedAt < @DateEnd
                          AND ap.IsWalletPayment = 0
                          AND ap.PaymentAs = 'FULL'
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
                        SELECT ISNULL(SUM(sp.PAYMENT_AMOUNT), 0) AS WalletRevenue
                        FROM dbo.SubscriptionPayment sp
                        INNER JOIN dbo.Subscriptions s ON s.Id = sp.SubscriptionId
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = s.CustomerRef
                        WHERE c.BRANCH_ID = @BranchId
                          AND sp.DELETED = 0
                          AND sp.PAYMENT_DATE >= @DateStart AND sp.PAYMENT_DATE < @DateEnd
                    ),
                    PackagesToday AS (
                        SELECT ISNULL(SUM(pp.PaymentAmount), 0) AS PackagesRevenue
                        FROM dbo.CustomerPackagePayments pp
                        INNER JOIN dbo.CustomerPackages cp ON cp.Id = pp.CustomerPackageId
                        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                        WHERE c.BRANCH_ID = @BranchId
                          AND ISNULL(pp.Deleted, 0) = 0
                          AND pp.AddedDate >= @DateStart AND pp.AddedDate < @DateEnd
                    )
                    SELECT
                    c.TotalInvoicePaid AS TotalCheckoutRevenue,
                    d.TodayDepositRevenue,
                    p.PendingFromDeposits,
                    wal.WalletRevenue,
                    pk.PackagesRevenue,
                    (c.TotalInvoicePaid + d.TodayDepositRevenue + wal.WalletRevenue + pk.PackagesRevenue) AS TotalEffectiveRevenue
                FROM CheckoutToday c
                CROSS JOIN DepositsToday d
                CROSS JOIN PendingDeposits p
                CROSS JOIN WalletToday wal
                CROSS JOIN PackagesToday pk;",
                        p).FirstOrDefault();

                decimal totalCheckout = kpi != null ? (decimal)kpi.TotalCheckoutRevenue : 0m;
                decimal todayDeposit = kpi != null ? (decimal)kpi.TodayDepositRevenue : 0m;
                decimal pendingDeposit = kpi != null ? (decimal)kpi.PendingFromDeposits : 0m;
                decimal walletRev = kpi != null ? (decimal)kpi.WalletRevenue : 0m;
                decimal packagesRev = kpi != null ? (decimal)kpi.PackagesRevenue : 0m;
                decimal totalEffective = kpi != null ? (decimal)kpi.TotalEffectiveRevenue : 0m;

                // ---------- 2B: Payment Type Breakdown ----------
                // ---------- 2B: Payment Type Breakdown ----------
                var paymentBreakdown = SqlMapper.Query<dynamic>(conn, @"
                    ;WITH AllPayments AS (
                        SELECT ap.PaymentTypeId, ap.Amount
                        FROM dbo.AppointmentPayments ap
                        INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                        WHERE a.BranchId = @BranchId
                          AND ap.PaidAt >= @DateStart AND ap.PaidAt < @DateEnd
                          AND ap.IsWalletPayment = 0    -- ← استبعد الـ Wallet payments
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
                    )
                    SELECT
                        pt.INVOICE_PAYMENT_TYPE_ID    AS PaymentTypeId,
                        pt.INVOICE_PAYMENT_TYPE_NAME1 AS PaymentTypeName,
                        pt.DocumentName               AS DocumentName,
                        SUM(ap.Amount)                AS Amount
                    FROM AllPayments ap
                    INNER JOIN dbo.INVOICE_PAYMENT_TYPE pt
                        ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                    GROUP BY pt.INVOICE_PAYMENT_TYPE_ID, pt.INVOICE_PAYMENT_TYPE_NAME1, pt.DocumentName
                    HAVING SUM(ap.Amount) > 0
                    ORDER BY SUM(ap.Amount) DESC;",
                    p)
                    .Select(r => new PaymentTypeBreakdownDto(
                        PaymentTypeId: (int)r.PaymentTypeId,
                        PaymentTypeName: (string)(r.PaymentTypeName ?? ""),
                        Amount: (decimal)r.Amount,
                        DocumentName: (string?)r.DocumentName
                    )).ToList();


                // ---------- 2C: Transactions ----------
                var transactions = SqlMapper.Query<dynamic>(conn, @"
                ;WITH
                -- الـ Wallet payments لكل invoice
                InvWallet AS (
                    SELECT ap.AppointmentId, ISNULL(SUM(ap.Amount), 0) AS WalletPaid
                    FROM dbo.AppointmentPayments ap
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = ap.AppointmentId
                    INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                    WHERE a.BranchId = @BranchId
                      AND inv.CreatedAt >= @DateStart AND inv.CreatedAt < @DateEnd
                      AND ap.IsWalletPayment = 1
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    GROUP BY ap.AppointmentId
                ),
                -- الـ FULL non-wallet payments لكل invoice
                InvFullPaid AS (
                    SELECT ap.AppointmentId,
                           ISNULL(SUM(ap.Amount), 0) AS NonDepositNonWalletPaid,
                           MAX(ap.PaidAt)            AS LastFullPaidAt
                    FROM dbo.AppointmentPayments ap
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = ap.AppointmentId
                    INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                    WHERE a.BranchId = @BranchId
                      AND inv.CreatedAt >= @DateStart AND inv.CreatedAt < @DateEnd
                      AND ap.IsWalletPayment = 0
                      AND ap.PaymentAs = 'FULL'
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    GROUP BY ap.AppointmentId
                ),
                -- آخر non-wallet FULL payment type لكل appointment
                InvLastPayType AS (
                SELECT ap.AppointmentId,
                       STRING_AGG(pt.INVOICE_PAYMENT_TYPE_NAME1, ' + ')
                           WITHIN GROUP (ORDER BY ap.PaidAt ASC) AS LastPaymentTypeName,
                       -- JSON array للـ breakdown
                       '[' + STRING_AGG(
                           '{""n"":""' + REPLACE(ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '-'), '""', '') +
                           '"",""a"":' + CAST(ap.Amount AS varchar(20)) + '}',
                           ','
                       ) WITHIN GROUP (ORDER BY ap.PaidAt ASC) + ']' AS PaymentBreakdownJson
                FROM dbo.AppointmentPayments ap
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt
                    ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                WHERE ap.IsWalletPayment = 0
                  AND ap.PaymentAs = 'FULL'
                GROUP BY ap.AppointmentId
                ),
                -- أسماء الـ services لكل invoice (New Sale = متعددة)
                InvServices AS (
                    SELECT ail.InvoiceId,
                           STRING_AGG(i.ITEM_NAME1, ' + ') AS AllServicesName
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = ail.ItemId
                    GROUP BY ail.InvoiceId
                ),
                -- الـ invoices الأساسية
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
                            SELECT TOP 1 i3.ITEM_NAME1
                            FROM dbo.AppointmentData a3
                            INNER JOIN dbo.ITEM i3 ON i3.ITEM_ID = a3.ItemId
                            WHERE a3.Id = ib.AppointmentId
                        ))                                    AS AllServicesName
                    FROM InvBase ib
                    LEFT JOIN InvFullPaid  fp  ON fp.AppointmentId  = ib.AppointmentId
                    LEFT JOIN InvLastPayType lp ON lp.AppointmentId = ib.AppointmentId
                    LEFT JOIN InvServices  svc ON svc.InvoiceId     = ib.InvoiceId
                ),
                Tx AS (
                    SELECT
                        'CHK-' + CAST(ia.InvoiceId AS varchar(20)) AS TransactionId,
                        'CHECKOUT'                AS TransactionType,
                        ia.InvoiceNumber,
                        c.CUSTOMER_NAME           AS CustomerName,
                        s.EnglishName             AS StaffName,
                        ia.AllServicesName        AS ServiceName,
                        ia.NonDepositNonWalletPaid AS Amount,
                        ia.LastPaymentTypeName    AS PaymentTypeName,
                        ia.PaymentBreakdownJson   AS PaymentBreakdownJson,
                        ia.AppointmentId          AS AppointmentId,
                        ia.CreatedAt              AS TxAt,
                        'completed'               AS Status
                    FROM InvAmounts ia
                    INNER JOIN dbo.AppointmentData a ON a.Id = ia.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
                    INNER JOIN dbo.STAFF s           ON s.Id = a.StaffId
                    WHERE ia.NonDepositNonWalletPaid > 0

                    UNION ALL

                    SELECT
                        'DEP-' + CAST(ap.Id AS varchar(20)),
                        'DEPOSIT', NULL,
                        c.CUSTOMER_NAME, s.EnglishName, i.ITEM_NAME1,
                        ap.Amount,
                        ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '-'),
                        NULL,
                        NULL,
                        ap.PaidAt,
                        CASE WHEN a.CheckoutStatus = 'checked_out' THEN 'completed' ELSE 'pending' END
                    FROM dbo.AppointmentPayments ap
                    INNER JOIN dbo.AppointmentData a ON a.Id = ap.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
                    INNER JOIN dbo.STAFF s           ON s.Id = a.StaffId
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
                        ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '-'),
                        NULL,
                        NULL,
                        sp.PAYMENT_DATE,
                        'completed'
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
                        'PKG-' + CAST(pp.Id AS varchar(20)),
                        'PACKAGE_SALE', NULL,
                        c.CUSTOMER_NAME, NULL, pkg.EnglishName,
                        pp.PaymentAmount,
                        ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '-'),
                        NULL,
                        NULL,
                        pp.AddedDate,
                        'completed'
                    FROM dbo.CustomerPackagePayments pp
                    INNER JOIN dbo.CustomerPackages cp  ON cp.Id = pp.CustomerPackageId
                    INNER JOIN dbo.Packages pkg         ON pkg.Id = cp.PackageId
                    INNER JOIN dbo.CUSTOMER c           ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                    LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = pp.PaymentTypeId
                    WHERE c.BRANCH_ID = @BranchId
                      AND ISNULL(pp.Deleted, 0) = 0
                      AND pp.AddedDate >= @DateStart AND pp.AddedDate < @DateEnd
                )
                SELECT
                    TransactionId, TransactionType, InvoiceNumber, CustomerName,
                    StaffName, ServiceName, Amount, PaymentTypeName,
                    PaymentBreakdownJson,
                    AppointmentId,
                    CONVERT(varchar(5), TxAt, 108) AS [Time],
                    Status
                FROM Tx
                ORDER BY TxAt DESC;",
                    p)
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
                            AppointmentId: r.AppointmentId != null ? (int?)r.AppointmentId : null
                        );
                    }).ToList();

                // ---------- 2D: Staff Performance + per-staff clients ----------


                var staffRows = SqlMapper.Query<dynamic>(conn, @"
                SELECT
                    s.Id            AS StaffId,
                    s.EnglishName   AS StaffName,
                    COUNT(DISTINCT a.Id)                                                    AS AppointmentCount,
                    SUM(CASE WHEN a.Status = 'completed' THEN 1 ELSE 0 END)                AS CompletedCount,
                    SUM(CASE WHEN a.Status = 'cancelled' THEN 1 ELSE 0 END)                AS CancelledCount,
                    SUM(CASE WHEN a.Status = 'no-show'   THEN 1 ELSE 0 END)                AS NoShowCount,
                    ISNULL(SUM(
                        CASE WHEN a.Status = 'completed' AND a.StartTime IS NOT NULL AND a.EndTime IS NOT NULL
                             THEN DATEDIFF(MINUTE, a.StartTime, a.EndTime)
                             ELSE 0
                        END), 0)                                                            AS TotalWorkMinutes,
                    ISNULL(SUM(
                        CASE WHEN a.CheckoutStatus = 'checked_out'
                             THEN a.DiscountedUnitPrice
                             ELSE 0
                        END), 0)                                                            AS TotalRevenue
                FROM dbo.STAFF s
                INNER JOIN (
                    -- الـ appointments الأصلية
                    SELECT
                        Id,
                        StaffId,
                        BranchId,
                        AppointmentDate,
                        Status,
                        CheckoutStatus,
                        StartTime,
                        EndTime,
                        DiscountedUnitPrice
                    FROM dbo.AppointmentData

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

                ) a ON a.StaffId = s.Id
                   AND a.BranchId = @BranchId
                   AND a.AppointmentDate = @DateOnly
                WHERE s.Deleted = 0
                  AND s.Active = 1
                  AND (s.BranchId IS NULL OR s.BranchId = @BranchId)
                  AND (@StaffId IS NULL OR s.Id = @StaffId)
                GROUP BY s.Id, s.EnglishName
                HAVING COUNT(DISTINCT a.Id) > 0
                ORDER BY TotalRevenue DESC;",
                p).ToList();

                var clientRows = SqlMapper.Query<dynamic>(conn, @"
                -- الـ appointments الأصلية
                SELECT
                    a.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    i.ITEM_NAME1                            AS ServiceName,
                    a.DiscountedUnitPrice                   AS Amount,
                    CONVERT(varchar(5), a.StartTime, 108)   AS [Time]
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM     i ON i.ITEM_ID     = a.ItemId
                WHERE a.BranchId        = @BranchId
                  AND a.AppointmentDate  = @DateOnly
                  AND (@StaffId IS NULL OR a.StaffId = @StaffId)

                UNION ALL

                -- الـ checkout items الإضافية
                SELECT
                    aci.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    i.ITEM_NAME1                            AS ServiceName,
                    aci.DiscountedUnitPrice                 AS Amount,
                    NULL                                    AS [Time]
                FROM dbo.AppointmentCheckoutItems aci
                INNER JOIN dbo.AppointmentData a ON a.Id  = aci.AppointmentId
                INNER JOIN dbo.CUSTOMER        c ON c.CUSTOMER_ID = aci.CustomerId
                INNER JOIN dbo.ITEM            i ON i.ITEM_ID     = aci.ItemId
                WHERE a.BranchId        = @BranchId
                  AND a.AppointmentDate  = @DateOnly
                  AND (@StaffId IS NULL OR aci.StaffId = @StaffId)

                UNION ALL

                -- الـ package sessions المقدّمة مباشرة (بدون appointment)
                SELECT
                    cps.StaffId,
                    c.CUSTOMER_NAME                         AS CustomerName,
                    i.ITEM_NAME1                            AS ServiceName,
                    cps.ItemPriceInPackage                  AS Amount,
                    NULL                                    AS [Time]
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
                  AND CAST(cps.ServedDate AS DATE) = @DateOnly
                  AND (@StaffId IS NULL OR cps.StaffId = @StaffId)

                ORDER BY StaffId, [Time];",
                                p).ToList();

                var clientsByStaff = clientRows
                    .GroupBy(r => (int)r.StaffId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => new StaffClientDto(
                            CustomerName: (string)(r.CustomerName ?? ""),
                            ServiceName: (string)(r.ServiceName ?? ""),
                            Amount: (decimal)r.Amount,
                            Time: (string)(r.Time ?? "00:00")
                        )).ToList()
                    );

                var staffPerformance = staffRows.Select(r =>
                {
                    int sId = (int)r.StaffId;
                    int totalWork = (int)r.TotalWorkMinutes;
                    decimal util = workdayMinutes > 0
                        ? Math.Round((decimal)totalWork * 100m / workdayMinutes, 1)
                        : 0m;
                    if (util > 100m) util = 100m;

                    return new StaffPerformanceDto(
                        StaffId: sId,
                        StaffName: (string)(r.StaffName ?? ""),
                        StaffColor: null,
                        AppointmentCount: (int)r.AppointmentCount,
                        CompletedCount: (int)r.CompletedCount,
                        CancelledCount: (int)r.CancelledCount,
                        NoShowCount: (int)r.NoShowCount,
                        TotalWorkMinutes: totalWork,
                        TotalRevenue: (decimal)r.TotalRevenue,
                        Utilization: util,
                        Clients: clientsByStaff.TryGetValue(sId, out var cs) ? cs : new List<StaffClientDto>()
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
                  AND AppointmentDate = @DateOnly
                  AND (@StaffId IS NULL OR StaffId = @StaffId);",
                    p).FirstOrDefault();

                var hourlyRows = SqlMapper.Query<dynamic>(conn, @"
                ;WITH HourBuckets AS (
                    SELECT
                        DATEPART(HOUR, a.StartTime) AS Hour,
                        i.ITEM_NAME1                AS ServiceName,
                        COUNT(*)                    AS Cnt
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = a.ItemId
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate = @DateOnly
                      AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                    GROUP BY DATEPART(HOUR, a.StartTime), i.ITEM_NAME1
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
                ORDER BY ht.Hour;",
                    p).ToList();

                // Build full 0-23 hour grid (fills missing hours with 0)
                var hourMap = hourlyRows.ToDictionary(r => (int)r.Hour, r => r);
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
                    ac.EnglishName                              AS CategoryName,
                    COUNT(a.Id)                                 AS AppointmentCount,
                    ISNULL(SUM(a.DiscountedUnitPrice), 0)       AS Revenue
                FROM dbo.AppointmentData a
                INNER JOIN dbo.ITEM                  i   ON i.ITEM_ID = a.ItemId
                INNER JOIN dbo.AppointmentCategories ac  ON ac.Id = i.AppointmentCategoryId
                WHERE a.BranchId = @BranchId
                  AND a.AppointmentDate = @DateOnly
                  AND (@StaffId IS NULL OR a.StaffId = @StaffId)
                  AND ISNULL(ac.Deleted, 0) = 0
                GROUP BY ac.EnglishName
                ORDER BY Revenue DESC;",
                p)
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
                      AND a.AppointmentDate = @DateOnly
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
                           OR (SELECT COUNT(*) FROM dbo.AppointmentData a2
                               WHERE a2.CustomerId = c.CUSTOMER_ID AND a2.BranchId = @BranchId) >= 5
                    ) AS VIPCustomers;",
                    p).FirstOrDefault();

                var topClients = SqlMapper.Query<dynamic>(conn, @"
                ;WITH ApptInvoices AS (
                    -- ربط كل appointment بالـ invoice بتاعها
                    SELECT a.Id AS AppointmentId, a.CustomerId, a.DiscountedUnitPrice,
                           inv.Id AS InvoiceId
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = a.Id
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate = @DateOnly

                    UNION

                    SELECT a.Id, a.CustomerId, a.DiscountedUnitPrice,
                           inv.Id AS InvoiceId
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.AppointmentInvoiceLines ail ON ail.AppointmentId = a.Id
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.Id = ail.InvoiceId
                    WHERE a.BranchId = @BranchId
                      AND a.AppointmentDate = @DateOnly
                )
                SELECT TOP 5
                    c.CUSTOMER_NAME            AS CustomerName,
                    SUM(ai.DiscountedUnitPrice) AS TotalSpent,
                    COUNT(DISTINCT ai.InvoiceId) AS VisitCount
                FROM ApptInvoices ai
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = ai.CustomerId
                GROUP BY c.CUSTOMER_NAME
                ORDER BY TotalSpent DESC;",
                    p)
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
                    TotalEffectiveRevenue: totalEffective,
                    PaymentTypeBreakdown: paymentBreakdown,
                    Transactions: transactions,
                    StaffPerformance: staffPerformance,
                    AppointmentStats: apptStats,
                    ServiceCategories: categories,
                    ClientInsights: clientInsights,
                    Currency: currency,
                    WorkdayMinutes: workdayMinutes,
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