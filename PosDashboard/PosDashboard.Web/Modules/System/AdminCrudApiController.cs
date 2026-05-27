// PosDashboard.Web.Modules.System/AdminCrudApiController.cs
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
using static PosDashboard.Web.Modules.System.Models.AdminCrudDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/admin-crud")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AdminCrudApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public AdminCrudApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // ─── helpers ────────────────────────────────────────────────
        private int GetUserId()
        {
            var claim = User.Claims.FirstOrDefault(c => c.Type == "userId" || c.Type == "sub" || c.Type == "UserId");
            return claim != null && int.TryParse(claim.Value, out var id) ? id : 1;
        }

        private PagedResult<T> BuildPage<T>(List<T> items, int totalCount, int page, int pageSize) =>
            new PagedResult<T>(items, totalCount, page, pageSize,
                (int)Math.Ceiling((double)totalCount / pageSize));

        // ════════════════════════════════════════════════════════════
        // ████  BRANCH  ████
        // ════════════════════════════════════════════════════════════

        /// <summary>GET /api/admin-crud/branches?page=1&pageSize=20&search=...</summary>
        [HttpGet("branches")]
        public ActionResult<ApiResult<PagedResult<BranchListDto>>> GetBranches(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] int? companyId = null,
            [FromQuery] int? isActive = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var sql = @"
                SELECT
                    BRANCH_ID         AS BranchId,
                    COMPANY_ID        AS CompanyId,
                    BRANCH_NAME1      AS BranchName1,
                    BRANCH_NAME2      AS BranchName2,
                    BRANCH_IS_ACTIVE  AS BranchIsActive,
                    BRANCH_ADRESS     AS BranchAddress,
                    BRANCH_PHONE      AS BranchPhone,
                    COLOR_CODE        AS ColorCode,
                    TaxValue,
                    ArabicCurrencyName,
                    EnglishCurrencyName,
                    RoundOfDigits,
                    Email,
                    WhatsappMobile,
                    CREATED_ON        AS CreatedOn
                FROM dbo.BRANCH
                WHERE 1=1
                    AND (@CompanyId IS NULL OR COMPANY_ID = @CompanyId)
                    AND (@IsActive IS NULL OR BRANCH_IS_ACTIVE = @IsActive)
                    AND (@Search IS NULL OR
                         BRANCH_NAME1 LIKE '%' + @Search + '%' OR
                         BRANCH_NAME2 LIKE '%' + @Search + '%' OR
                         BRANCH_PHONE LIKE '%' + @Search + '%')";

            var p = new { CompanyId = companyId, IsActive = isActive, Search = search };

            var all = conn.Query<BranchListDto>(sql, p).ToList();
            int total = all.Count;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new ApiResult<PagedResult<BranchListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        /// <summary>GET /api/admin-crud/branches/{id}</summary>
        [HttpGet("branches/{id:int}")]
        public ActionResult<ApiResult<BranchListDto>> GetBranchById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var item = conn.Query<BranchListDto>(@"
                SELECT BRANCH_ID AS BranchId, COMPANY_ID AS CompanyId,
                       BRANCH_NAME1 AS BranchName1, BRANCH_NAME2 AS BranchName2,
                       BRANCH_IS_ACTIVE AS BranchIsActive, BRANCH_ADRESS AS BranchAddress,
                       BRANCH_PHONE AS BranchPhone, COLOR_CODE AS ColorCode, TaxValue,
                       ArabicCurrencyName, EnglishCurrencyName, RoundOfDigits,
                       Email, WhatsappMobile, CREATED_ON AS CreatedOn
                FROM dbo.BRANCH WHERE BRANCH_ID = @Id", new { Id = id }).FirstOrDefault();
            if (item == null) return Ok(new ApiResult<BranchListDto>(false, "Branch not found", null));
            return Ok(new ApiResult<BranchListDto>(true, null, item));
        }

        /// <summary>POST /api/admin-crud/branches</summary>
        [HttpPost("branches")]
        public ActionResult<ApiResult<BranchListDto>> CreateBranch([FromBody] BranchCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.BranchName1))
                return Ok(new ApiResult<BranchListDto>(false, "BranchName1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var maxId = conn.Query<int?>("SELECT MAX(BRANCH_ID) FROM dbo.BRANCH").FirstOrDefault() ?? 0;
            int newId = maxId + 1;

            conn.Execute(@"
                INSERT INTO dbo.BRANCH (
                    BRANCH_ID, COMPANY_ID, BRANCH_NAME1, BRANCH_NAME2,
                    BRANCH_IS_ACTIVE, BRANCH_ADRESS, BRANCH_PHONE, COLOR_CODE,
                    TaxValue, ArabicCurrencyName, EnglishCurrencyName, RoundOfDigits,
                    Email, WhatsappMobile, EnjazatikToken, InvoiceCodePrefix, CREATED_ON
                ) VALUES (
                    @BranchId, @CompanyId, @BranchName1, @BranchName2,
                    @IsActive, @Address, @Phone, @ColorCode,
                    @TaxValue, @ArabicCurrency, @EnglishCurrency, @RoundDigits,
                    @Email, @WhatsappMobile, @EnjazatikToken, @InvoiceCodePrefix, @Now
                )", new
            {
                BranchId = newId,
                CompanyId = req.CompanyId,
                BranchName1 = req.BranchName1.Trim(),
                BranchName2 = req.BranchName2?.Trim() ?? "",
                IsActive = req.BranchIsActive ?? 1,
                Address = req.BranchAddress,
                Phone = req.BranchPhone,
                ColorCode = req.ColorCode,
                TaxValue = req.TaxValue,
                ArabicCurrency = string.IsNullOrWhiteSpace(req.ArabicCurrencyName) ? "د.ك" : req.ArabicCurrencyName,
                EnglishCurrency = string.IsNullOrWhiteSpace(req.EnglishCurrencyName) ? "KWD" : req.EnglishCurrencyName,
                RoundDigits = req.RoundOfDigits,
                Email = req.Email,
                WhatsappMobile = req.WhatsappMobile,
                EnjazatikToken = req.EnjazatikToken,
                InvoiceCodePrefix = req.InvoiceCodePrefix ?? "B",
                Now = DateTime.UtcNow
            });

            return GetBranchById(newId);
        }

        /// <summary>PUT /api/admin-crud/branches/{id}</summary>
        [HttpPost("branches/update/{id:int}")]
        public ActionResult<ApiResult<BranchListDto>> UpdateBranch(int id, [FromBody] BranchUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.BranchName1))
                return Ok(new ApiResult<BranchListDto>(false, "BranchName1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var exists = conn.Query<int?>("SELECT BRANCH_ID FROM dbo.BRANCH WHERE BRANCH_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<BranchListDto>(false, "Branch not found", null));

            conn.Execute(@"
                UPDATE dbo.BRANCH SET
                    COMPANY_ID = @CompanyId,
                    BRANCH_NAME1 = @BranchName1, BRANCH_NAME2 = @BranchName2,
                    BRANCH_IS_ACTIVE = @IsActive, BRANCH_ADRESS = @Address,
                    BRANCH_PHONE = @Phone, COLOR_CODE = @ColorCode,
                    TaxValue = @TaxValue, ArabicCurrencyName = @ArabicCurrency,
                    EnglishCurrencyName = @EnglishCurrency, RoundOfDigits = @RoundDigits,
                    Email = @Email, WhatsappMobile = @WhatsappMobile,
                    EnjazatikToken = @EnjazatikToken, InvoiceCodePrefix = @InvoiceCodePrefix,
                    EDIT_ON = @Now
                WHERE BRANCH_ID = @Id", new
            {
                Id = id,
                CompanyId = req.CompanyId,
                BranchName1 = req.BranchName1.Trim(),
                BranchName2 = req.BranchName2?.Trim() ?? "",
                IsActive = req.BranchIsActive ?? 1,
                Address = req.BranchAddress,
                Phone = req.BranchPhone,
                ColorCode = req.ColorCode,
                TaxValue = req.TaxValue,
                ArabicCurrency = req.ArabicCurrencyName,
                EnglishCurrency = req.EnglishCurrencyName,
                RoundDigits = req.RoundOfDigits,
                Email = req.Email,
                WhatsappMobile = req.WhatsappMobile,
                EnjazatikToken = req.EnjazatikToken,
                InvoiceCodePrefix = req.InvoiceCodePrefix ?? "B",
                Now = DateTime.UtcNow
            });

            return GetBranchById(id);
        }

        /// <summary>DELETE /api/admin-crud/branches/{id} — soft delete (deactivate)</summary>
        [HttpDelete("branches/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteBranch(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT BRANCH_ID FROM dbo.BRANCH WHERE BRANCH_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Branch not found", false));
            conn.Execute("UPDATE dbo.BRANCH SET BRANCH_IS_ACTIVE = 0, EDIT_ON = @Now WHERE BRANCH_ID = @Id",
                new { Id = id, Now = DateTime.UtcNow });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ════════════════════════════════════════════════════════════
        // ████  APPOINTMENT CATEGORIES  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("appointment-categories")]
        public ActionResult<ApiResult<PagedResult<AppointmentCategoryListDto>>> GetAppointmentCategories(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] bool? isMakeup = null,
            [FromQuery] bool? isPackage = null,
            [FromQuery] bool includeDeleted = false)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var sql = @"
                SELECT Id, ArabicName, EnglishName, Notes, IsMakeup, IsPackage,
                       Deposit, DocumentName, Deleted, AddedDate, ModifiedDate
                FROM dbo.AppointmentCategories
                WHERE (@IncludeDeleted = 1 OR Deleted = 0)
                  AND (@IsMakeup IS NULL OR IsMakeup = @IsMakeup)
                  AND (@IsPackage IS NULL OR IsPackage = @IsPackage)
                  AND (@Search IS NULL OR
                       ArabicName LIKE '%' + @Search + '%' OR
                       EnglishName LIKE '%' + @Search + '%')
                ORDER BY Id";

            var all = conn.Query<AppointmentCategoryListDto>(sql, new
            {
                IncludeDeleted = includeDeleted ? 1 : 0,
                IsMakeup = isMakeup,
                IsPackage = isPackage,
                Search = search
            }).ToList();

            int total = all.Count;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new ApiResult<PagedResult<AppointmentCategoryListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        [HttpGet("appointment-categories/{id:int}")]
        public ActionResult<ApiResult<AppointmentCategoryListDto>> GetAppointmentCategoryById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var item = conn.Query<AppointmentCategoryListDto>(@"
                SELECT Id, ArabicName, EnglishName, Notes, IsMakeup, IsPackage,
                       Deposit, DocumentName, Deleted, AddedDate, ModifiedDate
                FROM dbo.AppointmentCategories WHERE Id = @Id", new { Id = id }).FirstOrDefault();
            if (item == null) return Ok(new ApiResult<AppointmentCategoryListDto>(false, "Not found", null));
            return Ok(new ApiResult<AppointmentCategoryListDto>(true, null, item));
        }

        [HttpPost("appointment-categories")]
        public ActionResult<ApiResult<AppointmentCategoryListDto>> CreateAppointmentCategory(
            [FromBody] AppointmentCategoryCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.EnglishName))
                return Ok(new ApiResult<AppointmentCategoryListDto>(false, "EnglishName is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            conn.Execute(@"
                INSERT INTO dbo.AppointmentCategories
                    (ArabicName, EnglishName, Notes, IsMakeup, IsPackage, Deposit,
                     DocumentName, Deleted, AddedBy, AddedDate)
                VALUES
                    (@ArabicName, @EnglishName, @Notes, @IsMakeup, @IsPackage, @Deposit,
                     @DocumentName, 0, @AddedBy, @Now)",
                new
                {
                    ArabicName = req.ArabicName.Trim(),
                    EnglishName = req.EnglishName.Trim(),
                    req.Notes,
                    req.IsMakeup,
                    req.IsPackage,
                    req.Deposit,
                    req.DocumentName,
                    AddedBy = userId,
                    Now = DateTime.UtcNow
                });

            var newId = conn.Query<int>("SELECT MAX(Id) FROM dbo.AppointmentCategories").First();
            return GetAppointmentCategoryById(newId);
        }

        [HttpPost("appointment-categories/update/{id:int}")]
        public ActionResult<ApiResult<AppointmentCategoryListDto>> UpdateAppointmentCategory(
            int id, [FromBody] AppointmentCategoryUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.EnglishName))
                return Ok(new ApiResult<AppointmentCategoryListDto>(false, "EnglishName is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT Id FROM dbo.AppointmentCategories WHERE Id = @Id AND Deleted = 0", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<AppointmentCategoryListDto>(false, "Not found", null));

            conn.Execute(@"
                UPDATE dbo.AppointmentCategories SET
                    ArabicName = @ArabicName, EnglishName = @EnglishName,
                    Notes = @Notes, IsMakeup = @IsMakeup, IsPackage = @IsPackage,
                    Deposit = @Deposit, DocumentName = @DocumentName,
                    ModifiedBy = @ModifiedBy, ModifiedDate = @Now
                WHERE Id = @Id",
                new
                {
                    Id = id,
                    ArabicName = req.ArabicName.Trim(),
                    EnglishName = req.EnglishName.Trim(),
                    req.Notes,
                    req.IsMakeup,
                    req.IsPackage,
                    req.Deposit,
                    req.DocumentName,
                    ModifiedBy = userId,
                    Now = DateTime.UtcNow
                });

            return GetAppointmentCategoryById(id);
        }

        [HttpDelete("appointment-categories/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteAppointmentCategory(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT Id FROM dbo.AppointmentCategories WHERE Id = @Id AND Deleted = 0", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Not found or already deleted", false));

            conn.Execute(@"
                UPDATE dbo.AppointmentCategories
                SET Deleted = 1, DeletedDate = @Now
                WHERE Id = @Id",
                new { Id = id, Now = DateTime.UtcNow });

            return Ok(new ApiResult<bool>(true, null, true));
        }

        /// <summary>POST /api/admin-crud/appointment-categories/upload-image</summary>
        [HttpPost("appointment-categories/upload-image")]
        public async Task<ActionResult<ApiResult<string>>> UploadAppointmentCategoryImage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Ok(new ApiResult<string>(false, "No file provided", null));
            var url = await SaveUploadedFile(file, "appointment-categories");
            if (url == null) return Ok(new ApiResult<string>(false, "Invalid file. Max 3MB, jpg/png/webp only.", null));
            return Ok(new ApiResult<string>(true, null, url));
        }

        // ════════════════════════════════════════════════════════════
        // ████  GENERIC IMAGE UPLOAD  ████
        // ════════════════════════════════════════════════════════════

        private async Task<string?> SaveUploadedFile(IFormFile file, string subfolder)
        {
            var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
            if (!allowed.Contains(file.ContentType.ToLower())) return null;
            if (file.Length > 3 * 1024 * 1024) return null;

            var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", subfolder);
            Directory.CreateDirectory(folder);
            var ext = Path.GetExtension(file.FileName).ToLower();
            var fileName = $"{Guid.NewGuid():N}{ext}";
            using (var stream = new FileStream(Path.Combine(folder, fileName), FileMode.Create))
                await file.CopyToAsync(stream);
            return $"/uploads/{subfolder}/{fileName}";
        }

        [HttpPost("categories/upload-image")]
        public async Task<ActionResult<ApiResult<string>>> UploadCategoryImage(IFormFile file)
        {
            if (file == null || file.Length == 0) return Ok(new ApiResult<string>(false, "No file provided", null));
            var url = await SaveUploadedFile(file, "categories");
            if (url == null) return Ok(new ApiResult<string>(false, "Invalid file. Max 3MB, jpg/png/webp only.", null));
            return Ok(new ApiResult<string>(true, null, url));
        }

        [HttpPost("items/upload-image")]
        public async Task<ActionResult<ApiResult<string>>> UploadItemImage(IFormFile file)
        {
            if (file == null || file.Length == 0) return Ok(new ApiResult<string>(false, "No file provided", null));
            var url = await SaveUploadedFile(file, "items");
            if (url == null) return Ok(new ApiResult<string>(false, "Invalid file. Max 3MB, jpg/png/webp only.", null));
            return Ok(new ApiResult<string>(true, null, url));
        }

        [HttpPost("staff/upload-image")]
        public async Task<ActionResult<ApiResult<string>>> UploadStaffImage(IFormFile file)
        {
            if (file == null || file.Length == 0) return Ok(new ApiResult<string>(false, "No file provided", null));
            var url = await SaveUploadedFile(file, "staff");
            if (url == null) return Ok(new ApiResult<string>(false, "Invalid file. Max 3MB, jpg/png/webp only.", null));
            return Ok(new ApiResult<string>(true, null, url));
        }

        // ════════════════════════════════════════════════════════════
        // ████  CATEGORY  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("categories")]
        public ActionResult<ApiResult<PagedResult<CategoryListDto>>> GetCategories(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? search = null,
            [FromQuery] int? isActive = null,
            [FromQuery] int? parentCategory = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var sql = @"
                SELECT
                    c.CATEGORY_ID        AS CategoryId,
                    c.CATEGORY_NAME1     AS CategoryName1,
                    c.CATEGORY_NAME2     AS CategoryName2,
                    c.PARENT_CATEGORY    AS ParentCategory,
                    p.CATEGORY_NAME1     AS ParentName1,
                    c.CATEGORY_ORDERING  AS CategoryOrdering,
                    c.CATEGORY_IS_ACTIVE AS CategoryIsActive,
                    c.CATEGORY_COLOR     AS CategoryColor,
                    c.CATEGORY_LEVEL     AS CategoryLevel,
                    c.DocumentName
                FROM dbo.CATEGORY c
                LEFT JOIN dbo.CATEGORY p ON p.CATEGORY_ID = c.PARENT_CATEGORY
                WHERE 1=1
                  AND (@IsActive IS NULL OR c.CATEGORY_IS_ACTIVE = @IsActive)
                  AND (@ParentCategory IS NULL OR c.PARENT_CATEGORY = @ParentCategory)
                  AND (@Search IS NULL OR
                       c.CATEGORY_NAME1 LIKE '%' + @Search + '%' OR
                       c.CATEGORY_NAME2 LIKE '%' + @Search + '%')
                ORDER BY c.CATEGORY_ORDERING, c.CATEGORY_ID";

            var all = conn.Query<CategoryListDto>(sql, new { IsActive = isActive, ParentCategory = parentCategory, Search = search }).ToList();
            int total = all.Count;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new ApiResult<PagedResult<CategoryListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        [HttpGet("categories/{id:int}")]
        public ActionResult<ApiResult<CategoryListDto>> GetCategoryById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var item = conn.Query<CategoryListDto>(@"
                SELECT c.CATEGORY_ID AS CategoryId, c.CATEGORY_NAME1 AS CategoryName1,
                       c.CATEGORY_NAME2 AS CategoryName2, c.PARENT_CATEGORY AS ParentCategory,
                       p.CATEGORY_NAME1 AS ParentName1, c.CATEGORY_ORDERING AS CategoryOrdering,
                       c.CATEGORY_IS_ACTIVE AS CategoryIsActive, c.CATEGORY_COLOR AS CategoryColor,
                       c.CATEGORY_LEVEL AS CategoryLevel, c.DocumentName
                FROM dbo.CATEGORY c
                LEFT JOIN dbo.CATEGORY p ON p.CATEGORY_ID = c.PARENT_CATEGORY
                WHERE c.CATEGORY_ID = @Id", new { Id = id }).FirstOrDefault();
            if (item == null) return Ok(new ApiResult<CategoryListDto>(false, "Category not found", null));
            return Ok(new ApiResult<CategoryListDto>(true, null, item));
        }

        [HttpPost("categories")]
        public ActionResult<ApiResult<CategoryListDto>> CreateCategory([FromBody] CategoryCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CategoryName1))
                return Ok(new ApiResult<CategoryListDto>(false, "CategoryName1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var maxId = conn.Query<int?>("SELECT MAX(CATEGORY_ID) FROM dbo.CATEGORY").FirstOrDefault() ?? 0;
            int newId = maxId + 1;

            conn.Execute(@"
                INSERT INTO dbo.CATEGORY (
                    CATEGORY_ID, CATEGORY_NAME1, CATEGORY_NAME2,
                    PARENT_CATEGORY, CATEGORY_ORDERING, CATEGORY_IS_ACTIVE,
                    CATEGORY_COLOR, DocumentName, AddedByUserId, AddedDate
                ) VALUES (
                    @Id, @Name1, @Name2,
                    @ParentCategory, @Ordering, @IsActive,
                    @Color, @DocumentName, @UserId, @Now
                )", new
            {
                Id = newId,
                Name1 = req.CategoryName1.Trim(),
                Name2 = req.CategoryName2?.Trim() ?? "",
                req.ParentCategory,
                Ordering = req.CategoryOrdering,
                IsActive = req.CategoryIsActive,
                Color = req.CategoryColor,
                req.DocumentName,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            return GetCategoryById(newId);
        }

        [HttpPost("categories/update/{id:int}")]
        public ActionResult<ApiResult<CategoryListDto>> UpdateCategory(int id, [FromBody] CategoryUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CategoryName1))
                return Ok(new ApiResult<CategoryListDto>(false, "CategoryName1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT CATEGORY_ID FROM dbo.CATEGORY WHERE CATEGORY_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<CategoryListDto>(false, "Category not found", null));

            conn.Execute(@"
                UPDATE dbo.CATEGORY SET
                    CATEGORY_NAME1 = @Name1, CATEGORY_NAME2 = @Name2,
                    PARENT_CATEGORY = @ParentCategory, CATEGORY_ORDERING = @Ordering,
                    CATEGORY_IS_ACTIVE = @IsActive, CATEGORY_COLOR = @Color,
                    DocumentName = @DocumentName,
                    ModifiedByUserId = @UserId, LastModifiedDate = @Now
                WHERE CATEGORY_ID = @Id", new
            {
                Id = id,
                Name1 = req.CategoryName1.Trim(),
                Name2 = req.CategoryName2?.Trim() ?? "",
                req.ParentCategory,
                Ordering = req.CategoryOrdering,
                IsActive = req.CategoryIsActive,
                Color = req.CategoryColor,
                req.DocumentName,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            return GetCategoryById(id);
        }

        [HttpDelete("categories/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteCategory(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT CATEGORY_ID FROM dbo.CATEGORY WHERE CATEGORY_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Category not found", false));
            conn.Execute("UPDATE dbo.CATEGORY SET CATEGORY_IS_ACTIVE = 0 WHERE CATEGORY_ID = @Id", new { Id = id });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ════════════════════════════════════════════════════════════
        // ████  UNITS (Lookup - readonly)  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("units")]
        public ActionResult<ApiResult<List<UnitDto>>> GetUnits()
        {
            using var conn = sqlConnections.NewByKey("Default");
            var items = conn.Query<UnitDto>(@"
                SELECT UNIT_ID AS UnitId, UNIT_NAME1 AS UnitName1, UNIT_NAME2 AS UnitName2, [Order]
                FROM dbo.UNIT ORDER BY [Order], UNIT_ID").ToList();
            return Ok(new ApiResult<List<UnitDto>>(true, null, items));
        }

        // ════════════════════════════════════════════════════════════
        // ████  ITEM  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("items")]
        public ActionResult<ApiResult<PagedResult<ItemListDto>>> GetItems(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] int? categoryId = null,
            [FromQuery] int? appointmentCategoryId = null,
            [FromQuery] int? isActive = null,
            [FromQuery] bool? ecommerce = null,
            [FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var itemSql = @"
                SELECT
                    i.ITEM_ID              AS ItemId,
                    i.ITEM_NAME1           AS ItemName1,
                    i.ITEM_NAME2           AS ItemName2,
                    i.ITEM_CATEGORY_ID     AS ItemCategoryId,
                    c.CATEGORY_NAME1       AS CategoryName1,
                    i.AppointmentCategoryId,
                    ac.EnglishName         AS AppointmentCategoryName,
                    i.ITEM_TYPE            AS ItemType,
                    i.ITEM_CODE            AS ItemCode,
                    i.ITEM_IS_ACTIVE       AS ItemIsActive,
                    i.DocumentName,
                    CAST(i.ECommerce AS bit) AS ECommerce,
                    i.Description,
                    i.CostPrice,
                    i.Balance,
                    i.AddedDate
                FROM dbo.ITEM i
                LEFT JOIN dbo.CATEGORY c ON c.CATEGORY_ID = i.ITEM_CATEGORY_ID
                LEFT JOIN dbo.AppointmentCategories ac ON ac.Id = i.AppointmentCategoryId
                WHERE 1=1
                  AND (@CategoryId IS NULL OR i.ITEM_CATEGORY_ID = @CategoryId)
                  AND (@AppointmentCategoryId IS NULL OR i.AppointmentCategoryId = @AppointmentCategoryId)
                  AND (@IsActive IS NULL OR i.ITEM_IS_ACTIVE = @IsActive)
                  AND (@ECommerce IS NULL OR i.ECommerce = @ECommerce)
                  AND (@Search IS NULL OR
                       i.ITEM_NAME1 LIKE '%' + @Search + '%' OR
                       i.ITEM_NAME2 LIKE '%' + @Search + '%' OR
                       i.ITEM_CODE  LIKE '%' + @Search + '%')
                ORDER BY i.ITEM_ID";

            var rawItems = conn.Query<dynamic>(itemSql, new
            {
                CategoryId = categoryId,
                AppointmentCategoryId = appointmentCategoryId,
                IsActive = isActive,
                ECommerce = ecommerce,
                Search = search
            }).ToList();

            int total = rawItems.Count;
            var pagedRaw = rawItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // Load units for paged items
            var itemIds = pagedRaw.Select(x => (int)x.ItemId).ToList();
            List<dynamic> allUnits = new();
            if (itemIds.Count > 0)
            {
                allUnits = conn.Query<dynamic>(@"
                    SELECT
                        iu.ITEM_ID AS ItemId, iu.ITEM_UNIT_ID AS ItemUnitId,
                        iu.UNIT_ID AS UnitId, u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                        iu.BARCODE AS Barcode, iu.ITEM_UNIT_PRICE AS ItemUnitPrice,
                        iu.ITEM_UNIT_FACTOR AS ItemUnitFactor,
                        CAST(iu.ITEM_UNIT_DURATION AS float) AS ItemUnitDuration,
                        iu.MinimumPrice, iu.Deposit,
                        CAST(iu.Active AS bit) AS Active,
                        iu.BranchId,
                        b.BRANCH_NAME1 AS BranchName
                    FROM dbo.ITEM_UNIT iu
                    INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                    LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = iu.BranchId
                    WHERE iu.ITEM_ID IN @ItemIds
                      AND (@BranchId IS NULL OR iu.BranchId = @BranchId)",
                    new { ItemIds = itemIds, BranchId = branchId }).ToList();
            }

            var unitsByItem = allUnits
                .GroupBy(u => (int)u.ItemId)
                .ToDictionary(g => g.Key, g => g.Select(u => new ItemUnitSummaryDto(
                    (int)u.ItemUnitId, (int)u.UnitId,
                    (string)u.UnitName1, (string)u.UnitName2,
                    (string?)u.Barcode, (decimal)u.ItemUnitPrice,
                    (decimal)u.ItemUnitFactor, (double?)u.ItemUnitDuration,
                    (decimal)u.MinimumPrice, (decimal)u.Deposit,
                    (bool)u.Active, (int?)u.BranchId, (string?)u.BranchName
                )).ToList());

            var items = pagedRaw.Select(x => new ItemListDto(
                (int)x.ItemId, (string)x.ItemName1, (string)x.ItemName2,
                (int)x.ItemCategoryId, (string?)x.CategoryName1,
                (int?)x.AppointmentCategoryId, (string?)x.AppointmentCategoryName,
                (int)x.ItemType, (string?)x.ItemCode, (int?)x.ItemIsActive,
                (string?)x.DocumentName, (bool)x.ECommerce,
                (string?)x.Description, (decimal?)x.CostPrice, (decimal)x.Balance,
                (DateTime)x.AddedDate,
                unitsByItem.TryGetValue((int)x.ItemId, out var u) ? u : new List<ItemUnitSummaryDto>()
            )).ToList();

            return Ok(new ApiResult<PagedResult<ItemListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        [HttpGet("items/{id:int}")]
        public ActionResult<ApiResult<ItemListDto>> GetItemById(int id, [FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var raw = conn.Query<dynamic>(@"
                SELECT i.ITEM_ID AS ItemId, i.ITEM_NAME1 AS ItemName1, i.ITEM_NAME2 AS ItemName2,
                       i.ITEM_CATEGORY_ID AS ItemCategoryId, c.CATEGORY_NAME1 AS CategoryName1,
                       i.AppointmentCategoryId, ac.EnglishName AS AppointmentCategoryName,
                       i.ITEM_TYPE AS ItemType, i.ITEM_CODE AS ItemCode,
                       i.ITEM_IS_ACTIVE AS ItemIsActive, i.DocumentName,
                       CAST(i.ECommerce AS bit) AS ECommerce, i.Description,
                       i.CostPrice, i.Balance, i.AddedDate
                FROM dbo.ITEM i
                LEFT JOIN dbo.CATEGORY c ON c.CATEGORY_ID = i.ITEM_CATEGORY_ID
                LEFT JOIN dbo.AppointmentCategories ac ON ac.Id = i.AppointmentCategoryId
                WHERE i.ITEM_ID = @Id", new { Id = id }).FirstOrDefault();

            if (raw == null) return Ok(new ApiResult<ItemListDto>(false, "Item not found", null));

            var units = conn.Query<ItemUnitSummaryDto>(@"
                SELECT iu.ITEM_UNIT_ID AS ItemUnitId, iu.UNIT_ID AS UnitId,
                       u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                       iu.BARCODE AS Barcode, iu.ITEM_UNIT_PRICE AS ItemUnitPrice,
                       iu.ITEM_UNIT_FACTOR AS ItemUnitFactor,
                       CAST(iu.ITEM_UNIT_DURATION AS float) AS ItemUnitDuration,
                       iu.MinimumPrice, iu.Deposit, CAST(iu.Active AS bit) AS Active,
                       iu.BranchId, b.BRANCH_NAME1 AS BranchName
                FROM dbo.ITEM_UNIT iu
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = iu.BranchId
                WHERE iu.ITEM_ID = @Id AND (@BranchId IS NULL OR iu.BranchId = @BranchId)",
                new { Id = id, BranchId = branchId }).ToList();

            var item = new ItemListDto(
                (int)raw.ItemId, (string)raw.ItemName1, (string)raw.ItemName2,
                (int)raw.ItemCategoryId, (string?)raw.CategoryName1,
                (int?)raw.AppointmentCategoryId, (string?)raw.AppointmentCategoryName,
                (int)raw.ItemType, (string?)raw.ItemCode, (int?)raw.ItemIsActive,
                (string?)raw.DocumentName, (bool)raw.ECommerce,
                (string?)raw.Description, (decimal?)raw.CostPrice, (decimal)raw.Balance,
                (DateTime)raw.AddedDate, units);

            return Ok(new ApiResult<ItemListDto>(true, null, item));
        }

        [HttpPost("items")]
        public ActionResult<ApiResult<ItemListDto>> CreateItem([FromBody] ItemCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.ItemName1))
                return Ok(new ApiResult<ItemListDto>(false, "ItemName1 is required", null));
            if (req.ItemCategoryId <= 0)
                return Ok(new ApiResult<ItemListDto>(false, "ItemCategoryId is required", null));
            if (req.Units == null || req.Units.Count == 0)
                return Ok(new ApiResult<ItemListDto>(false, "At least one unit is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var maxItemId = conn.Query<int?>("SELECT MAX(ITEM_ID) FROM dbo.ITEM").FirstOrDefault() ?? 0;
            int newItemId = maxItemId + 1;

            conn.Execute(@"
                INSERT INTO dbo.ITEM (
                    ITEM_ID, ITEM_NAME1, ITEM_NAME2, ITEM_CATEGORY_ID,
                    AppointmentCategoryId, ITEM_TYPE, ITEM_CODE, ITEM_IS_ACTIVE,
                    DocumentName, ECommerce, Description, CostPrice, Balance,
                    ShowOnPOS, ChangePrice, AddedByUserId, AddedDate
                ) VALUES (
                    @ItemId, @Name1, @Name2, @CategoryId,
                    @AppointmentCategoryId, @ItemType, @ItemCode, @IsActive,
                    @DocumentName, @ECommerce, @Description, @CostPrice, 0,
                    1, 0, @UserId, @Now
                )", new
            {
                ItemId = newItemId,
                Name1 = req.ItemName1.Trim(),
                Name2 = req.ItemName2?.Trim() ?? "",
                CategoryId = req.ItemCategoryId,
                AppointmentCategoryId = req.AppointmentCategoryId,
                ItemType = req.ItemType,
                ItemCode = req.ItemCode,
                IsActive = req.ItemIsActive,
                req.DocumentName,
                ECommerce = req.ECommerce ? 1 : 0,
                req.Description,
                CostPrice = req.CostPrice,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            var maxUnitId = conn.Query<int?>("SELECT MAX(ITEM_UNIT_ID) FROM dbo.ITEM_UNIT").FirstOrDefault() ?? 0;
            int nextUnitId = maxUnitId + 1;

            foreach (var unit in req.Units)
            {
                conn.Execute(@"
                    INSERT INTO dbo.ITEM_UNIT (
                        ITEM_UNIT_ID, ITEM_ID, UNIT_ID, BARCODE, ITEM_UNIT_FACTOR,
                        ITEM_UNIT_PRICE, ITEM_UNIT_POINT, ITEM_UNIT_DURATION,
                        MinimumPrice, Deposit, Active, BranchId,
                        AddedByUserId, AddedDate
                    ) VALUES (
                        @UnitId, @ItemId, @UnitRefId, @Barcode, @Factor,
                        @Price, @Point, @Duration,
                        @MinPrice, @Deposit, @Active, @BranchId,
                        @UserId, @Now
                    )", new
                {
                    UnitId = nextUnitId++,
                    ItemId = newItemId,
                    UnitRefId = unit.UnitId,
                    Barcode = unit.Barcode,
                    Factor = unit.ItemUnitFactor,
                    Price = unit.ItemUnitPrice,
                    Point = unit.ItemUnitPoint,
                    Duration = unit.ItemUnitDuration,
                    MinPrice = unit.MinimumPrice,
                    Deposit = unit.Deposit,
                    Active = unit.Active ? 1 : 0,
                    BranchId = unit.BranchId,
                    UserId = userId,
                    Now = DateTime.UtcNow
                });
            }

            return GetItemById(newItemId);
        }

        [HttpPost("items/update/{id:int}")]
        public ActionResult<ApiResult<ItemListDto>> UpdateItem(int id, [FromBody] ItemUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.ItemName1))
                return Ok(new ApiResult<ItemListDto>(false, "ItemName1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT ITEM_ID FROM dbo.ITEM WHERE ITEM_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<ItemListDto>(false, "Item not found", null));

            conn.Execute(@"
                UPDATE dbo.ITEM SET
                    ITEM_NAME1 = @Name1, ITEM_NAME2 = @Name2,
                    ITEM_CATEGORY_ID = @CategoryId,
                    AppointmentCategoryId = @AppointmentCategoryId,
                    ITEM_TYPE = @ItemType, ITEM_CODE = @ItemCode,
                    ITEM_IS_ACTIVE = @IsActive, DocumentName = @DocumentName,
                    ECommerce = @ECommerce, Description = @Description,
                    CostPrice = @CostPrice,
                    ModifiedByUserId = @UserId, LastModifiedDate = @Now
                WHERE ITEM_ID = @Id", new
            {
                Id = id,
                Name1 = req.ItemName1.Trim(),
                Name2 = req.ItemName2?.Trim() ?? "",
                CategoryId = req.ItemCategoryId,
                AppointmentCategoryId = req.AppointmentCategoryId,
                ItemType = req.ItemType,
                ItemCode = req.ItemCode,
                IsActive = req.ItemIsActive,
                req.DocumentName,
                ECommerce = req.ECommerce ? 1 : 0,
                req.Description,
                CostPrice = req.CostPrice,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            return GetItemById(id);
        }

        /// <summary>
        /// POST /api/admin-crud/items/{id}/units/sync
        /// Upsert all units for an item in one call:
        ///   - Units with ItemUnitId > 0  → UPDATE
        ///   - Units with ItemUnitId == 0 → INSERT
        /// </summary>
        [HttpPost("items/{id:int}/units/sync")]
        public ActionResult<ApiResult<bool>> SyncItemUnits(int id, [FromBody] List<ItemUnitUpdateRequest> units)
        {
            if (units == null || units.Count == 0)
                return Ok(new ApiResult<bool>(false, "No units provided", false));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var itemExists = conn.Query<int?>("SELECT ITEM_ID FROM dbo.ITEM WHERE ITEM_ID = @Id", new { Id = id }).FirstOrDefault();
            if (itemExists == null) return Ok(new ApiResult<bool>(false, "Item not found", false));

            foreach (var unit in units)
            {
                if (unit.ItemUnitId > 0)
                {
                    // UPDATE existing unit
                    conn.Execute(@"
                        UPDATE dbo.ITEM_UNIT SET
                            UNIT_ID = @UnitId, BARCODE = @Barcode,
                            ITEM_UNIT_FACTOR = @Factor, ITEM_UNIT_PRICE = @Price,
                            ITEM_UNIT_POINT = @Point, ITEM_UNIT_DURATION = @Duration,
                            MinimumPrice = @MinPrice, Deposit = @Deposit,
                            Active = @Active, BranchId = @BranchId,
                            ModifiedByUserId = @UserId, LastModifiedDate = @Now
                        WHERE ITEM_UNIT_ID = @UnitId2 AND ITEM_ID = @ItemId",
                        new
                        {
                            UnitId = unit.UnitId,
                            Barcode = unit.Barcode,
                            Factor = unit.ItemUnitFactor,
                            Price = unit.ItemUnitPrice,
                            Point = unit.ItemUnitPoint,
                            Duration = unit.ItemUnitDuration,
                            MinPrice = unit.MinimumPrice,
                            Deposit = unit.Deposit,
                            Active = unit.Active ? 1 : 0,
                            BranchId = unit.BranchId,
                            UserId = userId,
                            Now = DateTime.UtcNow,
                            UnitId2 = unit.ItemUnitId,
                            ItemId = id
                        });
                }
                else
                {
                    // INSERT new unit
                    var maxId = conn.Query<int?>("SELECT MAX(ITEM_UNIT_ID) FROM dbo.ITEM_UNIT").FirstOrDefault() ?? 0;
                    int newId = maxId + 1;
                    conn.Execute(@"
                        INSERT INTO dbo.ITEM_UNIT (
                            ITEM_UNIT_ID, ITEM_ID, UNIT_ID, BARCODE, ITEM_UNIT_FACTOR,
                            ITEM_UNIT_PRICE, ITEM_UNIT_POINT, ITEM_UNIT_DURATION,
                            MinimumPrice, Deposit, Active, BranchId,
                            AddedByUserId, AddedDate
                        ) VALUES (
                            @NewId, @ItemId, @UnitId, @Barcode, @Factor,
                            @Price, @Point, @Duration, @MinPrice, @Deposit, @Active, @BranchId,
                            @UserId, @Now
                        )",
                        new
                        {
                            NewId = newId,
                            ItemId = id,
                            UnitId = unit.UnitId,
                            Barcode = unit.Barcode,
                            Factor = unit.ItemUnitFactor,
                            Price = unit.ItemUnitPrice,
                            Point = unit.ItemUnitPoint,
                            Duration = unit.ItemUnitDuration,
                            MinPrice = unit.MinimumPrice,
                            Deposit = unit.Deposit,
                            Active = unit.Active ? 1 : 0,
                            BranchId = unit.BranchId,
                            UserId = userId,
                            Now = DateTime.UtcNow
                        });
                }
            }

            return Ok(new ApiResult<bool>(true, null, true));
        }

        [HttpDelete("items/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteItem(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT ITEM_ID FROM dbo.ITEM WHERE ITEM_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Item not found", false));
            conn.Execute("UPDATE dbo.ITEM SET ITEM_IS_ACTIVE = 0 WHERE ITEM_ID = @Id", new { Id = id });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ── Item Unit sub-endpoints ──────────────────────────────────

        [HttpPost("items/{itemId:int}/units")]
        public ActionResult<ApiResult<ItemUnitSummaryDto>> AddItemUnit(int itemId, [FromBody] ItemUnitCreateRequest req)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var itemExists = conn.Query<int?>("SELECT ITEM_ID FROM dbo.ITEM WHERE ITEM_ID = @Id", new { Id = itemId }).FirstOrDefault();
            if (itemExists == null) return Ok(new ApiResult<ItemUnitSummaryDto>(false, "Item not found", null));

            var maxId = conn.Query<int?>("SELECT MAX(ITEM_UNIT_ID) FROM dbo.ITEM_UNIT").FirstOrDefault() ?? 0;
            int newId = maxId + 1;

            conn.Execute(@"
                INSERT INTO dbo.ITEM_UNIT (
                    ITEM_UNIT_ID, ITEM_ID, UNIT_ID, BARCODE, ITEM_UNIT_FACTOR,
                    ITEM_UNIT_PRICE, ITEM_UNIT_POINT, ITEM_UNIT_DURATION,
                    MinimumPrice, Deposit, Active, BranchId,
                    AddedByUserId, AddedDate
                ) VALUES (
                    @NewId, @ItemId, @UnitId, @Barcode, @Factor,
                    @Price, @Point, @Duration, @MinPrice, @Deposit, @Active, @BranchId,
                    @UserId, @Now
                )", new
            {
                NewId = newId,
                ItemId = itemId,
                UnitId = req.UnitId,
                Barcode = req.Barcode,
                Factor = req.ItemUnitFactor,
                Price = req.ItemUnitPrice,
                Point = req.ItemUnitPoint,
                Duration = req.ItemUnitDuration,
                MinPrice = req.MinimumPrice,
                Deposit = req.Deposit,
                Active = req.Active ? 1 : 0,
                BranchId = req.BranchId,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            var result = conn.Query<ItemUnitSummaryDto>(@"
                SELECT iu.ITEM_UNIT_ID AS ItemUnitId, iu.UNIT_ID AS UnitId,
                       u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                       iu.BARCODE AS Barcode, iu.ITEM_UNIT_PRICE AS ItemUnitPrice,
                       iu.ITEM_UNIT_FACTOR AS ItemUnitFactor,
                       CAST(iu.ITEM_UNIT_DURATION AS float) AS ItemUnitDuration,
                       iu.MinimumPrice, iu.Deposit, CAST(iu.Active AS bit) AS Active,
                       iu.BranchId, b.BRANCH_NAME1 AS BranchName
                FROM dbo.ITEM_UNIT iu
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = iu.BranchId
                WHERE iu.ITEM_UNIT_ID = @Id", new { Id = newId }).First();

            return Ok(new ApiResult<ItemUnitSummaryDto>(true, null, result));
        }

        [HttpPost("items/{itemId:int}/units/update/{unitId:int}")]
        public ActionResult<ApiResult<ItemUnitSummaryDto>> UpdateItemUnit(int itemId, int unitId, [FromBody] ItemUnitUpdateRequest req)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT ITEM_UNIT_ID FROM dbo.ITEM_UNIT WHERE ITEM_UNIT_ID = @Id AND ITEM_ID = @ItemId",
                new { Id = unitId, ItemId = itemId }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<ItemUnitSummaryDto>(false, "Item unit not found", null));

            conn.Execute(@"
                UPDATE dbo.ITEM_UNIT SET
                    UNIT_ID = @UnitId, BARCODE = @Barcode, ITEM_UNIT_FACTOR = @Factor,
                    ITEM_UNIT_PRICE = @Price, ITEM_UNIT_POINT = @Point,
                    ITEM_UNIT_DURATION = @Duration, MinimumPrice = @MinPrice,
                    Deposit = @Deposit, Active = @Active, BranchId = @BranchId,
                    ModifiedByUserId = @UserId, LastModifiedDate = @Now
                WHERE ITEM_UNIT_ID = @Id", new
            {
                Id = unitId,
                UnitId = req.UnitId,
                Barcode = req.Barcode,
                Factor = req.ItemUnitFactor,
                Price = req.ItemUnitPrice,
                Point = req.ItemUnitPoint,
                Duration = req.ItemUnitDuration,
                MinPrice = req.MinimumPrice,
                Deposit = req.Deposit,
                Active = req.Active ? 1 : 0,
                BranchId = req.BranchId,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            var result = conn.Query<ItemUnitSummaryDto>(@"
                SELECT iu.ITEM_UNIT_ID AS ItemUnitId, iu.UNIT_ID AS UnitId,
                       u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                       iu.BARCODE AS Barcode, iu.ITEM_UNIT_PRICE AS ItemUnitPrice,
                       iu.ITEM_UNIT_FACTOR AS ItemUnitFactor,
                       CAST(iu.ITEM_UNIT_DURATION AS float) AS ItemUnitDuration,
                       iu.MinimumPrice, iu.Deposit, CAST(iu.Active AS bit) AS Active,
                       iu.BranchId, b.BRANCH_NAME1 AS BranchName
                FROM dbo.ITEM_UNIT iu
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = iu.BranchId
                WHERE iu.ITEM_UNIT_ID = @Id", new { Id = unitId }).First();

            return Ok(new ApiResult<ItemUnitSummaryDto>(true, null, result));
        }

        [HttpDelete("items/{itemId:int}/units/{unitId:int}")]
        public ActionResult<ApiResult<bool>> DeleteItemUnit(int itemId, int unitId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT ITEM_UNIT_ID FROM dbo.ITEM_UNIT WHERE ITEM_UNIT_ID = @Id AND ITEM_ID = @ItemId",
                new { Id = unitId, ItemId = itemId }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Item unit not found", false));
            conn.Execute("UPDATE dbo.ITEM_UNIT SET Active = 0 WHERE ITEM_UNIT_ID = @Id", new { Id = unitId });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ════════════════════════════════════════════════════════════
        // ████  STAFF  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("staff")]
        public ActionResult<ApiResult<PagedResult<StaffListDto>>> GetStaff(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] int? branchId = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] bool? isAppointment = null,
            [FromQuery] bool? isMakeupArtist = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var all = conn.Query<StaffListDto>(@"
                SELECT
                    s.Id, s.ArabicName, s.EnglishName, s.Mobile,
                    s.Salary, s.Commission,
                    s.BranchId, b.BRANCH_NAME1 AS BranchName,
                    CAST(s.Active AS bit) AS Active,
                    CAST(s.isAppointment AS bit) AS IsAppointment,
                    CAST(s.IsMakeupArtist AS bit) AS IsMakeupArtist,
                    s.DocumentName, s.EmployeeCode, s.AddedDate
                FROM dbo.STAFF s
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = s.BranchId
                WHERE s.Deleted = 0
                  AND (@BranchId IS NULL OR s.BranchId = @BranchId)
                  AND (@IsActive IS NULL OR s.Active = @IsActive)
                  AND (@IsAppointment IS NULL OR s.isAppointment = @IsAppointment)
                  AND (@IsMakeupArtist IS NULL OR s.IsMakeupArtist = @IsMakeupArtist)
                  AND (@Search IS NULL OR
                       s.ArabicName LIKE '%' + @Search + '%' OR
                       s.EnglishName LIKE '%' + @Search + '%' OR
                       s.Mobile LIKE '%' + @Search + '%' OR
                       s.EmployeeCode LIKE '%' + @Search + '%')
                ORDER BY s.EnglishName",
                new
                {
                    BranchId = branchId,
                    IsActive = isActive.HasValue ? (isActive.Value ? 1 : 0) : (int?)null,
                    IsAppointment = isAppointment.HasValue ? (isAppointment.Value ? 1 : 0) : (int?)null,
                    IsMakeupArtist = isMakeupArtist.HasValue ? (isMakeupArtist.Value ? 1 : 0) : (int?)null,
                    Search = search
                }).ToList();

            int total = all.Count;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new ApiResult<PagedResult<StaffListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        [HttpGet("staff/{id:int}")]
        public ActionResult<ApiResult<StaffDetailDto>> GetStaffById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var raw = conn.Query<dynamic>(@"
                SELECT s.Id, s.ArabicName, s.EnglishName, s.Mobile,
                       s.Salary, s.Commission, s.BranchId,
                       b.BRANCH_NAME1 AS BranchName,
                       CAST(s.Active AS bit) AS Active,
                       CAST(s.isAppointment AS bit) AS IsAppointment,
                       CAST(s.IsMakeupArtist AS bit) AS IsMakeupArtist,
                       s.DocumentName, s.EmployeeCode, s.ServiceEndDate,
                       s.FixedAmount, s.AddedDate
                FROM dbo.STAFF s
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = s.BranchId
                WHERE s.Id = @Id AND s.Deleted = 0", new { Id = id }).FirstOrDefault();

            if (raw == null) return Ok(new ApiResult<StaffDetailDto>(false, "Staff not found", null));

            // Load StaffItems only for makeup artists
            var staffItems = new List<StaffItemDto>();
            if ((bool)raw.IsMakeupArtist)
            {
                staffItems = conn.Query<StaffItemDto>(@"
                    SELECT
                        si.Id, si.StaffId, si.ItemUnitId,
                        i.ITEM_NAME1 AS ItemName1, i.ITEM_NAME2 AS ItemName2,
                        u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                        si.Price, si.Notes,
                        CAST(si.Deleted AS bit) AS Deleted
                    FROM dbo.StaffItems si
                    INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = si.ItemUnitId
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                    INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                    WHERE si.StaffId = @StaffId AND si.Deleted = 0",
                    new { StaffId = id }).ToList();
            }

            var detail = new StaffDetailDto(
                (int)raw.Id, (string)raw.ArabicName, (string)raw.EnglishName,
                (string?)raw.Mobile, (decimal)raw.Salary, (decimal?)raw.Commission,
                (int)raw.BranchId, (string?)raw.BranchName,
                (bool)raw.Active, (bool)raw.IsAppointment, (bool)raw.IsMakeupArtist,
                (string?)raw.DocumentName, (string?)raw.EmployeeCode,
                (DateTime?)raw.ServiceEndDate, (decimal)raw.FixedAmount, staffItems);

            return Ok(new ApiResult<StaffDetailDto>(true, null, detail));
        }

        [HttpPost("staff")]
        public ActionResult<ApiResult<StaffDetailDto>> CreateStaff([FromBody] StaffCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.EnglishName))
                return Ok(new ApiResult<StaffDetailDto>(false, "EnglishName is required", null));
            if (req.BranchId <= 0)
                return Ok(new ApiResult<StaffDetailDto>(false, "BranchId is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            conn.Execute(@"
                INSERT INTO dbo.STAFF (
                    ArabicName, EnglishName, Mobile, Salary, Commission,
                    BranchId, Active, isAppointment, IsMakeupArtist,
                    DocumentName, EmployeeCode, ServiceEndDate, FixedAmount,
                    Deleted, AddedBy, AddedDate
                ) VALUES (
                    @ArabicName, @EnglishName, @Mobile, @Salary, @Commission,
                    @BranchId, @Active, @IsAppointment, @IsMakeupArtist,
                    @DocumentName, @EmployeeCode, @ServiceEndDate, @FixedAmount,
                    0, @UserId, @Now
                )", new
            {
                ArabicName = req.ArabicName.Trim(),
                EnglishName = req.EnglishName.Trim(),
                req.Mobile,
                req.Salary,
                req.Commission,
                req.BranchId,
                Active = req.Active ? 1 : 0,
                IsAppointment = req.IsAppointment ? 1 : 0,
                IsMakeupArtist = req.IsMakeupArtist ? 1 : 0,
                req.DocumentName,
                req.EmployeeCode,
                req.ServiceEndDate,
                req.FixedAmount,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            var newId = conn.Query<int>("SELECT MAX(Id) FROM dbo.STAFF").First();
            return GetStaffById(newId);
        }

        [HttpPost("staff/update/{id:int}")]
        public ActionResult<ApiResult<StaffDetailDto>> UpdateStaff(int id, [FromBody] StaffUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.EnglishName))
                return Ok(new ApiResult<StaffDetailDto>(false, "EnglishName is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Deleted = 0", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<StaffDetailDto>(false, "Staff not found", null));

            conn.Execute(@"
                UPDATE dbo.STAFF SET
                    ArabicName = @ArabicName, EnglishName = @EnglishName,
                    Mobile = @Mobile, Salary = @Salary, Commission = @Commission,
                    BranchId = @BranchId, Active = @Active,
                    isAppointment = @IsAppointment, IsMakeupArtist = @IsMakeupArtist,
                    DocumentName = @DocumentName, EmployeeCode = @EmployeeCode,
                    ServiceEndDate = @ServiceEndDate, FixedAmount = @FixedAmount,
                    ModifiedBy = @UserId, ModifiedDate = @Now
                WHERE Id = @Id", new
            {
                Id = id,
                ArabicName = req.ArabicName.Trim(),
                EnglishName = req.EnglishName.Trim(),
                req.Mobile,
                req.Salary,
                req.Commission,
                req.BranchId,
                Active = req.Active ? 1 : 0,
                IsAppointment = req.IsAppointment ? 1 : 0,
                IsMakeupArtist = req.IsMakeupArtist ? 1 : 0,
                req.DocumentName,
                req.EmployeeCode,
                req.ServiceEndDate,
                req.FixedAmount,
                UserId = userId,
                Now = DateTime.UtcNow
            });

            return GetStaffById(id);
        }

        [HttpDelete("staff/{id:int}")]
        public ActionResult<ApiResult<bool>> DeleteStaff(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Deleted = 0", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Staff not found", false));
            conn.Execute("UPDATE dbo.STAFF SET Deleted = 1, DeletedDate = @Now WHERE Id = @Id",
                new { Id = id, Now = DateTime.UtcNow });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ── StaffItems (Makeup Artist services) ─────────────────────

        [HttpPost("staff/{staffId:int}/items")]
        public ActionResult<ApiResult<StaffItemDto>> AddStaffItem(int staffId, [FromBody] StaffItemCreateRequest req)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var staff = conn.Query<dynamic>("SELECT Id, IsMakeupArtist FROM dbo.STAFF WHERE Id = @Id AND Deleted = 0",
                new { Id = staffId }).FirstOrDefault();
            if (staff == null) return Ok(new ApiResult<StaffItemDto>(false, "Staff not found", null));
            if (!(bool)staff.IsMakeupArtist) return Ok(new ApiResult<StaffItemDto>(false, "Staff is not a makeup artist", null));

            conn.Execute(@"
                INSERT INTO dbo.StaffItems (StaffId, ItemUnitId, Price, Notes, Deleted, AddedBy, AddedDate)
                VALUES (@StaffId, @ItemUnitId, @Price, @Notes, 0, @UserId, @Now)",
                new { StaffId = staffId, req.ItemUnitId, req.Price, req.Notes, UserId = userId, Now = DateTime.UtcNow });

            var newId = conn.Query<int>("SELECT MAX(Id) FROM dbo.StaffItems").First();

            var result = conn.Query<StaffItemDto>(@"
                SELECT si.Id, si.StaffId, si.ItemUnitId,
                       i.ITEM_NAME1 AS ItemName1, i.ITEM_NAME2 AS ItemName2,
                       u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                       si.Price, si.Notes, CAST(si.Deleted AS bit) AS Deleted
                FROM dbo.StaffItems si
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = si.ItemUnitId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                WHERE si.Id = @Id", new { Id = newId }).First();

            return Ok(new ApiResult<StaffItemDto>(true, null, result));
        }

        [HttpPost("staff/{staffId:int}/items/update/{itemId:int}")]
        public ActionResult<ApiResult<StaffItemDto>> UpdateStaffItem(int staffId, int itemId, [FromBody] StaffItemUpdateRequest req)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetUserId();

            var exists = conn.Query<int?>("SELECT Id FROM dbo.StaffItems WHERE Id = @Id AND StaffId = @StaffId AND Deleted = 0",
                new { Id = itemId, StaffId = staffId }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<StaffItemDto>(false, "Staff item not found", null));

            conn.Execute(@"
                UPDATE dbo.StaffItems SET ItemUnitId = @ItemUnitId, Price = @Price,
                    Notes = @Notes, ModifiedBy = @UserId, ModifiedDate = @Now
                WHERE Id = @Id",
                new { Id = itemId, req.ItemUnitId, req.Price, req.Notes, UserId = userId, Now = DateTime.UtcNow });

            var result = conn.Query<StaffItemDto>(@"
                SELECT si.Id, si.StaffId, si.ItemUnitId,
                       i.ITEM_NAME1 AS ItemName1, i.ITEM_NAME2 AS ItemName2,
                       u.UNIT_NAME1 AS UnitName1, u.UNIT_NAME2 AS UnitName2,
                       si.Price, si.Notes, CAST(si.Deleted AS bit) AS Deleted
                FROM dbo.StaffItems si
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = si.ItemUnitId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                WHERE si.Id = @Id", new { Id = itemId }).First();

            return Ok(new ApiResult<StaffItemDto>(true, null, result));
        }

        [HttpDelete("staff/{staffId:int}/items/{itemId:int}")]
        public ActionResult<ApiResult<bool>> DeleteStaffItem(int staffId, int itemId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT Id FROM dbo.StaffItems WHERE Id = @Id AND StaffId = @StaffId AND Deleted = 0",
                new { Id = itemId, StaffId = staffId }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Not found", false));
            conn.Execute("UPDATE dbo.StaffItems SET Deleted = 1, DeletedDate = @Now WHERE Id = @Id",
                new { Id = itemId, Now = DateTime.UtcNow });
            return Ok(new ApiResult<bool>(true, null, true));
        }

        // ════════════════════════════════════════════════════════════
        // ████  CUSTOMER  ████
        // ════════════════════════════════════════════════════════════

        [HttpGet("customers")]
        public ActionResult<ApiResult<PagedResult<CustomerListDto>>> GetCustomers(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] int? branchId = null,
            [FromQuery] int? isBlock = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var all = conn.Query<CustomerListDto>(@"
                SELECT
                    c.CUSTOMER_ID         AS CustomerId,
                    c.CUSTOMER_NAME       AS CustomerName,
                    c.CUSTOMER_PHONE1     AS CustomerPhone1,
                    c.CUSTOMER_PHONE2     AS CustomerPhone2,
                    c.BRANCH_ID           AS BranchId,
                    b.BRANCH_NAME1        AS BranchName,
                    c.CUSTOMER_IS_BLOCK   AS CustomerIsBlock,
                    c.CUSTOMER_BLOCK_REASON AS CustomerBlockReason,
                    c.CUSTOMER_NOTE       AS CustomerNote,
                    c.BIRTH_DATE          AS BirthDate,
                    ISNULL(c.LoyaltyBalance,0)    AS LoyaltyBalance,
                    ISNULL(c.MembershipBalance,0) AS MembershipBalance,
                    ISNULL(c.UnpaidSales,0)       AS UnpaidSales,
                    ISNULL(c.HasRefundHistory,0)  AS HasRefundHistory,
                    ISNULL(c.NotificationLang,'ar') AS NotificationLang,
                    c.CUSTOMER_CREATED_DATE AS CustomerCreatedDate
                FROM dbo.CUSTOMER c
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = c.BRANCH_ID
                WHERE 1=1
                  AND (@BranchId IS NULL OR c.BRANCH_ID = @BranchId)
                  AND (@IsBlock IS NULL OR c.CUSTOMER_IS_BLOCK = @IsBlock)
                  AND (@Search IS NULL OR
                       c.CUSTOMER_NAME   LIKE '%' + @Search + '%' OR
                       c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%' OR
                       c.CUSTOMER_PHONE2 LIKE '%' + @Search + '%')
                ORDER BY c.CUSTOMER_NAME",
                new { BranchId = branchId, IsBlock = isBlock, Search = search }).ToList();

            int total = all.Count;
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Ok(new ApiResult<PagedResult<CustomerListDto>>(true, null, BuildPage(items, total, page, pageSize)));
        }

        [HttpGet("customers/{id:int}")]
        public ActionResult<ApiResult<CustomerListDto>> GetCustomerById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var item = conn.Query<CustomerListDto>(@"
                SELECT c.CUSTOMER_ID AS CustomerId, c.CUSTOMER_NAME AS CustomerName,
                       c.CUSTOMER_PHONE1 AS CustomerPhone1, c.CUSTOMER_PHONE2 AS CustomerPhone2,
                       c.BRANCH_ID AS BranchId, b.BRANCH_NAME1 AS BranchName,
                       c.CUSTOMER_IS_BLOCK AS CustomerIsBlock,
                       c.CUSTOMER_BLOCK_REASON AS CustomerBlockReason,
                       c.CUSTOMER_NOTE AS CustomerNote, c.BIRTH_DATE AS BirthDate,
                       ISNULL(c.LoyaltyBalance,0) AS LoyaltyBalance,
                       ISNULL(c.MembershipBalance,0) AS MembershipBalance,
                       ISNULL(c.UnpaidSales,0) AS UnpaidSales,
                       ISNULL(c.HasRefundHistory,0) AS HasRefundHistory,
                       ISNULL(c.NotificationLang,'ar') AS NotificationLang,
                       c.CUSTOMER_CREATED_DATE AS CustomerCreatedDate
                FROM dbo.CUSTOMER c
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = c.BRANCH_ID
                WHERE c.CUSTOMER_ID = @Id", new { Id = id }).FirstOrDefault();
            if (item == null) return Ok(new ApiResult<CustomerListDto>(false, "Customer not found", null));
            return Ok(new ApiResult<CustomerListDto>(true, null, item));
        }

        /// <summary>POST /api/admin-crud/customers — delegates to same logic as LookupsApiController</summary>
        [HttpPost("customers")]
        public ActionResult<ApiResult<CustomerListDto>> CreateCustomer([FromBody] CustomerCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CustomerName))
                return Ok(new ApiResult<CustomerListDto>(false, "CustomerName is required", null));
            if (string.IsNullOrWhiteSpace(req.CustomerPhone1))
                return Ok(new ApiResult<CustomerListDto>(false, "CustomerPhone1 is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Check duplicate phone
            var dup = conn.Query<int?>(
                "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_PHONE1 = @Phone AND BRANCH_ID = @BranchId",
                new { Phone = req.CustomerPhone1.Trim(), BranchId = req.BranchId }).FirstOrDefault();
            if (dup != null)
                return Ok(new ApiResult<CustomerListDto>(false, $"Phone '{req.CustomerPhone1.Trim()}' already exists in this branch", null));

            var maxId = conn.Query<int?>("SELECT MAX(CUSTOMER_ID) FROM dbo.CUSTOMER").FirstOrDefault() ?? 0;
            int newId = maxId + 1;
            var lang = (req.NotificationLang ?? "ar").ToLower();
            if (lang != "ar" && lang != "en") lang = "ar";

            conn.Execute(@"
                INSERT INTO dbo.CUSTOMER (
                    CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1, CUSTOMER_PHONE2,
                    BIRTH_DATE, CUSTOMER_IS_BLOCK, CUSTOMER_BLOCK_REASON, CUSTOMER_NOTE,
                    CUSTOMER_CREATED_DATE, BRANCH_ID, CUSTOMER_REF_GUIDE,
                    LoyaltyBalance, MembershipBalance, UnpaidSales, NotificationLang
                ) VALUES (
                    @CustomerId, @Name, @Phone1, @Phone2,
                    @BirthDate, @IsBlock, @BlockReason, @Note,
                    @Now, @BranchId, @RefGuide,
                    0, 0, 0, @Lang
                )", new
            {
                CustomerId = newId,
                Name = req.CustomerName.Trim(),
                Phone1 = req.CustomerPhone1.Trim(),
                Phone2 = string.IsNullOrWhiteSpace(req.CustomerPhone2) ? null : req.CustomerPhone2.Trim(),
                req.BirthDate,
                IsBlock = req.CustomerIsBlock ?? 0,
                BlockReason = req.CustomerBlockReason,
                Note = req.CustomerNote,
                Now = DateTime.UtcNow,
                req.BranchId,
                RefGuide = Guid.NewGuid(),
                Lang = lang
            });

            return GetCustomerById(newId);
        }

        [HttpPost("customers/update/{id:int}")]
        public ActionResult<ApiResult<CustomerListDto>> UpdateCustomer(int id, [FromBody] CustomerUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CustomerName))
                return Ok(new ApiResult<CustomerListDto>(false, "CustomerName is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var exists = conn.Query<int?>("SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<CustomerListDto>(false, "Customer not found", null));

            var lang = (req.NotificationLang ?? "ar").ToLower();
            if (lang != "ar" && lang != "en") lang = "ar";

            conn.Execute(@"
                UPDATE dbo.CUSTOMER SET
                    CUSTOMER_NAME = @Name,
                    CUSTOMER_PHONE1 = @Phone1,
                    CUSTOMER_PHONE2 = @Phone2,
                    BIRTH_DATE = @BirthDate,
                    CUSTOMER_IS_BLOCK = @IsBlock,
                    CUSTOMER_BLOCK_REASON = @BlockReason,
                    CUSTOMER_NOTE = @Note,
                    NotificationLang = @Lang,
                    LastUpdatedDate = @Now
                WHERE CUSTOMER_ID = @Id", new
            {
                Id = id,
                Name = req.CustomerName.Trim(),
                Phone1 = req.CustomerPhone1.Trim(),
                Phone2 = string.IsNullOrWhiteSpace(req.CustomerPhone2) ? null : req.CustomerPhone2.Trim(),
                req.BirthDate,
                IsBlock = req.CustomerIsBlock ?? 0,
                BlockReason = req.CustomerBlockReason,
                Note = req.CustomerNote,
                Lang = lang,
                Now = DateTime.UtcNow
            });

            return GetCustomerById(id);
        }

        /// <summary>DELETE = block customer (not physical delete)</summary>
        [HttpDelete("customers/{id:int}")]
        public ActionResult<ApiResult<bool>> BlockCustomer(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<int?>("SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id", new { Id = id }).FirstOrDefault();
            if (exists == null) return Ok(new ApiResult<bool>(false, "Customer not found", false));
            conn.Execute("UPDATE dbo.CUSTOMER SET CUSTOMER_IS_BLOCK = 1 WHERE CUSTOMER_ID = @Id", new { Id = id });
            return Ok(new ApiResult<bool>(true, null, true));
        }
    }
}