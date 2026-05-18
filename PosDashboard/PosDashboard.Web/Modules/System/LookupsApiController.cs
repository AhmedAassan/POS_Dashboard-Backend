using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.LookupsDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/lookups")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class LookupsApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public LookupsApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // Helper: validate branchId and load branch info (single row)
        private BranchInfoDto? GetBranchInfo(IDbConnection conn, int branchId)
        {
            return conn.Query<BranchInfoDto>(@"
                SELECT
                    BRANCH_ID        AS BranchId,
                    COMPANY_ID       AS CompanyId,
                    BRANCH_NAME1     AS BranchName1,
                    BRANCH_NAME2     AS BranchName2,
                    BRANCH_ADRESS    AS BranchAddress,
                    BRANCH_PHONE     AS BranchPhone,
                    ArabicCurrencyName,
                    EnglishCurrencyName,
                    RoundOfDigits,
                    TaxValue
                FROM dbo.BRANCH
                WHERE BRANCH_ID = @BranchId
                  AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { BranchId = branchId }).FirstOrDefault();
        }

        // 0) Branches (doesn't require branchId)
        [HttpGet("branches")]
        public ActionResult<ApiResult<List<BranchDto>>> Branches([FromQuery] int? companyId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var list = conn.Query<BranchDto>(@"
                SELECT
                    BRANCH_ID         AS BranchId,
                    COMPANY_ID        AS CompanyId,
                    BRANCH_NAME1      AS BranchName1,
                    BRANCH_NAME2      AS BranchName2,
                    BRANCH_IS_ACTIVE  AS BranchIsActive,
                    BRANCH_ADRESS     AS BranchAddress,
                    BRANCH_PHONE      AS BranchPhone,
                    TaxValue          AS TaxValue,
                    ArabicCurrencyName,
                    EnglishCurrencyName,
                    RoundOfDigits,
                    COLOR_CODE        AS ColorCode,
                    Email,
                    WhatsappMobile
                FROM dbo.BRANCH
                WHERE (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)
                  AND (@CompanyId IS NULL OR COMPANY_ID = @CompanyId)
                ORDER BY BRANCH_ID",
                new { CompanyId = companyId }).ToList();

            return new ApiResult<List<BranchDto>>(true, null, list);
        }

        // 1) AppointmentCategories (branch-scoped via branch info)
        [HttpGet("appointment-categories")]
        public ActionResult<ApiResult<List<AppointmentCategoryDto>>> AppointmentCategories([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            BranchInfoDto? b = null;
            if (branchId.HasValue)
            {
                b = GetBranchInfo(conn, branchId.Value);
                if (b == null)
                    return new ApiResult<List<AppointmentCategoryDto>>(false, "Branch not found or inactive", null);
            }

            var list = conn.Query<AppointmentCategoryDto>(@"
        SELECT
            @BranchId       AS BranchId,
            @BranchName1    AS BranchName1,
            @BranchName2    AS BranchName2,

            Id,
            ArabicName,
            EnglishName,
            Deleted,
            IsMakeup,
            IsPackage,
            Deposit,
            DocumentName
        FROM dbo.AppointmentCategories
        WHERE Deleted = 0
        ORDER BY Id",
                new
                {
                    BranchId = (int?)b?.BranchId,
                    BranchName1 = b?.BranchName1,
                    BranchName2 = b?.BranchName2
                }).ToList();

            return new ApiResult<List<AppointmentCategoryDto>>(true, null, list);
        }

        // 2) Categories (branch-scoped via branch info)
        [HttpGet("categories")]
        public ActionResult<ApiResult<List<CategoryDto>>> Categories([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            BranchInfoDto? b = null;
            if (branchId.HasValue)
            {
                b = GetBranchInfo(conn, branchId.Value);
                if (b == null)
                    return new ApiResult<List<CategoryDto>>(false, "Branch not found or inactive", null);
            }

            var list = conn.Query<CategoryDto>(@"
        SELECT
            @BranchId       AS BranchId,
            @BranchName1    AS BranchName1,
            @BranchName2    AS BranchName2,

            CATEGORY_ID        AS CategoryId,
            CATEGORY_NAME1     AS CategoryName1,
            CATEGORY_IS_ACTIVE AS CategoryIsActive,
            PARENT_CATEGORY    AS ParentCategory,
            CATEGORY_ORDERING  AS CategoryOrdering,
            DocumentName
        FROM dbo.CATEGORY
        WHERE CATEGORY_IS_ACTIVE = 1
        ORDER BY CATEGORY_ORDERING, CATEGORY_ID",
                new
                {
                    BranchId = (int?)b?.BranchId,
                    BranchName1 = b?.BranchName1,
                    BranchName2 = b?.BranchName2
                }).ToList();

            return new ApiResult<List<CategoryDto>>(true, null, list);
        }

        // 3) Customers (has BranchId)
        [HttpGet("customers")]
        public ActionResult<ApiResult<List<CustomerDto>>> Customers([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            BranchInfoDto? b = null;
            if (branchId.HasValue)
            {
                b = GetBranchInfo(conn, branchId.Value);
                if (b == null)
                    return new ApiResult<List<CustomerDto>>(false, "Branch not found or inactive", null);
            }

            var list = conn.Query<CustomerDto>(@"
                SELECT
                    c.BRANCH_ID AS BranchId,
                    b.BRANCH_NAME1 AS BranchName1,
                    b.BRANCH_NAME2 AS BranchName2,

                    c.CUSTOMER_ID     AS CustomerId,
                    c.CUSTOMER_NAME   AS CustomerName,
                    c.CUSTOMER_PHONE1 AS CustomerPhone1,
                    c.CUSTOMER_PHONE2 AS CustomerPhone2,
                    c.CUSTOMER_IS_BLOCK AS CustomerIsBlock,
                    c.CUSTOMER_BLOCK_REASON AS CustomerBlockReason,
                    c.CUSTOMER_NOTE   AS CustomerNote,
                    ISNULL(c.NotificationLang, 'ar') AS NotificationLang
                FROM dbo.CUSTOMER c
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = c.BRANCH_ID
                WHERE (@BranchId IS NULL OR c.BRANCH_ID = @BranchId)
                ORDER BY c.CUSTOMER_NAME",
                        new { BranchId = branchId }).ToList();

            return new ApiResult<List<CustomerDto>>(true, null, list);
        }


        // 4) Payment Types (branch-scoped via branch info)
        [HttpGet("payment-types")]
        public ActionResult<ApiResult<List<InvoicePaymentTypeDto>>> PaymentTypes([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            BranchInfoDto? b = null;
            if (branchId.HasValue)
            {
                b = GetBranchInfo(conn, branchId.Value);
                if (b == null)
                    return new ApiResult<List<InvoicePaymentTypeDto>>(false, "Branch not found or inactive", null);
            }

            var list = conn.Query<InvoicePaymentTypeDto>(@"
        SELECT
            @BranchId    AS BranchId,
            @BranchName1 AS BranchName1,
            @BranchName2 AS BranchName2,

            INVOICE_PAYMENT_TYPE_ID     AS Id,
            INVOICE_PAYMENT_TYPE_NAME1  AS Name1,
            INVOICE_PAYMENT_TYPE_NAME2  AS Name2,
            INVOICE_PAYMENT_TYPE_RATE   AS Rate,
            Treasury,
            Loyalty,
            Reservation,
            OnlinePayment,
            DocumentName
        FROM dbo.INVOICE_PAYMENT_TYPE
        ORDER BY INVOICE_PAYMENT_TYPE_ID",
                new
                {
                    BranchId = (int?)b?.BranchId,
                    BranchName1 = b?.BranchName1,
                    BranchName2 = b?.BranchName2
                }).ToList();

            return new ApiResult<List<InvoicePaymentTypeDto>>(true, null, list);
        }

        // 5) Staff (has BranchId)
        [HttpGet("staff")]
        public ActionResult<ApiResult<List<StaffDto>>> Staff([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var list = conn.Query<StaffDto>(@"
        SELECT
            s.BranchId AS BranchId,
            b.BRANCH_NAME1 AS BranchName1,
            b.BRANCH_NAME2 AS BranchName2,

            s.Id,
            s.ArabicName,
            s.EnglishName,
            s.Mobile,
            s.Active,
            s.DocumentName
        FROM dbo.STAFF s
        LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = s.BranchId
        WHERE s.Deleted = 0
          AND s.Active = 1
          AND (@BranchId IS NULL OR s.BranchId = @BranchId)
        ORDER BY s.EnglishName",
                new { BranchId = branchId }).ToList();

            return new ApiResult<List<StaffDto>>(true, null, list);
        }

        // 6) Staff with CategoryIds (has BranchId)
        [HttpGet("staff-with-categories")]
        public ActionResult<ApiResult<List<StaffWithCategoriesDto>>> StaffWithCategories([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var staff = conn.Query<dynamic>(@"
        SELECT 
            s.Id, s.ArabicName, s.EnglishName, s.Mobile, s.BranchId, s.Active,
            b.BRANCH_NAME1 AS BranchName1,
            b.BRANCH_NAME2 AS BranchName2
        FROM dbo.STAFF s
        LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = s.BranchId
        WHERE s.Deleted = 0 AND s.Active = 1
          AND (@BranchId IS NULL OR s.BranchId = @BranchId)",
                new { BranchId = branchId }).ToList();

            var staffIds = staff.Select(s => (int)s.Id).ToList();

            var rel = (staffIds.Count == 0)
                ? new List<dynamic>()
                : conn.Query<dynamic>(@"
            SELECT StaffId, CategoryId
            FROM dbo.STAFF_CATEGORY
            WHERE Active = 1 AND Deleted = 0
              AND StaffId IN @StaffIds",
                    new { StaffIds = staffIds }).ToList();

            var catsByStaff = rel
                .GroupBy(x => (int)x.StaffId)
                .ToDictionary(g => g.Key, g => g.Select(x => (int)x.CategoryId).Distinct().ToList());

            var result = staff.Select(s =>
                new StaffWithCategoriesDto(
                    BranchId: (int?)s.BranchId,
                    BranchName1: (string?)s.BranchName1,
                    BranchName2: (string?)s.BranchName2,

                    Id: (int)s.Id,
                    ArabicName: (string)s.ArabicName,
                    EnglishName: (string)s.EnglishName,
                    Mobile: (string?)s.Mobile,
                    Active: (bool)s.Active,
                    CategoryIds: catsByStaff.TryGetValue((int)s.Id, out var ids) ? ids : new List<int>()
                )).ToList();

            return new ApiResult<List<StaffWithCategoriesDto>>(true, null, result);
        }

        // 7) Services Lookup (Item + Unit + AppointmentCategory + Category + Branch)
        [HttpGet("services")]
        public ActionResult<ApiResult<List<ServiceLookupDto>>> Services(
    [FromQuery] int? branchId = null,
    [FromQuery] int? appointmentCategoryId = null,
    [FromQuery] int? categoryId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var list = conn.Query<ServiceLookupDto>(@"
        SELECT
            b.BRANCH_ID            AS BranchId,
            b.BRANCH_NAME1         AS BranchName1,
            b.BRANCH_NAME2         AS BranchName2,
            b.ArabicCurrencyName   AS ArabicCurrencyName,
            b.EnglishCurrencyName  AS EnglishCurrencyName,
            b.RoundOfDigits        AS RoundOfDigits,
            b.BRANCH_PHONE         AS BranchPhone,

            ac.Id                  AS AppointmentCategoryId,
            ac.EnglishName         AS AppointmentCategoryNameEn,
            ac.ArabicName          AS AppointmentCategoryNameAr,

            c.CATEGORY_ID          AS CategoryId,
            c.CATEGORY_NAME1       AS CategoryNameEn,
            c.CATEGORY_NAME2       AS CategoryNameAr,

            i.ITEM_ID              AS ItemId,
            i.ITEM_NAME1           AS ItemEnName,
            i.ITEM_NAME2           AS ItemArName,
            i.ITEM_IS_ACTIVE       AS ItemIsActive,
            i.DocumentName         AS ItemDocumentName,
            iu.ITEM_UNIT_ID        AS ItemUnitId,
            u.UNIT_ID              AS UnitId,
            u.UNIT_NAME1           AS UnitEnName,
            u.UNIT_NAME2           AS UnitArName,

            iu.ITEM_UNIT_PRICE     AS ItemUnitPrice,
            CAST(iu.ITEM_UNIT_DURATION AS float) AS ItemUnitDuration,
            iu.MinimumPrice        AS MinimumPrice,
            CAST(iu.Active AS bit) AS UnitActive
        FROM dbo.ITEM i
        INNER JOIN dbo.CATEGORY c
            ON c.CATEGORY_ID = i.ITEM_CATEGORY_ID
        INNER JOIN dbo.AppointmentCategories ac
            ON ac.Id = i.AppointmentCategoryId
        INNER JOIN dbo.ITEM_UNIT iu
            ON iu.ITEM_ID = i.ITEM_ID
        INNER JOIN dbo.UNIT u
            ON u.UNIT_ID = iu.UNIT_ID
        LEFT JOIN dbo.BRANCH b
            ON b.BRANCH_ID = iu.BranchId
        WHERE (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
          AND (c.CATEGORY_IS_ACTIVE = 1)
          AND (ac.Deleted = 0)
          AND (iu.Active = 1)
          AND (@BranchId IS NULL OR iu.BranchId = @BranchId)
          AND (@AppointmentCategoryId IS NULL OR ac.Id = @AppointmentCategoryId)
          AND (@CategoryId IS NULL OR c.CATEGORY_ID = @CategoryId)
        ORDER BY ac.EnglishName, c.CATEGORY_ORDERING, i.ITEM_NAME1",
                new
                {
                    BranchId = branchId,
                    AppointmentCategoryId = appointmentCategoryId,
                    CategoryId = categoryId
                }).ToList();

            return new ApiResult<List<ServiceLookupDto>>(true, null, list);
        }



        // 8) Appointment Settings (Start/End Hour, Duration, TimeZone)
        [HttpGet("appointment-settings")]
        public ActionResult<ApiResult<AppointmentSettingsDto>> AppointmentSettings()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var settings = conn.Query<(string Key, string Value)>(@"
                SELECT 
                    SETTING_KEY   AS [Key], 
                    SETTING_VALUE AS [Value]
                FROM dbo.SYSTEM_SETTING
                WHERE SETTING_KEY IN ('calendarStartHour', 'calendarEndHour', 'AppointmentDuration', 'timeZoneOffset')")
                .ToDictionary(x => x.Key, x => x.Value);

            // تحويل القيم من نصوص (Strings) إلى أرقام (Integers) مع وضع قيم افتراضية للأمان
            var result = new AppointmentSettingsDto(
                StartHour: settings.TryGetValue("calendarStartHour", out var start) ? int.Parse(start) : 10,
                EndHour: settings.TryGetValue("calendarEndHour", out var end) ? int.Parse(end) : 22,
                SlotDuration: settings.TryGetValue("AppointmentDuration", out var dur) ? int.Parse(dur) : 5,
                TimeZoneOffset: settings.TryGetValue("timeZoneOffset", out var tz) ? int.Parse(tz) : 3
            );

            return new ApiResult<AppointmentSettingsDto>(true, null, result);
        }



        // 9) Create Customer
        [HttpPost("customers")]
        public ActionResult<ApiResult<CreateCustomerResponse>> CreateCustomer(
         [FromBody] CreateCustomerRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<CreateCustomerResponse>(false, "Request body is required", null));

            if (string.IsNullOrWhiteSpace(request.CustomerName))
                return Ok(new ApiResult<CreateCustomerResponse>(false, "CustomerName is required", null));

            if (string.IsNullOrWhiteSpace(request.CustomerPhone1))
                return Ok(new ApiResult<CreateCustomerResponse>(false, "CustomerPhone1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Validate Branch
            var branch = conn.Query<dynamic>(
                @"SELECT BRANCH_ID FROM dbo.BRANCH 
                  WHERE BRANCH_ID = @Id 
                    AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { Id = request.BranchId }).FirstOrDefault();

            if (branch == null)
                return Ok(new ApiResult<CreateCustomerResponse>(false, "Branch not found or inactive", null));

            // Check duplicate phone in same branch
            var existingPhone = conn.Query<dynamic>(
                @"SELECT CUSTOMER_ID FROM dbo.CUSTOMER 
                 WHERE (CUSTOMER_PHONE1 = @Phone OR CUSTOMER_PHONE2 = @Phone) AND BRANCH_ID = @BranchId",
                new { Phone = request.CustomerPhone1.Trim(), BranchId = request.BranchId }).FirstOrDefault();

            if (existingPhone != null)
                return Ok(new ApiResult<CreateCustomerResponse>(false,
                    $"A customer with phone '{request.CustomerPhone1.Trim()}' already exists in this branch (ID: {(int)existingPhone.CUSTOMER_ID})", null));

            // Check phone2 duplicate too if provided
            if (!string.IsNullOrWhiteSpace(request.CustomerPhone2))
            {
                var existingPhone2 = conn.Query<dynamic>(
                    @"SELECT CUSTOMER_ID FROM dbo.CUSTOMER 
              WHERE (CUSTOMER_PHONE1 = @Phone OR CUSTOMER_PHONE2 = @Phone) AND BRANCH_ID = @BranchId",
                    new { Phone = request.CustomerPhone2.Trim(), BranchId = request.BranchId }).FirstOrDefault();

                if (existingPhone2 != null)
                    return Ok(new ApiResult<CreateCustomerResponse>(false,
                        $"A customer with phone '{request.CustomerPhone2.Trim()}' already exists in this branch (ID: {(int)existingPhone2.CUSTOMER_ID})", null));
            }

            // Generate next CUSTOMER_ID
            var maxId = conn.Query<int?>("SELECT MAX(CUSTOMER_ID) FROM dbo.CUSTOMER").FirstOrDefault();
            int newCustomerId = (maxId ?? 0) + 1;

            var refGuide = Guid.NewGuid();
            var createdDate = DateTime.UtcNow;

            // Validate lang
            var lang = (request.NotificationLang ?? "ar").ToLower().Trim();
            if (lang != "ar" && lang != "en") lang = "ar";

            conn.Execute(@"
                INSERT INTO dbo.CUSTOMER (
                    CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1, CUSTOMER_PHONE2,
                    BIRTH_DATE, CUSTOMER_IS_BLOCK, CUSTOMER_BLOCK_REASON,
                    CUSTOMER_CREATED_DATE, BRANCH_ID, CUSTOMER_REF_GUIDE,
                    LoyaltyBalance, MembershipBalance, UnpaidSales,
                    NotificationLang
                )
                VALUES (
                    @CustomerId, @CustomerName, @CustomerPhone1, @CustomerPhone2,
                    @BirthDate, @CustomerIsBlock, @CustomerBlockReason,
                    @CreatedDate, @BranchId, @RefGuide,
                    0, 0, 0,
                    @NotificationLang
                )",
                new
                {
                    CustomerId = newCustomerId,
                    CustomerName = request.CustomerName.Trim(),
                    CustomerPhone1 = request.CustomerPhone1.Trim(),
                    CustomerPhone2 = string.IsNullOrWhiteSpace(request.CustomerPhone2) ? null : request.CustomerPhone2.Trim(),
                    BirthDate = request.BirthDate,
                    CustomerIsBlock = request.CustomerIsBlock ?? 0,
                    CustomerBlockReason = string.IsNullOrWhiteSpace(request.CustomerBlockReason) ? null : request.CustomerBlockReason.Trim(),
                    CreatedDate = createdDate,
                    BranchId = request.BranchId,
                    RefGuide = refGuide,
                    NotificationLang = lang
                });

            var response = new CreateCustomerResponse(
                CustomerId: newCustomerId,
                CustomerName: request.CustomerName.Trim(),
                CustomerPhone1: request.CustomerPhone1.Trim(),
                CustomerPhone2: string.IsNullOrWhiteSpace(request.CustomerPhone2) ? null : request.CustomerPhone2.Trim(),
                BirthDate: request.BirthDate,
                CustomerIsBlock: request.CustomerIsBlock ?? 0,
                CustomerBlockReason: string.IsNullOrWhiteSpace(request.CustomerBlockReason) ? null : request.CustomerBlockReason.Trim(),
                CustomerCreatedDate: createdDate,
                BranchId: request.BranchId,
                CustomerRefGuide: refGuide,
                NotificationLang: lang
            );

            return Ok(new ApiResult<CreateCustomerResponse>(true, null, response));
        }


        // 10) Company Info
        [HttpGet("company-info")]
        public ActionResult<ApiResult<CompanyInfoDto>> CompanyInfo()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var result = conn.Query<CompanyInfoDto>(@"
        SELECT TOP 1
            COMPANY_ID       AS CompanyId,
            COMPANY_NAME1    AS CompanyName1,
            COMPANY_NAME2    AS CompanyName2,
            COMPANY_LOGO     AS CompanyLogo,
            COMPANY_PHONE    AS CompanyPhone,
            COMPANY_ADRESS1  AS CompanyAddress1,
            COMPANY_ADRESS2  AS CompanyAddress2,
            FOOTER           AS Footer,
            FOOTER1          AS Footer1,
            FOOTER2          AS Footer2,
            FOOTER3          AS Footer3,
            FOOTER4          AS Footer4,
            FOOTER5          AS Footer5
        FROM dbo.COMPANY
        ORDER BY COMPANY_ID ASC")
                .FirstOrDefault();

            if (result == null)
                return Ok(new ApiResult<CompanyInfoDto>(false, "No company record found", null));

            return Ok(new ApiResult<CompanyInfoDto>(true, null, result));
        }



        // 11) Update Company Info
        [HttpPost("company-info")]
        public ActionResult<ApiResult<CompanyInfoDto>> UpdateCompanyInfo(
        [FromBody] UpdateCompanyInfoRequest request)
            {
                if (request == null)
                    return Ok(new ApiResult<CompanyInfoDto>(false, "Request body is required", null));

                if (string.IsNullOrWhiteSpace(request.CompanyName1))
                    return Ok(new ApiResult<CompanyInfoDto>(false, "CompanyName1 is required", null));

                using var conn = sqlConnections.NewByKey("Default");

                var existingId = conn.Query<int?>(
                    "SELECT TOP 1 COMPANY_ID FROM dbo.COMPANY ORDER BY COMPANY_ID ASC")
                    .FirstOrDefault();

                if (existingId.HasValue)
                {
                    // UPDATE
                    conn.Execute(@"
                UPDATE dbo.COMPANY SET
                    COMPANY_NAME1   = @CompanyName1,
                    COMPANY_NAME2   = @CompanyName2,
                    COMPANY_LOGO    = @CompanyLogo,
                    COMPANY_PHONE   = @CompanyPhone,
                    COMPANY_ADRESS1 = @CompanyAddress1,
                    COMPANY_ADRESS2 = @CompanyAddress2,
                    FOOTER          = @Footer,
                    FOOTER1         = @Footer1,
                    FOOTER2         = @Footer2,
                    FOOTER3         = @Footer3,
                    FOOTER4         = @Footer4,
                    FOOTER5         = @Footer5
                WHERE COMPANY_ID = @CompanyId",
                        new
                        {
                            CompanyId = existingId.Value,
                            request.CompanyName1,
                            request.CompanyName2,
                            request.CompanyLogo,
                            request.CompanyPhone,
                            request.CompanyAddress1,
                            request.CompanyAddress2,
                            request.Footer,
                            request.Footer1,
                            request.Footer2,
                            request.Footer3,
                            request.Footer4,
                            request.Footer5
                        });
                }
                else
                {
                    // INSERT
                    conn.Execute(@"
                INSERT INTO dbo.COMPANY (
                    COMPANY_ID, COMPANY_NAME1, COMPANY_NAME2,
                    COMPANY_LOGO, COMPANY_PHONE,
                    COMPANY_ADRESS1, COMPANY_ADRESS2,
                    FOOTER, FOOTER1, FOOTER2, FOOTER3, FOOTER4, FOOTER5,
                    UseWhatsAppNotification
                ) VALUES (
                    1, @CompanyName1, @CompanyName2,
                    @CompanyLogo, @CompanyPhone,
                    @CompanyAddress1, @CompanyAddress2,
                    @Footer, @Footer1, @Footer2, @Footer3, @Footer4, @Footer5,
                    0
                )",
                        new
                        {
                            request.CompanyName1,
                            request.CompanyName2,
                            request.CompanyLogo,
                            request.CompanyPhone,
                            request.CompanyAddress1,
                            request.CompanyAddress2,
                            request.Footer,
                            request.Footer1,
                            request.Footer2,
                            request.Footer3,
                            request.Footer4,
                            request.Footer5
                        });
                }

                var updated = conn.Query<CompanyInfoDto>(@"
            SELECT TOP 1
                COMPANY_ID      AS CompanyId,
                COMPANY_NAME1   AS CompanyName1,
                COMPANY_NAME2   AS CompanyName2,
                COMPANY_LOGO    AS CompanyLogo,
                COMPANY_PHONE   AS CompanyPhone,
                COMPANY_ADRESS1 AS CompanyAddress1,
                COMPANY_ADRESS2 AS CompanyAddress2,
                FOOTER          AS Footer,
                FOOTER1         AS Footer1,
                FOOTER2         AS Footer2,
                FOOTER3         AS Footer3,
                FOOTER4         AS Footer4,
                FOOTER5         AS Footer5
            FROM dbo.COMPANY
            ORDER BY COMPANY_ID ASC")
                    .FirstOrDefault();

                return Ok(new ApiResult<CompanyInfoDto>(true, null, updated));
            }


        // 12) Upload Company Logo
        [HttpPost("company-logo")]
        public async Task<ActionResult<ApiResult<string>>> UploadCompanyLogo(
            IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Ok(new ApiResult<string>(false, "No file provided", null));

            var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
            if (!allowedTypes.Contains(file.ContentType.ToLower()))
                return Ok(new ApiResult<string>(false, "Only images are allowed (jpg, png, webp, gif)", null));

            if (file.Length > 2 * 1024 * 1024) // 2MB max
                return Ok(new ApiResult<string>(false, "File size must be under 2MB", null));

            var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "company");
            Directory.CreateDirectory(uploadsFolder);

            var ext = Path.GetExtension(file.FileName).ToLower();
            var fileName = $"logo{ext}";
            var filePath = Path.Combine(uploadsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var url = $"/uploads/company/{fileName}";
            return Ok(new ApiResult<string>(true, null, url));
        }
    }
}