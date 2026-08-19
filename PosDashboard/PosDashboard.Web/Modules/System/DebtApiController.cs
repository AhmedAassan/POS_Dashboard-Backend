// Modules/System/Controllers/DebtApiController.cs
//
// Deferred Payment (Debt) — api/debt
//
//   GET  /api/debt/config                      → flags + branch + payment types + delivery lookups
//   GET  /api/debt/invoices?tab=…              → the /orders table (filters + paging + summary)
//                                                 tab = unpaid (default) | paid | wallet
//   GET  /api/debt/customer/{id}/summary       → one customer's open debt
//   GET  /api/debt/customer/{id}/invoices      → that customer's open debt invoices (collect dialog)
//   GET  /api/debt/driver/{id}/invoices        → one driver's open debt invoices (collect dialog)
//   GET  /api/debt/customer/{id}/history       → the Customer History dialog payload
//   GET  /api/debt/customers                   → customers that currently owe money
//   POST /api/debt/settle                      → collect 1..N invoices in one payment
//   GET  /api/debt/settlement/{id}             → one collection receipt
//
// Design notes
// ------------
// • A debt invoice is an ORDINARY AppointmentInvoices row with IsDeferred = 1
//   and SettledAt IS NULL. There is no shadow invoice model, so refunds, the
//   PDF, the invoice dialog and every report keep working untouched.
// • Collecting writes REAL dbo.AppointmentPayments rows against each invoice's
//   lead appointment. That is what makes the money show up in the dashboard and
//   in the existing "Paid via" block without special-casing anything.
// • A settlement discount is distributed across the selected invoices
//   proportionally, largest-remainder style, so the shares always add back up to
//   the exact discount — no drifting fils.
// • Everything that mutates runs inside one UnitOfWork. A partially-collected
//   batch is not a state this system can reach.
// • /orders has three tabs, and they are three predicates over the SAME invoice
//   table — not three tables and not three endpoints. 'paid' is every invoice
//   with nothing left owing; 'wallet' narrows that to the ones a wallet payment
//   touched. Because 'paid' grows with the whole sales history, that endpoint
//   pages, sorts and totals in SQL rather than in memory.
// • "Paid via wallet" is decided by AppointmentPayments.IsWalletPayment, the
//   same flag the POS writes and the dashboard reads — never by payment-type
//   name, which is only a display label.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using DebtDtos = PosDashboard.Web.Modules.System.Models.DebtDtos;
using DeliveryDtos = PosDashboard.Web.Modules.System.Models.DeliveryDtos;
using PosDtos = PosDashboard.Web.Modules.System.Models.PosDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/debt")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class DebtApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        /// <summary>A debt older than this many days is flagged as overdue in the summary.</summary>
        private const int OverdueDays = 30;

        // The three tabs of /orders. Kept as constants because they travel from
        // the query string all the way into the SQL predicate — a typo anywhere
        // in that chain would silently return the wrong tab's data.
        private const string TabUnpaid = "unpaid";
        private const string TabPaid = "paid";
        private const string TabWallet = "wallet";
        private const string TabVoided = "voided";

        /// <summary>Unknown / missing tab falls back to the historical behaviour.</summary>
        private static string NormalizeTab(string? tab) => (tab?.Trim().ToLowerInvariant()) switch
        {
            TabPaid => TabPaid,
            TabWallet => TabWallet,
            TabVoided => TabVoided,
            _ => TabUnpaid
        };

        /// <summary>
        /// An invoice is "paid via wallet" when at least one wallet payment row
        /// points at it. Payments hang off appointments, and an invoice covers the
        /// lead appointment plus every appointment on its lines — a deposit taken
        /// at booking time lands on the line's appointment, not the lead. Missing
        /// that is how a wallet-paid invoice goes missing from the wallet tab.
        /// </summary>
        private const string WalletPaymentExists = @"
            EXISTS (
                SELECT 1
                FROM dbo.AppointmentPayments wap
                WHERE ISNULL(wap.IsWalletPayment, 0) = 1
                  AND wap.Amount > 0
                  AND (wap.AppointmentId = inv.AppointmentId
                       OR wap.AppointmentId IN (
                            SELECT wl.AppointmentId
                            FROM dbo.AppointmentInvoiceLines wl
                            WHERE wl.InvoiceId = inv.Id))
            )";

        private const string NonWalletPaymentExists = @"
            EXISTS (
                SELECT 1
                FROM dbo.AppointmentPayments nap
                WHERE ISNULL(nap.IsWalletPayment, 0) = 0
                  AND nap.Amount > 0
                  AND (nap.AppointmentId = inv.AppointmentId
                       OR nap.AppointmentId IN (
                            SELECT nl.AppointmentId
                            FROM dbo.AppointmentInvoiceLines nl
                            WHERE nl.InvoiceId = inv.Id))
            )";

        /// <summary>
        /// The mandatory predicate for each tab — what makes a row belong here at
        /// all, before any user filter is applied.
        /// </summary>
        private static string TabPredicate(string tab) => tab switch
        {
            // Settled or refunded money is not revenue we can show as "collected",
            // so a voided invoice is out of every tab but its own.
            TabPaid => @"ISNULL(inv.IsVoid, 0) = 0
                         AND inv.RemainingAmount <= 0
                         AND inv.PaidAmount > 0",

            TabWallet => $@"ISNULL(inv.IsVoid, 0) = 0
                            AND inv.RemainingAmount <= 0
                            AND inv.PaidAmount > 0
                            AND {WalletPaymentExists}",

            // The audit trail for a destructive action. Voiding removes the row
            // from every other view, so without this the cashier has no way to
            // confirm what they just cancelled.
            TabVoided => "ISNULL(inv.IsVoid, 0) = 1",

            // Unchanged from day one, plus the void guard. A void already zeroes
            // RemainingAmount, so this is belt-and-braces — but an open-debt list
            // is the last place a cancelled ticket should be able to reappear.
            _ => @"inv.IsDeferred = 1
                   AND inv.SettledAt IS NULL
                   AND inv.RemainingAmount > 0
                   AND ISNULL(inv.IsVoid, 0) = 0"
        };

        /// <summary>
        /// "When did the money arrive." CreatedAt for a counter sale that was paid
        /// on the spot, SettledAt for a debt that was collected later. Computed
        /// from the invoice row alone so it stays sortable and range-filterable
        /// without dragging the payments table into every query.
        /// </summary>
        private const string PaidAtExpr = "ISNULL(inv.SettledAt, inv.CreatedAt)";

        public DebtApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // =====================================================================
        // GET /api/debt/config
        // =====================================================================
        [HttpGet("config")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.DebtConfigDto>> Config([FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                branchId ??= ResolveUserBranchId(conn);

                var branch = SqlMapper.Query(conn, @"
                    SELECT TOP 1
                        BRANCH_ID AS BranchId, COMPANY_ID AS CompanyId,
                        BRANCH_NAME1 AS BranchName1, BRANCH_NAME2 AS BranchName2,
                        BRANCH_PHONE AS BranchPhone,
                        EnglishCurrencyName, ArabicCurrencyName, RoundOfDigits, TaxValue
                    FROM dbo.BRANCH
                    WHERE (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)
                      AND (@BranchId IS NULL OR BRANCH_ID = @BranchId)
                    ORDER BY BRANCH_ID", new { BranchId = branchId }).FirstOrDefault();

                if (branch == null)
                    return Ok(new DebtDtos.ApiResult<DebtDtos.DebtConfigDto>(false, "Branch not found or inactive", null));

                int resolvedBranchId = (int)branch.BranchId;

                var branchDto = new PosDtos.PosBranchDto(
                    BranchId: resolvedBranchId,
                    CompanyId: (int)(branch.CompanyId ?? 0),
                    BranchName1: (string?)branch.BranchName1 ?? "",
                    BranchName2: (string?)branch.BranchName2 ?? "",
                    BranchPhone: (string?)branch.BranchPhone,
                    CurrencyEn: (string?)branch.EnglishCurrencyName ?? "KWD",
                    CurrencyAr: (string?)branch.ArabicCurrencyName ?? "د.ك",
                    RoundOfDigits: (int)(branch.RoundOfDigits ?? 3),
                    TaxValue: (decimal?)branch.TaxValue);

                // There is NO IsWallet column on INVOICE_PAYMENT_TYPE — the wallet
                // method is recognised by its name. This mirrors the POS catalog
                // (PosApiController.Catalog) exactly on purpose: if the two ever
                // disagreed, the collect dialog would offer a "wallet" the POS
                // doesn't know about, or hide the one it does.
                var paymentTypes = SqlMapper.Query(conn, @"
                    SELECT
                        INVOICE_PAYMENT_TYPE_ID    AS PaymentTypeId,
                        INVOICE_PAYMENT_TYPE_NAME1 AS NameEn,
                        INVOICE_PAYMENT_TYPE_NAME2 AS NameAr,
                        OnlinePayment
                    FROM dbo.INVOICE_PAYMENT_TYPE
                    ORDER BY INVOICE_PAYMENT_TYPE_ID")
                    .Select(p =>
                    {
                        string en = (string?)p.NameEn ?? "";
                        string ar = (string?)p.NameAr ?? en;
                        bool isWallet =
                            en.ToLowerInvariant().Contains("wallet") || ar.Contains("محفظة");
                        return new PosDtos.PosPaymentTypeDto(
                            PaymentTypeId: (int)p.PaymentTypeId,
                            NameEn: en,
                            NameAr: ar,
                            IsWallet: isWallet,
                            OnlinePayment: (bool?)p.OnlinePayment ?? false);
                    })
                    .ToList();

                var deliveryTypes = SqlMapper.Query<DeliveryDtos.DeliveryTypeDto>(conn, @"
                    SELECT Id, Code, NameEn, NameAr, IsDelivery, IsDefault, IsActive,
                           Ordering, ColorCode, Icon, ChargeOverride, Notes, BranchId
                    FROM dbo.DeliveryType
                    WHERE Deleted = 0 AND IsActive = 1
                      AND (BranchId IS NULL OR BranchId = @BranchId)
                    ORDER BY Ordering, Id", new { BranchId = resolvedBranchId }).ToList();

                var drivers = DeliveryApiController.LoadDrivers(conn, resolvedBranchId, null);

                var governorates = SqlMapper.Query<DeliveryDtos.GovernorateOptionDto>(conn, @"
                    SELECT GOVERNORATE_ID AS GovernorateId,
                           GOVERNORATE_NAME1 AS NameEn,
                           GOVERNORATE_NAME2 AS NameAr,
                           COLOR_CODE AS ColorCode
                    FROM dbo.GOVERNORATE
                    ORDER BY GOVERNORATE_NAME1").ToList();

                // Only areas the branch actually delivers to are worth filtering by.
                var areas = SqlMapper.Query<DeliveryDtos.AreaOptionDto>(conn, @"
                    SELECT
                        a.AREA_ID           AS AreaId,
                        a.AREA_NAME1        AS NameEn,
                        a.AREA_NAME2        AS NameAr,
                        a.GOVERNORATE_ID    AS GovernorateId,
                        g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                        g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                        ISNULL(adc.Charge, 0) AS Charge,
                        CAST(CASE WHEN adc.Id IS NULL THEN 0 ELSE 1 END AS BIT) AS HasCharge
                    FROM dbo.GOVERNORATE_AREA a
                    INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = a.GOVERNORATE_ID
                    LEFT  JOIN dbo.AreaDeliveryCharge adc
                           ON adc.AreaId = a.AREA_ID AND adc.BranchId = @BranchId
                    ORDER BY g.GOVERNORATE_NAME1, a.AREA_NAME1",
                    new { BranchId = resolvedBranchId }).ToList();

                var dto = new DebtDtos.DebtConfigDto(
                    Settings: LoadDebtSettings(conn, resolvedBranchId),
                    Branch: branchDto,
                    PaymentTypes: paymentTypes,
                    DeliveryTypes: deliveryTypes,
                    Drivers: drivers,
                    Areas: areas,
                    Governorates: governorates,
                    TzOffset: BusinessSettingsService.GetTimeZoneOffset(conn));

                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtConfigDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtConfigDto>(
                    false, $"Failed to load debt config: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/invoices  — the /orders table
        //
        // One endpoint, three tabs:
        //   tab=unpaid  (default) open debt — IsDeferred, not settled, still owed
        //   tab=paid              every fully-paid invoice
        //   tab=wallet            fully-paid invoices where the wallet paid part or all
        //
        // Paging, sorting and the summary totals are all done in SQL. That is not
        // a micro-optimisation: 'unpaid' is bounded by how much a business is
        // willing to be owed, but 'paid' is the entire sales history, and pulling
        // it into memory to slice 25 rows off the top does not survive year two.
        // =====================================================================
        [HttpGet("invoices")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>> Invoices(
            [FromQuery] string? tab = TabUnpaid,        // unpaid | paid | wallet
            [FromQuery] int? branchId = null,
            [FromQuery] string? search = null,          // invoice no / customer / phone / driver / area
            [FromQuery] string? invoiceNumber = null,
            [FromQuery] int? customerId = null,
            [FromQuery] int? driverId = null,
            [FromQuery] int? areaId = null,
            [FromQuery] int? governorateId = null,
            [FromQuery] string? orderType = null,       // 'delivery' | 'pickup' | null (all)
            [FromQuery] DateTime? dateFrom = null,      // branch-local dates, inclusive
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] decimal? minAmount = null,
            [FromQuery] decimal? maxAmount = null,
            [FromQuery] bool onlyOverdue = false,
            [FromQuery] int? paymentTypeId = null,      // paid/wallet tabs: "show me the KNET ones"
            [FromQuery] bool walletOnly = false,        // wallet tab: 100% wallet, no cash top-up
            [FromQuery] string? sortBy = "date",        // date | amount | customer | age
            [FromQuery] string? sortDir = "desc",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                string activeTab = NormalizeTab(tab);
                // The voided tab shows money that WAS taken, so it needs the same
                // payment enrichment the paid tabs get.
                bool isPaidTab = activeTab != TabUnpaid;

                branchId ??= ResolveUserBranchId(conn);
                int tzOffset = BusinessSettingsService.GetTimeZoneOffset(conn);

                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 500) pageSize = 25;

                var (where, prm) = BuildDebtWhere(
                    activeTab, branchId, search, invoiceNumber, customerId, driverId,
                    areaId, governorateId, orderType, dateFrom, dateTo,
                    minAmount, maxAmount, onlyOverdue, paymentTypeId, walletOnly, tzOffset);

                // ---- 1) Totals across the WHOLE filter (not just this page) ----
                var summary = LoadSummary(conn, activeTab, where, prm, branchId);

                int total = summary.InvoiceCount;
                int totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
                // A filter change can strand the user past the last page; clamp
                // instead of handing back a confusing empty table.
                if (page > totalPages) page = totalPages;

                // ---- 2) One page of rows ----
                var items = QueryInvoicePage(
                    conn, activeTab, where, prm, sortBy, sortDir, page, pageSize);

                // ---- 3) Page-scoped enrichment (never the whole result set) ----
                var ids = items.Select(x => x.InvoiceId).ToList();
                if (ids.Count > 0)
                {
                    var services = LoadServiceSummaries(conn, ids);
                    var methodMap = isPaidTab
                        ? LoadPaymentBreakdown(conn, ids)
                        : new Dictionary<int, List<DebtDtos.InvoicePaymentMethodDto>>();
                    var refundMap = isPaidTab
                        ? LoadRefundTotals(conn, ids)
                        : new Dictionary<int, decimal>();

                    items = items.Select(x =>
                    {
                        services.TryGetValue(x.InvoiceId, out var svc);
                        refundMap.TryGetValue(x.InvoiceId, out var refunded);

                        if (!methodMap.TryGetValue(x.InvoiceId, out var pm))
                            pm = new List<DebtDtos.InvoicePaymentMethodDto>();

                        decimal wallet = Round3(pm.Where(p => p.IsWallet).Sum(p => p.Amount));
                        decimal other = Round3(pm.Where(p => !p.IsWallet).Sum(p => p.Amount));

                        return x with
                        {
                            ServicesSummary = svc,
                            PaymentMethods = isPaidTab ? pm : null,
                            WalletPaidAmount = wallet,
                            OtherPaidAmount = other,
                            // "Fully wallet" means nothing else was tendered — not
                            // merely that the wallet was the largest slice.
                            IsFullyWalletPaid = wallet > 0m && other <= 0m,
                            TotalRefunded = refunded
                        };
                    }).ToList();
                }

                var paged = new DebtDtos.PagedResult<DebtDtos.DebtInvoiceDto>(
                    Items: items,
                    TotalCount: total,
                    Page: page,
                    PageSize: pageSize,
                    TotalPages: totalPages);

                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>(true, null,
                    new DebtDtos.DebtInvoiceListDto(paged, summary, tzOffset, activeTab)));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>(
                    false, $"Failed to load invoices: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/customer/{id}/summary
        // =====================================================================
        [HttpGet("customer/{customerId:int}/summary")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.CustomerDebtSummaryDto>> CustomerSummary(
            int customerId, [FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var dto = LoadCustomerDebtSummary(conn, customerId, branchId);
                if (dto == null)
                    return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerDebtSummaryDto>(false, "Customer not found", null));

                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerDebtSummaryDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerDebtSummaryDto>(
                    false, $"Failed to load customer debt: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/customer/{id}/invoices  — feeds the collect dialog
        // =====================================================================
        [HttpGet("customer/{customerId:int}/invoices")]
        public ActionResult<DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>> CustomerInvoices(
            int customerId, [FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int tzOffset = BusinessSettingsService.GetTimeZoneOffset(conn);
                var list = QueryDebtInvoices(conn,
                    "inv.CustomerId = @CustomerId AND (@BranchId IS NULL OR inv.BranchId = @BranchId)",
                    new { CustomerId = customerId, BranchId = branchId }, tzOffset);

                return Ok(new DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>(true, null,
                    list.OrderBy(x => x.CreatedAt).ToList()));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>(
                    false, $"Failed to load customer invoices: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/driver/{id}/invoices  — the driver collect dialog
        // =====================================================================
        [HttpGet("driver/{driverId:int}/invoices")]
        public ActionResult<DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>> DriverInvoices(
            int driverId, [FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int tzOffset = BusinessSettingsService.GetTimeZoneOffset(conn);
                var list = QueryDebtInvoices(conn,
                    "ISNULL(idl.DriverId, inv.DeliveryDriverId) = @DriverId AND (@BranchId IS NULL OR inv.BranchId = @BranchId)",
                    new { DriverId = driverId, BranchId = branchId }, tzOffset);

                return Ok(new DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>(true, null,
                    list.OrderBy(x => x.CreatedAt).ToList()));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<List<DebtDtos.DebtInvoiceDto>>(
                    false, $"Failed to load driver invoices: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/customers — customers who currently owe money
        // =====================================================================
        [HttpGet("customers")]
        public ActionResult<DebtDtos.ApiResult<List<DebtDtos.CustomerDebtSummaryDto>>> CustomersWithDebt(
            [FromQuery] int? branchId = null, [FromQuery] string? search = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var rows = SqlMapper.Query(conn, @"
                    SELECT
                        c.CUSTOMER_ID       AS CustomerId,
                        c.CUSTOMER_NAME     AS CustomerName,
                        c.CUSTOMER_PHONE1   AS CustomerPhone,
                        SUM(inv.RemainingAmount) AS TotalDebt,
                        COUNT(*)            AS InvoiceCount,
                        MIN(inv.CreatedAt)  AS OldestInvoiceAt,
                        MAX(b.EnglishCurrencyName) AS Currency,
                        (SELECT MAX(s.SettledAt) FROM dbo.DebtSettlements s
                          WHERE s.CustomerId = c.CUSTOMER_ID AND s.Deleted = 0) AS LastPaymentAt
                    FROM dbo.AppointmentInvoices inv
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = inv.CustomerId
                    LEFT  JOIN dbo.BRANCH  b  ON b.BRANCH_ID   = inv.BranchId
                    WHERE inv.IsDeferred = 1
                      AND inv.SettledAt IS NULL
                      AND inv.RemainingAmount > 0
                      AND (@BranchId IS NULL OR inv.BranchId = @BranchId)
                      AND (@Search IS NULL OR c.CUSTOMER_NAME LIKE '%' + @Search + '%'
                                           OR c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%')
                    GROUP BY c.CUSTOMER_ID, c.CUSTOMER_NAME, c.CUSTOMER_PHONE1
                    ORDER BY SUM(inv.RemainingAmount) DESC",
                    new { BranchId = branchId, Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() })
                    .Select(r => new DebtDtos.CustomerDebtSummaryDto(
                        CustomerId: (int)r.CustomerId,
                        CustomerName: (string?)r.CustomerName ?? "",
                        CustomerPhone: (string?)r.CustomerPhone ?? "",
                        TotalDebt: (decimal)r.TotalDebt,
                        InvoiceCount: (int)r.InvoiceCount,
                        OldestInvoiceAt: (DateTime?)r.OldestInvoiceAt,
                        LastPaymentAt: (DateTime?)r.LastPaymentAt,
                        Currency: (string?)r.Currency ?? "KWD"))
                    .ToList();

                return Ok(new DebtDtos.ApiResult<List<DebtDtos.CustomerDebtSummaryDto>>(true, null, rows));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<List<DebtDtos.CustomerDebtSummaryDto>>(
                    false, $"Failed to load customers with debt: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/customer/{id}/history  — the Customer History dialog
        // =====================================================================
        [HttpGet("customer/{customerId:int}/history")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.CustomerHistoryDto>> CustomerHistory(
            int customerId,
            [FromQuery] int? branchId = null,
            [FromQuery] string? invoiceFilter = null,     // 'paid' | 'unpaid' | null
            [FromQuery] string? invoiceSearch = null,
            [FromQuery] int invoicePage = 1,
            [FromQuery] int invoicePageSize = 10,
            [FromQuery] int bookingPage = 1,
            [FromQuery] int bookingPageSize = 10)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int tzOffset = BusinessSettingsService.GetTimeZoneOffset(conn);

                var cust = SqlMapper.Query(conn, @"
                    SELECT
                        c.CUSTOMER_ID   AS CustomerId, c.CUSTOMER_NAME AS CustomerName,
                        c.CUSTOMER_PHONE1 AS CustomerPhone, c.CUSTOMER_PHONE2 AS CustomerPhone2,
                        c.BRANCH_ID     AS BranchId, b.BRANCH_NAME1 AS BranchName,
                        c.CUSTOMER_CREATED_DATE AS CreatedDate,
                        ISNULL(c.CUSTOMER_IS_BLOCK, 0) AS IsBlocked,
                        c.CUSTOMER_NOTE AS Note, c.CUSTOMER_REF_GUIDE AS RefGuide,
                        b.EnglishCurrencyName AS Currency
                    FROM dbo.CUSTOMER c
                    LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = c.BRANCH_ID
                    WHERE c.CUSTOMER_ID = @Id", new { Id = customerId }).FirstOrDefault();

                if (cust == null)
                    return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerHistoryDto>(false, "Customer not found", null));

                Guid customerRef = (Guid)cust.RefGuide;
                string currency = (string?)cust.Currency ?? "KWD";

                // ---- Invoices (all of them, filtered/paged in memory) ----
                var invoiceRows = SqlMapper.Query(conn, @"
                    SELECT
                        inv.Id              AS InvoiceId,
                        inv.InvoiceNumber   AS InvoiceNumber,
                        inv.AppointmentId   AS LeadAppointmentId,
                        inv.CreatedAt       AS CreatedAt,
                        inv.TotalAmount     AS TotalAmount,
                        inv.PaidAmount      AS PaidAmount,
                        inv.RemainingAmount AS RemainingAmount,
                        inv.PaymentStatus   AS PaymentStatus,
                        ISNULL(inv.IsDeferred, 0) AS IsDeferred,
                        inv.SettledAt       AS SettledAt,
                        inv.Currency        AS Currency,
                        idl.IsDelivery      AS IsDelivery,
                        idl.DeliveryTypeNameEn AS DeliveryTypeNameEn,
                        idl.DeliveryTypeNameAr AS DeliveryTypeNameAr,
                        idl.DriverName      AS DriverName,
                        idl.AreaNameEn      AS AreaNameEn,
                        idl.AreaNameAr      AS AreaNameAr,
                        (SELECT COUNT(*) FROM dbo.AppointmentInvoiceLines l
                          WHERE l.InvoiceId = inv.Id AND ISNULL(l.IsRefunded, 0) = 0) AS ItemCount,
                        (SELECT ISNULL(SUM(r.RefundAmount), 0) FROM dbo.RefundTransactions r
                          WHERE r.InvoiceId = inv.Id AND ISNULL(r.Deleted, 0) = 0) AS TotalRefunded
                    FROM dbo.AppointmentInvoices inv
                    LEFT JOIN dbo.InvoiceDelivery idl ON idl.InvoiceId = inv.Id
                    WHERE inv.CustomerId = @CustomerId
                      AND (@BranchId IS NULL OR inv.BranchId = @BranchId)
                    ORDER BY inv.CreatedAt DESC",
                    new { CustomerId = customerId, BranchId = branchId }).ToList();

                var invoiceIds = invoiceRows.Select(r => (int)r.InvoiceId).ToList();
                var summaries = LoadServiceSummaries(conn, invoiceIds);

                var invoices = invoiceRows.Select(r =>
                {
                    int invId = (int)r.InvoiceId;
                    return new DebtDtos.CustomerInvoiceRowDto(
                        InvoiceId: invId,
                        InvoiceNumber: (string?)r.InvoiceNumber ?? "",
                        LeadAppointmentId: (int)r.LeadAppointmentId,
                        CreatedAt: (DateTime)r.CreatedAt,
                        TotalAmount: (decimal)r.TotalAmount,
                        PaidAmount: (decimal)r.PaidAmount,
                        RemainingAmount: (decimal)r.RemainingAmount,
                        PaymentStatus: (string?)r.PaymentStatus ?? "FULL",
                        IsDeferred: Convert.ToInt32(r.IsDeferred) == 1,
                        SettledAt: (DateTime?)r.SettledAt,
                        IsDelivery: r.IsDelivery != null && Convert.ToInt32(r.IsDelivery) == 1,
                        DeliveryTypeNameEn: (string?)r.DeliveryTypeNameEn,
                        DeliveryTypeNameAr: (string?)r.DeliveryTypeNameAr,
                        DriverName: (string?)r.DriverName,
                        AreaNameEn: (string?)r.AreaNameEn,
                        AreaNameAr: (string?)r.AreaNameAr,
                        ItemCount: (int)r.ItemCount,
                        ServicesSummary: summaries.TryGetValue(invId, out var s) ? s : null,
                        TotalRefunded: (decimal)r.TotalRefunded,
                        Currency: (string?)r.Currency ?? currency);
                }).ToList();

                var filteredInvoices = invoices.AsEnumerable();
                if (invoiceFilter == "unpaid")
                    filteredInvoices = filteredInvoices.Where(x => x.RemainingAmount > 0);
                else if (invoiceFilter == "paid")
                    filteredInvoices = filteredInvoices.Where(x => x.RemainingAmount <= 0);

                if (!string.IsNullOrWhiteSpace(invoiceSearch))
                {
                    string q = invoiceSearch.Trim().ToLowerInvariant();
                    filteredInvoices = filteredInvoices.Where(x =>
                        x.InvoiceNumber.ToLowerInvariant().Contains(q) ||
                        (x.ServicesSummary ?? "").ToLowerInvariant().Contains(q) ||
                        (x.DriverName ?? "").ToLowerInvariant().Contains(q));
                }

                var invList = filteredInvoices.ToList();
                var invPaged = Paginate(invList, invoicePage, invoicePageSize);

                // ---- Subscriptions (packages + wallet) ----
                var subscriptions = SqlMapper.Query(conn, @"
                    SELECT
                        s.Id AS SubscriptionId, st.NAME AS SubTypeName,
                        ISNULL(s.Value, 0) AS Value, ISNULL(s.Net, 0) AS Net,
                        s.StartDate, s.EndDate, ISNULL(s.IsPaid, 0) AS IsPaid,
                        ISNULL((SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                                 WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                 ORDER BY sh.Id DESC), 0) AS CurrentBalance
                    FROM dbo.Subscriptions s
                    LEFT JOIN dbo.SUBS_TYPE st ON st.ID = s.SubTypeId
                    WHERE s.CustomerRef = @Ref AND ISNULL(s.Deleted, 0) = 0
                    ORDER BY s.Id DESC", new { Ref = customerRef })
                    .Select(s => new DebtDtos.CustomerSubscriptionRowDto(
                        SubscriptionId: (int)s.SubscriptionId,
                        SubTypeName: (string?)s.SubTypeName ?? "",
                        Value: (decimal)s.Value,
                        Net: (decimal)s.Net,
                        CurrentBalance: (decimal)s.CurrentBalance,
                        StartDate: (DateTime)s.StartDate,
                        EndDate: (DateTime)s.EndDate,
                        IsPaid: Convert.ToInt32(s.IsPaid) == 1,
                        IsExpired: (DateTime)s.EndDate < DateTime.UtcNow,
                        // "Wallet" in this system = an active, paid subscription that still
                        // carries a balance (see WalletApiController.GetCustomerWalletSummary).
                        IsWallet: (decimal)s.CurrentBalance > 0m))
                    .ToList();

                // ---- Wallet ledger ----
                //
                // sh.InvoiceId is a foreign key onto the LEGACY dbo.INVOICE_HEADER,
                // not onto AppointmentInvoices. This used to LEFT JOIN it to
                // AppointmentInvoices by id, which looks right and is not: on any
                // row where the column is set it resolves a legacy invoice id
                // against a modern table and labels the transaction with whichever
                // unrelated invoice happens to share that number.
                //
                // Every wallet write in this system (POS, New Sale, refund, debt
                // settlement, void) passes NULL here, so there is no label to
                // recover — and no join that could recover one without reading a
                // legacy table this module does not otherwise touch. Showing
                // nothing beats showing the wrong invoice number.
                var walletTx = SqlMapper.Query(conn, @"
                    SELECT TOP 200
                        sh.Id, sh.SubscriptionId, sh.AddedDate, sh.Amount, sh.Balance,
                        ISNULL(sh.RefType, 0) AS RefType,
                        sh.InvoiceId
                    FROM dbo.SubscriptionsHistory sh
                    WHERE sh.CustomerRef = @Ref AND sh.Deleted = 0
                    ORDER BY sh.Id DESC", new { Ref = customerRef })
                    .Select(t => new DebtDtos.CustomerWalletTxRowDto(
                        Id: (int)t.Id,
                        SubscriptionId: (int)(t.SubscriptionId ?? 0),
                        AddedDate: (DateTime)t.AddedDate,
                        Amount: (decimal)t.Amount,
                        Balance: (decimal)t.Balance,
                        RefType: (int)t.RefType,
                        InvoiceId: (int?)t.InvoiceId,
                        InvoiceNumber: null))
                    .ToList();

                // ---- Bookings (calendar appointments only — POS lines are invoices) ----
                var bookingRows = SqlMapper.Query(conn, @"
                    SELECT
                        a.Id AS AppointmentId, a.AppointmentDate, a.StartTime, a.EndTime,
                        i.ITEM_NAME1 AS ItemNameEn, i.ITEM_NAME2 AS ItemNameAr,
                        s.EnglishName AS StaffNameEn, s.ArabicName AS StaffNameAr,
                        ISNULL(a.Status, '') AS Status,
                        ISNULL(a.CheckoutStatus, '') AS CheckoutStatus,
                        ISNULL(a.PaymentStatus, '') AS PaymentStatus,
                        ISNULL(a.TotalPrice, 0) AS TotalPrice,
                        ISNULL(a.PaidAmount, 0) AS PaidAmount,
                        ISNULL(a.IsOnlineBooking, 0) AS IsOnlineBooking
                    FROM dbo.AppointmentData a
                    LEFT JOIN dbo.ITEM  i ON i.ITEM_ID = a.ItemId
                    LEFT JOIN dbo.STAFF s ON s.Id      = a.StaffId
                    WHERE a.CustomerId = @CustomerId
                      AND ISNULL(a.ShowOnCalendar, 1) = 1
                      AND (@BranchId IS NULL OR a.BranchId = @BranchId)
                    ORDER BY a.AppointmentDate DESC, a.Id DESC",
                    new { CustomerId = customerId, BranchId = branchId })
                    .Select(b => new DebtDtos.CustomerBookingRowDto(
                        AppointmentId: (int)b.AppointmentId,
                        AppointmentDate: (DateTime)b.AppointmentDate,
                        StartTime: b.StartTime?.ToString(),
                        EndTime: b.EndTime?.ToString(),
                        ItemNameEn: (string?)b.ItemNameEn ?? "",
                        ItemNameAr: (string?)b.ItemNameAr ?? (string?)b.ItemNameEn ?? "",
                        StaffNameEn: (string?)b.StaffNameEn,
                        StaffNameAr: (string?)b.StaffNameAr,
                        Status: (string)b.Status,
                        CheckoutStatus: (string)b.CheckoutStatus,
                        PaymentStatus: (string)b.PaymentStatus,
                        TotalPrice: (decimal)b.TotalPrice,
                        PaidAmount: (decimal)b.PaidAmount,
                        IsOnlineBooking: Convert.ToInt32(b.IsOnlineBooking) == 1))
                    .ToList();

                var bookingPaged = Paginate(bookingRows, bookingPage, bookingPageSize);

                // ---- Collection history ----
                var settlements = LoadSettlementRows(conn, customerId, null);

                // ---- Addresses ----
                var addresses = SqlMapper.Query<DeliveryDtos.DeliveryAddressDto>(conn, @"
                    SELECT
                        ca.CUSTOMER_ADRESS_ID AS AddressId,
                        @CustomerId           AS CustomerId,
                        ca.CUSTOMER_REF       AS CustomerRef,
                        ca.AREA_ID            AS AreaId,
                        ga.AREA_NAME1         AS AreaNameEn,
                        ga.AREA_NAME2         AS AreaNameAr,
                        ga.GOVERNORATE_ID     AS GovernorateId,
                        g.GOVERNORATE_NAME1   AS GovernorateNameEn,
                        g.GOVERNORATE_NAME2   AS GovernorateNameAr,
                        ca.BLOCK_NO           AS BlockNo,
                        ca.STREET             AS Street,
                        ca.AVENUE             AS Avenue,
                        ca.BUILDING_NO        AS BuildingNo,
                        ca.FLAT_NO            AS FlatNo,
                        ca.Floor              AS Floor,
                        ca.NOTE               AS Note,
                        ca.Location           AS Location,
                        CAST(CASE WHEN ca.DEFAULT_ADDRESS = 1 THEN 1 ELSE 0 END AS BIT) AS IsDefault,
                        ISNULL(adc.Charge, 0) AS DeliveryCharge,
                        CAST(CASE WHEN adc.Id IS NULL THEN 0 ELSE 1 END AS BIT) AS HasCharge,
                        CAST(0 AS BIT)        AS InUse
                    FROM dbo.CUSTOMER_ADRESS ca
                    LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                    LEFT JOIN dbo.GOVERNORATE g       ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                    LEFT JOIN dbo.AreaDeliveryCharge adc
                           ON adc.AreaId = ca.AREA_ID AND adc.BranchId = @BranchId
                    WHERE ca.CUSTOMER_REF = @Ref AND ISNULL(ca.IsDeleted, 0) = 0
                    ORDER BY ca.DEFAULT_ADDRESS DESC, ca.CUSTOMER_ADRESS_ID",
                    new { CustomerId = customerId, Ref = customerRef, BranchId = branchId ?? (int)cust.BranchId })
                    .ToList();

                var walletSub = subscriptions.FirstOrDefault(s => s.IsWallet && s.IsPaid && !s.IsExpired);

                var header = new DebtDtos.CustomerHistoryHeaderDto(
                    CustomerId: customerId,
                    CustomerName: (string?)cust.CustomerName ?? "",
                    CustomerPhone: (string?)cust.CustomerPhone ?? "",
                    CustomerPhone2: (string?)cust.CustomerPhone2,
                    BranchId: (int)cust.BranchId,
                    BranchName: (string?)cust.BranchName,
                    CreatedDate: (DateTime?)cust.CreatedDate,
                    IsBlocked: Convert.ToInt32(cust.IsBlocked) == 1,
                    Note: (string?)cust.Note,
                    TotalDebt: Round3(invoices.Where(x => x.IsDeferred && x.SettledAt == null).Sum(x => x.RemainingAmount)),
                    DebtInvoiceCount: invoices.Count(x => x.IsDeferred && x.SettledAt == null && x.RemainingAmount > 0),
                    LastPaymentAt: settlements.FirstOrDefault()?.SettledAt,
                    LifetimeSpend: Round3(invoices.Sum(x => x.PaidAmount)),
                    TotalInvoices: invoices.Count,
                    TotalBookings: bookingRows.Count,
                    WalletBalance: walletSub?.CurrentBalance ?? 0m,
                    HasActiveWallet: walletSub != null,
                    AddressCount: addresses.Count,
                    Currency: currency);

                var dto = new DebtDtos.CustomerHistoryDto(
                    Header: header,
                    Invoices: invPaged,
                    Subscriptions: subscriptions,
                    WalletTransactions: walletTx,
                    Bookings: bookingPaged,
                    Settlements: settlements,
                    Addresses: addresses,
                    TzOffset: tzOffset);

                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerHistoryDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerHistoryDto>(
                    false, $"Failed to load customer history: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/invoice/{id}/void-preview
        //
        // Answers "what happens if I void this?" before anything is written.
        // A void pushes money in three different directions depending on how the
        // invoice was paid, and the cashier cannot see which one applies from the
        // row alone — so the dialog states the consequence in numbers instead of
        // asking them to guess.
        // =====================================================================
        [HttpGet("invoice/{invoiceId:int}/void-preview")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.VoidInvoicePreviewDto>> VoidPreview(int invoiceId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var inv = LoadVoidableInvoice(conn, invoiceId);
                if (inv == null)
                    return Ok(new DebtDtos.ApiResult<DebtDtos.VoidInvoicePreviewDto>(
                        false, "Invoice not found", null));

                string? block = VoidBlockReason(inv);

                var apptIds = CollectInvoiceAppointmentIds(conn, invoiceId, (int)inv.AppointmentId);
                var methods = LoadPaymentBreakdown(conn, new List<int> { invoiceId })
                    .TryGetValue(invoiceId, out var pm) ? pm : new List<DebtDtos.InvoicePaymentMethodDto>();

                decimal walletPaid = Round3(methods.Where(m => m.IsWallet).Sum(m => m.Amount));
                decimal otherPaid = Round3(methods.Where(m => !m.IsWallet).Sum(m => m.Amount));

                bool isDeferred = Convert.ToInt32(inv.IsDeferred) == 1;
                decimal remaining = (decimal)inv.RemainingAmount;
                decimal debtToClear = isDeferred && inv.SettledAt == null && remaining > 0 ? remaining : 0m;

                var wallet = LoadCustomerWallet(conn, (Guid)inv.CustomerRef);

                int? settlementId = (int?)inv.SettlementId;
                string? settlementNumber = null;
                int siblings = 0;
                if (settlementId.HasValue)
                {
                    settlementNumber = SqlMapper.Query<string>(conn,
                        "SELECT SettlementNumber FROM dbo.DebtSettlements WHERE Id = @Id",
                        new { Id = settlementId.Value }).FirstOrDefault();
                    siblings = SqlMapper.Query<int>(conn, @"
                        SELECT COUNT(*) FROM dbo.DebtSettlementInvoices
                        WHERE SettlementId = @Id AND InvoiceId <> @InvoiceId",
                        new { Id = settlementId.Value, InvoiceId = invoiceId }).FirstOrDefault();
                }

                var dto = new DebtDtos.VoidInvoicePreviewDto(
                    InvoiceId: invoiceId,
                    InvoiceNumber: (string?)inv.InvoiceNumber ?? "",
                    CustomerId: (int)inv.CustomerId,
                    CustomerName: (string?)inv.CustomerName ?? "",
                    TotalAmount: (decimal)inv.TotalAmount,
                    PaidAmount: (decimal)inv.PaidAmount,
                    RemainingAmount: remaining,
                    Currency: (string?)inv.Currency ?? "KWD",
                    CanVoid: block == null,
                    BlockReason: block,
                    DebtToClear: debtToClear,
                    WalletToRestore: walletPaid,
                    WalletBalanceBefore: wallet?.Balance ?? 0m,
                    OtherPaidToReverse: otherPaid,
                    IsDeferred: isDeferred,
                    IsSettledDebt: isDeferred && inv.SettledAt != null,
                    SettlementId: settlementId,
                    SettlementNumber: settlementNumber,
                    SettlementSiblingCount: siblings,
                    AppointmentCount: apptIds.Count,
                    PaymentMethods: methods);

                return Ok(new DebtDtos.ApiResult<DebtDtos.VoidInvoicePreviewDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.VoidInvoicePreviewDto>(
                    false, $"Failed to prepare void: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/debt/invoice/{id}/void
        //
        // One void, three outcomes, decided by how the invoice was paid:
        //
        //   unpaid debt  → the debt comes off the customer. RemainingAmount goes
        //                  to zero, which is what every open-debt query in the
        //                  system already filters on, so the invoice drops out of
        //                  the POS badge, the customers grid and /orders at once
        //                  without six separate predicates having to agree.
        //
        //   wallet paid  → the credit goes back to the customer's wallet as a
        //                  RefType 3 (Return) ledger row — the same mechanism the
        //                  refund flow uses. The original spend row stays put, so
        //                  the ledger reads as a spend followed by a return
        //                  rather than as money that was never taken.
        //
        //   cash/card    → nothing is handed back here. The revenue disappears
        //                  because every dashboard query already excludes voided
        //                  invoices; returning physical money is a refund, which
        //                  is a different action with a different paper trail.
        //
        // Mixed invoices fall out of this naturally: only the wallet share is
        // returned, only the open share is written off.
        //
        // The write path deliberately mirrors AppointmentsApiController.VoidInvoice
        // (flag the invoice, zero the lines, cancel the appointments, zero the
        // checkout extras) so an invoice voided from /orders and one voided from
        // the dashboard end up in exactly the same state.
        // =====================================================================
        [HttpPost("invoice/{invoiceId:int}/void")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.VoidInvoiceResultDto>> VoidInvoice(
            int invoiceId, [FromBody] DebtDtos.VoidInvoiceRequest? request = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var inv = LoadVoidableInvoice(conn, invoiceId);
                if (inv == null) return FailVoid("Invoice not found");

                string? block = VoidBlockReason(inv);
                if (block != null) return FailVoid(block);

                int currentUserId = ResolveCurrentUserId();
                int leadApptId = (int)inv.AppointmentId;
                var customerRef = (Guid)inv.CustomerRef;
                string currency = (string?)inv.Currency ?? "KWD";

                bool isDeferred = Convert.ToInt32(inv.IsDeferred) == 1;
                decimal remaining = (decimal)inv.RemainingAmount;
                decimal debtToClear = isDeferred && inv.SettledAt == null && remaining > 0 ? remaining : 0m;

                var apptIds = CollectInvoiceAppointmentIds(conn, invoiceId, leadApptId);

                var methods = LoadPaymentBreakdown(conn, new List<int> { invoiceId })
                    .TryGetValue(invoiceId, out var pm) ? pm : new List<DebtDtos.InvoicePaymentMethodDto>();
                decimal walletPaid = Round3(methods.Where(m => m.IsWallet).Sum(m => m.Amount));
                decimal otherPaid = Round3(methods.Where(m => !m.IsWallet).Sum(m => m.Amount));

                // The wallet has to exist before we start writing, otherwise the
                // customer's credit would evaporate inside a committed void.
                var wallet = walletPaid > 0 ? LoadCustomerWallet(conn, customerRef) : null;
                if (walletPaid > 0 && wallet == null)
                    return FailVoid(
                        "This invoice was paid from a wallet that no longer exists, so the credit cannot be returned. " +
                        "Restore the customer's wallet first, or refund the amount manually.");

                decimal walletBalanceAfter = wallet?.Balance ?? 0m;
                int? settlementAdjusted = null;
                bool settlementClosed = false;

                using (var uow = new UnitOfWork(conn))
                {
                    var tx = uow.Connection;
                    DateTime now = DateTime.UtcNow;

                    // ── 1) Flag the invoice and clear anything still owed ──────
                    // PaidAmount and TotalAmount stay: they are the record of what
                    // the ticket said. RemainingAmount is the live claim on the
                    // customer, and after a void there is no claim — zeroing it is
                    // what makes the invoice vanish from the POS debt badge, the
                    // customers grid and /orders at once, since all three already
                    // filter on RemainingAmount > 0. PaymentStatus is left alone:
                    // IsVoid is the flag every other screen reads, and inventing a
                    // new status value would only surprise the ones that switch on it.
                    SqlMapper.Execute(tx, @"
                        UPDATE dbo.AppointmentInvoices
                        SET IsVoid          = 1,
                            RemainingAmount = 0,
                            VoidedAt        = @Now,
                            VoidedBy        = @UserId,
                            VoidReason      = @Reason
                        WHERE Id = @Id",
                        new
                        {
                            Id = invoiceId,
                            Now = now,
                            UserId = currentUserId > 0 ? currentUserId : (int?)null,
                            Reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request!.Reason!.Trim()
                        });

                    // ── 2) Zero the lines so no report counts the revenue ──────
                    SqlMapper.Execute(tx, @"
                        UPDATE dbo.AppointmentInvoiceLines
                        SET IsRefunded = 1, DiscountedUnitPrice = 0, TotalPrice = 0
                        WHERE InvoiceId = @InvoiceId",
                        new { InvoiceId = invoiceId });

                    // ── 3) Cancel every appointment behind the invoice ─────────
                    // Byte-for-byte the same SET list AppointmentsApiController
                    // uses. An invoice voided from /orders and one voided from the
                    // dashboard must land in the same state, or Staff Performance
                    // will disagree with itself depending on which button was used.
                    if (apptIds.Count > 0)
                    {
                        SqlMapper.Execute(tx, @"
                            UPDATE dbo.AppointmentData
                            SET Status              = 'cancelled',
                                CheckoutStatus      = 'open',
                                DiscountedUnitPrice = 0,
                                UpdatedAt           = SYSUTCDATETIME()
                            WHERE Id IN @Ids",
                            new { Ids = apptIds });

                        SqlMapper.Execute(tx, @"
                            UPDATE dbo.AppointmentCheckoutItems
                            SET IsRefunded = 1, DiscountedUnitPrice = 0, TotalPrice = 0
                            WHERE AppointmentId IN @Ids",
                            new { Ids = apptIds });
                    }

                    // ── 4) Give the wallet its credit back ─────────────────────
                    if (walletPaid > 0 && wallet != null)
                    {
                        walletBalanceAfter = Round3(wallet.Balance + walletPaid);

                        // RefType 3 = Return, the same code the refund flow writes.
                        //
                        // InvoiceId stays NULL. That column is NOT a link to
                        // AppointmentInvoices — it carries a foreign key onto the
                        // legacy dbo.INVOICE_HEADER table, so writing a modern
                        // invoice id into it fails the constraint outright. Every
                        // other wallet write in this system (POS, New Sale, refund)
                        // passes NULL here for exactly that reason.
                        //
                        // The trail back to this void is therefore the pair of
                        // ledger rows themselves: a RefType 1 spend and a matching
                        // RefType 3 return of the same amount, on the same
                        // subscription, timestamped at the void.
                        SqlMapper.Execute(tx, @"
                            INSERT INTO dbo.SubscriptionsHistory
                                (CustomerRef, RefType, InvoiceId, SubscriptionId,
                                 Amount, Balance, AddedBy, AddedDate, Deleted)
                            VALUES (@CustomerRef, 3, NULL, @SubscriptionId,
                                    @Amount, @Balance, @AddedBy, @AddedDate, 0)",
                            new
                            {
                                CustomerRef = customerRef,
                                SubscriptionId = wallet.SubscriptionId,
                                Amount = walletPaid,
                                Balance = walletBalanceAfter,
                                AddedBy = currentUserId,
                                AddedDate = now
                            });

                        // Returned credit is unusable if the wallet expired in the
                        // meantime, so an expired wallet is pushed back out — the
                        // same rule the refund flow applies.
                        SqlMapper.Execute(tx, @"
                            UPDATE dbo.Subscriptions
                            SET Value   = Value + @Amount,
                                Net     = Net   + @Amount,
                                IsPaid  = 1,
                                EndDate = CASE WHEN EndDate < SYSUTCDATETIME()
                                               THEN DATEADD(YEAR, 1, SYSUTCDATETIME())
                                               ELSE EndDate END
                            WHERE Id = @Id",
                            new { Id = wallet.SubscriptionId, Amount = walletPaid });
                    }

                    // ── 5) Take this invoice back out of its settlement ────────
                    // A settlement can cover several invoices, so the header is
                    // adjusted by this invoice's own share rather than deleted.
                    int? settlementId = (int?)inv.SettlementId;
                    if (settlementId.HasValue)
                    {
                        // ROWS and BEFORE are reserved in T-SQL, so the aliases
                        // are prefixed rather than bracketed.
                        var share = SqlMapper.Query(tx, @"
                            SELECT ISNULL(SUM(AmountBefore), 0)    AS ShareBefore,
                                   ISNULL(SUM(DiscountShare), 0)   AS ShareDiscount,
                                   ISNULL(SUM(AmountCollected), 0) AS ShareCollected,
                                   COUNT(*)                        AS ShareRows
                            FROM dbo.DebtSettlementInvoices
                            WHERE SettlementId = @Sid AND InvoiceId = @Iid",
                            new { Sid = settlementId.Value, Iid = invoiceId }).FirstOrDefault();

                        if (share != null && (int)share.ShareRows > 0)
                        {
                            SqlMapper.Execute(tx, @"
                                UPDATE dbo.DebtSettlements
                                SET InvoiceCount   = CASE WHEN InvoiceCount > @Rows
                                                          THEN InvoiceCount - @Rows ELSE 0 END,
                                    TotalBefore    = TotalBefore    - @Before,
                                    DiscountAmount = DiscountAmount - @Discount,
                                    TotalCollected = TotalCollected - @Collected
                                WHERE Id = @Sid",
                                new
                                {
                                    Sid = settlementId.Value,
                                    Rows = (int)share.ShareRows,
                                    Before = (decimal)share.ShareBefore,
                                    Discount = (decimal)share.ShareDiscount,
                                    Collected = (decimal)share.ShareCollected
                                });

                            // A settlement with nothing left in it is not a
                            // settlement; leaving a zero-value receipt behind would
                            // show up as a phantom collection in the customer's
                            // history.
                            settlementClosed = SqlMapper.Query<int>(tx, @"
                                SELECT InvoiceCount FROM dbo.DebtSettlements WHERE Id = @Sid",
                                new { Sid = settlementId.Value }).FirstOrDefault() <= 0;

                            if (settlementClosed)
                                SqlMapper.Execute(tx,
                                    "UPDATE dbo.DebtSettlements SET Deleted = 1 WHERE Id = @Sid",
                                    new { Sid = settlementId.Value });

                            settlementAdjusted = settlementId.Value;
                        }
                    }

                    uow.Commit();
                }

                var result = new DebtDtos.VoidInvoiceResultDto(
                    InvoiceId: invoiceId,
                    InvoiceNumber: (string?)inv.InvoiceNumber ?? "",
                    DebtCleared: debtToClear,
                    WalletRestored: walletPaid,
                    WalletSubscriptionId: wallet?.SubscriptionId,
                    WalletBalanceAfter: walletBalanceAfter,
                    RevenueReversed: otherPaid,
                    CancelledAppointmentIds: apptIds,
                    SettlementAdjustedId: settlementAdjusted,
                    SettlementClosed: settlementClosed,
                    Currency: currency);

                return Ok(new DebtDtos.ApiResult<DebtDtos.VoidInvoiceResultDto>(true, null, result));
            }
            catch (Exception ex)
            {
                return FailVoid($"Void failed (rolled back): {ex.Message}");
            }
        }

        private static ActionResult<DebtDtos.ApiResult<DebtDtos.VoidInvoiceResultDto>> FailVoid(string msg) =>
            new OkObjectResult(new DebtDtos.ApiResult<DebtDtos.VoidInvoiceResultDto>(false, msg, null));

        /// <summary>The invoice header plus the bits every void decision needs.</summary>
        private static dynamic? LoadVoidableInvoice(IDbConnection conn, int invoiceId) =>
            SqlMapper.Query(conn, @"
                SELECT
                    inv.Id, inv.InvoiceNumber, inv.AppointmentId, inv.BranchId,
                    inv.CustomerId, c.CUSTOMER_NAME AS CustomerName,
                    c.CUSTOMER_REF_GUIDE AS CustomerRef,
                    inv.TotalAmount, inv.PaidAmount, inv.RemainingAmount,
                    ISNULL(inv.Currency, b.EnglishCurrencyName) AS Currency,
                    ISNULL(inv.IsVoid, 0)              AS IsVoid,
                    ISNULL(inv.IsDeferred, 0)          AS IsDeferred,
                    ISNULL(inv.IsFullyRefunded, 0)     AS IsFullyRefunded,
                    ISNULL(inv.IsPartiallyRefunded, 0) AS IsPartiallyRefunded,
                    ISNULL(inv.TotalRefunded, 0)       AS TotalRefunded,
                    inv.SettledAt, inv.SettlementId
                FROM dbo.AppointmentInvoices inv
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = inv.CustomerId
                LEFT  JOIN dbo.BRANCH   b ON b.BRANCH_ID   = inv.BranchId
                WHERE inv.Id = @Id", new { Id = invoiceId }).FirstOrDefault();

        /// <summary>
        /// Why this invoice cannot be voided, or null when it can. Refunds are the
        /// hard stop: money has already moved back to the customer through a flow
        /// with its own records, and voiding on top of that would reverse it twice.
        /// </summary>
        private static string? VoidBlockReason(dynamic inv)
        {
            if (Convert.ToInt32(inv.IsVoid) == 1)
                return "This invoice has already been voided";
            if (Convert.ToInt32(inv.IsFullyRefunded) == 1)
                return "This invoice has already been fully refunded — void does not apply";
            if (Convert.ToInt32(inv.IsPartiallyRefunded) == 1 || (decimal)inv.TotalRefunded > 0m)
                return "This invoice has refunds against it — reverse those before voiding";
            return null;
        }

        /// <summary>
        /// Every appointment the invoice touches: the lead, plus one per line.
        /// A New Sale ticket spreads across several appointments, and cancelling
        /// only the lead would leave the rest of them looking sold.
        /// </summary>
        private static List<int> CollectInvoiceAppointmentIds(
            IDbConnection conn, int invoiceId, int leadAppointmentId)
        {
            var ids = SqlMapper.Query<int>(conn, @"
                SELECT DISTINCT AppointmentId
                FROM dbo.AppointmentInvoiceLines
                WHERE InvoiceId = @InvoiceId", new { InvoiceId = invoiceId }).ToList();

            if (leadAppointmentId > 0) ids.Add(leadAppointmentId);
            return ids.Where(x => x > 0).Distinct().ToList();
        }

        private sealed record CustomerWallet(int SubscriptionId, decimal Balance);

        /// <summary>
        /// The customer's wallet and its live balance. UX_Subscriptions_CustomerRef_Active
        /// makes this at most one row per customer, which is why the lookup is by
        /// CustomerRef alone and never by wallet type.
        /// </summary>
        private static CustomerWallet? LoadCustomerWallet(IDbConnection conn, Guid customerRef)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT TOP 1 s.Id,
                       ISNULL((SELECT TOP 1 sh.Balance
                                 FROM dbo.SubscriptionsHistory sh
                                WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                ORDER BY sh.Id DESC), 0) AS Balance
                FROM dbo.Subscriptions s
                WHERE s.CustomerRef = @Ref AND ISNULL(s.Deleted, 0) = 0
                ORDER BY s.Id DESC", new { Ref = customerRef }).FirstOrDefault();

            return row == null ? null : new CustomerWallet((int)row.Id, (decimal)row.Balance);
        }

        // =====================================================================
        // POST /api/debt/settle  — collect 1..N debt invoices in one payment
        // =====================================================================
        [HttpPost("settle")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.SettleDebtResponse>> Settle(
            [FromBody] DebtDtos.SettleDebtRequest request)
        {
            try
            {
                if (request == null) return FailSettle("Request body is required");
                if (request.InvoiceIds == null || request.InvoiceIds.Count == 0)
                    return FailSettle("Select at least one invoice");

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var settings = LoadDebtSettings(conn, request.BranchId);
                if (!settings.Enabled) return FailSettle("The deferred payment flow is disabled");

                var ids = request.InvoiceIds.Distinct().ToList();

                // -------- Load + lock-check the invoices --------
                var invoices = SqlMapper.Query(conn, @"
                    SELECT
                        inv.Id, inv.InvoiceNumber, inv.AppointmentId, inv.BranchId, inv.CustomerId,
                        inv.TotalAmount, inv.PaidAmount, inv.RemainingAmount, inv.Currency,
                        ISNULL(inv.IsDeferred, 0) AS IsDeferred, inv.SettledAt,
                        ISNULL(inv.SubTotal, inv.TotalAmount) AS SubTotal,
                        ISNULL(inv.DiscountAmount, 0) AS DiscountAmount
                    FROM dbo.AppointmentInvoices inv
                    WHERE inv.Id IN @Ids", new { Ids = ids }).ToList();

                if (invoices.Count != ids.Count)
                    return FailSettle("One or more invoices no longer exist");

                foreach (var inv in invoices)
                {
                    if (Convert.ToInt32(inv.IsDeferred) != 1)
                        return FailSettle($"Invoice {inv.InvoiceNumber} is not a deferred (debt) invoice");
                    if (inv.SettledAt != null)
                        return FailSettle($"Invoice {inv.InvoiceNumber} has already been collected");
                    if ((decimal)inv.RemainingAmount <= 0m)
                        return FailSettle($"Invoice {inv.InvoiceNumber} has nothing left to collect");
                }

                // A settlement is one customer's debt, or one driver's run.
                var customerIds = invoices.Select(i => (int)i.CustomerId).Distinct().ToList();
                if (request.DriverId == null && customerIds.Count > 1)
                    return FailSettle("Invoices from different customers cannot be collected together");

                int? settlementCustomerId = customerIds.Count == 1 ? customerIds[0] : (int?)null;
                string currency = (string?)invoices[0].Currency ?? "KWD";
                int branchId = request.BranchId > 0 ? request.BranchId : (int)invoices[0].BranchId;

                // -------- Discount --------
                decimal totalBefore = Round3(invoices.Sum(i => (decimal)i.RemainingAmount));
                string? discountType = request.Discount?.Type?.Trim().ToLowerInvariant();
                decimal discountValue = request.Discount?.Value ?? 0m;
                decimal discountAmount = 0m;

                if (discountType == "percentage" || discountType == "fixed")
                {
                    if (!settings.AllowSettlementDiscount)
                        return FailSettle("Discount on collection is disabled");

                    discountAmount = discountType == "percentage"
                        ? Round3(totalBefore * Math.Min(100m, Math.Max(0m, discountValue)) / 100m)
                        : Round3(Math.Min(Math.Max(0m, discountValue), totalBefore));
                }
                if (discountAmount <= 0m) { discountType = null; discountAmount = 0m; }

                decimal totalCollected = Round3(totalBefore - discountAmount);

                // -------- Split the discount across the invoices --------
                var amountsBefore = invoices.Select(i => (decimal)i.RemainingAmount).ToList();
                var discountShares = DistributeProportionally(amountsBefore, discountAmount);

                // -------- Validate the payment --------
                decimal walletAmount = 0m;
                int? walletSubId = null, walletPtId = null;
                decimal walletBalanceBefore = 0m;
                Guid walletCustomerRef = Guid.Empty;

                var splits = request.Payments?.Splits ?? new List<DebtDtos.DebtSplitPaymentRequest>();

                if (request.Payments?.WalletAmount is > 0)
                {
                    if (settlementCustomerId == null)
                        return FailSettle("Wallet payment needs a single customer");

                    walletAmount = request.Payments.WalletAmount!.Value;
                    walletSubId = request.Payments.WalletSubscriptionId;
                    walletPtId = request.Payments.WalletPaymentTypeId;

                    if (walletSubId == null) return FailSettle("WalletAmount given but WalletSubscriptionId is missing");
                    if (walletPtId == null) return FailSettle("WalletAmount given but WalletPaymentTypeId is missing");

                    var sub = SqlMapper.Query(conn, @"
                        SELECT s.Id, s.CustomerRef, s.EndDate,
                               ISNULL(s.Deleted, 0) AS Deleted, ISNULL(s.IsPaid, 0) AS IsPaid,
                               ISNULL((SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                                        WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                        ORDER BY sh.Id DESC), 0) AS CurrentBalance
                        FROM dbo.Subscriptions s WHERE s.Id = @Id",
                        new { Id = walletSubId.Value }).FirstOrDefault();

                    if (sub == null || (int)sub.Deleted == 1) return FailSettle("Wallet subscription not found");
                    if ((int)sub.IsPaid != 1) return FailSettle("Wallet subscription is not paid");
                    if ((DateTime)sub.EndDate < DateTime.UtcNow) return FailSettle("Wallet subscription has expired");

                    walletBalanceBefore = (decimal)sub.CurrentBalance;
                    walletCustomerRef = (Guid)sub.CustomerRef;
                    if (walletBalanceBefore < walletAmount)
                        return FailSettle($"Insufficient wallet balance. Available: {walletBalanceBefore:F3}");
                }

                decimal splitsTotal = 0m;
                foreach (var sp in splits)
                {
                    if (sp.Amount <= 0) return FailSettle("Split payment amount must be greater than 0");
                    var pt = SqlMapper.Query(conn,
                        "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = sp.PaymentTypeId }).FirstOrDefault();
                    if (pt == null) return FailSettle($"Payment type #{sp.PaymentTypeId} not found");
                    splitsTotal += sp.Amount;
                }

                decimal paidTotal = Round3(walletAmount + splitsTotal);
                if (Math.Abs(paidTotal - totalCollected) > 0.0001m)
                    return FailSettle($"Collection must be paid in full. Due: {totalCollected:F3}, Paid: {paidTotal:F3}");

                int currentUserId = ResolveCurrentUserId();

                // -------- Atomic write --------
                var settled = new List<DebtDtos.SettledInvoiceDto>();
                int settlementId;
                string settlementNumber;
                DateTime settledAt = DateTime.UtcNow;

                using (var uow = new UnitOfWork(conn))
                {
                    settlementNumber = InvoiceNumberService.Next(uow.Connection, "DBT");

                    settlementId = SqlMapper.Query<int>(uow.Connection, @"
                        INSERT INTO dbo.DebtSettlements (
                            SettlementNumber, BranchId, CustomerId, DriverId, InvoiceCount,
                            TotalBefore, DiscountType, DiscountValue, DiscountAmount,
                            TotalCollected, Currency, SettledAt, SettledBy, Notes, Deleted
                        )
                        OUTPUT INSERTED.Id
                        VALUES (
                            @Number, @BranchId, @CustomerId, @DriverId, @Count,
                            @TotalBefore, @DiscountType, @DiscountValue, @DiscountAmount,
                            @TotalCollected, @Currency, @SettledAt, @SettledBy, @Notes, 0
                        )",
                        new
                        {
                            Number = settlementNumber,
                            BranchId = branchId,
                            CustomerId = settlementCustomerId,
                            DriverId = request.DriverId,
                            Count = invoices.Count,
                            TotalBefore = totalBefore,
                            DiscountType = discountType,
                            DiscountValue = discountType == null ? (decimal?)null : discountValue,
                            DiscountAmount = discountAmount,
                            TotalCollected = totalCollected,
                            Currency = currency,
                            SettledAt = settledAt,
                            SettledBy = currentUserId > 0 ? currentUserId : (int?)null,
                            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
                        }).First();

                    // Payments are recorded once on the settlement, and again per invoice
                    // as real AppointmentPayments rows (see below) so the money lands in
                    // every existing report without them knowing debt exists.
                    if (walletAmount > 0 && walletSubId.HasValue && walletPtId.HasValue)
                    {
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.DebtSettlementPayments
                                (SettlementId, PaymentTypeId, Amount, IsWalletPayment, WalletSubscriptionId, VoucherCode)
                            VALUES (@SettlementId, @PaymentTypeId, @Amount, 1, @SubId, NULL)",
                            new
                            {
                                SettlementId = settlementId,
                                PaymentTypeId = walletPtId.Value,
                                Amount = walletAmount,
                                SubId = walletSubId.Value
                            });

                        decimal newBalance = walletBalanceBefore - walletAmount;

                        // InvoiceId must stay NULL: the column is a foreign key onto
                        // the legacy dbo.INVOICE_HEADER, not onto AppointmentInvoices.
                        // It used to be handed invoices[0].Id here, which meant every
                        // attempt to settle a debt from the customer's wallet died on
                        // FK_SubscriptionsHistory_InvoiceId and rolled the whole
                        // collection back. The settlement id below is the durable link
                        // between this ledger row and the invoices it paid off.
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.SubscriptionsHistory
                                (CustomerRef, RefType, InvoiceId, SubscriptionId, Amount, Balance, AddedBy, AddedDate, Deleted)
                            VALUES (@CustomerRef, 1, NULL, @SubscriptionId, @Amount, @Balance, @AddedBy, @AddedDate, 0)",
                            new
                            {
                                CustomerRef = walletCustomerRef,
                                SubscriptionId = walletSubId.Value,
                                Amount = -walletAmount,
                                Balance = newBalance,
                                AddedBy = currentUserId,
                                AddedDate = settledAt
                            });
                    }

                    foreach (var sp in splits)
                    {
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.DebtSettlementPayments
                                (SettlementId, PaymentTypeId, Amount, IsWalletPayment, WalletSubscriptionId, VoucherCode)
                            VALUES (@SettlementId, @PaymentTypeId, @Amount, 0, NULL, @VoucherCode)",
                            new
                            {
                                SettlementId = settlementId,
                                sp.PaymentTypeId,
                                sp.Amount,
                                VoucherCode = string.IsNullOrWhiteSpace(sp.VoucherCode) ? null : sp.VoucherCode.Trim()
                            });
                    }

                    // How the money maps back onto individual invoices: each invoice takes
                    // its share of every payment method, proportional to what it collected.
                    var collectedPerInvoice = new List<decimal>();
                    for (int i = 0; i < invoices.Count; i++)
                        collectedPerInvoice.Add(Round3(amountsBefore[i] - discountShares[i]));

                    var methodRows = new List<(int PaymentTypeId, decimal Amount, bool IsWallet, string? Voucher)>();
                    if (walletAmount > 0 && walletPtId.HasValue)
                        methodRows.Add((walletPtId.Value, walletAmount, true, null));
                    foreach (var sp in splits)
                        methodRows.Add((sp.PaymentTypeId, sp.Amount, false, sp.VoucherCode));

                    // Per-method allocation across invoices (largest remainder → exact totals).
                    var perMethodShares = methodRows
                        .Select(m => DistributeProportionally(collectedPerInvoice, m.Amount))
                        .ToList();

                    for (int i = 0; i < invoices.Count; i++)
                    {
                        var inv = invoices[i];
                        int invoiceId = (int)inv.Id;
                        int leadApptId = (int)inv.AppointmentId;
                        decimal before = amountsBefore[i];
                        decimal share = discountShares[i];
                        decimal collected = collectedPerInvoice[i];

                        // The invoice's total shrinks by the write-off so Total = Paid and
                        // Remaining = 0. Anything else would leave a phantom balance behind.
                        decimal newTotal = Round3((decimal)inv.TotalAmount - share);
                        decimal newPaid = Round3((decimal)inv.PaidAmount + collected);

                        SqlMapper.Execute(uow.Connection, @"
                            UPDATE dbo.AppointmentInvoices
                            SET TotalAmount        = @NewTotal,
                                PaidAmount         = @NewPaid,
                                RemainingAmount    = 0,
                                PaymentStatus      = 'FULL',
                                SettledAt          = @SettledAt,
                                SettlementId       = @SettlementId,
                                DebtDiscountType   = @DiscType,
                                DebtDiscountValue  = @DiscValue,
                                DebtDiscountAmount = @DiscShare
                            WHERE Id = @InvoiceId",
                            new
                            {
                                NewTotal = newTotal,
                                NewPaid = newPaid,
                                SettledAt = settledAt,
                                SettlementId = settlementId,
                                DiscType = share > 0 ? discountType : null,
                                DiscValue = share > 0 ? (decimal?)discountValue : null,
                                DiscShare = share > 0 ? (decimal?)share : null,
                                InvoiceId = invoiceId
                            });

                        // The sale rows behind the invoice follow suit, so a per-appointment
                        // report never disagrees with the invoice header.
                        SqlMapper.Execute(uow.Connection, @"
                            UPDATE a
                            SET a.PaidAmount    = a.TotalPrice,
                                a.PaymentStatus = 'FULL'
                            FROM dbo.AppointmentData a
                            INNER JOIN dbo.AppointmentInvoiceLines l ON l.AppointmentId = a.Id
                            WHERE l.InvoiceId = @InvoiceId",
                            new { InvoiceId = invoiceId });

                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.DebtSettlementInvoices
                                (SettlementId, InvoiceId, InvoiceNumber, AppointmentId,
                                 AmountBefore, DiscountShare, AmountCollected)
                            VALUES (@SettlementId, @InvoiceId, @InvoiceNumber, @AppointmentId,
                                    @Before, @Share, @Collected)",
                            new
                            {
                                SettlementId = settlementId,
                                InvoiceId = invoiceId,
                                InvoiceNumber = (string?)inv.InvoiceNumber,
                                AppointmentId = leadApptId,
                                Before = before,
                                Share = share,
                                Collected = collected
                            });

                        // Real payment rows — this is what makes the collection visible to
                        // the dashboard, the invoice dialog's "Paid via", and refunds.
                        for (int m = 0; m < methodRows.Count; m++)
                        {
                            decimal amt = perMethodShares[m][i];
                            if (amt <= 0m) continue;
                            var meth = methodRows[m];

                            SqlMapper.Execute(uow.Connection, @"
                                INSERT INTO dbo.AppointmentPayments
                                    (AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt, IsWalletPayment)
                                VALUES (@AppointmentId, @Amount, @PaymentTypeId, 'FULL', @VoucherCode, @PaidAt, @IsWallet)",
                                new
                                {
                                    AppointmentId = leadApptId,
                                    Amount = amt,
                                    meth.PaymentTypeId,
                                    VoucherCode = meth.Voucher,
                                    PaidAt = settledAt,
                                    IsWallet = meth.IsWallet ? 1 : 0
                                });
                        }

                        settled.Add(new DebtDtos.SettledInvoiceDto(
                            InvoiceId: invoiceId,
                            InvoiceNumber: (string?)inv.InvoiceNumber ?? "",
                            AmountBefore: before,
                            DiscountShare: share,
                            AmountCollected: collected,
                            NewTotalAmount: newTotal));
                    }

                    uow.Commit();
                }

                var response = new DebtDtos.SettleDebtResponse(
                    SettlementId: settlementId,
                    SettlementNumber: settlementNumber,
                    SettledAt: settledAt,
                    InvoiceCount: settled.Count,
                    TotalBefore: totalBefore,
                    DiscountAmount: discountAmount,
                    TotalCollected: totalCollected,
                    WalletDeductedAmount: walletAmount,
                    Currency: currency,
                    Invoices: settled,
                    WhatsAppSent: false,
                    WhatsAppError: null);

                return Ok(new DebtDtos.ApiResult<DebtDtos.SettleDebtResponse>(true, null, response));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.SettleDebtResponse>(
                    false, $"Failed to collect debt: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/debt/settlement/{id}
        // =====================================================================
        [HttpGet("settlement/{settlementId:int}")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.CustomerSettlementRowDto>> Settlement(int settlementId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var row = LoadSettlementRows(conn, null, settlementId).FirstOrDefault();
                if (row == null)
                    return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerSettlementRowDto>(false, "Settlement not found", null));

                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerSettlementRowDto>(true, null, row));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.CustomerSettlementRowDto>(
                    false, $"Failed to load settlement: {ex.Message}", null));
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private ActionResult<DebtDtos.ApiResult<DebtDtos.SettleDebtResponse>> FailSettle(string error) =>
            Ok(new DebtDtos.ApiResult<DebtDtos.SettleDebtResponse>(false, error, null));

        /// <summary>The three debt flags, branch override winning over the global row.</summary>
        public static DebtDtos.DebtSettingsDto LoadDebtSettings(IDbConnection conn, int? branchId)
        {
            return new DebtDtos.DebtSettingsDto(
                Enabled: BusinessSettingsService.GetBool(conn, "debt.enabled", false, branchId),
                AllowSettlementDiscount: BusinessSettingsService.GetBool(conn, "debt.allowSettlementDiscount", true, branchId),
                CustomerLimit: BusinessSettingsService.GetDecimal(conn, "debt.customerLimit", 0m, branchId));
        }

        /// <summary>Open debt for one customer — also used by the POS to gate the ticket.</summary>
        public static decimal GetCustomerOpenDebt(IDbConnection conn, int customerId, int? branchId = null)
        {
            return SqlMapper.Query<decimal?>(conn, @"
                SELECT SUM(RemainingAmount)
                FROM dbo.AppointmentInvoices
                WHERE CustomerId = @CustomerId
                  AND IsDeferred = 1 AND SettledAt IS NULL AND RemainingAmount > 0
                  AND (@BranchId IS NULL OR BranchId = @BranchId)",
                new { CustomerId = customerId, BranchId = branchId }).FirstOrDefault() ?? 0m;
        }

        private static DebtDtos.CustomerDebtSummaryDto? LoadCustomerDebtSummary(
            IDbConnection conn, int customerId, int? branchId)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT
                    c.CUSTOMER_ID     AS CustomerId,
                    c.CUSTOMER_NAME   AS CustomerName,
                    c.CUSTOMER_PHONE1 AS CustomerPhone,
                    b.EnglishCurrencyName AS Currency,
                    ISNULL(d.TotalDebt, 0)    AS TotalDebt,
                    ISNULL(d.InvoiceCount, 0) AS InvoiceCount,
                    d.OldestInvoiceAt,
                    (SELECT MAX(s.SettledAt) FROM dbo.DebtSettlements s
                      WHERE s.CustomerId = c.CUSTOMER_ID AND s.Deleted = 0) AS LastPaymentAt
                FROM dbo.CUSTOMER c
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = c.BRANCH_ID
                OUTER APPLY (
                    SELECT SUM(inv.RemainingAmount) AS TotalDebt,
                           COUNT(*)                 AS InvoiceCount,
                           MIN(inv.CreatedAt)       AS OldestInvoiceAt
                    FROM dbo.AppointmentInvoices inv
                    WHERE inv.CustomerId = c.CUSTOMER_ID
                      AND inv.IsDeferred = 1 AND inv.SettledAt IS NULL AND inv.RemainingAmount > 0
                      AND (@BranchId IS NULL OR inv.BranchId = @BranchId)
                ) d
                WHERE c.CUSTOMER_ID = @Id",
                new { Id = customerId, BranchId = branchId }).FirstOrDefault();

            if (row == null) return null;

            return new DebtDtos.CustomerDebtSummaryDto(
                CustomerId: (int)row.CustomerId,
                CustomerName: (string?)row.CustomerName ?? "",
                CustomerPhone: (string?)row.CustomerPhone ?? "",
                TotalDebt: (decimal)row.TotalDebt,
                InvoiceCount: (int)row.InvoiceCount,
                OldestInvoiceAt: (DateTime?)row.OldestInvoiceAt,
                LastPaymentAt: (DateTime?)row.LastPaymentAt,
                Currency: (string?)row.Currency ?? "KWD");
        }

        /// <summary>
        /// Builds the WHERE fragment + parameters for the /orders table.
        ///
        /// Two things shift with the tab:
        ///   • the date range and the amount range follow the money — CreatedAt +
        ///     RemainingAmount on the unpaid tab, PaidAt + PaidAmount on the paid
        ///     ones. Filtering a paid invoice by "what's still owed" would match
        ///     every row at zero, which is not a filter.
        ///   • the mandatory tab predicate is prepended here so no caller can
        ///     forget it.
        /// </summary>
        private static (string Where, Dapper.DynamicParameters Params) BuildDebtWhere(
            string tab,
            int? branchId, string? search, string? invoiceNumber, int? customerId, int? driverId,
            int? areaId, int? governorateId, string? orderType,
            DateTime? dateFrom, DateTime? dateTo, decimal? minAmount, decimal? maxAmount,
            bool onlyOverdue, int? paymentTypeId, bool walletOnly, int tzOffset)
        {
            bool isPaidTab = tab != TabUnpaid;
            string dateCol = isPaidTab ? PaidAtExpr : "inv.CreatedAt";
            string amountCol = isPaidTab ? "inv.PaidAmount" : "inv.RemainingAmount";

            var sb = new StringBuilder($"({TabPredicate(tab)})");
            sb.Append(" AND (@BranchId IS NULL OR inv.BranchId = @BranchId)");

            if (customerId.HasValue) sb.Append(" AND inv.CustomerId = @CustomerId");
            if (driverId.HasValue) sb.Append(" AND ISNULL(idl.DriverId, inv.DeliveryDriverId) = @DriverId");
            if (areaId.HasValue) sb.Append(" AND idl.AreaId = @AreaId");
            if (governorateId.HasValue) sb.Append(" AND idl.GovernorateId = @GovernorateId");

            if (orderType == "delivery") sb.Append(" AND ISNULL(idl.IsDelivery, 0) = 1");
            else if (orderType == "pickup") sb.Append(" AND ISNULL(idl.IsDelivery, 0) = 0");

            if (!string.IsNullOrWhiteSpace(invoiceNumber))
                sb.Append(" AND inv.InvoiceNumber LIKE '%' + @InvoiceNumber + '%'");

            // Dates are branch-local in the UI, UTC in the column.
            if (dateFrom.HasValue) sb.Append($" AND {dateCol} >= @DateFromUtc");
            if (dateTo.HasValue) sb.Append($" AND {dateCol} < @DateToUtc");

            if (minAmount.HasValue) sb.Append($" AND {amountCol} >= @MinAmount");
            if (maxAmount.HasValue) sb.Append($" AND {amountCol} <= @MaxAmount");

            // "Overdue" is an open-debt idea; on the paid tabs it has nothing to say.
            if (onlyOverdue && !isPaidTab)
                sb.Append($" AND inv.CreatedAt < DATEADD(day, -{OverdueDays}, SYSUTCDATETIME())");

            // Narrow to one payment method — "show me everything settled by KNET".
            if (isPaidTab && paymentTypeId.HasValue)
                sb.Append(@" AND EXISTS (
                        SELECT 1
                        FROM dbo.AppointmentPayments fap
                        WHERE fap.PaymentTypeId = @PaymentTypeId
                          AND fap.Amount > 0
                          AND (fap.AppointmentId = inv.AppointmentId
                               OR fap.AppointmentId IN (
                                    SELECT fl.AppointmentId
                                    FROM dbo.AppointmentInvoiceLines fl
                                    WHERE fl.InvoiceId = inv.Id))
                    )");

            // Wallet tab only: drop the invoices where the wallet was topped up
            // with cash or a card, leaving the ones the wallet covered outright.
            if (tab == TabWallet && walletOnly)
                sb.Append($" AND NOT {NonWalletPaymentExists}");

            if (!string.IsNullOrWhiteSpace(search))
                sb.Append(@" AND (
                    inv.InvoiceNumber LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_NAME   LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_PHONE2 LIKE '%' + @Search + '%' OR
                    idl.DriverName    LIKE '%' + @Search + '%' OR
                    idl.DriverNameAr  LIKE '%' + @Search + '%' OR
                    idl.DriverPhone   LIKE '%' + @Search + '%' OR
                    idl.AreaNameEn    LIKE '%' + @Search + '%' OR
                    idl.AreaNameAr    LIKE '%' + @Search + '%')");

            // DynamicParameters rather than an anonymous type so the paging query
            // can bolt @Skip / @Take onto the very same set.
            var prm = new Dapper.DynamicParameters();
            prm.Add("BranchId", branchId);
            prm.Add("CustomerId", customerId);
            prm.Add("DriverId", driverId);
            prm.Add("AreaId", areaId);
            prm.Add("GovernorateId", governorateId);
            prm.Add("PaymentTypeId", paymentTypeId);
            prm.Add("InvoiceNumber", invoiceNumber?.Trim());
            prm.Add("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim());
            prm.Add("MinAmount", minAmount);
            prm.Add("MaxAmount", maxAmount);
            // local midnight → UTC instant; DateToUtc is inclusive of the whole end day
            prm.Add("DateFromUtc", dateFrom?.Date.AddHours(-tzOffset));
            prm.Add("DateToUtc", dateTo?.Date.AddDays(1).AddHours(-tzOffset));

            return (sb.ToString(), prm);
        }

        // =====================================================================
        // Shared SQL for the /orders table
        // =====================================================================

        /// <summary>The join graph behind every invoice list. Aliases are load-bearing:
        /// the WHERE fragments above are written against inv / c / idl / b.</summary>
        private const string InvoiceFromJoins = @"
            FROM dbo.AppointmentInvoices inv
            INNER JOIN dbo.CUSTOMER c          ON c.CUSTOMER_ID = inv.CustomerId
            LEFT  JOIN dbo.BRANCH   b          ON b.BRANCH_ID   = inv.BranchId
            LEFT  JOIN dbo.InvoiceDelivery idl ON idl.InvoiceId = inv.Id
            LEFT  JOIN dbo.AppointmentData a   ON a.Id = inv.AppointmentId";

        /// <summary>
        /// The row projection. ServicesSummary, the payment breakdown and refunds
        /// are deliberately absent: they are loaded per page, after paging, so a
        /// 200k-row filter never pays for data that 25 rows will use.
        /// </summary>
        private static string InvoiceSelectColumns => $@"
            inv.Id              AS InvoiceId,
            inv.InvoiceNumber   AS InvoiceNumber,
            inv.AppointmentId   AS LeadAppointmentId,
            inv.BranchId        AS BranchId,
            inv.CreatedAt       AS CreatedAt,
            inv.CustomerId      AS CustomerId,
            c.CUSTOMER_NAME     AS CustomerName,
            c.CUSTOMER_PHONE1   AS CustomerPhone,
            c.CUSTOMER_PHONE2   AS CustomerPhone2,
            ISNULL(inv.SubTotal, inv.TotalAmount) AS SubTotal,
            ISNULL(inv.DiscountAmount, 0)         AS DiscountAmount,
            ISNULL(inv.DeliveryCharge, 0)         AS DeliveryCharge,
            inv.TotalAmount     AS TotalAmount,
            inv.PaidAmount      AS PaidAmount,
            inv.RemainingAmount AS RemainingAmount,
            ISNULL(inv.Currency, b.EnglishCurrencyName) AS Currency,

            ISNULL(idl.IsDelivery, 0) AS IsDelivery,
            idl.DeliveryTypeId, idl.DeliveryTypeNameEn, idl.DeliveryTypeNameAr,
            ISNULL(idl.DriverId, inv.DeliveryDriverId) AS DriverId,
            idl.DriverName, idl.DriverNameAr, idl.DriverPhone,
            idl.AreaId, idl.AreaNameEn, idl.AreaNameAr,
            idl.GovernorateId, idl.GovernorateNameEn, idl.GovernorateNameAr,
            idl.AddressBlock, idl.AddressStreet, idl.AddressBuilding, idl.AddressFlat,
            idl.DeliveryDate,
            a.Notes AS Notes,

            inv.PaymentStatus         AS PaymentStatus,
            ISNULL(inv.IsDeferred, 0) AS IsDeferred,
            inv.SettledAt             AS SettledAt,
            {PaidAtExpr}              AS PaidAt,
            ISNULL(inv.IsVoid, 0)     AS IsVoid,
            inv.VoidedAt              AS VoidedAt,
            inv.VoidReason            AS VoidReason,

            (SELECT COUNT(*) FROM dbo.AppointmentInvoiceLines l
              WHERE l.InvoiceId = inv.Id AND ISNULL(l.IsRefunded, 0) = 0) AS ItemCount,
            DATEDIFF(day, inv.CreatedAt, SYSUTCDATETIME())   AS AgeDays,
            DATEDIFF(day, {PaidAtExpr}, SYSUTCDATETIME())    AS PaidAgeDays";

        /// <summary>
        /// Turns a sort key into an ORDER BY expression. 'age' inverts the
        /// direction on purpose: the oldest invoice has the largest age but the
        /// smallest date, and the UI arrow points at age, not at the column.
        /// </summary>
        private static string BuildOrderBy(string tab, string? sortBy, string? sortDir)
        {
            bool isPaidTab = tab != TabUnpaid;
            bool asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            string key = (sortBy ?? "date").ToLowerInvariant();

            string col = key switch
            {
                "amount" => isPaidTab ? "inv.PaidAmount" : "inv.RemainingAmount",
                "customer" => "c.CUSTOMER_NAME",
                "age" => isPaidTab ? PaidAtExpr : "inv.CreatedAt",
                _ => isPaidTab ? PaidAtExpr : "inv.CreatedAt"
            };

            bool flip = key == "age";
            string dir = (asc ^ flip) ? "ASC" : "DESC";

            // inv.Id breaks ties so paging can never show or skip a row twice.
            return $"{col} {dir}, inv.Id DESC";
        }

        /// <summary>One page of rows, sliced in SQL.</summary>
        private static List<DebtDtos.DebtInvoiceDto> QueryInvoicePage(
            IDbConnection conn, string tab, string where, Dapper.DynamicParameters prm,
            string? sortBy, string? sortDir, int page, int pageSize)
        {
            prm.Add("Skip", (page - 1) * pageSize);
            prm.Add("Take", pageSize);

            var rows = SqlMapper.Query(conn, $@"
                SELECT {InvoiceSelectColumns}
                {InvoiceFromJoins}
                WHERE ({where})
                ORDER BY {BuildOrderBy(tab, sortBy, sortDir)}
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", prm).ToList();

            return rows.Select(r => (DebtDtos.DebtInvoiceDto)MapInvoiceRow(r)).ToList();
        }

        /// <summary>
        /// Totals across the entire filter. The wallet split needs the payments
        /// table, so it is only computed on the tabs that show it — and it uses the
        /// UNION map rather than an OR-join, because an OR across two appointment
        /// sources costs every index seek on AppointmentPayments.
        /// </summary>
        private static DebtDtos.DebtSummaryDto LoadSummary(
            IDbConnection conn, string tab, string where, Dapper.DynamicParameters prm, int? branchId)
        {
            bool isPaidTab = tab != TabUnpaid;

            var agg = SqlMapper.Query(conn, $@"
                SELECT
                    COUNT(*)                        AS InvoiceCount,
                    COUNT(DISTINCT inv.CustomerId)  AS CustomerCount,
                    ISNULL(SUM(inv.RemainingAmount), 0) AS TotalDebt,
                    ISNULL(SUM(inv.PaidAmount), 0)      AS TotalPaid,
                    ISNULL(SUM(CASE WHEN ISNULL(idl.IsDelivery, 0) = 1
                                    THEN inv.RemainingAmount ELSE 0 END), 0) AS DeliveryDebt,
                    ISNULL(SUM(CASE WHEN ISNULL(idl.IsDelivery, 0) = 0
                                    THEN inv.RemainingAmount ELSE 0 END), 0) AS PickupDebt,
                    ISNULL(SUM(CASE WHEN inv.CreatedAt < DATEADD(day, -{OverdueDays}, SYSUTCDATETIME())
                                    THEN inv.RemainingAmount ELSE 0 END), 0) AS OverdueDebt,
                    MAX(ISNULL(inv.Currency, b.EnglishCurrencyName)) AS Currency
                {InvoiceFromJoins}
                WHERE ({where})", prm).FirstOrDefault();

            int invoiceCount = agg == null ? 0 : (int)agg.InvoiceCount;

            string currency = (agg == null ? null : (string?)agg.Currency)
                ?? SqlMapper.Query<string>(conn,
                    "SELECT TOP 1 EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                    new { Id = branchId }).FirstOrDefault() ?? "KWD";

            decimal walletPaid = 0m, otherPaid = 0m, totalRefunded = 0m;
            int walletInvoiceCount = 0;

            if (isPaidTab && invoiceCount > 0)
            {
                var money = SqlMapper.Query(conn, $@"
                    WITH Base AS (
                        SELECT inv.Id AS InvoiceId, inv.AppointmentId
                        {InvoiceFromJoins}
                        WHERE ({where})
                    ),
                    Map AS (
                        SELECT b2.InvoiceId, b2.AppointmentId FROM Base b2
                        UNION
                        SELECT b2.InvoiceId, l.AppointmentId
                        FROM Base b2
                        INNER JOIN dbo.AppointmentInvoiceLines l ON l.InvoiceId = b2.InvoiceId
                    ),
                    Pay AS (
                        SELECT m.InvoiceId,
                               ISNULL(SUM(CASE WHEN ISNULL(ap.IsWalletPayment, 0) = 1
                                               THEN ap.Amount ELSE 0 END), 0) AS WalletAmt,
                               ISNULL(SUM(CASE WHEN ISNULL(ap.IsWalletPayment, 0) = 0
                                               THEN ap.Amount ELSE 0 END), 0) AS OtherAmt
                        FROM Map m
                        INNER JOIN dbo.AppointmentPayments ap ON ap.AppointmentId = m.AppointmentId
                        GROUP BY m.InvoiceId
                    )
                    SELECT
                        ISNULL(SUM(p.WalletAmt), 0) AS WalletPaid,
                        ISNULL(SUM(p.OtherAmt), 0)  AS OtherPaid,
                        SUM(CASE WHEN p.WalletAmt > 0 THEN 1 ELSE 0 END) AS WalletInvoiceCount,
                        (SELECT ISNULL(SUM(r.RefundAmount), 0)
                           FROM dbo.RefundTransactions r
                          WHERE ISNULL(r.Deleted, 0) = 0
                            AND r.InvoiceId IN (SELECT InvoiceId FROM Base)) AS TotalRefunded
                    FROM Pay p", prm).FirstOrDefault();

                if (money != null)
                {
                    walletPaid = Round3((decimal)money.WalletPaid);
                    otherPaid = Round3((decimal)money.OtherPaid);
                    walletInvoiceCount = (int)(money.WalletInvoiceCount ?? 0);
                    totalRefunded = Round3((decimal)money.TotalRefunded);
                }
            }

            return new DebtDtos.DebtSummaryDto(
                InvoiceCount: invoiceCount,
                TotalDebt: agg == null ? 0m : Round3((decimal)agg.TotalDebt),
                CustomerCount: agg == null ? 0 : (int)agg.CustomerCount,
                DeliveryDebt: agg == null ? 0m : Round3((decimal)agg.DeliveryDebt),
                PickupDebt: agg == null ? 0m : Round3((decimal)agg.PickupDebt),
                OverdueDebt: agg == null ? 0m : Round3((decimal)agg.OverdueDebt),
                OverdueDays: OverdueDays,
                Currency: currency,
                TotalPaid: agg == null ? 0m : Round3((decimal)agg.TotalPaid),
                WalletPaid: walletPaid,
                OtherPaid: otherPaid,
                TotalRefunded: totalRefunded,
                WalletInvoiceCount: walletInvoiceCount);
        }

        /// <summary>
        /// "Cash 12.000 · Wallet 5.000" per invoice, for one page, in one round
        /// trip. Grouped exactly the way the invoice dialog groups it, so the row
        /// and the dialog can never tell the cashier two different stories.
        /// </summary>
        private static Dictionary<int, List<DebtDtos.InvoicePaymentMethodDto>> LoadPaymentBreakdown(
            IDbConnection conn, List<int> invoiceIds)
        {
            var map = new Dictionary<int, List<DebtDtos.InvoicePaymentMethodDto>>();
            if (invoiceIds == null || invoiceIds.Count == 0) return map;

            var rows = SqlMapper.Query(conn, @"
                WITH Map AS (
                    SELECT inv.Id AS InvoiceId, inv.AppointmentId
                    FROM dbo.AppointmentInvoices inv
                    WHERE inv.Id IN @Ids
                    UNION
                    SELECT l.InvoiceId, l.AppointmentId
                    FROM dbo.AppointmentInvoiceLines l
                    WHERE l.InvoiceId IN @Ids
                )
                SELECT
                    m.InvoiceId,
                    ap.PaymentTypeId,
                    ISNULL(ap.IsWalletPayment, 0)  AS IsWallet,
                    SUM(ap.Amount)                 AS Amount,
                    MIN(ap.PaidAt)                 AS FirstPaidAt,
                    pt.INVOICE_PAYMENT_TYPE_NAME1  AS NameEn,
                    pt.INVOICE_PAYMENT_TYPE_NAME2  AS NameAr
                FROM Map m
                INNER JOIN dbo.AppointmentPayments ap ON ap.AppointmentId = m.AppointmentId
                LEFT  JOIN dbo.INVOICE_PAYMENT_TYPE pt
                       ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                GROUP BY m.InvoiceId, ap.PaymentTypeId, ISNULL(ap.IsWalletPayment, 0),
                         pt.INVOICE_PAYMENT_TYPE_NAME1, pt.INVOICE_PAYMENT_TYPE_NAME2
                ORDER BY m.InvoiceId, MIN(ap.PaidAt)", new { Ids = invoiceIds }).ToList();

            foreach (var g in rows.GroupBy(r => (int)r.InvoiceId))
            {
                map[g.Key] = g.Select(r =>
                {
                    bool isWallet = Convert.ToInt32(r.IsWallet) == 1;
                    // A wallet deduction is booked against an ordinary payment type,
                    // so labelling it by that type would read "Cash" for money that
                    // never touched the drawer.
                    return new DebtDtos.InvoicePaymentMethodDto(
                        PaymentTypeId: (int)r.PaymentTypeId,
                        NameEn: isWallet ? "Wallet" : ((string?)r.NameEn ?? ""),
                        NameAr: isWallet ? "محفظة" : ((string?)r.NameAr ?? (string?)r.NameEn ?? ""),
                        Amount: Round3((decimal)r.Amount),
                        IsWallet: isWallet);
                }).ToList();
            }

            return map;
        }

        /// <summary>Refunded totals for one page of invoices.</summary>
        private static Dictionary<int, decimal> LoadRefundTotals(IDbConnection conn, List<int> invoiceIds)
        {
            var map = new Dictionary<int, decimal>();
            if (invoiceIds == null || invoiceIds.Count == 0) return map;

            var rows = SqlMapper.Query(conn, @"
                SELECT r.InvoiceId, SUM(r.RefundAmount) AS Total
                FROM dbo.RefundTransactions r
                WHERE r.InvoiceId IN @Ids AND ISNULL(r.Deleted, 0) = 0
                GROUP BY r.InvoiceId", new { Ids = invoiceIds }).ToList();

            foreach (var r in rows)
                map[(int)r.InvoiceId] = Round3((decimal)r.Total);

            return map;
        }

        /// <summary>
        /// The single projection every debt list uses. `extraWhere` is appended to
        /// the mandatory "open debt" predicate — callers never have to repeat it.
        /// </summary>
        private static List<DebtDtos.DebtInvoiceDto> QueryDebtInvoices(
            IDbConnection conn, string extraWhere, object prm, int tzOffset)
        {
            var rows = SqlMapper.Query(conn, $@"
                SELECT {InvoiceSelectColumns}
                {InvoiceFromJoins}
                WHERE inv.IsDeferred = 1
                  AND inv.SettledAt IS NULL
                  AND inv.RemainingAmount > 0
                  AND ISNULL(inv.IsVoid, 0) = 0
                  AND ({extraWhere})
                ORDER BY inv.CreatedAt DESC", prm).ToList();

            var list = rows.Select(r => (DebtDtos.DebtInvoiceDto)MapInvoiceRow(r)).ToList();

            var summaries = LoadServiceSummaries(conn, list.Select(x => x.InvoiceId).ToList());
            return list
                .Select(x =>
                {
                    summaries.TryGetValue(x.InvoiceId, out var s);
                    return x with { ServicesSummary = s };
                })
                .ToList();
        }

        /// <summary>
        /// Row → DTO for the shared projection. Everything that needs a second
        /// query (services, payment methods, refunds) is filled in afterwards by
        /// the caller, so this stays a pure, allocation-only mapping.
        /// </summary>
        private static DebtDtos.DebtInvoiceDto MapInvoiceRow(dynamic r)
        {
            return new DebtDtos.DebtInvoiceDto(
                InvoiceId: (int)r.InvoiceId,
                InvoiceNumber: (string?)r.InvoiceNumber ?? "",
                LeadAppointmentId: (int)r.LeadAppointmentId,
                BranchId: (int)r.BranchId,
                CreatedAt: (DateTime)r.CreatedAt,
                CustomerId: (int)r.CustomerId,
                CustomerName: (string?)r.CustomerName ?? "",
                CustomerPhone: (string?)r.CustomerPhone ?? "",
                CustomerPhone2: (string?)r.CustomerPhone2,
                SubTotal: (decimal)r.SubTotal,
                DiscountAmount: (decimal)r.DiscountAmount,
                DeliveryCharge: (decimal)r.DeliveryCharge,
                TotalAmount: (decimal)r.TotalAmount,
                PaidAmount: (decimal)r.PaidAmount,
                RemainingAmount: (decimal)r.RemainingAmount,
                Currency: (string?)r.Currency ?? "KWD",
                IsDelivery: Convert.ToInt32(r.IsDelivery) == 1,
                DeliveryTypeId: (int?)r.DeliveryTypeId,
                DeliveryTypeNameEn: (string?)r.DeliveryTypeNameEn,
                DeliveryTypeNameAr: (string?)r.DeliveryTypeNameAr,
                DriverId: (int?)r.DriverId,
                DriverName: (string?)r.DriverName,
                DriverNameAr: (string?)r.DriverNameAr,
                DriverPhone: (string?)r.DriverPhone,
                AreaId: (int?)r.AreaId,
                AreaNameEn: (string?)r.AreaNameEn,
                AreaNameAr: (string?)r.AreaNameAr,
                GovernorateId: (int?)r.GovernorateId,
                GovernorateNameEn: (string?)r.GovernorateNameEn,
                GovernorateNameAr: (string?)r.GovernorateNameAr,
                AddressSummary: BuildAddressSummary(
                    (string?)r.AddressBlock, (string?)r.AddressStreet,
                    (string?)r.AddressBuilding, (string?)r.AddressFlat),
                DeliveryDate: (DateTime?)r.DeliveryDate,
                ItemCount: (int)r.ItemCount,
                ServicesSummary: null,
                AgeDays: (int)r.AgeDays,
                Notes: (string?)r.Notes,
                PaymentStatus: (string?)r.PaymentStatus,
                IsDeferred: Convert.ToInt32(r.IsDeferred) == 1,
                SettledAt: (DateTime?)r.SettledAt,
                PaidAt: (DateTime?)r.PaidAt,
                PaidAgeDays: (int)r.PaidAgeDays,
                IsVoid: Convert.ToInt32(r.IsVoid) == 1,
                VoidedAt: (DateTime?)r.VoidedAt,
                VoidReason: (string?)r.VoidReason);
        }

        /// <summary>"Haircut, Beard trim +2" per invoice, in one round trip.</summary>
        private static Dictionary<int, string> LoadServiceSummaries(IDbConnection conn, List<int> invoiceIds)
        {
            var map = new Dictionary<int, string>();
            if (invoiceIds == null || invoiceIds.Count == 0) return map;

            var rows = SqlMapper.Query(conn, @"
                SELECT l.InvoiceId, i.ITEM_NAME1 AS NameEn, i.ITEM_NAME2 AS NameAr
                FROM dbo.AppointmentInvoiceLines l
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = l.ItemId
                WHERE l.InvoiceId IN @Ids AND ISNULL(l.IsRefunded, 0) = 0
                ORDER BY l.InvoiceId, l.Id", new { Ids = invoiceIds }).ToList();

            foreach (var g in rows.GroupBy(r => (int)r.InvoiceId))
            {
                var names = g.Select(r => (string?)r.NameEn ?? (string?)r.NameAr ?? "")
                             .Where(n => !string.IsNullOrWhiteSpace(n))
                             .ToList();
                if (names.Count == 0) continue;
                string text = string.Join(", ", names.Take(2));
                if (names.Count > 2) text += $" +{names.Count - 2}";
                map[g.Key] = text;
            }
            return map;
        }

        private static List<DebtDtos.CustomerSettlementRowDto> LoadSettlementRows(
            IDbConnection conn, int? customerId, int? settlementId)
        {
            var heads = SqlMapper.Query(conn, @"
                SELECT
                    s.Id, s.SettlementNumber, s.SettledAt, s.InvoiceCount,
                    s.TotalBefore, s.DiscountAmount, s.TotalCollected, s.Notes,
                    d.DRIVER_NAME AS DriverName
                FROM dbo.DebtSettlements s
                LEFT JOIN dbo.DRIVER d ON d.DRIVER_ID = s.DriverId
                WHERE s.Deleted = 0
                  AND (@CustomerId IS NULL OR s.CustomerId = @CustomerId)
                  AND (@SettlementId IS NULL OR s.Id = @SettlementId)
                ORDER BY s.SettledAt DESC",
                new { CustomerId = customerId, SettlementId = settlementId }).ToList();

            if (heads.Count == 0) return new List<DebtDtos.CustomerSettlementRowDto>();

            var ids = heads.Select(h => (int)h.Id).ToList();
            var pays = SqlMapper.Query(conn, @"
                SELECT p.SettlementId, p.Amount,
                       pt.INVOICE_PAYMENT_TYPE_NAME1 AS Name
                FROM dbo.DebtSettlementPayments p
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = p.PaymentTypeId
                WHERE p.SettlementId IN @Ids", new { Ids = ids }).ToList();

            var payMap = pays
                .GroupBy(p => (int)p.SettlementId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(" · ", g.Select(p =>
                        $"{(string?)p.Name ?? "?"} {((decimal)p.Amount).ToString("0.###", CultureInfo.InvariantCulture)}")));

            return heads.Select(h => new DebtDtos.CustomerSettlementRowDto(
                SettlementId: (int)h.Id,
                SettlementNumber: (string?)h.SettlementNumber ?? "",
                SettledAt: (DateTime)h.SettledAt,
                InvoiceCount: (int)h.InvoiceCount,
                TotalBefore: (decimal)h.TotalBefore,
                DiscountAmount: (decimal)h.DiscountAmount,
                TotalCollected: (decimal)h.TotalCollected,
                DriverName: (string?)h.DriverName,
                PaymentSummary: payMap.TryGetValue((int)h.Id, out var s) ? s : "",
                Notes: (string?)h.Notes)).ToList();
        }

        /// <summary>
        /// Splits <paramref name="total"/> across <paramref name="weights"/> so the
        /// parts sum EXACTLY back to it. Proportional first, then the rounding
        /// remainder goes to the largest fractional parts (largest-remainder method).
        /// </summary>
        private static List<decimal> DistributeProportionally(List<decimal> weights, decimal total)
        {
            var result = weights.Select(_ => 0m).ToList();
            if (total <= 0m || weights.Count == 0) return result;

            decimal sum = weights.Sum();
            if (sum <= 0m) { result[0] = Round3(total); return result; }

            var exact = weights.Select(w => total * w / sum).ToList();
            var floors = exact.Select(Round3Down).ToList();
            decimal assigned = floors.Sum();

            // Hand out the leftover in 0.001 steps, biggest fraction first.
            var order = Enumerable.Range(0, weights.Count)
                .OrderByDescending(i => exact[i] - floors[i])
                .ToList();

            decimal remainder = Round3(total - assigned);
            int idx = 0;
            const decimal step = 0.001m;
            while (remainder >= step && order.Count > 0)
            {
                floors[order[idx % order.Count]] += step;
                remainder = Round3(remainder - step);
                idx++;
                if (idx > 100000) break;   // paranoia: never spin forever
            }

            for (int i = 0; i < floors.Count; i++) result[i] = Round3(floors[i]);
            return result;
        }

        private static DebtDtos.PagedResult<T> Paginate<T>(List<T> all, int page, int pageSize)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = 10;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return new DebtDtos.PagedResult<T>(
                items, all.Count, page, pageSize,
                (int)Math.Ceiling(all.Count / (double)pageSize));
        }

        private static string? BuildAddressSummary(string? block, string? street, string? building, string? flat)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(block)) parts.Add($"Block {block.Trim()}");
            if (!string.IsNullOrWhiteSpace(street)) parts.Add($"St {street.Trim()}");
            if (!string.IsNullOrWhiteSpace(building)) parts.Add($"Bldg {building.Trim()}");
            if (!string.IsNullOrWhiteSpace(flat)) parts.Add($"Flat {flat.Trim()}");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        private static decimal Round3(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
        private static decimal Round3Down(decimal v) => Math.Floor(v * 1000m) / 1000m;

        private int ResolveCurrentUserId()
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userId" || c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : 0;
        }

        private int? ResolveUserBranchId(IDbConnection conn)
        {
            int userId = ResolveCurrentUserId();
            if (userId <= 0) return null;
            return SqlMapper.Query<int?>(conn,
                "SELECT BRANCH_ID FROM dbo.[USER] WHERE USER_ID = @UserId",
                new { UserId = userId }).FirstOrDefault();
        }
    }
}
