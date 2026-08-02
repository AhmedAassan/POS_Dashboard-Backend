// Modules/System/Controllers/DebtApiController.cs
//
// Deferred Payment (Debt) — api/debt
//
//   GET  /api/debt/config                      → flags + branch + payment types + delivery lookups
//   GET  /api/debt/invoices                    → the /orders table (filters + paging + summary)
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
        // =====================================================================
        [HttpGet("invoices")]
        public ActionResult<DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>> Invoices(
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
            [FromQuery] string? sortBy = "date",        // date | amount | customer | age
            [FromQuery] string? sortDir = "desc",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                branchId ??= ResolveUserBranchId(conn);
                int tzOffset = BusinessSettingsService.GetTimeZoneOffset(conn);

                var (where, prm) = BuildDebtWhere(
                    branchId, search, invoiceNumber, customerId, driverId, areaId, governorateId,
                    orderType, dateFrom, dateTo, minAmount, maxAmount, onlyOverdue, tzOffset);

                var all = QueryDebtInvoices(conn, where, prm, tzOffset);

                // Sorting happens in memory: the result set is one branch's OPEN debt,
                // which is bounded by how much money a business is willing to be owed.
                all = SortDebt(all, sortBy, sortDir);

                int total = all.Count;
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 500) pageSize = 25;
                var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                string currency = all.FirstOrDefault()?.Currency
                    ?? SqlMapper.Query<string>(conn,
                        "SELECT TOP 1 EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                        new { Id = branchId }).FirstOrDefault() ?? "KWD";

                var summary = new DebtDtos.DebtSummaryDto(
                    InvoiceCount: total,
                    TotalDebt: Round3(all.Sum(x => x.RemainingAmount)),
                    CustomerCount: all.Select(x => x.CustomerId).Distinct().Count(),
                    DeliveryDebt: Round3(all.Where(x => x.IsDelivery).Sum(x => x.RemainingAmount)),
                    PickupDebt: Round3(all.Where(x => !x.IsDelivery).Sum(x => x.RemainingAmount)),
                    OverdueDebt: Round3(all.Where(x => x.AgeDays >= OverdueDays).Sum(x => x.RemainingAmount)),
                    OverdueDays: OverdueDays,
                    Currency: currency);

                var paged = new DebtDtos.PagedResult<DebtDtos.DebtInvoiceDto>(
                    Items: items,
                    TotalCount: total,
                    Page: page,
                    PageSize: pageSize,
                    TotalPages: (int)Math.Ceiling(total / (double)pageSize));

                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>(true, null,
                    new DebtDtos.DebtInvoiceListDto(paged, summary, tzOffset)));
            }
            catch (Exception ex)
            {
                return Ok(new DebtDtos.ApiResult<DebtDtos.DebtInvoiceListDto>(
                    false, $"Failed to load debt invoices: {ex.Message}", null));
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
                var walletTx = SqlMapper.Query(conn, @"
                    SELECT TOP 200
                        sh.Id, sh.SubscriptionId, sh.AddedDate, sh.Amount, sh.Balance,
                        ISNULL(sh.RefType, 0) AS RefType,
                        sh.InvoiceId,
                        inv.InvoiceNumber
                    FROM dbo.SubscriptionsHistory sh
                    LEFT JOIN dbo.AppointmentInvoices inv ON inv.Id = sh.InvoiceId
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
                        InvoiceNumber: (string?)t.InvoiceNumber))
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
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.SubscriptionsHistory
                                (CustomerRef, RefType, InvoiceId, SubscriptionId, Amount, Balance, AddedBy, AddedDate, Deleted)
                            VALUES (@CustomerRef, 1, @InvoiceId, @SubscriptionId, @Amount, @Balance, @AddedBy, @AddedDate, 0)",
                            new
                            {
                                CustomerRef = walletCustomerRef,
                                InvoiceId = (int)invoices[0].Id,
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

        /// <summary>Builds the WHERE fragment + parameters shared by the list endpoints.</summary>
        private static (string Where, object Params) BuildDebtWhere(
            int? branchId, string? search, string? invoiceNumber, int? customerId, int? driverId,
            int? areaId, int? governorateId, string? orderType,
            DateTime? dateFrom, DateTime? dateTo, decimal? minAmount, decimal? maxAmount,
            bool onlyOverdue, int tzOffset)
        {
            var sb = new StringBuilder("(@BranchId IS NULL OR inv.BranchId = @BranchId)");

            if (customerId.HasValue) sb.Append(" AND inv.CustomerId = @CustomerId");
            if (driverId.HasValue) sb.Append(" AND ISNULL(idl.DriverId, inv.DeliveryDriverId) = @DriverId");
            if (areaId.HasValue) sb.Append(" AND idl.AreaId = @AreaId");
            if (governorateId.HasValue) sb.Append(" AND idl.GovernorateId = @GovernorateId");

            if (orderType == "delivery") sb.Append(" AND ISNULL(idl.IsDelivery, 0) = 1");
            else if (orderType == "pickup") sb.Append(" AND ISNULL(idl.IsDelivery, 0) = 0");

            if (!string.IsNullOrWhiteSpace(invoiceNumber))
                sb.Append(" AND inv.InvoiceNumber LIKE '%' + @InvoiceNumber + '%'");

            // Dates are branch-local in the UI, UTC in the column.
            if (dateFrom.HasValue) sb.Append(" AND inv.CreatedAt >= @DateFromUtc");
            if (dateTo.HasValue) sb.Append(" AND inv.CreatedAt < @DateToUtc");

            if (minAmount.HasValue) sb.Append(" AND inv.RemainingAmount >= @MinAmount");
            if (maxAmount.HasValue) sb.Append(" AND inv.RemainingAmount <= @MaxAmount");

            if (onlyOverdue) sb.Append($" AND inv.CreatedAt < DATEADD(day, -{OverdueDays}, SYSUTCDATETIME())");

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

            var prm = new
            {
                BranchId = branchId,
                CustomerId = customerId,
                DriverId = driverId,
                AreaId = areaId,
                GovernorateId = governorateId,
                InvoiceNumber = invoiceNumber?.Trim(),
                Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                MinAmount = minAmount,
                MaxAmount = maxAmount,
                // local midnight → UTC instant; ToUtc is inclusive of the whole end day
                DateFromUtc = dateFrom?.Date.AddHours(-tzOffset),
                DateToUtc = dateTo?.Date.AddDays(1).AddHours(-tzOffset)
            };

            return (sb.ToString(), prm);
        }

        /// <summary>
        /// The single projection every debt list uses. `extraWhere` is appended to
        /// the mandatory "open debt" predicate — callers never have to repeat it.
        /// </summary>
        private static List<DebtDtos.DebtInvoiceDto> QueryDebtInvoices(
            IDbConnection conn, string extraWhere, object prm, int tzOffset)
        {
            var rows = SqlMapper.Query(conn, $@"
                SELECT
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

                    (SELECT COUNT(*) FROM dbo.AppointmentInvoiceLines l
                      WHERE l.InvoiceId = inv.Id AND ISNULL(l.IsRefunded, 0) = 0) AS ItemCount,
                    DATEDIFF(day, inv.CreatedAt, SYSUTCDATETIME()) AS AgeDays
                FROM dbo.AppointmentInvoices inv
                INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = inv.CustomerId
                LEFT  JOIN dbo.BRANCH   b        ON b.BRANCH_ID   = inv.BranchId
                LEFT  JOIN dbo.InvoiceDelivery idl ON idl.InvoiceId = inv.Id
                LEFT  JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
                WHERE inv.IsDeferred = 1
                  AND inv.SettledAt IS NULL
                  AND inv.RemainingAmount > 0
                  AND ({extraWhere})
                ORDER BY inv.CreatedAt DESC", prm).ToList();

            var ids = rows.Select(r => (int)r.InvoiceId).ToList();
            var summaries = LoadServiceSummaries(conn, ids);

            return rows.Select(r =>
            {
                int invId = (int)r.InvoiceId;
                return new DebtDtos.DebtInvoiceDto(
                    InvoiceId: invId,
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
                    ServicesSummary: summaries.TryGetValue(invId, out var s) ? s : null,
                    AgeDays: (int)r.AgeDays,
                    Notes: (string?)r.Notes);
            }).ToList();
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

        private static List<DebtDtos.DebtInvoiceDto> SortDebt(
            List<DebtDtos.DebtInvoiceDto> list, string? sortBy, string? sortDir)
        {
            bool asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            IOrderedEnumerable<DebtDtos.DebtInvoiceDto> sorted = (sortBy?.ToLowerInvariant()) switch
            {
                "amount" => asc ? list.OrderBy(x => x.RemainingAmount) : list.OrderByDescending(x => x.RemainingAmount),
                "customer" => asc ? list.OrderBy(x => x.CustomerName) : list.OrderByDescending(x => x.CustomerName),
                "age" => asc ? list.OrderBy(x => x.AgeDays) : list.OrderByDescending(x => x.AgeDays),
                _ => asc ? list.OrderBy(x => x.CreatedAt) : list.OrderByDescending(x => x.CreatedAt)
            };
            return sorted.ThenBy(x => x.InvoiceId).ToList();
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
