// Modules/System/Controllers/PosApiController.cs
//
// Standalone Point-of-Sale controller (api/pos).
//
//   GET  /api/pos/catalog?branchId=   → bootstrap: branch + categories + services + staff + payment types
//   POST /api/pos/checkout            → create a counter sale (invoice + payments + wallet + WhatsApp)
//   GET  /api/pos/receipt/{invoiceId} → receipt data for the invoice dialog
//
// Design notes
// ------------
// • POS sales are ALWAYS hidden from the calendar (ShowOnCalendar = 0) but
//   still count in the Dashboard (the dashboard query does not filter on it).
// • POS is "pay now in full" — checkout requires Paid == Total.
// • Services are organised by CATEGORY (dbo.CATEGORY) on the client; the
//   catalog endpoint returns the CATEGORY fields so the grid can group by them.
// • DB write pattern (AppointmentData / AppointmentInvoices /
//   AppointmentInvoiceLines / AppointmentPayments / SubscriptionsHistory)
//   mirrors the proven New Sale flow so reports, refunds, PDF and the existing
//   invoice dialog all keep working unchanged.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PosDashboard.Web.Modules.System.Services;   // InvoicePdfData + InvoiceLineData + PdfInvoiceService
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PosDtos = PosDashboard.Web.Modules.System.Models.PosDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/pos")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class PosApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration _configuration;
        private const string EnjazatikUrl = "https://business.enjazatik.com/api/v1/send-message";

        public PosApiController(
            ISqlConnections sqlConnections,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            this.httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // =====================================================================
        // GET /api/pos/catalog
        // =====================================================================
        [HttpGet("catalog")]
        public ActionResult<PosDtos.ApiResult<PosDtos.PosCatalogDto>> Catalog(
            [FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                // If the caller didn't pin a branch explicitly, use the branch the
                // logged-in user is linked to (USER.BRANCH_ID). This drives the POS
                // currency/tax/round-off from the user's own branch. Falls back to the
                // first active branch below when the user has no branch assigned.
                if (branchId == null)
                {
                    int currentUserId = ResolveCurrentUserId();
                    if (currentUserId > 0)
                    {
                        var userBranchRow = SqlMapper.Query<int?>(conn,
                            "SELECT BRANCH_ID FROM dbo.[USER] WHERE USER_ID = @UserId",
                            new { UserId = currentUserId }).FirstOrDefault();
                        if (userBranchRow.HasValue)
                            branchId = userBranchRow.Value;
                    }
                }

                // -------- Branch (resolve a default if none supplied) --------
                var branch = SqlMapper.Query(conn, @"
                    SELECT TOP 1
                        BRANCH_ID    AS BranchId,
                        COMPANY_ID   AS CompanyId,
                        BRANCH_NAME1 AS BranchName1,
                        BRANCH_NAME2 AS BranchName2,
                        BRANCH_PHONE AS BranchPhone,
                        EnglishCurrencyName,
                        ArabicCurrencyName,
                        RoundOfDigits,
                        TaxValue
                    FROM dbo.BRANCH
                    WHERE (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)
                      AND (@BranchId IS NULL OR BRANCH_ID = @BranchId)
                    ORDER BY BRANCH_ID",
                    new { BranchId = branchId }).FirstOrDefault();

                if (branch == null)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosCatalogDto>(
                        false, "Branch not found or inactive", null));

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

                // -------- Categories --------
                var categories = SqlMapper.Query(conn, @"
                    SELECT
                        CATEGORY_ID       AS CategoryId,
                        CATEGORY_NAME1    AS CategoryNameEn,
                        CATEGORY_NAME2    AS CategoryNameAr,
                        CATEGORY_ORDERING AS Ordering,
                        DocumentName
                    FROM dbo.CATEGORY
                    WHERE CATEGORY_IS_ACTIVE = 1
                    ORDER BY CATEGORY_ORDERING, CATEGORY_ID")
                    .Select(c => new PosDtos.PosCategoryDto(
                        CategoryId: (int)c.CategoryId,
                        CategoryNameEn: (string?)c.CategoryNameEn ?? "",
                        CategoryNameAr: (string?)c.CategoryNameAr ?? (string?)c.CategoryNameEn ?? "",
                        Ordering: (int)(c.Ordering ?? 0),
                        DocumentName: (string?)c.DocumentName))
                    .ToList();

                // -------- Services (grouped-ready, branch scoped) --------
                var services = SqlMapper.Query(conn, @"
                    SELECT
                        i.ITEM_ID            AS ItemId,
                        i.ITEM_NAME1         AS ItemNameEn,
                        i.ITEM_NAME2         AS ItemNameAr,
                        c.CATEGORY_ID        AS CategoryId,
                        c.CATEGORY_NAME1     AS CategoryNameEn,
                        c.CATEGORY_NAME2     AS CategoryNameAr,
                        ac.Id                AS AppointmentCategoryId,
                        iu.ITEM_UNIT_ID      AS ItemUnitId,
                        u.UNIT_ID            AS UnitId,
                        u.UNIT_NAME1         AS UnitNameEn,
                        u.UNIT_NAME2         AS UnitNameAr,
                        iu.ITEM_UNIT_PRICE   AS Price,
                        iu.MinimumPrice      AS MinimumPrice,
                        CAST(iu.ITEM_UNIT_DURATION AS float) AS DurationMinutes,
                        i.DocumentName       AS DocumentName,
                        CAST(CASE WHEN i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL THEN 1 ELSE 0 END AS bit) AS IsActive
                    FROM dbo.ITEM i
                    INNER JOIN dbo.CATEGORY c             ON c.CATEGORY_ID = i.ITEM_CATEGORY_ID
                    INNER JOIN dbo.AppointmentCategories ac ON ac.Id        = i.AppointmentCategoryId
                    INNER JOIN dbo.ITEM_UNIT iu           ON iu.ITEM_ID    = i.ITEM_ID
                    INNER JOIN dbo.UNIT u                 ON u.UNIT_ID     = iu.UNIT_ID
                    WHERE (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                      AND c.CATEGORY_IS_ACTIVE = 1
                      AND ac.Deleted = 0
                      AND iu.Active = 1
                      AND iu.BranchId = @BranchId
                    ORDER BY c.CATEGORY_ORDERING, i.ITEM_NAME1",
                    new { BranchId = resolvedBranchId })
                    .Select(s => new PosDtos.PosServiceDto(
                        ItemId: (int)s.ItemId,
                        ItemNameEn: (string?)s.ItemNameEn ?? "",
                        ItemNameAr: (string?)s.ItemNameAr ?? (string?)s.ItemNameEn ?? "",
                        CategoryId: (int)s.CategoryId,
                        CategoryNameEn: (string?)s.CategoryNameEn ?? "",
                        CategoryNameAr: (string?)s.CategoryNameAr ?? (string?)s.CategoryNameEn ?? "",
                        AppointmentCategoryId: (int)s.AppointmentCategoryId,
                        ItemUnitId: (int)(s.ItemUnitId ?? 0),
                        UnitId: (int)s.UnitId,
                        UnitNameEn: (string?)s.UnitNameEn ?? "",
                        UnitNameAr: (string?)s.UnitNameAr ?? (string?)s.UnitNameEn ?? "",
                        Price: (decimal)(s.Price ?? 0m),
                        MinimumPrice: (decimal)(s.MinimumPrice ?? 0m),
                        DurationMinutes: (double)(s.DurationMinutes ?? 0d),
                        ImageUrl: BuildImageUrl((string?)s.DocumentName),
                        DocumentName: (string?)s.DocumentName,
                        IsActive: (bool)s.IsActive))
                    .ToList();

                // -------- Staff (branch scoped) --------
                var staff = SqlMapper.Query(conn, @"
                    SELECT
                        s.Id          AS StaffId,
                        s.EnglishName AS NameEn,
                        s.ArabicName  AS NameAr,
                        s.Mobile,
                        s.Active,
                        s.EmployeeCode AS EmployeeCode
                    FROM dbo.STAFF s
                    WHERE s.Deleted = 0
                      AND s.Active = 1
                      AND (s.BranchId = @BranchId OR s.BranchId IS NULL)
                    ORDER BY s.EnglishName",
                    new { BranchId = resolvedBranchId })
                    .Select(s => new PosDtos.PosStaffDto(
                        StaffId: (int)s.StaffId,
                        NameEn: (string?)s.NameEn ?? (string?)s.NameAr ?? "",
                        NameAr: (string?)s.NameAr ?? (string?)s.NameEn ?? "",
                        Mobile: (string?)s.Mobile,
                        Active: (bool)s.Active,
                        EmployeeCode: (s.EmployeeCode == null || s.EmployeeCode is DBNull) ? null : (string?)Convert.ToString(s.EmployeeCode)))
                    .ToList();

                // -------- Payment types --------
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

                // -------- Offers (OFFER packages + their services) --------
                var offerRows = SqlMapper.Query(conn, @"
                    SELECT
                        p.Id          AS PackageOfferId,
                        p.EnglishName AS NameEn,
                        p.ArabicName  AS NameAr,
                        p.Amount      AS Amount,
                        ISNULL(p.NoOfDays, 0) AS NoOfDays,
                        pi.ItemUnitId AS ItemUnitId,
                        iu.ITEM_ID    AS ItemId,
                        i.ITEM_NAME1  AS ItemNameEn,
                        i.ITEM_NAME2  AS ItemNameAr,
                        iu.UNIT_ID    AS UnitId,
                        u.UNIT_NAME1  AS UnitNameEn,
                        u.UNIT_NAME2  AS UnitNameAr,
                        iu.ITEM_UNIT_PRICE AS ItemPrice,
                        CAST(iu.ITEM_UNIT_DURATION AS float) AS DurationMinutes
                    FROM dbo.Packages p
                    INNER JOIN dbo.PackageItems pi ON pi.PackageId = p.Id AND ISNULL(pi.Deleted,0) = 0
                    INNER JOIN dbo.ITEM_UNIT iu    ON iu.ITEM_UNIT_ID = pi.ItemUnitId AND iu.Active = 1
                    INNER JOIN dbo.ITEM i          ON i.ITEM_ID = iu.ITEM_ID
                    INNER JOIN dbo.UNIT u          ON u.UNIT_ID = iu.UNIT_ID
                    WHERE ISNULL(p.OFFER, 0) = 1
                      AND ISNULL(p.Deleted, 0) = 0
                      AND ISNULL(p.Active, 1) = 1
                      AND (p.BranchId = @BranchId OR p.BranchId IS NULL)
                    ORDER BY p.Id, pi.Id",
                    new { BranchId = resolvedBranchId }).ToList();

                var offers = offerRows
                    .GroupBy(r => (int)r.PackageOfferId)
                    .Select(g =>
                    {
                        var first = g.First();
                        var items = g.Select(r => new PosDtos.PosOfferItemDto(
                            ItemUnitId: (int)r.ItemUnitId,
                            ItemId: (int)r.ItemId,
                            ItemNameEn: (string?)r.ItemNameEn ?? "",
                            ItemNameAr: (string?)r.ItemNameAr ?? (string?)r.ItemNameEn ?? "",
                            UnitId: (int)r.UnitId,
                            UnitNameEn: (string?)r.UnitNameEn ?? "",
                            UnitNameAr: (string?)r.UnitNameAr ?? (string?)r.UnitNameEn ?? "",
                            ItemPrice: (decimal)(r.ItemPrice ?? 0m),
                            DurationMinutes: (double)(r.DurationMinutes ?? 0d))).ToList();

                        decimal amount = (decimal)first.Amount;
                        decimal realValue = items.Sum(x => x.ItemPrice);
                        return new PosDtos.PosOfferDto(
                            PackageOfferId: g.Key,
                            NameEn: (string?)first.NameEn ?? "",
                            NameAr: (string?)first.NameAr ?? (string?)first.NameEn ?? "",
                            Amount: amount,
                            NoOfDays: (int)(first.NoOfDays ?? 0),
                            TotalRealValue: realValue,
                            Savings: Math.Max(0m, realValue - amount),
                            Items: items);
                    })
                    .ToList();

                var dto = new PosDtos.PosCatalogDto(
                    Branch: branchDto,
                    Categories: categories,
                    Services: services,
                    Staff: staff,
                    PaymentTypes: paymentTypes,
                    Offers: offers);

                return Ok(new PosDtos.ApiResult<PosDtos.PosCatalogDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosCatalogDto>(
                    false, $"Failed to load POS catalog: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/pos/checkout
        // =====================================================================
        [HttpPost("checkout")]
        public async Task<ActionResult<PosDtos.ApiResult<PosDtos.PosCheckoutResponse>>> Checkout(
            [FromBody] PosDtos.PosCheckoutRequest request)
        {
            try
            {
                if (request == null) return FailCheckout("Request body is required");
                bool hasLines = request.Lines != null && request.Lines.Count > 0;
                bool hasPackages = request.Packages != null && request.Packages.Count > 0;
                if (!hasLines && !hasPackages)
                    return FailCheckout("At least one service line or package is required");

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                // -------- Branch --------
                var branch = SqlMapper.Query(conn, @"
                    SELECT BRANCH_ID, EnglishCurrencyName, ArabicCurrencyName, RoundOfDigits
                    FROM dbo.BRANCH
                    WHERE BRANCH_ID = @Id
                      AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                    new { Id = request.BranchId }).FirstOrDefault();

                if (branch == null) return FailCheckout("Branch not found or inactive");
                string currency = (string?)branch.EnglishCurrencyName ?? "KWD";
                string currencyAr = (string?)branch.ArabicCurrencyName ?? currency;
                int roundDigits = (int)(branch.RoundOfDigits ?? 3);

                // -------- Customer --------
                var customer = SqlMapper.Query(conn, @"
                    SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1,
                           CUSTOMER_REF_GUIDE AS RefGuide,
                           ISNULL(NotificationLang, 'ar') AS Lang
                    FROM dbo.CUSTOMER
                    WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();

                if (customer == null) return FailCheckout("Customer not found");

                // -------- Slot config (POS times are sequential, cosmetic, off-calendar) --------
                var settings = SqlMapper.Query<(string Key, string Value)>(conn, @"
                    SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
                    FROM dbo.SYSTEM_SETTING
                    WHERE SETTING_KEY IN ('calendarStartHour','calendarEndHour','AppointmentDuration','timeZoneOffset')")
                    .ToDictionary(x => x.Key, x => x.Value);

                int startHour = settings.TryGetValue("calendarStartHour", out var sh) && int.TryParse(sh, out var shi) ? shi : 10;
                int endHour = settings.TryGetValue("calendarEndHour", out var eh) && int.TryParse(eh, out var ehi) ? ehi : 22;
                int slot = settings.TryGetValue("AppointmentDuration", out var sd) && int.TryParse(sd, out var sdi) ? sdi : 5;
                int tzOffset = settings.TryGetValue("timeZoneOffset", out var tz) && int.TryParse(tz, out var tzi) ? tzi : 3;
                if (slot < 1) slot = 5;

                var saleDate = DateTime.UtcNow.AddHours(tzOffset).Date;
                var cursor = NearestSlotNow(DateTime.UtcNow.AddHours(tzOffset), startHour, endHour, slot);

                // -------- Resolve standalone (extra) lines --------
                var requestLines = request.Lines ?? new List<PosDtos.PosCheckoutLineRequest>();
                var resolved = new List<ResolvedLine>(requestLines.Count);
                for (int i = 0; i < requestLines.Count; i++)
                {
                    var line = requestLines[i];
                    if (line == null) return FailCheckout($"Line #{i + 1} is null");

                    // Staff is OPTIONAL now. null / 0 => sell without a staff member;
                    // a printable label is generated and the staff is assigned later (Phase 2).
                    int? lineStaffId = (line.StaffId.HasValue && line.StaffId.Value > 0) ? line.StaffId : null;
                    string staffEn = "", staffAr = "";
                    if (lineStaffId.HasValue)
                    {
                        var staff = SqlMapper.Query(conn, @"
                            SELECT Id, ArabicName, EnglishName
                            FROM dbo.STAFF
                            WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                            new { Id = lineStaffId.Value }).FirstOrDefault();
                        if (staff == null) return FailCheckout($"Line #{i + 1}: staff not found or not active");
                        staffEn = (string?)staff.EnglishName ?? "";
                        staffAr = (string?)staff.ArabicName ?? "";
                    }

                    var item = SqlMapper.Query(conn, @"
                        SELECT iu.ITEM_ID, i.ITEM_NAME1, i.ITEM_NAME2,
                               iu.UNIT_ID, iu.ITEM_UNIT_PRICE,
                               CAST(iu.ITEM_UNIT_DURATION AS float) AS ITEM_UNIT_DURATION
                        FROM dbo.ITEM_UNIT iu
                        INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                        WHERE iu.ITEM_ID = @ItemId
                          AND iu.UNIT_ID = @UnitId
                          AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                          AND iu.Active = 1",
                        new { ItemId = line.ItemId, UnitId = line.UnitId }).FirstOrDefault();
                    if (item == null) return FailCheckout($"Line #{i + 1}: item/unit not found or not active");

                    int duration = ResolveDuration(line.DurationMinutes, (double?)item.ITEM_UNIT_DURATION, slot);
                    decimal masterPrice = (decimal)item.ITEM_UNIT_PRICE;
                    decimal salePrice = line.UnitPriceOverride.HasValue
                        ? Math.Max(0m, line.UnitPriceOverride.Value)
                        : masterPrice;

                    // Sequential, off-calendar times (kept valid; never crosses midnight).
                    var start = cursor;
                    var end = start + TimeSpan.FromMinutes(duration);
                    if (end >= TimeSpan.FromDays(1))
                    {
                        start = TimeSpan.FromHours(startHour);
                        end = start + TimeSpan.FromMinutes(duration);
                    }
                    cursor = end;

                    resolved.Add(new ResolvedLine
                    {
                        Index = i,
                        ItemId = line.ItemId,
                        UnitId = line.UnitId,
                        StaffId = lineStaffId,
                        ItemEnName = (string?)item.ITEM_NAME1 ?? "",
                        ItemArName = (string?)item.ITEM_NAME2 ?? "",
                        StaffEnName = staffEn,
                        StaffArName = staffAr,
                        DurationMinutes = duration,
                        UnitPrice = masterPrice,
                        SalePrice = salePrice,
                        OriginalUnitPrice = salePrice,   // pre-ticket-discount (override ?? master)
                        StartTime = start,
                        EndTime = end,
                        Notes = string.IsNullOrWhiteSpace(line.Notes) ? null : line.Notes.Trim()
                    });
                }

                // -------- Resolve PACKAGE (offer) lines --------
                var reqPackages = request.Packages ?? new List<PosDtos.PosCheckoutPackageRequest>();
                foreach (var pkgReq in reqPackages)
                {
                    if (pkgReq == null || pkgReq.Lines == null || pkgReq.Lines.Count == 0)
                        return FailCheckout("Each package must contain at least one service line");

                    var pkg = SqlMapper.Query(conn, @"
                        SELECT Id, EnglishName, ArabicName, Amount
                        FROM dbo.Packages
                        WHERE Id = @Id AND ISNULL(OFFER,0) = 1 AND ISNULL(Deleted,0) = 0",
                        new { Id = pkgReq.PackageOfferId }).FirstOrDefault();
                    if (pkg == null) return FailCheckout($"Package OFFER #{pkgReq.PackageOfferId} not found");

                    string pkgName = (string?)pkg.EnglishName ?? "";
                    decimal pkgPrice = (decimal)pkg.Amount;
                    var pkgGroupId = Guid.NewGuid();

                    var pkgItems = SqlMapper.Query(conn, @"
                        SELECT pi.ItemUnitId, iu.ITEM_ID AS ItemId, iu.UNIT_ID AS UnitId,
                               i.ITEM_NAME1, i.ITEM_NAME2, iu.ITEM_UNIT_PRICE,
                               CAST(iu.ITEM_UNIT_DURATION AS float) AS DurationMinutes
                        FROM dbo.PackageItems pi
                        INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                        INNER JOIN dbo.ITEM i       ON i.ITEM_ID = iu.ITEM_ID
                        WHERE pi.PackageId = @Id AND ISNULL(pi.Deleted,0) = 0",
                        new { Id = pkgReq.PackageOfferId })
                        .ToDictionary(r => (int)r.ItemUnitId, r => r);

                    var pkgLines = new List<ResolvedLine>(pkgReq.Lines.Count);
                    foreach (var pl in pkgReq.Lines)
                    {
                        if (!pkgItems.TryGetValue(pl.ItemUnitId, out var pit))
                            return FailCheckout($"Service (ItemUnit #{pl.ItemUnitId}) does not belong to package '{pkgName}'");

                        // Staff optional inside packages too (un-staffed -> label).
                        int? pkgLineStaffId = (pl.StaffId.HasValue && pl.StaffId.Value > 0) ? pl.StaffId : null;
                        string pStaffEn = "", pStaffAr = "";
                        if (pkgLineStaffId.HasValue)
                        {
                            var pstaff = SqlMapper.Query(conn, @"
                                SELECT Id, ArabicName, EnglishName FROM dbo.STAFF
                                WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                                new { Id = pkgLineStaffId.Value }).FirstOrDefault();
                            if (pstaff == null) return FailCheckout($"Package '{pkgName}': staff not found or not active");
                            pStaffEn = (string?)pstaff.EnglishName ?? "";
                            pStaffAr = (string?)pstaff.ArabicName ?? "";
                        }

                        int pdur = ResolveDuration(pl.DurationMinutes, (double?)pit.DurationMinutes, slot);
                        var pstart = cursor;
                        var pend = pstart + TimeSpan.FromMinutes(pdur);
                        if (pend >= TimeSpan.FromDays(1))
                        {
                            pstart = TimeSpan.FromHours(startHour);
                            pend = pstart + TimeSpan.FromMinutes(pdur);
                        }
                        cursor = pend;

                        pkgLines.Add(new ResolvedLine
                        {
                            Index = resolved.Count + pkgLines.Count,
                            ItemId = (int)pit.ItemId,
                            UnitId = (int)pit.UnitId,
                            StaffId = pkgLineStaffId,
                            ItemEnName = (string?)pit.ITEM_NAME1 ?? "",
                            ItemArName = (string?)pit.ITEM_NAME2 ?? "",
                            StaffEnName = pStaffEn,
                            StaffArName = pStaffAr,
                            DurationMinutes = pdur,
                            UnitPrice = (decimal)pit.ITEM_UNIT_PRICE,  // catalog price (pre-discount)
                            SalePrice = 0m,                            // set by distribution below
                            StartTime = pstart,
                            EndTime = pend,
                            Notes = string.IsNullOrWhiteSpace(pl.Notes) ? null : pl.Notes.Trim(),
                            PackageOfferId = pkgReq.PackageOfferId,
                            PackageGroupId = pkgGroupId,
                            PackageOfferName = pkgName
                        });
                    }

                    DistributePackagePrice(pkgLines, pkgPrice, roundDigits);
                    // Packages are NOT eligible for the ticket discount — their listed
                    // (original) price equals the distributed share.
                    foreach (var pl in pkgLines) pl.OriginalUnitPrice = pl.SalePrice;
                    resolved.AddRange(pkgLines);
                }

                if (resolved.Count == 0) return FailCheckout("Nothing to sell");

                // -------- Ticket discount (SERVICES only, never OFFER packages) --------
                // Services subtotal = standalone (non-package) lines at their pre-discount
                // price. The discount is spread across those lines so each line's SalePrice
                // becomes the AFTER-discount value used everywhere (invoice total, revenue,
                // staff performance). Package lines are untouched.
                var serviceLines = resolved.Where(l => l.PackageOfferId == null).ToList();
                decimal servicesSubtotal = serviceLines.Sum(l => l.OriginalUnitPrice);
                string? discountType = request.Discount?.Type?.Trim().ToLowerInvariant();
                decimal discountValueRaw = request.Discount?.Value ?? 0m;

                // -------- Customer discount code (CARD-######) --------
                // When a code is supplied it OVERRIDES any manual ticket discount and is
                // applied to the SERVICES subtotal only. It is validated here and then
                // redeemed (used-count + redemption row) inside the transaction below.
                int? redeemedCodeId = null;
                string? redeemedCode = null;
                if (!string.IsNullOrWhiteSpace(request.DiscountCode))
                {
                    string codeInput = request.DiscountCode.Trim().ToUpperInvariant();
                    var dc = SqlMapper.Query(conn, @"
                        SELECT dc.Id, dc.DiscountType, dc.DiscountAmount, dc.MaxUses,
                               dc.UsedCount, dc.ExpiresAt,
                               ISNULL(t.IsActive, 1) AS TemplateActive
                        FROM dbo.DiscountCodes dc
                        LEFT JOIN dbo.DiscountTemplates t ON t.Id = dc.TemplateId
                        WHERE dc.Code = @Code AND dc.Deleted = 0",
                        new { Code = codeInput }).FirstOrDefault();

                    if (dc == null) return FailCheckout($"Discount code '{codeInput}' not found");

                    DateTime? codeExpiresAt = (DateTime?)dc.ExpiresAt;
                    int codeUsed = (int)dc.UsedCount;
                    int codeMax = (int)dc.MaxUses;
                    if (codeExpiresAt.HasValue && codeExpiresAt.Value < DateTime.UtcNow)
                        return FailCheckout("Discount code has expired");
                    if (codeUsed >= codeMax)
                        return FailCheckout("Discount code usage limit reached");
                    if (!(bool)dc.TemplateActive)
                        return FailCheckout("Discount code template is inactive");
                    if (servicesSubtotal <= 0m)
                        return FailCheckout("Discount code applies to services only — add a service first");

                    // Map the code's type to the ticket-discount vocabulary
                    // ('value' -> 'fixed', 'percentage' -> 'percentage').
                    string codeType = (string)dc.DiscountType;
                    discountType = codeType == "percentage" ? "percentage" : "fixed";
                    discountValueRaw = (decimal)dc.DiscountAmount;

                    redeemedCodeId = (int)dc.Id;
                    redeemedCode = codeInput;
                }
                // The POS UI works in 2 decimals (round2) and builds the payment splits from
                // that total, so the discount MUST be rounded the same way here or the
                // fully-paid check below could reject a legitimate sale.
                const int discountDigits = 2;
                decimal discountAmount = ComputeDiscountAmount(discountType, discountValueRaw, servicesSubtotal, discountDigits);

                // Normalise the stored type/value so a no-op discount is persisted as NULL.
                string? storedDiscountType = discountAmount > 0m ? discountType : null;
                decimal? storedDiscountValue = discountAmount > 0m ? discountValueRaw : (decimal?)null;

                if (discountAmount > 0m)
                    DistributeDiscount(serviceLines, discountAmount, discountDigits);

                decimal subTotal = resolved.Sum(x => x.OriginalUnitPrice);   // services + offers, pre-discount
                decimal grandTotal = resolved.Sum(x => x.SalePrice);         // after discount

                // -------- Validate payments (POS = pay now in full) --------
                decimal walletAmount = 0m;
                int? walletSubId = null;
                int? walletPtId = null;
                decimal walletBalanceBefore = 0m;
                Guid walletCustomerRef = Guid.Empty;
                decimal splitsTotal = 0m;

                var splits = request.Payments?.Splits ?? new List<PosDtos.PosSplitPaymentRequest>();

                if (request.Payments?.WalletAmount.HasValue == true && request.Payments.WalletAmount.Value > 0)
                {
                    walletAmount = request.Payments.WalletAmount.Value;
                    walletSubId = request.Payments.WalletSubscriptionId;
                    walletPtId = request.Payments.WalletPaymentTypeId;

                    if (walletSubId == null) return FailCheckout("WalletAmount given but WalletSubscriptionId is missing");
                    if (walletPtId == null) return FailCheckout("WalletAmount given but WalletPaymentTypeId is missing");

                    var sub = SqlMapper.Query(conn, @"
                        SELECT s.Id, s.CustomerRef, s.EndDate,
                               ISNULL(s.Deleted, 0) AS Deleted,
                               ISNULL(s.IsPaid, 0)  AS IsPaid,
                               ISNULL((
                                   SELECT TOP 1 sh.Balance FROM dbo.SubscriptionsHistory sh
                                   WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                   ORDER BY sh.Id DESC), 0) AS CurrentBalance
                        FROM dbo.Subscriptions s WHERE s.Id = @Id",
                        new { Id = walletSubId.Value }).FirstOrDefault();

                    if (sub == null || (int)sub.Deleted == 1) return FailCheckout("Wallet subscription not found");
                    if ((int)sub.IsPaid != 1) return FailCheckout("Wallet subscription is not paid");
                    if ((DateTime)sub.EndDate < DateTime.UtcNow) return FailCheckout("Wallet subscription has expired");
                    if ((Guid)sub.CustomerRef != (Guid)customer.RefGuide) return FailCheckout("Wallet subscription does not belong to this customer");

                    walletBalanceBefore = (decimal)sub.CurrentBalance;
                    walletCustomerRef = (Guid)sub.CustomerRef;
                    if (walletBalanceBefore < walletAmount)
                        return FailCheckout($"Insufficient wallet balance. Available: {walletBalanceBefore:F3}");

                    var walletPt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = walletPtId.Value }).FirstOrDefault();
                    if (walletPt == null) return FailCheckout("Wallet payment type not found");
                }

                foreach (var sp in splits)
                {
                    if (sp.Amount <= 0) return FailCheckout("Split payment amount must be greater than 0");
                    var pt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = sp.PaymentTypeId }).FirstOrDefault();
                    if (pt == null) return FailCheckout($"Payment type #{sp.PaymentTypeId} not found");
                    splitsTotal += sp.Amount;
                }

                decimal paidTotal = walletAmount + splitsTotal;
                if (paidTotal - grandTotal > 0.0001m)
                    return FailCheckout($"Payment total ({paidTotal:F3}) exceeds sale total ({grandTotal:F3})");
                if (Math.Abs(paidTotal - grandTotal) > 0.0001m)
                    return FailCheckout($"POS sale must be fully paid. Total: {grandTotal:F3}, Paid: {paidTotal:F3}");

                int currentUserId = ResolveCurrentUserId();

                // -------- Atomic transaction --------
                try
                {
                    using var uow = new UnitOfWork(conn);

                    var saleGroupId = Guid.NewGuid();
                    var apptIds = new List<int>(resolved.Count);

                    // 1) AppointmentData — one row per line (ShowOnCalendar = 0)
                    foreach (var rl in resolved)
                    {
                        int newId = SqlMapper.Query<int>(uow.Connection, @"
                            INSERT INTO dbo.AppointmentData (
                                BranchId, CustomerId, ItemId, UnitId, StaffId,
                                AppointmentDate, StartTime, EndTime,
                                NumberOfPersons, ServiceType, IsOnlineBooking, Notes,
                                UnitPrice, DiscountPercent, DiscountedUnitPrice, TotalPrice,
                                PaidAmount, PaymentStatus, DepositAmount,
                                Status, CheckoutStatus, CreatedAt, SaleGroupId, ShowOnCalendar,
                                PackageOfferId, PackageGroupId
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                @BranchId, @CustomerId, @ItemId, @UnitId, @StaffId,
                                @AppointmentDate, @StartTime, @EndTime,
                                1, 'SALON', 0, @Notes,
                                @UnitPrice, @DiscountPercent, @DiscountedUnitPrice, @TotalPrice,
                                @PaidAmount, 'FULL', 0,
                                'completed', 'checked_out', SYSUTCDATETIME(), @SaleGroupId, 0,
                                @PackageOfferId, @PackageGroupId
                            )",
                            new
                            {
                                BranchId = request.BranchId,
                                CustomerId = request.CustomerId,
                                rl.ItemId,
                                rl.UnitId,
                                rl.StaffId,
                                AppointmentDate = saleDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                Notes = rl.Notes ?? request.Notes,
                                UnitPrice = rl.UnitPrice,
                                DiscountPercent = ComputeDiscountPercent(rl.UnitPrice, rl.SalePrice),
                                DiscountedUnitPrice = rl.SalePrice,
                                TotalPrice = rl.SalePrice,
                                PaidAmount = rl.SalePrice,
                                SaleGroupId = saleGroupId,
                                rl.PackageOfferId,
                                rl.PackageGroupId
                            }).First();

                        apptIds.Add(newId);
                    }

                    int leadApptId = apptIds[0];
                    // Sequential, gap-free, per-day number (POS-yyyyMMdd-NNNN) reserved
                    // on the transactional connection so it rolls back with the checkout.
                    string invoiceNumber = InvoiceNumberService.Next(uow.Connection, InvoiceNumberService.PrefixPos);

                    int? invoicePaymentTypeId = (int?)splits.FirstOrDefault()?.PaymentTypeId ?? walletPtId;
                    if (invoicePaymentTypeId == null)
                        return FailCheckout("Payment type is required for a fully paid POS sale");

                    // 2) Shared invoice
                    int invoiceId = SqlMapper.Query<int>(uow.Connection, @"
                        INSERT INTO dbo.AppointmentInvoices (
                            InvoiceNumber, AppointmentId, BranchId, CustomerId,
                            TotalAmount, PaidAmount, RemainingAmount, Currency,
                            PaymentTypeId, PaymentStatus, CreatedAt,
                            SubTotal, DiscountType, DiscountValue, DiscountAmount,
                            DiscountCode, DiscountCodeId
                        )
                        OUTPUT INSERTED.Id
                        VALUES (
                            @InvoiceNumber, @AppointmentId, @BranchId, @CustomerId,
                            @TotalAmount, @PaidAmount, 0, @Currency,
                            @PaymentTypeId, 'FULL', SYSUTCDATETIME(),
                            @SubTotal, @DiscountType, @DiscountValue, @DiscountAmount,
                            @DiscountCode, @DiscountCodeId
                        )",
                        new
                        {
                            InvoiceNumber = invoiceNumber,
                            AppointmentId = leadApptId,
                            BranchId = request.BranchId,
                            CustomerId = request.CustomerId,
                            TotalAmount = grandTotal,
                            PaidAmount = paidTotal,
                            Currency = currency,
                            PaymentTypeId = invoicePaymentTypeId,
                            SubTotal = subTotal,
                            DiscountType = storedDiscountType,
                            DiscountValue = storedDiscountValue,
                            DiscountAmount = discountAmount,
                            DiscountCode = redeemedCode,
                            DiscountCodeId = redeemedCodeId
                        }).First();

                    // 3) Invoice lines
                    var invoiceLineIds = new List<int?>(resolved.Count);
                    for (int idx = 0; idx < resolved.Count; idx++)
                    {
                        var rl = resolved[idx];
                        int newLineId = SqlMapper.Query<int>(uow.Connection, @"
                            INSERT INTO dbo.AppointmentInvoiceLines (
                                InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                                UnitPrice, DiscountedUnitPrice, TotalPrice, OriginalUnitPrice,
                                AppointmentDate, StartTime, EndTime, DurationMinutes, Notes,
                                PackageOfferId, PackageGroupId, PackageOfferName
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                @InvoiceId, @AppointmentId, @ItemId, @UnitId, @StaffId,
                                @UnitPrice, @DiscountedUnitPrice, @TotalPrice, @OriginalUnitPrice,
                                @AppointmentDate, @StartTime, @EndTime, @DurationMinutes, @Notes,
                                @PackageOfferId, @PackageGroupId, @PackageOfferName
                            )",
                            new
                            {
                                InvoiceId = invoiceId,
                                AppointmentId = apptIds[idx],
                                rl.ItemId,
                                rl.UnitId,
                                rl.StaffId,
                                rl.UnitPrice,
                                DiscountedUnitPrice = rl.SalePrice,
                                TotalPrice = rl.SalePrice,
                                OriginalUnitPrice = rl.OriginalUnitPrice,
                                AppointmentDate = saleDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                rl.DurationMinutes,
                                rl.Notes,
                                rl.PackageOfferId,
                                rl.PackageGroupId,
                                rl.PackageOfferName
                            }).First();
                        invoiceLineIds.Add(newLineId);
                    }

                    // 3b) Service labels — one per UN-STAFFED line (Phase 1).
                    //     LabelNumber follows the service order within the invoice.
                    var labels = new List<PosDtos.PosLabelDto>();
                    for (int idx = 0; idx < resolved.Count; idx++)
                    {
                        var rl = resolved[idx];
                        if (rl.StaffId.HasValue && rl.StaffId.Value > 0) continue;   // staffed -> no label

                        string barcode = GenerateUniqueBarcode(uow.Connection);
                        int labelNumber = idx + 1;

                        int labelId = SqlMapper.Query<int>(uow.Connection, @"
                            INSERT INTO dbo.PosServiceLabels (
                                InvoiceId, AppointmentId, InvoiceLineId, LabelNumber, BranchId,
                                ItemId, ServiceName, ServiceNameAr, Price,
                                Barcode, QrPayload, IsAssigned, PrintCount, CreatedAt
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                @InvoiceId, @AppointmentId, @InvoiceLineId, @LabelNumber, @BranchId,
                                @ItemId, @ServiceName, @ServiceNameAr, @Price,
                                @Barcode, @QrPayload, 0, 0, SYSUTCDATETIME()
                            )",
                            new
                            {
                                InvoiceId = invoiceId,
                                AppointmentId = apptIds[idx],
                                InvoiceLineId = invoiceLineIds[idx],
                                LabelNumber = labelNumber,
                                BranchId = request.BranchId,
                                rl.ItemId,
                                ServiceName = rl.ItemEnName,
                                ServiceNameAr = rl.ItemArName,
                                Price = rl.SalePrice,
                                Barcode = barcode,
                                QrPayload = barcode
                            }).First();

                        labels.Add(new PosDtos.PosLabelDto(
                            LabelId: labelId,
                            InvoiceId: invoiceId,
                            AppointmentId: apptIds[idx],
                            InvoiceLineId: invoiceLineIds[idx],
                            LabelNumber: labelNumber,
                            ItemId: rl.ItemId,
                            ServiceName: rl.ItemEnName,
                            ServiceNameAr: rl.ItemArName,
                            Price: rl.SalePrice,
                            Currency: currency,
                            Barcode: barcode,
                            QrPayload: barcode,
                            CreatedAt: DateTime.UtcNow,
                            IsAssigned: false,
                            AssignedStaffId: null,
                            AssignedStaffName: null));
                    }

                    // 4) Wallet payment + ledger
                    if (walletAmount > 0 && walletSubId.HasValue && walletPtId.HasValue)
                    {
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.AppointmentPayments (
                                AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt, IsWalletPayment
                            )
                            VALUES (@AppointmentId, @Amount, @PaymentTypeId, 'FULL', NULL, SYSUTCDATETIME(), 1)",
                            new { AppointmentId = leadApptId, Amount = walletAmount, PaymentTypeId = walletPtId.Value });

                        decimal newBalance = walletBalanceBefore - walletAmount;
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.SubscriptionsHistory (
                                CustomerRef, RefType, InvoiceId, SubscriptionId,
                                Amount, Balance, AddedBy, AddedDate, Deleted
                            )
                            VALUES (@CustomerRef, 1, NULL, @SubscriptionId, @Amount, @Balance, @AddedBy, @AddedDate, 0)",
                            new
                            {
                                CustomerRef = walletCustomerRef,
                                SubscriptionId = walletSubId.Value,
                                Amount = -walletAmount,
                                Balance = newBalance,
                                AddedBy = currentUserId,
                                AddedDate = DateTime.UtcNow
                            });
                    }

                    // 5) Split payments
                    foreach (var sp in splits)
                    {
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.AppointmentPayments (
                                AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt
                            )
                            VALUES (@AppointmentId, @Amount, @PaymentTypeId, 'FULL', @VoucherCode, SYSUTCDATETIME())",
                            new
                            {
                                AppointmentId = leadApptId,
                                sp.Amount,
                                sp.PaymentTypeId,
                                VoucherCode = string.IsNullOrWhiteSpace(sp.VoucherCode) ? null : sp.VoucherCode.Trim()
                            });
                    }

                    // 6) Redeem the discount code (atomic — rolls back with the sale).
                    //    The guard (UsedCount < MaxUses) prevents two concurrent sales from
                    //    pushing the same code past its limit.
                    if (redeemedCodeId.HasValue)
                    {
                        int bumped = SqlMapper.Execute(uow.Connection, @"
                            UPDATE dbo.DiscountCodes
                            SET UsedCount = UsedCount + 1
                            WHERE Id = @Id AND Deleted = 0 AND UsedCount < MaxUses",
                            new { Id = redeemedCodeId.Value });

                        if (bumped == 0)
                            throw new InvalidOperationException("Discount code was just exhausted by another sale");

                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.DiscountCodeRedemptions (
                                DiscountCodeId, InvoiceId, InvoiceNumber, InvoiceValue,
                                DiscountAmount, RedeemedByCustomerId, RedeemedByUserId, RedeemedAt
                            )
                            VALUES (
                                @DiscountCodeId, @InvoiceId, @InvoiceNumber, @InvoiceValue,
                                @DiscountAmount, @RedeemedByCustomerId, @RedeemedByUserId, SYSUTCDATETIME()
                            )",
                            new
                            {
                                DiscountCodeId = redeemedCodeId.Value,
                                InvoiceId = invoiceId,
                                InvoiceNumber = invoiceNumber,
                                InvoiceValue = grandTotal,
                                DiscountAmount = discountAmount,
                                RedeemedByCustomerId = request.CustomerId,
                                RedeemedByUserId = currentUserId > 0 ? currentUserId : (int?)null
                            });
                    }

                    uow.Commit();

                    // Store the invoice PDF so the invoice-pdf link works for POS too.
                    try { GenerateAndStoreInvoicePdf(conn, leadApptId); }
                    catch (Exception ex) { Debug.WriteLine($"[POS PDF] {ex.Message}"); }

                    string pdfUrl = $"{Request.Scheme}://{Request.Host}/api/myfatoorah/invoice-pdf/{leadApptId}";

                    // -------- WhatsApp (best-effort) --------
                    bool waSent = false; string? waErr = null;
                    if (request.SendWhatsApp)
                    {
                        try
                        {
                            (waSent, waErr) = await SendPosReceiptAsync(
                                conn,
                                appointmentId: leadApptId,
                                customerName: (string)customer.CUSTOMER_NAME,
                                customerPhone: (string)customer.CUSTOMER_PHONE1,
                                customerLang: (string)customer.Lang,
                                currency: currency, currencyAr: currencyAr,
                                invoiceNumber: invoiceNumber,
                                grandTotal: grandTotal, paidTotal: paidTotal,
                                subTotal: subTotal, discountAmount: discountAmount,
                                lines: resolved);
                        }
                        catch (Exception ex) { waSent = false; waErr = $"Send failed: {ex.Message}"; }
                    }

                    var response = new PosDtos.PosCheckoutResponse(
                        InvoiceId: invoiceId,
                        InvoiceNumber: invoiceNumber,
                        LeadAppointmentId: leadApptId,
                        SaleGroupId: saleGroupId,
                        AppointmentIds: apptIds,
                        TotalAmount: grandTotal,
                        PaidAmount: paidTotal,
                        RemainingAmount: 0m,
                        WalletDeductedAmount: walletAmount,
                        PaymentStatus: "FULL",
                        Currency: currency,
                        WhatsAppSent: waSent,
                        WhatsAppError: waErr,
                        InvoicePdfUrl: pdfUrl,
                        Labels: labels,
                        SubTotal: subTotal,
                        DiscountAmount: discountAmount,
                        DiscountCode: redeemedCode,
                        DiscountCodeId: redeemedCodeId);

                    return Ok(new PosDtos.ApiResult<PosDtos.PosCheckoutResponse>(true, null, response));
                }
                catch (Exception ex)
                {
                    return Ok(new PosDtos.ApiResult<PosDtos.PosCheckoutResponse>(
                        false, $"Failed to create POS sale: {ex.Message}", null));
                }
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosCheckoutResponse>(
                    false, $"POS checkout failed before transaction: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/pos/receipt/{invoiceId}
        // =====================================================================
        [HttpGet("receipt/{invoiceId:int}")]
        public ActionResult<PosDtos.ApiResult<PosDtos.PosReceiptDto>> Receipt(int invoiceId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var head = SqlMapper.Query(conn, @"
                    SELECT
                        inv.Id              AS InvoiceId,
                        inv.InvoiceNumber   AS InvoiceNumber,
                        inv.AppointmentId   AS LeadAppointmentId,
                        inv.TotalAmount     AS TotalAmount,
                        inv.PaidAmount      AS PaidAmount,
                        inv.RemainingAmount AS RemainingAmount,
                        inv.Currency        AS Currency,
                        inv.PaymentStatus   AS PaymentStatus,
                        inv.CreatedAt       AS CreatedAt,
                        ISNULL(inv.SubTotal, inv.TotalAmount) AS SubTotal,
                        inv.DiscountType    AS DiscountType,
                        inv.DiscountValue   AS DiscountValue,
                        ISNULL(inv.DiscountAmount, 0) AS DiscountAmount,
                        c.CUSTOMER_NAME     AS CustomerName,
                        c.CUSTOMER_PHONE1   AS CustomerPhone,
                        b.ArabicCurrencyName AS CurrencyAr
                    FROM dbo.AppointmentInvoices inv
                    INNER JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = inv.CustomerId
                    LEFT  JOIN dbo.BRANCH b          ON b.BRANCH_ID   = inv.BranchId
                    WHERE inv.Id = @Id",
                    new { Id = invoiceId }).FirstOrDefault();

                if (head == null)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosReceiptDto>(false, "Invoice not found", null));

                int leadApptId = (int)head.LeadAppointmentId;
                string currency = (string?)head.Currency ?? "KWD";
                string currencyAr = (string?)head.CurrencyAr ?? currency;

                var lines = SqlMapper.Query(conn, @"
                    SELECT
                        ail.Id            AS Id,
                        ail.AppointmentId AS AppointmentId,
                        ail.ItemId        AS ItemId,
                        ail.StaffId       AS StaffId,
                        i.ITEM_NAME1      AS ItemName,
                        i.ITEM_NAME2      AS ItemNameAr,
                        s.EnglishName     AS StaffName,
                        s.ArabicName      AS StaffNameAr,
                        ail.DiscountedUnitPrice AS UnitPrice,
                        ail.TotalPrice    AS TotalPrice,
                        ISNULL(ail.OriginalUnitPrice, ail.DiscountedUnitPrice) AS OriginalUnitPrice,
                        ail.PackageOfferId   AS PackageOfferId,
                        ail.PackageOfferName AS PackageOfferName,
                        ail.PackageGroupId   AS PackageGroupId
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.ITEM  i ON i.ITEM_ID = ail.ItemId
                    LEFT  JOIN dbo.STAFF s ON s.Id      = ail.StaffId
                    WHERE ail.InvoiceId = @InvoiceId AND ISNULL(ail.IsRefunded, 0) = 0
                    ORDER BY ail.Id",
                    new { InvoiceId = invoiceId })
                    .Select(l => new PosDtos.PosReceiptLineDto(
                        Id: (int?)l.Id,
                        AppointmentId: (int)l.AppointmentId,
                        ItemId: (int)l.ItemId,
                        StaffId: (int?)l.StaffId,
                        ItemName: (string?)l.ItemName ?? "",
                        ItemNameAr: (string?)l.ItemNameAr ?? (string?)l.ItemName ?? "",
                        StaffName: (string?)l.StaffName ?? "",
                        StaffNameAr: (string?)l.StaffNameAr ?? (string?)l.StaffName ?? "",
                        Quantity: 1,
                        UnitPrice: (decimal)l.UnitPrice,
                        TotalPrice: (decimal)l.TotalPrice,
                        PackageOfferId: (int?)l.PackageOfferId,
                        PackageOfferName: (string?)l.PackageOfferName,
                        PackageGroupId: (Guid?)l.PackageGroupId,
                        OriginalUnitPrice: (decimal)l.OriginalUnitPrice))
                    .ToList();

                var payments = SqlMapper.Query(conn, @"
                    SELECT
                        ap.PaymentTypeId             AS PaymentTypeId,
                        pt.INVOICE_PAYMENT_TYPE_NAME1 AS Name,
                        pt.INVOICE_PAYMENT_TYPE_NAME2 AS NameAr,
                        ap.Amount                    AS Amount,
                        ISNULL(ap.IsWalletPayment, 0) AS IsWallet
                    FROM dbo.AppointmentPayments ap
                    LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                    WHERE ap.AppointmentId = @AppointmentId
                    ORDER BY ap.Id",
                    new { AppointmentId = leadApptId })
                    .Select(p => new PosDtos.PosReceiptPaymentDto(
                        PaymentTypeId: (int)(p.PaymentTypeId ?? 0),
                        PaymentTypeName: (string?)p.Name ?? "",
                        PaymentTypeNameAr: (string?)p.NameAr ?? (string?)p.Name ?? "",
                        Amount: (decimal)p.Amount,
                        IsWallet: Convert.ToInt32(p.IsWallet) == 1))
                    .ToList();

                var company = SqlMapper.Query(conn, @"
                    SELECT TOP 1
                        COMPANY_NAME1 AS CompanyName1, COMPANY_NAME2 AS CompanyName2,
                        COMPANY_LOGO  AS CompanyLogo,  COMPANY_PHONE AS CompanyPhone,
                        COMPANY_ADRESS1 AS CompanyAddress1,
                        FOOTER AS Footer, FOOTER1 AS Footer1, FOOTER2 AS Footer2
                    FROM dbo.COMPANY ORDER BY COMPANY_ID").FirstOrDefault();

                PosDtos.PosCompanyInfoDto? companyDto = null;
                if (company != null)
                {
                    string? logo = (string?)company.CompanyLogo;
                    if (!string.IsNullOrWhiteSpace(logo) && !logo.StartsWith("http"))
                        logo = $"{Request.Scheme}://{Request.Host}{logo}";

                    companyDto = new PosDtos.PosCompanyInfoDto(
                        CompanyName1: (string?)company.CompanyName1 ?? "",
                        CompanyName2: (string?)company.CompanyName2 ?? "",
                        CompanyLogo: logo,
                        CompanyPhone: (string?)company.CompanyPhone,
                        CompanyAddress1: (string?)company.CompanyAddress1,
                        Footer: (string?)company.Footer,
                        Footer1: (string?)company.Footer1,
                        Footer2: (string?)company.Footer2);
                }

                // -------- Labels (un-staffed service lines) --------
                var labels = SqlMapper.Query(conn, @"
                    SELECT
                        lbl.Id            AS LabelId,
                        lbl.InvoiceId     AS InvoiceId,
                        lbl.AppointmentId AS AppointmentId,
                        lbl.InvoiceLineId AS InvoiceLineId,
                        lbl.LabelNumber   AS LabelNumber,
                        lbl.ItemId        AS ItemId,
                        lbl.ServiceName   AS ServiceName,
                        lbl.ServiceNameAr AS ServiceNameAr,
                        lbl.Price         AS Price,
                        lbl.Barcode       AS Barcode,
                        lbl.QrPayload     AS QrPayload,
                        lbl.CreatedAt     AS CreatedAt,
                        lbl.IsAssigned    AS IsAssigned,
                        lbl.AssignedStaffId AS AssignedStaffId,
                        st.EnglishName    AS AssignedStaffName
                    FROM dbo.PosServiceLabels lbl
                    LEFT JOIN dbo.STAFF st ON st.Id = lbl.AssignedStaffId
                    WHERE lbl.InvoiceId = @InvoiceId
                    ORDER BY lbl.LabelNumber, lbl.Id",
                    new { InvoiceId = invoiceId })
                    .Select(x => new PosDtos.PosLabelDto(
                        LabelId: (int)x.LabelId,
                        InvoiceId: (int)x.InvoiceId,
                        AppointmentId: (int)x.AppointmentId,
                        InvoiceLineId: (int?)x.InvoiceLineId,
                        LabelNumber: (int)x.LabelNumber,
                        ItemId: (int)x.ItemId,
                        ServiceName: (string?)x.ServiceName ?? "",
                        ServiceNameAr: (string?)x.ServiceNameAr ?? (string?)x.ServiceName ?? "",
                        Price: (decimal)x.Price,
                        Currency: currency,
                        Barcode: (string?)x.Barcode ?? "",
                        QrPayload: (string?)x.QrPayload ?? (string?)x.Barcode ?? "",
                        CreatedAt: (DateTime)x.CreatedAt,
                        IsAssigned: Convert.ToInt32(x.IsAssigned) == 1,
                        AssignedStaffId: (int?)x.AssignedStaffId,
                        AssignedStaffName: (string?)x.AssignedStaffName))
                    .ToList();

                var dto = new PosDtos.PosReceiptDto(
                    InvoiceId: (int)head.InvoiceId,
                    InvoiceNumber: (string?)head.InvoiceNumber ?? "",
                    LeadAppointmentId: leadApptId,
                    CreatedAt: (DateTime)head.CreatedAt,
                    CustomerName: (string?)head.CustomerName ?? "",
                    CustomerPhone: (string?)head.CustomerPhone ?? "",
                    TotalAmount: (decimal)head.TotalAmount,
                    PaidAmount: (decimal)head.PaidAmount,
                    RemainingAmount: (decimal)head.RemainingAmount,
                    Currency: currency,
                    CurrencyAr: currencyAr,
                    PaymentStatus: (string?)head.PaymentStatus ?? "FULL",
                    Lines: lines,
                    Payments: payments,
                    Company: companyDto,
                    InvoicePdfUrl: $"{Request.Scheme}://{Request.Host}/api/myfatoorah/invoice-pdf/{leadApptId}",
                    Labels: labels,
                    SubTotal: (decimal)head.SubTotal,
                    DiscountType: (string?)head.DiscountType,
                    DiscountValue: (decimal?)head.DiscountValue,
                    DiscountAmount: (decimal)head.DiscountAmount);

                return Ok(new PosDtos.ApiResult<PosDtos.PosReceiptDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosReceiptDto>(
                    false, $"Failed to load receipt: {ex.Message}", null));
            }
        }

        // =====================================================================
        // PHASE 2 — assign a staff member to printed service labels
        //   GET  /api/pos/labels/lookup?barcode=      → find one label (scan / manual)
        //   GET  /api/pos/labels/unassigned?branchId= → list labels awaiting a staff
        //   POST /api/pos/labels/assign               → assign staff to label(s)
        //   POST /api/pos/labels/{id}/reprint         → bump print count
        // =====================================================================

        /// <summary>Find a single label by its barcode/QR value (8 digits). Used by scan + manual entry.</summary>
        [HttpGet("labels/lookup")]
        public ActionResult<PosDtos.ApiResult<PosDtos.PosLabelDto>> LookupLabel([FromQuery] string barcode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(barcode))
                    return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(false, "Barcode is required", null));

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var label = QueryLabels(conn, "l.Barcode = @Barcode", new { Barcode = barcode.Trim() }).FirstOrDefault();
                if (label == null)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(false, "No label matches this code", null));

                return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(true, null, label));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(false, $"Lookup failed: {ex.Message}", null));
            }
        }

        /// <summary>List labels still awaiting staff assignment (optionally filtered by branch + free text).</summary>
        [HttpGet("labels/unassigned")]
        public ActionResult<PosDtos.ApiResult<List<PosDtos.PosLabelDto>>> UnassignedLabels(
            [FromQuery] int? branchId, [FromQuery] string? search, [FromQuery] int? take)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var where = new StringBuilder("ISNULL(l.IsAssigned, 0) = 0");
                if (branchId.HasValue && branchId.Value > 0) where.Append(" AND l.BranchId = @BranchId");
                if (!string.IsNullOrWhiteSpace(search))
                    where.Append(" AND (l.Barcode LIKE @Like OR l.ServiceName LIKE @Like OR l.ServiceNameAr LIKE @Like)");

                int top = take.HasValue && take.Value > 0 && take.Value <= 500 ? take.Value : 200;

                var labels = QueryLabels(conn, where.ToString(), new
                {
                    BranchId = branchId ?? 0,
                    Like = "%" + (search ?? "").Trim() + "%"
                }, top);

                return Ok(new PosDtos.ApiResult<List<PosDtos.PosLabelDto>>(true, null, labels));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<List<PosDtos.PosLabelDto>>(false, $"Failed to load labels: {ex.Message}", null));
            }
        }

        /// <summary>
        /// Assign a staff member to one or more labels — each label can go to a DIFFERENT staff.
        /// For each pair this writes the staff onto the underlying service row
        /// (AppointmentData + AppointmentInvoiceLines) and marks the label assigned.
        /// Already-assigned / unknown labels (or unknown staff) are skipped, not an error.
        /// </summary>
        [HttpPost("labels/assign")]
        public ActionResult<PosDtos.ApiResult<PosDtos.PosAssignLabelsResponse>> AssignLabels(
            [FromBody] PosDtos.PosAssignLabelsRequest request)
        {
            try
            {
                if (request == null || request.Assignments == null || request.Assignments.Count == 0)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosAssignLabelsResponse>(false, "No labels selected", null));

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                // de-dup by label (last write wins) and keep only valid pairs
                var pairs = request.Assignments
                    .Where(a => a != null && a.LabelId > 0 && a.StaffId > 0)
                    .GroupBy(a => a.LabelId)
                    .ToDictionary(g => g.Key, g => g.Last().StaffId);

                if (pairs.Count == 0)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosAssignLabelsResponse>(false, "A staff member is required for each label", null));

                // valid staff ids (active, not deleted)
                var staffIds = pairs.Values.Distinct().ToList();
                var validStaff = SqlMapper.Query(conn,
                    "SELECT Id FROM dbo.STAFF WHERE Deleted = 0 AND Id IN @Ids",
                    new { Ids = staffIds })
                    .Select(r => (int)r.Id).ToHashSet();

                int userId = ResolveCurrentUserId();
                int assigned = 0, skipped = 0;

                using (var uow = new UnitOfWork(conn))
                {
                    foreach (var kv in pairs)
                    {
                        int labelId = kv.Key, staffId = kv.Value;
                        if (!validStaff.Contains(staffId)) { skipped++; continue; }

                        var lbl = SqlMapper.Query(uow.Connection, @"
                            SELECT Id, AppointmentId, InvoiceLineId, ISNULL(IsAssigned,0) AS IsAssigned
                            FROM dbo.PosServiceLabels WHERE Id = @Id",
                            new { Id = labelId }).FirstOrDefault();

                        if (lbl == null || Convert.ToBoolean(lbl.IsAssigned)) { skipped++; continue; }

                        // 1) the appointment row that represents this service line
                        SqlMapper.Execute(uow.Connection,
                            "UPDATE dbo.AppointmentData SET StaffId = @StaffId WHERE Id = @AppointmentId",
                            new { StaffId = staffId, AppointmentId = (int)lbl.AppointmentId });

                        // 2) the matching invoice line (when known)
                        if (lbl.InvoiceLineId != null && !(lbl.InvoiceLineId is DBNull))
                        {
                            SqlMapper.Execute(uow.Connection,
                                "UPDATE dbo.AppointmentInvoiceLines SET StaffId = @StaffId WHERE Id = @LineId",
                                new { StaffId = staffId, LineId = (int)lbl.InvoiceLineId });
                        }

                        // 3) mark the label assigned
                        SqlMapper.Execute(uow.Connection, @"
                            UPDATE dbo.PosServiceLabels
                            SET IsAssigned = 1, AssignedStaffId = @StaffId,
                                AssignedAt = SYSUTCDATETIME(), AssignedBy = @UserId
                            WHERE Id = @Id",
                            new { StaffId = staffId, UserId = userId, Id = labelId });

                        assigned++;
                    }

                    uow.Commit();
                }

                var ids = pairs.Keys.ToList();
                var resulting = ids.Count > 0
                    ? QueryLabels(conn, "l.Id IN @Ids", new { Ids = ids })
                    : new List<PosDtos.PosLabelDto>();

                return Ok(new PosDtos.ApiResult<PosDtos.PosAssignLabelsResponse>(
                    true, null,
                    new PosDtos.PosAssignLabelsResponse(assigned, skipped, resulting)));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosAssignLabelsResponse>(
                    false, $"Assignment failed: {ex.Message}", null));
            }
        }

        /// <summary>Bump the print counter for a label (reprint bookkeeping). Returns the label.</summary>
        [HttpPost("labels/{id:int}/reprint")]
        public ActionResult<PosDtos.ApiResult<PosDtos.PosLabelDto>> ReprintLabel(int id)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                using (var uow = new UnitOfWork(conn))
                {
                    SqlMapper.Execute(uow.Connection,
                        "UPDATE dbo.PosServiceLabels SET PrintCount = ISNULL(PrintCount,0) + 1 WHERE Id = @Id",
                        new { Id = id });
                    uow.Commit();
                }

                var label = QueryLabels(conn, "l.Id = @Id", new { Id = id }).FirstOrDefault();
                if (label == null)
                    return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(false, "Label not found", null));

                return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(true, null, label));
            }
            catch (Exception ex)
            {
                return Ok(new PosDtos.ApiResult<PosDtos.PosLabelDto>(false, $"Reprint failed: {ex.Message}", null));
            }
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        /// <summary>Reads service labels with the assigned-staff name + invoice currency joined.</summary>
        private List<PosDtos.PosLabelDto> QueryLabels(IDbConnection conn, string where, object param, int? top = null)
        {
            string topClause = top.HasValue ? $"TOP ({top.Value}) " : "";
            string sql = $@"
                SELECT {topClause}
                    l.Id            AS LabelId,
                    l.InvoiceId     AS InvoiceId,
                    l.AppointmentId AS AppointmentId,
                    l.InvoiceLineId AS InvoiceLineId,
                    l.LabelNumber   AS LabelNumber,
                    l.ItemId        AS ItemId,
                    l.ServiceName   AS ServiceName,
                    l.ServiceNameAr AS ServiceNameAr,
                    l.Price         AS Price,
                    ISNULL(inv.Currency, 'KWD') AS Currency,
                    l.Barcode       AS Barcode,
                    l.QrPayload     AS QrPayload,
                    l.CreatedAt     AS CreatedAt,
                    ISNULL(l.IsAssigned, 0) AS IsAssigned,
                    l.AssignedStaffId       AS AssignedStaffId,
                    s.EnglishName           AS AssignedStaffName
                FROM dbo.PosServiceLabels l
                LEFT JOIN dbo.AppointmentInvoices inv ON inv.Id = l.InvoiceId
                LEFT JOIN dbo.STAFF s ON s.Id = l.AssignedStaffId
                WHERE {where}
                ORDER BY l.CreatedAt DESC, l.LabelNumber";

            return SqlMapper.Query(conn, sql, param)
                .Select(l => new PosDtos.PosLabelDto(
                    LabelId: (int)l.LabelId,
                    InvoiceId: (int)l.InvoiceId,
                    AppointmentId: (int)l.AppointmentId,
                    InvoiceLineId: (l.InvoiceLineId == null || l.InvoiceLineId is DBNull) ? (int?)null : (int)l.InvoiceLineId,
                    LabelNumber: (int)l.LabelNumber,
                    ItemId: (int)l.ItemId,
                    ServiceName: (string)(l.ServiceName ?? ""),
                    ServiceNameAr: (string)(l.ServiceNameAr ?? ""),
                    Price: (decimal)l.Price,
                    Currency: (string)(l.Currency ?? "KWD"),
                    Barcode: (string)(l.Barcode ?? ""),
                    QrPayload: (string)(l.QrPayload ?? (l.Barcode ?? "")),
                    CreatedAt: (DateTime)l.CreatedAt,
                    IsAssigned: Convert.ToBoolean(l.IsAssigned),
                    AssignedStaffId: (l.AssignedStaffId == null || l.AssignedStaffId is DBNull) ? (int?)null : (int)l.AssignedStaffId,
                    AssignedStaffName: (l.AssignedStaffName == null || l.AssignedStaffName is DBNull) ? null : (string?)l.AssignedStaffName))
                .ToList();
        }

        private ActionResult<PosDtos.ApiResult<PosDtos.PosCheckoutResponse>> FailCheckout(string error) =>
            Ok(new PosDtos.ApiResult<PosDtos.PosCheckoutResponse>(false, error, null));

        private static TimeSpan NearestSlotNow(DateTime localNow, int startHour, int endHour, int slotMinutes)
        {
            int totalMins = localNow.Hour * 60 + localNow.Minute;
            int snapped = ((totalMins + slotMinutes - 1) / slotMinutes) * slotMinutes;
            int minStart = startHour * 60, minEnd = endHour * 60;
            if (snapped < minStart) snapped = minStart;
            if (snapped > minEnd) snapped = minStart;   // late-night POS → roll to opening hour (cosmetic only)
            return TimeSpan.FromMinutes(snapped);
        }

        private static int ResolveDuration(int? lineOverride, double? itemUnitDuration, int slotMinutes)
        {
            if (lineOverride.HasValue && lineOverride.Value > 0) return lineOverride.Value;
            if (itemUnitDuration.HasValue && itemUnitDuration.Value > 0) return (int)Math.Ceiling(itemUnitDuration.Value);
            return Math.Max(slotMinutes, 5);
        }

        private static decimal ComputeDiscountPercent(decimal master, decimal sale)
        {
            if (master <= 0m || sale >= master) return 0m;
            return Math.Round((master - sale) / master * 100m, 2);
        }

        /// <summary>Generates a unique 8-digit numeric barcode for a service label.</summary>
        private static string GenerateUniqueBarcode(IDbConnection conn)
        {
            var rng = Random.Shared;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                string code = rng.Next(0, 100_000_000).ToString("D8");   // 00000000..99999999
                int exists = SqlMapper.Query<int>(conn,
                    "SELECT COUNT(1) FROM dbo.PosServiceLabels WHERE Barcode = @c",
                    new { c = code }).FirstOrDefault();
                if (exists == 0) return code;
            }
            // Extremely unlikely fallback — still 8 digits.
            return (DateTime.UtcNow.Ticks % 100_000_000).ToString("D8");
        }

        private int ResolveCurrentUserId()
        {
            var idClaim =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                User.FindFirst("sub")?.Value ??
                User.FindFirst("UserId")?.Value;
            return int.TryParse(idClaim, out var id) ? id : 0;
        }

        /// <summary>Turn a stored relative image path (/uploads/items/..) into an absolute URL.</summary>
        private string? BuildImageUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return raw;
            var path = raw.StartsWith("/") ? raw : "/" + raw;
            return $"{Request.Scheme}://{Request.Host}{path}";
        }

        private async Task<(bool Sent, string? Error)> SendPosReceiptAsync(
            IDbConnection conn, int appointmentId,
            string customerName, string customerPhone, string customerLang,
            string currency, string currencyAr, string invoiceNumber,
            decimal grandTotal, decimal paidTotal, List<ResolvedLine> lines,
            decimal subTotal = 0m, decimal discountAmount = 0m)
        {
            var config = SqlMapper.Query(conn, @"
                SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
                FROM dbo.WHATSAPP_CONFIG ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
                return (false, "WhatsApp sending is disabled");

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string pdfUrl = $"{Request.Scheme}://{Request.Host}/api/myfatoorah/invoice-pdf/{appointmentId}";

            string message = customerLang == "en"
                ? BuildReceiptEn(header, footer, invoiceNumber, lines, grandTotal, paidTotal, currency, pdfUrl, subTotal, discountAmount)
                : BuildReceiptAr(header, footer, invoiceNumber, lines, grandTotal, paidTotal, currencyAr, pdfUrl, subTotal, discountAmount);

            string phone = NormalizePhone(customerPhone);

            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

            var payload = new { instance_id = instanceId, message, number = phone };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(EnjazatikUrl, content);
            if (resp.IsSuccessStatusCode) return (true, null);

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"API error: {resp.StatusCode} — {body}");
        }

        private static string BuildReceiptEn(
            string header, string footer, string invoiceNumber, List<ResolvedLine> lines,
            decimal total, decimal paid, string currency, string pdfUrl,
            decimal subTotal = 0m, decimal discountAmount = 0m)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
            string services = string.Join(", ", lines.Select(l => l.ItemEnName));
            sb.AppendLine("✅ Payment received successfully");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"Invoice: {invoiceNumber}");
            sb.AppendLine($"Services: {services}");
            if (discountAmount > 0m)
            {
                sb.AppendLine($"Subtotal: {currency} {subTotal:F2}");
                sb.AppendLine($"Discount: - {currency} {discountAmount:F2}");
            }
            sb.AppendLine($"Total: {currency} {total:F2}");
            sb.AppendLine($"Paid: {currency} {paid:F2}");
            sb.AppendLine($"📄 Invoice: {pdfUrl}");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("Thank you!");
            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string BuildReceiptAr(
            string header, string footer, string invoiceNumber, List<ResolvedLine> lines,
            decimal total, decimal paid, string currencyAr, string pdfUrl,
            decimal subTotal = 0m, decimal discountAmount = 0m)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
            string services = string.Join("، ",
                lines.Select(l => string.IsNullOrWhiteSpace(l.ItemArName) ? l.ItemEnName : l.ItemArName));
            sb.AppendLine("✅ تم استلام الدفع بنجاح");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"الفاتورة: {invoiceNumber}");
            sb.AppendLine($"الخدمات: {services}");
            if (discountAmount > 0m)
            {
                sb.AppendLine($"الإجمالي قبل الخصم: {subTotal:F2} {currencyAr}");
                sb.AppendLine($"الخصم: - {discountAmount:F2} {currencyAr}");
            }
            sb.AppendLine($"الإجمالي: {total:F2} {currencyAr}");
            sb.AppendLine($"المدفوع: {paid:F2} {currencyAr}");
            sb.AppendLine($"📄 الفاتورة: {pdfUrl}");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("شكراً لكم!");
            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("0")) digits = "965" + digits.Substring(1);
            if (digits.Length == 8) digits = "965" + digits;
            return digits;
        }

        private void GenerateAndStoreInvoicePdf(IDbConnection conn, int appointmentId, string? paymentMethod = null)
        {
            try
            {
                var head = conn.Query<dynamic>(@"
                    SELECT TOP 1
                        inv.Id AS InvoiceId, inv.InvoiceNumber, inv.TotalAmount,
                        inv.PaidAmount, inv.RemainingAmount,
                        c.CUSTOMER_NAME AS CustomerName, c.CUSTOMER_PHONE1 AS CustomerPhone,
                        a.Notes AS Notes, b.EnglishCurrencyName, b.ArabicCurrencyName
                    FROM dbo.AppointmentInvoices inv
                    INNER JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
                    INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
                    LEFT  JOIN dbo.BRANCH b          ON b.BRANCH_ID   = a.BranchId
                    WHERE inv.AppointmentId = @Id",
                    new { Id = appointmentId }).FirstOrDefault();
                if (head == null) return;

                var companyInfo = conn.Query<dynamic>(@"
                    SELECT TOP 1 COMPANY_NAME1, COMPANY_NAME2, COMPANY_ADRESS1, COMPANY_PHONE, COMPANY_LOGO,
                                 FOOTER, FOOTER1, FOOTER2
                    FROM dbo.COMPANY ORDER BY COMPANY_ID").FirstOrDefault();

                int invoiceId = (int)head.InvoiceId;
                string currency = (string)(head.EnglishCurrencyName ?? "KWD");
                string currencyAr = (string)(head.ArabicCurrencyName ?? "د.ك");
                string invoiceNumber = (string?)head.InvoiceNumber ?? $"POS-{DateTime.UtcNow:yyyyMMdd}-{appointmentId}";
                decimal total = (decimal)head.TotalAmount;
                decimal paid = (decimal)head.PaidAmount;

                var pdfData = new InvoicePdfData
                {
                    InvoiceNumber = invoiceNumber,
                    InvoiceDate = DateTime.UtcNow,
                    CustomerName = (string)head.CustomerName,
                    CustomerPhone = (string)head.CustomerPhone,
                    Currency = currency,
                    CurrencyAr = currencyAr,
                    TotalAmount = total,
                    PaidAmount = paid,
                    RemainingAmount = Math.Max(0, total - paid),
                    PaymentMethod = paymentMethod ?? "—",
                    PaymentStatus = paid >= total ? "Paid" : "Deposit Paid",
                    Notes = (string?)head.Notes,
                    SalonName = (string?)(companyInfo?.COMPANY_NAME1) ?? "Salon",
                    SalonNameAr = (string?)(companyInfo?.COMPANY_NAME2) ?? "",
                    SalonAddress = (string?)(companyInfo?.COMPANY_ADRESS1) ?? "",
                    SalonPhone = (string?)(companyInfo?.COMPANY_PHONE) ?? "",
                    SalonLogoUrl = companyInfo?.COMPANY_LOGO != null
                        ? (((string)companyInfo.COMPANY_LOGO).StartsWith("http")
                            ? (string)companyInfo.COMPANY_LOGO
                            : $"{Request.Scheme}://{Request.Host}{companyInfo.COMPANY_LOGO}")
                        : null,
                    FooterLine1 = (string?)(companyInfo?.FOOTER),
                    FooterLine2 = (string?)(companyInfo?.FOOTER1),
                    FooterLine3 = (string?)(companyInfo?.FOOTER2),
                    LineItems = new List<InvoiceLineData>()
                };

                var lines = conn.Query<dynamic>(@"
                    SELECT i.ITEM_NAME1 AS ItemEn, i.ITEM_NAME2 AS ItemAr,
                           s.EnglishName AS StaffEn, s.ArabicName AS StaffAr,
                           ail.DiscountedUnitPrice AS UnitPrice, ail.TotalPrice AS TotalPrice,
                           ail.PackageGroupId AS PackageGroupId, ail.PackageOfferName AS PackageOfferName
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.ITEM  i ON i.ITEM_ID = ail.ItemId
                    LEFT  JOIN dbo.STAFF s ON s.Id      = ail.StaffId
                    WHERE ail.InvoiceId = @InvoiceId AND ISNULL(ail.IsRefunded, 0) = 0
                    ORDER BY ail.Id",
                    new { InvoiceId = invoiceId }).ToList();

                foreach (var ln in lines)
                {
                    pdfData.LineItems.Add(new InvoiceLineData
                    {
                        ItemName = (string)(ln.ItemEn ?? ln.ItemAr ?? "Service"),
                        StaffName = (string)(ln.StaffEn ?? ln.StaffAr ?? "—"),
                        Quantity = 1,
                        UnitPrice = (decimal)ln.UnitPrice,
                        TotalPrice = (decimal)ln.TotalPrice,
                        PackageGroupId = ln.PackageGroupId == null || ln.PackageGroupId is DBNull
                            ? (Guid?)null : (Guid)ln.PackageGroupId,
                        PackageOfferName = ln.PackageOfferName == null || ln.PackageOfferName is DBNull
                            ? null : (string)ln.PackageOfferName
                    });
                }

                byte[] pdfBytes = PdfInvoiceService.GenerateInvoicePdf(pdfData);
                string fileName = $"Invoice_{invoiceNumber}_{appointmentId}.pdf";

                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.INVOICE_PDF (AppointmentId, TransactionId, FileName, PdfData, CreatedAt)
                    VALUES (@AppointmentId, NULL, @FileName, @PdfData, SYSUTCDATETIME())",
                    new { AppointmentId = appointmentId, FileName = fileName, PdfData = pdfBytes });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[POS PDF] {ex.Message}");
            }
        }

        // ===== Internal model =====
        private sealed class ResolvedLine
        {
            public int Index { get; set; }
            public int ItemId { get; set; }
            public int UnitId { get; set; }
            public int? StaffId { get; set; }   // null = un-staffed (label generated)
            public string ItemEnName { get; set; } = "";
            public string ItemArName { get; set; } = "";
            public string StaffEnName { get; set; } = "";
            public string StaffArName { get; set; } = "";
            public int DurationMinutes { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal SalePrice { get; set; }
            /// <summary>Charged unit price BEFORE the ticket discount (= per-line override
            /// or master for services; distributed share for package lines). Kept so the
            /// receipt can print listed prices + a discount line, while SalePrice/TotalPrice
            /// carry the AFTER-discount value used for revenue and staff performance.</summary>
            public decimal OriginalUnitPrice { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string? Notes { get; set; }
            public int? PackageOfferId { get; set; }
            public Guid? PackageGroupId { get; set; }
            public string? PackageOfferName { get; set; }
        }

        // Distributes a fixed package price across its lines so the line totals
        // sum EXACTLY to the package price (proportional to each line's catalog
        // price; the last line absorbs the rounding remainder).
        private static void DistributePackagePrice(List<ResolvedLine> lines, decimal packagePrice, int digits)
        {
            if (lines.Count == 0) return;
            if (digits < 0) digits = 3;

            decimal baseSum = lines.Sum(l => l.UnitPrice);
            decimal allocated = 0m;

            for (int i = 0; i < lines.Count; i++)
            {
                decimal share;
                if (i == lines.Count - 1)
                    share = packagePrice - allocated;                 // last line = remainder
                else if (baseSum > 0m)
                    share = Math.Round(packagePrice * (lines[i].UnitPrice / baseSum), digits, MidpointRounding.AwayFromZero);
                else
                    share = Math.Round(packagePrice / lines.Count, digits, MidpointRounding.AwayFromZero);

                if (share < 0m) share = 0m;
                lines[i].SalePrice = share;
                allocated += share;
            }
        }

        // Computes the money amount a ticket discount removes from the services
        // subtotal. "percentage" clamps to 0..100; "fixed" clamps to 0..subtotal.
        // Anything else (unknown type, value <= 0, subtotal <= 0) => 0 (no discount).
        private static decimal ComputeDiscountAmount(string? type, decimal value, decimal servicesSubtotal, int digits)
        {
            if (digits < 0) digits = 2;
            if (servicesSubtotal <= 0m || value <= 0m || string.IsNullOrWhiteSpace(type))
                return 0m;

            decimal amount;
            if (string.Equals(type, "percentage", StringComparison.OrdinalIgnoreCase))
            {
                decimal pct = Math.Min(100m, Math.Max(0m, value));
                amount = Math.Round(servicesSubtotal * pct / 100m, digits, MidpointRounding.AwayFromZero);
            }
            else if (string.Equals(type, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                amount = Math.Round(value, digits, MidpointRounding.AwayFromZero);
            }
            else
            {
                return 0m;
            }

            if (amount < 0m) amount = 0m;
            if (amount > servicesSubtotal) amount = servicesSubtotal;   // never below zero
            return amount;
        }

        // Spreads a ticket discount across the given SERVICE lines proportionally to
        // each line's SalePrice, so the line totals sum EXACTLY to (subtotal - discount).
        // The last line absorbs the rounding remainder. OriginalUnitPrice is preserved.
        private static void DistributeDiscount(List<ResolvedLine> serviceLines, decimal discountAmount, int digits)
        {
            if (serviceLines.Count == 0 || discountAmount <= 0m) return;
            if (digits < 0) digits = 2;

            decimal baseSum = serviceLines.Sum(l => l.SalePrice);
            if (baseSum <= 0m) return;

            decimal targetTotal = baseSum - discountAmount;
            if (targetTotal < 0m) targetTotal = 0m;

            decimal allocated = 0m;
            for (int i = 0; i < serviceLines.Count; i++)
            {
                decimal share;
                if (i == serviceLines.Count - 1)
                    share = targetTotal - allocated;                  // last line = remainder
                else
                    share = Math.Round(targetTotal * (serviceLines[i].SalePrice / baseSum), digits, MidpointRounding.AwayFromZero);

                if (share < 0m) share = 0m;
                serviceLines[i].SalePrice = share;                    // AFTER-discount price
                allocated += share;
            }
        }
    }
}