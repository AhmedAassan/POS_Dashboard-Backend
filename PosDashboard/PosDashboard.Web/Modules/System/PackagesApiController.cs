using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using static PosDashboard.Web.Modules.System.Models.PackageDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/packages")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class PackagesApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        public PackagesApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        private int GetCurrentUserId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(claim, out var id) ? id : 0;
        }

        private const int DEFAULT_BRANCH_ID = 1;
        private const int DEFAULT_SHIFT_ID = 0;

        // =============================================
        // GET /api/packages
        // =============================================
        [HttpGet]
        public ActionResult<ApiResult<List<PackageMasterDto>>> GetPackages(
            [FromQuery] int? branchId = null,
            [FromQuery] bool activeOnly = false)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var packages = conn.Query<dynamic>(@"
                SELECT 
                    p.Id, p.BranchId, p.EnglishName, p.ArabicName,
                    ISNULL(p.Amount, 0) AS Amount,
                    ISNULL(p.NoOfDays, 0) AS NoOfDays,
                    CAST(ISNULL(p.Active, 0) AS bit) AS Active,
                    ISNULL(p.AddedDate, SYSUTCDATETIME()) AS AddedDate
                FROM dbo.Packages p
                WHERE ISNULL(p.Deleted, 0) = 0
                  AND (@BranchId IS NULL OR p.BranchId = @BranchId)
                  AND (@ActiveOnly = 0 OR ISNULL(p.Active, 0) = 1)
                ORDER BY p.Id DESC",
                new { BranchId = branchId, ActiveOnly = activeOnly ? 1 : 0 }).ToList();

            var result = new List<PackageMasterDto>();
            foreach (var p in packages)
            {
                var items = LoadPackageItems(conn, (int)p.Id);
                int totalSessions = items.Count;
                decimal totalReal = items.Sum(x => x.ItemPrice);
                decimal amount = (decimal)p.Amount;

                result.Add(new PackageMasterDto(
                    Id: (int)p.Id,
                    BranchId: (int)p.BranchId,
                    EnglishName: (string)(p.EnglishName ?? ""),
                    ArabicName: (string)(p.ArabicName ?? ""),
                    Amount: amount,
                    NoOfDays: (int)p.NoOfDays,
                    Active: (bool)p.Active,
                    TotalSessions: totalSessions,
                    TotalRealValue: totalReal,
                    Savings: Math.Max(0, totalReal - amount),
                    AddedDate: (DateTime)p.AddedDate,
                    Items: items
                ));
            }

            return Ok(new ApiResult<List<PackageMasterDto>>(true, null, result));
        }

        // =============================================
        // GET /api/packages/{id}
        // =============================================
        [HttpGet("{id:int}")]
        public ActionResult<ApiResult<PackageMasterDto>> GetPackageById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var p = conn.Query<dynamic>(@"
                SELECT 
                    p.Id, p.BranchId, p.EnglishName, p.ArabicName,
                    ISNULL(p.Amount, 0) AS Amount,
                    ISNULL(p.NoOfDays, 0) AS NoOfDays,
                    CAST(ISNULL(p.Active, 0) AS bit) AS Active,
                    ISNULL(p.AddedDate, SYSUTCDATETIME()) AS AddedDate
                FROM dbo.Packages p
                WHERE p.Id = @Id AND ISNULL(p.Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault();

            if (p == null)
                return Ok(new ApiResult<PackageMasterDto>(false, "Package not found", null));

            var items = LoadPackageItems(conn, id);
            int totalSessions = items.Count;
            decimal totalReal = items.Sum(x => x.ItemPrice);
            decimal amount = (decimal)p.Amount;

            var dto = new PackageMasterDto(
                Id: (int)p.Id,
                BranchId: (int)p.BranchId,
                EnglishName: (string)(p.EnglishName ?? ""),
                ArabicName: (string)(p.ArabicName ?? ""),
                Amount: amount,
                NoOfDays: (int)p.NoOfDays,
                Active: (bool)p.Active,
                TotalSessions: totalSessions,
                TotalRealValue: totalReal,
                Savings: Math.Max(0, totalReal - amount),
                AddedDate: (DateTime)p.AddedDate,
                Items: items
            );

            return Ok(new ApiResult<PackageMasterDto>(true, null, dto));
        }

        // =============================================
        // POST /api/packages
        // =============================================
        [HttpPost]
        public ActionResult<ApiResult<PackageMasterDto>> CreatePackage(
            [FromBody] CreatePackageRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<PackageMasterDto>(false, "Request required", null));

            if (string.IsNullOrWhiteSpace(request.EnglishName))
                return Ok(new ApiResult<PackageMasterDto>(false, "EnglishName is required", null));

            if (request.Amount < 0)
                return Ok(new ApiResult<PackageMasterDto>(false, "Amount must be >= 0", null));

            if (request.NoOfDays <= 0)
                return Ok(new ApiResult<PackageMasterDto>(false, "NoOfDays must be > 0", null));

            if (request.Items == null || request.Items.Count == 0)
                return Ok(new ApiResult<PackageMasterDto>(false, "At least one item is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetCurrentUserId();
            var now = DateTime.UtcNow;

            // Validate all ItemUnitIds — check existence only, not Active flag
            var itemUnitIds = request.Items.Select(x => x.ItemUnitId).Distinct().ToList();
            var validIds = conn.Query<int>(@"
                SELECT ITEM_UNIT_ID FROM dbo.ITEM_UNIT 
                WHERE ITEM_UNIT_ID IN @Ids",
                new { Ids = itemUnitIds }).ToList();

            var invalidIds = itemUnitIds.Except(validIds).ToList();
            if (invalidIds.Any())
                return Ok(new ApiResult<PackageMasterDto>(false,
                    $"Invalid item unit IDs: {string.Join(", ", invalidIds)}", null));

            // Insert package
            var packageId = conn.Query<int>(@"
                INSERT INTO dbo.Packages (
                    BranchId, EnglishName, ArabicName, Amount, 
                    Active, Deleted, NoOfDays, 
                    MaxCount, OFFER, isFlex,
                    AddedBy, AddedDate
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @BranchId, @EnglishName, @ArabicName, @Amount,
                    @Active, 0, @NoOfDays,
                    0, 0, 0,
                    @AddedBy, @AddedDate
                )",
                new
                {
                    BranchId = DEFAULT_BRANCH_ID,
                    request.EnglishName,
                    request.ArabicName,
                    request.Amount,
                    Active = request.Active,
                    request.NoOfDays,
                    AddedBy = userId,
                    AddedDate = now
                }).FirstOrDefault();

            // Insert one row per session slot
            foreach (var it in request.Items)
            {
                conn.Execute(@"
                    INSERT INTO dbo.PackageItems (
                        PackageId, ItemUnitId, Active, Deleted,
                        AddedBy, AddedDate
                    )
                    VALUES (
                        @PackageId, @ItemUnitId, 1, 0,
                        @AddedBy, @AddedDate
                    )",
                    new
                    {
                        PackageId = packageId,
                        it.ItemUnitId,
                        AddedBy = userId,
                        AddedDate = now
                    });
            }

            return GetPackageById(packageId);
        }

        // =============================================
        // PUT /api/packages/{id}
        // =============================================
        [HttpPut("{id:int}")]
        public ActionResult<ApiResult<PackageMasterDto>> UpdatePackage(
            int id, [FromBody] UpdatePackageRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<PackageMasterDto>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetCurrentUserId();
            var now = DateTime.UtcNow;

            var exists = conn.Query<int>(
                "SELECT Id FROM dbo.Packages WHERE Id = @Id AND ISNULL(Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault();
            if (exists == 0)
                return Ok(new ApiResult<PackageMasterDto>(false, "Package not found", null));

            var hasAssignments = conn.Query<int>(
                "SELECT COUNT(*) FROM dbo.CustomerPackages WHERE PackageId = @Id AND ISNULL(Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault() > 0;

            var updates = new List<string>();
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("Id", id);

            if (!string.IsNullOrWhiteSpace(request.EnglishName))
            {
                updates.Add("EnglishName = @EnglishName");
                parameters.Add("EnglishName", request.EnglishName);
            }
            if (!string.IsNullOrWhiteSpace(request.ArabicName))
            {
                updates.Add("ArabicName = @ArabicName");
                parameters.Add("ArabicName", request.ArabicName);
            }
            if (request.Amount.HasValue)
            {
                updates.Add("Amount = @Amount");
                parameters.Add("Amount", request.Amount.Value);
            }
            if (request.NoOfDays.HasValue)
            {
                updates.Add("NoOfDays = @NoOfDays");
                parameters.Add("NoOfDays", request.NoOfDays.Value);
            }
            if (request.Active.HasValue)
            {
                updates.Add("Active = @Active");
                parameters.Add("Active", request.Active.Value);
            }

            if (updates.Count > 0)
            {
                updates.Add("ModifiedBy = @ModifiedBy");
                updates.Add("ModifiedDate = @ModifiedDate");
                parameters.Add("ModifiedBy", userId);
                parameters.Add("ModifiedDate", now);

                var sql = $"UPDATE dbo.Packages SET {string.Join(", ", updates)} WHERE Id = @Id";
                conn.Execute(sql, parameters);
            }

            if (request.Items != null)
            {
                if (hasAssignments)
                    return Ok(new ApiResult<PackageMasterDto>(false,
                        "Cannot edit items: this package already has customer assignments.", null));

                conn.Execute(
                    "UPDATE dbo.PackageItems SET Deleted = 1, DeletedDate = @Now WHERE PackageId = @Id",
                    new { Id = id, Now = now });

                foreach (var it in request.Items)
                {
                    conn.Execute(@"
                        INSERT INTO dbo.PackageItems (
                            PackageId, ItemUnitId, Active, Deleted,
                            AddedBy, AddedDate
                        )
                        VALUES (
                            @PackageId, @ItemUnitId, 1, 0,
                            @AddedBy, @AddedDate
                        )",
                        new
                        {
                            PackageId = id,
                            it.ItemUnitId,
                            AddedBy = userId,
                            AddedDate = now
                        });
                }
            }

            return GetPackageById(id);
        }

        // =============================================
        // DELETE /api/packages/{id}
        // =============================================
        [HttpDelete("{id:int}")]
        public ActionResult<ApiResult<object>> DeletePackage(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetCurrentUserId();
            var now = DateTime.UtcNow;

            var hasAssignments = conn.Query<int>(
                "SELECT COUNT(*) FROM dbo.CustomerPackages WHERE PackageId = @Id AND ISNULL(Deleted, 0) = 0",
                new { Id = id }).FirstOrDefault() > 0;

            if (hasAssignments)
                return Ok(new ApiResult<object>(false,
                    "Cannot delete: this package has customer assignments. Deactivate it instead.", null));

            conn.Execute(@"
                UPDATE dbo.Packages SET 
                    Deleted = 1, DeletedDate = @Now,
                    ModifiedBy = @UserId, ModifiedDate = @Now
                WHERE Id = @Id",
                new { Id = id, UserId = userId, Now = now });

            return Ok(new ApiResult<object>(true, null, new { DeletedId = id }));
        }

        // =============================================
        // GET /api/packages/assignments
        // =============================================
        [HttpGet("assignments")]
        public ActionResult<ApiResult<PagedResult<List<CustomerPackageDto>>>> GetAssignments(
            [FromQuery] int? customerId = null,
            [FromQuery] int? packageId = null,
            [FromQuery] string? status = null,
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var now = DateTime.UtcNow;
            var offset = (page - 1) * pageSize;
            var totalCount = conn.Query<int>(@"
                SELECT COUNT(*) FROM (
                    SELECT
                        CASE
                            WHEN (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                  WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0
                                    AND ISNULL(s.Served,0) = 1) >= 
                                 (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                  WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0)
                                 AND (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                      WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0) > 0
                                 THEN 'COMPLETED'
                            WHEN cp.ExpiryDate < @Now THEN 'EXPIRED'
                            ELSE 'ACTIVE'
                        END AS ComputedStatus
                    FROM dbo.CustomerPackages cp
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                    INNER JOIN dbo.Packages p ON p.Id = cp.PackageId
                    WHERE ISNULL(cp.Deleted, 0) = 0
                      AND (@CustomerId IS NULL OR c.CUSTOMER_ID = @CustomerId)
                      AND (@PackageId IS NULL OR cp.PackageId = @PackageId)
                      AND (@Search IS NULL OR 
                         c.CUSTOMER_NAME LIKE '%' + @Search + '%' OR 
                         c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%' OR
                         p.EnglishName LIKE '%' + @Search + '%')
                ) t
                WHERE (@Status IS NULL OR t.ComputedStatus = @Status)",
                new { CustomerId = customerId, PackageId = packageId, Status = status, Search = search, Now = now })
                .FirstOrDefault();

            var rows = conn.Query<dynamic>(@"
                SELECT * FROM (
                    SELECT 
                        cp.Id, cp.PackageId,
                        p.EnglishName AS PackageEnName,
                        p.ArabicName AS PackageArName,
                        c.CUSTOMER_ID AS CustomerId,
                        c.CUSTOMER_NAME AS CustomerName,
                        c.CUSTOMER_PHONE1 AS CustomerPhone,
                        ISNULL(cp.Amount, 0) AS Amount,
                        ISNULL((
                            SELECT SUM(ISNULL(pay.PaymentAmount, 0))
                            FROM dbo.CustomerPackagePayments pay
                            WHERE pay.CustomerPackageId = cp.Id
                              AND ISNULL(pay.Deleted, 0) = 0
                        ), 0) AS PaidAmount,
                        CAST(ISNULL(cp.IsPaid, 0) AS bit) AS IsPaid,
                        ISNULL(cp.isOnline, 0) AS IsOnline,
                        cp.AddedDate,
                        cp.ExpiryDate,
                        ISNULL((
                            SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                            WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted, 0) = 0
                        ), 0) AS TotalSessions,
                        ISNULL((
                            SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                            WHERE s.CustomerPackageId = cp.Id 
                              AND ISNULL(s.Deleted, 0) = 0
                              AND ISNULL(s.Served, 0) = 1
                        ), 0) AS ServedSessions,
                        CASE
                            WHEN (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                  WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0
                                    AND ISNULL(s.Served,0) = 1) >=
                                 (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                  WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0)
                                 AND (SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                                      WHERE s.CustomerPackageId = cp.Id AND ISNULL(s.Deleted,0) = 0) > 0
                                 THEN 'COMPLETED'
                            WHEN cp.ExpiryDate < @Now THEN 'EXPIRED'
                            ELSE 'ACTIVE'
                        END AS ComputedStatus
                    FROM dbo.CustomerPackages cp
                    INNER JOIN dbo.Packages p ON p.Id = cp.PackageId
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                    WHERE ISNULL(cp.Deleted, 0) = 0
                      AND (@CustomerId IS NULL OR c.CUSTOMER_ID = @CustomerId)
                      AND (@PackageId IS NULL OR cp.PackageId = @PackageId)
                      AND (@Search IS NULL OR 
                             c.CUSTOMER_NAME LIKE '%' + @Search + '%' OR 
                             c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%' OR
                             p.EnglishName LIKE '%' + @Search + '%')
                ) sub
                WHERE (@Status IS NULL OR sub.ComputedStatus = @Status)
                ORDER BY sub.AddedDate DESC
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
                new
                {
                    CustomerId = customerId,
                    PackageId = packageId,
                    Status = status,
                    Search = search,
                    Now = now,
                    Offset = offset,
                    PageSize = pageSize
                })
                .ToList();

            var result = new List<CustomerPackageDto>();
            foreach (var r in rows)
            {
                int total = (int)r.TotalSessions;
                int served = (int)r.ServedSessions;
                int remaining = total - served;
                bool isCompleted = total > 0 && served >= total;
                bool isExpired = ((DateTime)r.ExpiryDate) < now;
                decimal amount = (decimal)r.Amount;
                decimal paid = (decimal)r.PaidAmount;

                string computedStatus = (string)r.ComputedStatus;

                result.Add(new CustomerPackageDto(
                    Id: (int)r.Id,
                    PackageId: (int)r.PackageId,
                    PackageEnName: (string)(r.PackageEnName ?? ""),
                    PackageArName: (string)(r.PackageArName ?? ""),
                    CustomerId: (int)r.CustomerId,
                    CustomerName: (string)(r.CustomerName ?? ""),
                    CustomerPhone: (string)(r.CustomerPhone ?? ""),
                    Amount: amount,
                    PaidAmount: paid,
                    RemainingAmount: Math.Max(0, amount - paid),
                    IsPaid: (bool)r.IsPaid,
                    BranchId: DEFAULT_BRANCH_ID,
                    AddedDate: (DateTime)r.AddedDate,
                    ExpiryDate: (DateTime)r.ExpiryDate,
                    IsExpired: isExpired,
                    TotalSessions: total,
                    ServedSessions: served,
                    RemainingSessions: remaining,
                    IsCompleted: isCompleted,
                    Status: computedStatus,
                    IsOnline: Convert.ToInt32(r.IsOnline) == 1
                ));
            }

            return Ok(new ApiResult<object>(true, null, new
            {
                Items = result,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            }));
        }

        // =============================================
        // GET /api/packages/assignments/{id}
        // =============================================
        [HttpGet("assignments/{id:int}")]
        public ActionResult<ApiResult<CustomerPackageDetailDto>> GetAssignmentDetail(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var detail = LoadAssignmentDetail(conn, id);
            if (detail == null)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Assignment not found", null));
            return Ok(new ApiResult<CustomerPackageDetailDto>(true, null, detail));
        }

        // =============================================
        // POST /api/packages/assign
        // =============================================
        [HttpPost("assign")]
        public ActionResult<ApiResult<CustomerPackageDetailDto>> AssignPackage(
            [FromBody] AssignPackageRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetCurrentUserId();
            var now = DateTime.UtcNow;

            var cust = conn.Query<dynamic>(
                "SELECT CUSTOMER_ID, CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = request.CustomerId }).FirstOrDefault();
            if (cust == null)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Customer not found", null));

            var pkg = conn.Query<dynamic>(@"
                SELECT Id, Amount, NoOfDays, Active, ISNULL(Deleted, 0) AS Deleted
                FROM dbo.Packages WHERE Id = @Id",
                new { Id = request.PackageId }).FirstOrDefault();
            if (pkg == null || Convert.ToBoolean(pkg.Deleted))
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Package not found", null));
            if (Convert.ToBoolean(pkg.Active) == false)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Package is not active", null));

            var pt = conn.Query<int>(
                "SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                new { Id = request.PaymentTypeId }).FirstOrDefault();
            if (pt == 0)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Payment type not found", null));

            // Each row = 1 session slot
            var pkgItems = conn.Query<PackageItemPriceRow>(@"
                SELECT 
                    pi.Id AS PackageItemId,
                    pi.ItemUnitId,
                    CAST(ISNULL(iu.ITEM_UNIT_PRICE, 0) AS decimal(18,3)) AS ItemPrice
                FROM dbo.PackageItems pi
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                WHERE pi.PackageId = @PackageId 
                  AND ISNULL(pi.Deleted, 0) = 0
                  AND ISNULL(pi.Active, 1) = 1",
                new { PackageId = request.PackageId }).ToList();

            if (pkgItems.Count == 0)
                return Ok(new ApiResult<CustomerPackageDetailDto>(false, "Package has no items", null));

            int totalSessions = pkgItems.Count;
            decimal amount = request.CustomAmount ?? Convert.ToDecimal(pkg.Amount ?? 0);
            int noOfDays = Convert.ToInt32(pkg.NoOfDays ?? 0);
            if (noOfDays <= 0) noOfDays = 365;
            DateTime expiry = now.AddDays(noOfDays);
            decimal totalRealPrice = pkgItems.Sum(x => x.ItemPrice);

            if (totalRealPrice <= 0)
                return Ok(new ApiResult<CustomerPackageDetailDto>(
                    false,
                    "Package items real total price must be greater than zero",
                    null));

            Guid customerRef = (Guid)cust.RefGuide;

            var cpId = conn.Query<int>(@"
                INSERT INTO dbo.CustomerPackages (
                    PackageId, CustomerRef, Amount, IsPaid, ShiftId,
                    Served, Deleted, ExpiryDate, isOnline,
                    AddedBy, AddedDate
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @PackageId, @CustomerRef, @Amount, 1, @ShiftId,
                    0, 0, @ExpiryDate, 0,
                    @AddedBy, @AddedDate
                )",
                new
                {
                    request.PackageId,
                    CustomerRef = customerRef,
                    Amount = amount,
                    ShiftId = DEFAULT_SHIFT_ID,
                    ExpiryDate = expiry,
                    AddedBy = userId,
                    AddedDate = now
                }).FirstOrDefault();

            conn.Execute(@"
                INSERT INTO dbo.CustomerPackagePayments (
                    CustomerPackageId, PaymentTypeId, PaymentAmount, ShiftId,
                    Notes, Deleted, isDeposit, isCollected,
                    AddedBy, AddedDate
                )
                VALUES (
                    @CustomerPackageId, @PaymentTypeId, @PaymentAmount, @ShiftId,
                    @Notes, 0, 0, 0,
                    @AddedBy, @AddedDate
                )",
                new
                {
                    CustomerPackageId = cpId,
                    request.PaymentTypeId,
                    PaymentAmount = amount,
                    ShiftId = DEFAULT_SHIFT_ID,
                    request.Notes,
                    AddedBy = userId,
                    AddedDate = now
                });

            // One row per session slot (no quantity loop)
            decimal allocated = 0m;

            for (int i = 0; i < pkgItems.Count; i++)
            {
                var pi = pkgItems[i];
                decimal realPrice = pi.ItemPrice;

                decimal itemPriceInPackage;

                if (i == pkgItems.Count - 1)
                {
                    itemPriceInPackage = amount - allocated;
                }
                else
                {
                    itemPriceInPackage = Math.Round(
                        amount * (realPrice / totalRealPrice),
                        3,
                        MidpointRounding.AwayFromZero
                    );

                    allocated += itemPriceInPackage;
                }

                conn.Execute(@"
                    INSERT INTO dbo.CustomerPackageSessions (
                        CustomerPackageId, PackageItemId, ItemPrice, ItemPriceInPackage,
                        Served, Deleted, AddedBy, AddedDate
                    )
                    VALUES (
                        @CustomerPackageId, @PackageItemId, @ItemPrice, @ItemPriceInPackage,
                        0, 0, @AddedBy, @AddedDate
                    )",
                    new
                    {
                        CustomerPackageId = cpId,
                        pi.PackageItemId,
                        ItemPrice = realPrice,
                        ItemPriceInPackage = itemPriceInPackage,
                        AddedBy = userId,
                        AddedDate = now
                    });
            }

            var detail = LoadAssignmentDetail(conn, cpId);
            return Ok(new ApiResult<CustomerPackageDetailDto>(true, null, detail));
        }

        // =============================================
        // POST /api/packages/serve
        // =============================================
        [HttpPost("serve")]
        public ActionResult<ApiResult<CustomerPackageSessionDto>> ServeSession(
            [FromBody] ServeSessionRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");
            int userId = GetCurrentUserId();
            var now = DateTime.UtcNow;

            var session = conn.Query<dynamic>(@"
                SELECT s.Id, s.CustomerPackageId, s.Served, s.Deleted,
                       cp.ExpiryDate
                FROM dbo.CustomerPackageSessions s
                INNER JOIN dbo.CustomerPackages cp ON cp.Id = s.CustomerPackageId
                WHERE s.Id = @Id",
                new { Id = request.CustomerPackageSessionId }).FirstOrDefault();

            if (session == null)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Session not found", null));
            if (Convert.ToInt32(session.Deleted) == 1)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Session deleted", null));
            if (Convert.ToInt32(session.Served) == 1)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Session already served", null));
            if (((DateTime)session.ExpiryDate) < now)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Package has expired", null));

            var staffExists = conn.Query<int>(
                "SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = request.StaffId }).FirstOrDefault();
            if (staffExists == 0)
                return Ok(new ApiResult<CustomerPackageSessionDto>(false, "Staff not found or inactive", null));

            conn.Execute(@"
                UPDATE dbo.CustomerPackageSessions SET
                    Served = 1, ServedDate = @Now,
                    StaffId = @StaffId,
                    Notes = COALESCE(@Notes, Notes),
                    ModifiedBy = @UserId, ModifiedDate = @Now
                WHERE Id = @Id",
                new
                {
                    Id = request.CustomerPackageSessionId,
                    StaffId = request.StaffId,
                    request.Notes,
                    UserId = userId,
                    Now = now
                });

            var dto = LoadSessionById(conn, request.CustomerPackageSessionId);
            return Ok(new ApiResult<CustomerPackageSessionDto>(true, null, dto));
        }

        // =============================================
        // GET /api/packages/eligible-sessions
        // =============================================
        [HttpGet("eligible-sessions")]
        public ActionResult<ApiResult<List<EligiblePackageSessionDto>>> GetEligibleSessions(
            [FromQuery] int customerId,
            [FromQuery] int? itemId = null,
            [FromQuery] int? unitId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var now = DateTime.UtcNow;

            var cust = conn.Query<Guid?>(
                "SELECT CUSTOMER_REF_GUIDE FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (cust == null)
                return Ok(new ApiResult<List<EligiblePackageSessionDto>>(true, null,
                    new List<EligiblePackageSessionDto>()));

            var sessions = conn.Query<dynamic>(@"
                SELECT 
                    s.Id AS CustomerPackageSessionId,
                    cp.Id AS CustomerPackageId,
                    cp.PackageId,
                    p.EnglishName AS PackageEnName,
                    p.ArabicName  AS PackageArName,
                    cp.ExpiryDate,
                    pi.ItemUnitId,
                    iu.ITEM_ID    AS ItemId,
                    iu.UNIT_ID    AS UnitId,
                    i.ITEM_NAME1  AS ItemEnName,
                    i.ITEM_NAME2  AS ItemArName,
                    ISNULL((
                        SELECT COUNT(*) 
                        FROM dbo.CustomerPackageSessions s2
                        WHERE s2.CustomerPackageId = cp.Id
                          AND ISNULL(s2.Deleted, 0) = 0
                          AND ISNULL(s2.Served, 0) = 0
                    ), 0) AS RemainingInPackage
                FROM dbo.CustomerPackageSessions s
                INNER JOIN dbo.CustomerPackages cp  ON cp.Id = s.CustomerPackageId
                INNER JOIN dbo.Packages p           ON p.Id  = cp.PackageId
                INNER JOIN dbo.PackageItems pi      ON pi.Id = s.PackageItemId
                INNER JOIN dbo.ITEM_UNIT iu         ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                INNER JOIN dbo.ITEM i               ON i.ITEM_ID = iu.ITEM_ID
                WHERE cp.CustomerRef = @CustomerRef
                  AND ISNULL(cp.Deleted, 0) = 0
                  AND ISNULL(cp.IsPaid,   0) = 1
                  AND cp.ExpiryDate >= @Now
                  AND ISNULL(s.Deleted, 0) = 0
                  AND ISNULL(s.Served,  0) = 0
                  AND (@ItemId IS NULL OR iu.ITEM_ID = @ItemId)
                  AND (@UnitId IS NULL OR iu.UNIT_ID = @UnitId)
                ORDER BY cp.ExpiryDate, cp.Id, s.Id",
                new { CustomerRef = cust.Value, Now = now, ItemId = itemId, UnitId = unitId })
                .ToList();

            // ── No deduplication: every unserved session row is returned ──
            var result = sessions.Select(s => new EligiblePackageSessionDto(
                CustomerPackageSessionId: (int)s.CustomerPackageSessionId,
                CustomerPackageId: (int)s.CustomerPackageId,
                PackageId: (int)s.PackageId,
                PackageEnName: (string)(s.PackageEnName ?? ""),
                PackageArName: (string)(s.PackageArName ?? ""),
                ExpiryDate: (DateTime)s.ExpiryDate,
                RemainingInPackage: (int)s.RemainingInPackage,
                ItemUnitId: (int)s.ItemUnitId,
                ItemId: (int)s.ItemId,
                UnitId: (int)s.UnitId,
                ItemEnName: (string)(s.ItemEnName ?? ""),
                ItemArName: (string)(s.ItemArName ?? "")
            )).ToList();

            return Ok(new ApiResult<List<EligiblePackageSessionDto>>(true, null, result));
        }

        // =============================================
        // GET /api/packages/customer-summary
        // =============================================
        [HttpGet("customer-summary")]
        public ActionResult<ApiResult<object>> GetCustomerSummary([FromQuery] int customerId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var now = DateTime.UtcNow;

            var cust = conn.Query<Guid?>(
                "SELECT CUSTOMER_REF_GUIDE FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (cust == null)
                return Ok(new ApiResult<object>(true, null, new
                {
                    HasActivePackage = false,
                    ActivePackageCount = 0,
                    TotalRemainingSessions = 0
                }));

            var row = conn.Query<dynamic>(@"
                SELECT 
                    COUNT(DISTINCT cp.Id) AS PkgCount,
                    ISNULL(SUM(CASE WHEN s.Served = 0 AND ISNULL(s.Deleted,0) = 0 THEN 1 ELSE 0 END), 0) AS Remaining
                FROM dbo.CustomerPackages cp
                LEFT JOIN dbo.CustomerPackageSessions s ON s.CustomerPackageId = cp.Id
                WHERE cp.CustomerRef = @Ref
                  AND ISNULL(cp.Deleted, 0) = 0
                  AND ISNULL(cp.IsPaid, 0) = 1
                  AND cp.ExpiryDate >= @Now",
                new { Ref = cust.Value, Now = now }).FirstOrDefault();

            int pkgCount = row != null ? (int)row.PkgCount : 0;
            int remaining = row != null ? (int)row.Remaining : 0;

            return Ok(new ApiResult<object>(true, null, new
            {
                HasActivePackage = pkgCount > 0 && remaining > 0,
                ActivePackageCount = pkgCount,
                TotalRemainingSessions = remaining
            }));
        }

        #region Private Helpers
        
        private List<PackageItemDefDto> LoadPackageItems(IDbConnection conn, int packageId)
        {
            return conn.Query<dynamic>(@"
                SELECT 
                    pi.Id,
                    pi.ItemUnitId,
                    iu.ITEM_ID AS ItemId,
                    i.ITEM_NAME1 AS ItemEnName,
                    i.ITEM_NAME2 AS ItemArName,
                    iu.UNIT_ID AS UnitId,
                    u.UNIT_NAME1 AS UnitEnName,
                    u.UNIT_NAME2 AS UnitArName,
                    iu.ITEM_UNIT_PRICE AS ItemPrice,
                    CAST(ISNULL(pi.Active, 1) AS bit) AS Active
                FROM dbo.PackageItems pi
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                INNER JOIN dbo.UNIT u ON u.UNIT_ID = iu.UNIT_ID
                WHERE pi.PackageId = @PackageId
                  AND ISNULL(pi.Deleted, 0) = 0
                ORDER BY pi.Id",
                new { PackageId = packageId })
                .Select(r => new PackageItemDefDto(
                    Id: (int)r.Id,
                    ItemUnitId: (int)r.ItemUnitId,
                    ItemId: (int)r.ItemId,
                    ItemEnName: (string)(r.ItemEnName ?? ""),
                    ItemArName: (string)(r.ItemArName ?? ""),
                    UnitId: (int)r.UnitId,
                    UnitEnName: (string)(r.UnitEnName ?? ""),
                    UnitArName: (string)(r.UnitArName ?? ""),
                    ItemPrice: (decimal)r.ItemPrice,
                    Active: (bool)r.Active
                )).ToList();
        }

        private CustomerPackageDetailDto? LoadAssignmentDetail(IDbConnection conn, int cpId)
        {
            var now = DateTime.UtcNow;

            var r = conn.Query<dynamic>(@"
                SELECT 
                    cp.Id, cp.PackageId,
                    p.EnglishName AS PackageEnName,
                    p.ArabicName AS PackageArName,
                    c.CUSTOMER_ID AS CustomerId,
                    c.CUSTOMER_NAME AS CustomerName,
                    c.CUSTOMER_PHONE1 AS CustomerPhone,
                    ISNULL(cp.Amount, 0) AS Amount,
                    ISNULL((
                        SELECT SUM(ISNULL(pay.PaymentAmount, 0))
                        FROM dbo.CustomerPackagePayments pay
                        WHERE pay.CustomerPackageId = cp.Id
                          AND ISNULL(pay.Deleted, 0) = 0
                    ), 0) AS PaidAmount,
                    CAST(ISNULL(cp.IsPaid, 0) AS bit) AS IsPaid,
                    ISNULL(cp.isOnline, 0) AS IsOnline,
                    cp.AddedDate,
                    cp.ExpiryDate
                FROM dbo.CustomerPackages cp
                INNER JOIN dbo.Packages p ON p.Id = cp.PackageId
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
                WHERE cp.Id = @Id AND ISNULL(cp.Deleted, 0) = 0",
                new { Id = cpId }).FirstOrDefault();

            if (r == null) return null;

            var sessions = conn.Query<dynamic>(@"
                SELECT 
                    s.Id, s.CustomerPackageId, s.PackageItemId,
                    pi.ItemUnitId,
                    iu.ITEM_ID AS ItemId,
                    i.ITEM_NAME1 AS ItemEnName,
                    i.ITEM_NAME2 AS ItemArName,
                    iu.UNIT_ID AS UnitId,
                    s.StaffId,
                    st.EnglishName AS StaffEnName,
                    st.ArabicName AS StaffArName,
                    ISNULL(s.ItemPrice, 0) AS ItemPrice,
                    ISNULL(s.ItemPriceInPackage, 0) AS ItemPriceInPackage,
                    CAST(ISNULL(s.Served, 0) AS bit) AS Served,
                    s.ServedDate,
                    s.AppointmentId,
                    s.Notes
                FROM dbo.CustomerPackageSessions s
                INNER JOIN dbo.PackageItems pi ON pi.Id = s.PackageItemId
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                LEFT JOIN dbo.STAFF st ON st.Id = s.StaffId
                WHERE s.CustomerPackageId = @Id AND ISNULL(s.Deleted, 0) = 0
                ORDER BY s.Id",
                new { Id = cpId })
                .Select(x => new CustomerPackageSessionDto(
                    Id: (int)x.Id,
                    CustomerPackageId: (int)x.CustomerPackageId,
                    PackageItemId: (int)x.PackageItemId,
                    ItemUnitId: (int)x.ItemUnitId,
                    ItemId: (int)x.ItemId,
                    ItemEnName: (string)(x.ItemEnName ?? ""),
                    ItemArName: (string)(x.ItemArName ?? ""),
                    UnitId: (int)x.UnitId,
                    StaffId: (int?)x.StaffId,
                    StaffEnName: (string?)x.StaffEnName,
                    StaffArName: (string?)x.StaffArName,
                    ItemPrice: (decimal)x.ItemPrice,
                    ItemPriceInPackage: (decimal)x.ItemPriceInPackage,
                    Served: (bool)x.Served,
                    ServedDate: (DateTime?)x.ServedDate,
                    AppointmentId: (int?)x.AppointmentId,
                    Notes: (string?)x.Notes
                )).ToList();

            var payments = conn.Query<dynamic>(@"
                SELECT 
                    pay.Id, pay.CustomerPackageId,
                    pay.PaymentTypeId,
                    ISNULL(pt.INVOICE_PAYMENT_TYPE_NAME1, '') AS PaymentTypeName,
                    ISNULL(pay.PaymentAmount, 0) AS PaymentAmount,
                    CAST(ISNULL(pay.isDeposit, 0) AS bit) AS IsDeposit,
                    pay.Notes,
                    pay.AddedDate
                FROM dbo.CustomerPackagePayments pay
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt
                    ON pt.INVOICE_PAYMENT_TYPE_ID = pay.PaymentTypeId
                WHERE pay.CustomerPackageId = @Id AND ISNULL(pay.Deleted, 0) = 0
                ORDER BY pay.AddedDate",
                new { Id = cpId })
                .Select(x => new CustomerPackagePaymentDto(
                    Id: (int)x.Id,
                    CustomerPackageId: (int)x.CustomerPackageId,
                    PaymentTypeId: (int)x.PaymentTypeId,
                    PaymentTypeName: (string)x.PaymentTypeName,
                    PaymentAmount: (decimal)x.PaymentAmount,
                    IsDeposit: (bool)x.IsDeposit,
                    Notes: (string?)x.Notes,
                    AddedDate: (DateTime)x.AddedDate
                )).ToList();

            int total = sessions.Count;
            int served = sessions.Count(s => s.Served);
            int remaining = total - served;
            bool isCompleted = total > 0 && served >= total;
            bool isExpired = ((DateTime)r.ExpiryDate) < now;
            decimal amount = (decimal)r.Amount;
            decimal paid = (decimal)r.PaidAmount;

            string computedStatus = isCompleted ? "COMPLETED"
                : isExpired ? "EXPIRED"
                : "ACTIVE";

            var assignment = new CustomerPackageDto(
                Id: (int)r.Id,
                PackageId: (int)r.PackageId,
                PackageEnName: (string)(r.PackageEnName ?? ""),
                PackageArName: (string)(r.PackageArName ?? ""),
                CustomerId: (int)r.CustomerId,
                CustomerName: (string)(r.CustomerName ?? ""),
                CustomerPhone: (string)(r.CustomerPhone ?? ""),
                Amount: amount,
                PaidAmount: paid,
                RemainingAmount: Math.Max(0, amount - paid),
                IsPaid: (bool)r.IsPaid,
                BranchId: DEFAULT_BRANCH_ID,
                AddedDate: (DateTime)r.AddedDate,
                ExpiryDate: (DateTime)r.ExpiryDate,
                IsExpired: isExpired,
                TotalSessions: total,
                ServedSessions: served,
                RemainingSessions: remaining,
                IsCompleted: isCompleted,
                Status: computedStatus,
                IsOnline: Convert.ToInt32(r.IsOnline) == 1
            );

            return new CustomerPackageDetailDto(assignment, sessions, payments);
        }

        private CustomerPackageSessionDto? LoadSessionById(IDbConnection conn, int id)
        {
            var x = conn.Query<dynamic>(@"
                SELECT 
                    s.Id, s.CustomerPackageId, s.PackageItemId,
                    pi.ItemUnitId,
                    iu.ITEM_ID AS ItemId,
                    i.ITEM_NAME1 AS ItemEnName,
                    i.ITEM_NAME2 AS ItemArName,
                    iu.UNIT_ID AS UnitId,
                    s.StaffId,
                    st.EnglishName AS StaffEnName,
                    st.ArabicName AS StaffArName,
                    ISNULL(s.ItemPrice, 0) AS ItemPrice,
                    ISNULL(s.ItemPriceInPackage, 0) AS ItemPriceInPackage,
                    CAST(ISNULL(s.Served, 0) AS bit) AS Served,
                    s.ServedDate,
                    s.AppointmentId,
                    s.Notes
                FROM dbo.CustomerPackageSessions s
                INNER JOIN dbo.PackageItems pi ON pi.Id = s.PackageItemId
                INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                LEFT JOIN dbo.STAFF st ON st.Id = s.StaffId
                WHERE s.Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (x == null) return null;

            return new CustomerPackageSessionDto(
                Id: (int)x.Id,
                CustomerPackageId: (int)x.CustomerPackageId,
                PackageItemId: (int)x.PackageItemId,
                ItemUnitId: (int)x.ItemUnitId,
                ItemId: (int)x.ItemId,
                ItemEnName: (string)(x.ItemEnName ?? ""),
                ItemArName: (string)(x.ItemArName ?? ""),
                UnitId: (int)x.UnitId,
                StaffId: (int?)x.StaffId,
                StaffEnName: (string?)x.StaffEnName,
                StaffArName: (string?)x.StaffArName,
                ItemPrice: (decimal)x.ItemPrice,
                ItemPriceInPackage: (decimal)x.ItemPriceInPackage,
                Served: (bool)x.Served,
                ServedDate: (DateTime?)x.ServedDate,
                AppointmentId: (int?)x.AppointmentId,
                Notes: (string?)x.Notes
            );
        }
        private record PackageItemPriceRow(
            int PackageItemId,
            int ItemUnitId,
            decimal ItemPrice
        );
        #endregion
    }
}