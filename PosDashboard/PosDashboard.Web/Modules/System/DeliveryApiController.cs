// Modules/System/DeliveryApiController.cs
//
// Everything the Delivery flow needs, in one place:
//
//   POS bootstrap
//     GET  /api/delivery/context?customerId=&branchId=   one-shot dialog load
//     GET  /api/delivery/settings?branchId=              the 4 delivery flags
//     GET  /api/delivery/charge?areaId=&branchId=        single price lookup
//
//   Delivery types  (/settings page)
//     GET  /api/delivery/types?branchId=&includeInactive=
//     POST /api/delivery/types
//     POST /api/delivery/types/update/{id}
//     POST /api/delivery/types/delete/{id}
//
//   Area price list (/management page)
//     GET  /api/delivery/area-charges?branchId=&search=
//     POST /api/delivery/area-charges              upsert one
//     POST /api/delivery/area-charges/bulk         upsert many
//     POST /api/delivery/area-charges/delete/{id}
//     GET  /api/delivery/governorates
//     GET  /api/delivery/areas?branchId=&governorateId=
//
//   Customer addresses (POS delivery step + /management customers)
//     GET  /api/delivery/addresses?customerId=&branchId=
//     GET  /api/delivery/addresses/count?customerId=
//     POST /api/delivery/addresses
//     POST /api/delivery/addresses/update/{addressId}
//     POST /api/delivery/addresses/delete/{addressId}
//     POST /api/delivery/addresses/{addressId}/make-default
//
// Charges are ALWAYS resolved server-side from AreaDeliveryCharge (or the
// type's ChargeOverride). The client never gets to name its own price.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using PosDashboard.Web.Modules.System.Services;
using static PosDashboard.Web.Modules.System.Models.DeliveryDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/delivery")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class DeliveryApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public DeliveryApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        private int CurrentUserId()
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userId" || c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : 1;
        }

        // ═════════════════════════════════════════════════════════════════
        // POS BOOTSTRAP
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Everything the POS delivery dialog needs in one round-trip.</summary>
        [HttpGet("context")]
        public ActionResult<ApiResult<DeliveryContextDto>> GetContext(
            [FromQuery] int customerId,
            [FromQuery] int branchId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var settings = BusinessSettingsService.GetDeliverySettings(conn, branchId);

            var customer = SqlMapper.Query(conn,
                "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<DeliveryContextDto>(false, "Customer not found", null));

            var addresses = LoadAddresses(conn, customerId, (Guid)customer.RefGuide, branchId);
            var defaultAddress = addresses.FirstOrDefault(a => a.IsDefault) ?? addresses.FirstOrDefault();

            var types = LoadDeliveryTypes(conn, branchId, includeInactive: false);

            var governorates = SqlMapper.Query<GovernorateOptionDto>(conn, @"
                SELECT
                    GOVERNORATE_ID    AS GovernorateId,
                    GOVERNORATE_NAME1 AS NameEn,
                    GOVERNORATE_NAME2 AS NameAr,
                    COLOR_CODE        AS ColorCode
                FROM dbo.GOVERNORATE
                ORDER BY GOVERNORATE_NAME1").ToList();

            var areas = LoadAreaOptions(conn, branchId, null);
            var drivers = LoadDrivers(conn, branchId, null);

            int tz = BusinessSettingsService.GetTimeZoneOffset(conn);
            var suggested = DateTime.UtcNow.AddHours(tz).Date.AddDays(settings.DefaultLeadDays);

            var ctx = new DeliveryContextDto(
                Settings: settings,
                DeliveryTypes: types,
                DefaultDeliveryType: types.FirstOrDefault(t => t.IsDelivery && t.IsDefault)
                                     ?? types.FirstOrDefault(t => t.IsDelivery),
                DefaultPickupType: types.FirstOrDefault(t => !t.IsDelivery && t.IsDefault)
                                   ?? types.FirstOrDefault(t => !t.IsDelivery),
                Addresses: addresses,
                DefaultAddress: defaultAddress,
                Governorates: governorates,
                Areas: areas,
                Drivers: drivers,
                SuggestedDeliveryDate: suggested);

            return Ok(new ApiResult<DeliveryContextDto>(true, null, ctx));
        }

        /// <summary>The four delivery flags — cheap enough to poll on POS boot.</summary>
        [HttpGet("settings")]
        public ActionResult<ApiResult<DeliverySettingsDto>> GetSettings([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            return Ok(new ApiResult<DeliverySettingsDto>(
                true, null, BusinessSettingsService.GetDeliverySettings(conn, branchId)));
        }

        /// <summary>Price for one area in one branch. HasCharge=false ⇒ the area is unpriced.</summary>
        [HttpGet("charge")]
        public ActionResult<ApiResult<AreaChargeLookupDto>> GetCharge(
            [FromQuery] int areaId, [FromQuery] int branchId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var (charge, has) = ResolveAreaCharge(conn, areaId, branchId);
            return Ok(new ApiResult<AreaChargeLookupDto>(
                true, null, new AreaChargeLookupDto(areaId, branchId, charge, has)));
        }

        /// <summary>
        /// Active drivers, optionally narrowed to one governorate. The POS uses the
        /// governorate filter so the cashier only sees drivers who serve the chosen
        /// address's area — same rule as Home Service.
        /// </summary>
        [HttpGet("drivers")]
        public ActionResult<ApiResult<List<DeliveryDriverDto>>> GetDrivers(
            [FromQuery] int branchId,
            [FromQuery] int? governorateId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            return Ok(new ApiResult<List<DeliveryDriverDto>>(
                true, null, LoadDrivers(conn, branchId, governorateId)));
        }

        /// <summary>All drivers for a branch incl. inactive — the /management grid.</summary>
        [HttpGet("drivers/all")]
        public ActionResult<ApiResult<List<DeliveryDriverDto>>> GetAllDrivers(
            [FromQuery] int? branchId = null,
            [FromQuery] string? search = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var list = SqlMapper.Query<DeliveryDriverDto>(conn, @"
                SELECT
                    d.DRIVER_ID         AS DriverId,
                    d.DRIVER_NAME       AS DriverName,
                    d.DRIVER_NAME_AR    AS DriverNameAr,
                    d.DRIVER_PHONE      AS DriverPhone,
                    d.DRIVER_ADRESS     AS DriverAddress,
                    d.BRANCH_ID         AS BranchId,
                    d.GOVERNORATE_ID    AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                    CAST(CASE WHEN d.IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive
                FROM dbo.DRIVER d
                INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE (@BranchId IS NULL OR d.BRANCH_ID = @BranchId)
                  AND (@Search IS NULL
                       OR d.DRIVER_NAME LIKE '%' + @Search + '%'
                       OR d.DRIVER_NAME_AR LIKE '%' + @Search + '%'
                       OR d.DRIVER_PHONE LIKE '%' + @Search + '%')
                ORDER BY d.GOVERNORATE_ID, d.DRIVER_NAME",
                new { BranchId = branchId, Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() })
                .ToList();
            return Ok(new ApiResult<List<DeliveryDriverDto>>(true, null, list));
        }

        [HttpPost("drivers")]
        public ActionResult<ApiResult<DeliveryDriverDto>> CreateDriver([FromBody] SaveDeliveryDriverRequest request)
        {
            var invalid = ValidateDriver(request);
            if (invalid != null) return Ok(new ApiResult<DeliveryDriverDto>(false, invalid, null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            if (!FkExists(conn, "dbo.BRANCH", "BRANCH_ID", request.BranchId))
                return Ok(new ApiResult<DeliveryDriverDto>(false, "Branch not found", null));
            if (!FkExists(conn, "dbo.GOVERNORATE", "GOVERNORATE_ID", request.GovernorateId))
                return Ok(new ApiResult<DeliveryDriverDto>(false, "Governorate not found", null));

            try
            {
                using var uow = new UnitOfWork(conn);
                // DRIVER_ID is not IDENTITY in this schema — allocate MAX+1.
                int newId = (SqlMapper.Query<int?>(uow.Connection,
                    "SELECT MAX(DRIVER_ID) FROM dbo.DRIVER").FirstOrDefault() ?? 0) + 1;

                SqlMapper.Execute(uow.Connection, @"
                    INSERT INTO dbo.DRIVER (
                        DRIVER_ID, DRIVER_NAME, DRIVER_NAME_AR, DRIVER_PHONE,
                        DRIVER_ADRESS, IS_ACTIVE, BRANCH_ID, GOVERNORATE_ID
                    )
                    VALUES (
                        @Id, @Name, @NameAr, @Phone,
                        @Address, @IsActive, @BranchId, @GovernorateId
                    )",
                    new
                    {
                        Id = newId,
                        Name = request.DriverName.Trim(),
                        NameAr = (request.DriverNameAr ?? request.DriverName).Trim(),
                        Phone = request.DriverPhone.Trim(),
                        Address = (request.DriverAddress ?? "").Trim(),
                        IsActive = request.IsActive ? 1 : 0,
                        request.BranchId,
                        request.GovernorateId
                    });

                uow.Commit();
                return Ok(new ApiResult<DeliveryDriverDto>(true, null, LoadDriverById(conn, newId)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryDriverDto>(false, $"Failed to create driver: {ex.Message}", null));
            }
        }

        [HttpPost("drivers/update/{id:int}")]
        public ActionResult<ApiResult<DeliveryDriverDto>> UpdateDriver(int id, [FromBody] SaveDeliveryDriverRequest request)
        {
            var invalid = ValidateDriver(request);
            if (invalid != null) return Ok(new ApiResult<DeliveryDriverDto>(false, invalid, null));

            using var conn = sqlConnections.NewByKey("Default");
            if (LoadDriverById(conn, id) == null)
                return Ok(new ApiResult<DeliveryDriverDto>(false, "Driver not found", null));
            if (!FkExists(conn, "dbo.BRANCH", "BRANCH_ID", request.BranchId))
                return Ok(new ApiResult<DeliveryDriverDto>(false, "Branch not found", null));
            if (!FkExists(conn, "dbo.GOVERNORATE", "GOVERNORATE_ID", request.GovernorateId))
                return Ok(new ApiResult<DeliveryDriverDto>(false, "Governorate not found", null));

            try
            {
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.DRIVER SET
                        DRIVER_NAME    = @Name,
                        DRIVER_NAME_AR = @NameAr,
                        DRIVER_PHONE   = @Phone,
                        DRIVER_ADRESS  = @Address,
                        IS_ACTIVE      = @IsActive,
                        BRANCH_ID      = @BranchId,
                        GOVERNORATE_ID = @GovernorateId
                    WHERE DRIVER_ID = @Id",
                    new
                    {
                        Id = id,
                        Name = request.DriverName.Trim(),
                        NameAr = (request.DriverNameAr ?? request.DriverName).Trim(),
                        Phone = request.DriverPhone.Trim(),
                        Address = (request.DriverAddress ?? "").Trim(),
                        IsActive = request.IsActive ? 1 : 0,
                        request.BranchId,
                        request.GovernorateId
                    });

                return Ok(new ApiResult<DeliveryDriverDto>(true, null, LoadDriverById(conn, id)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryDriverDto>(false, $"Failed to update driver: {ex.Message}", null));
            }
        }

        /// <summary>
        /// Delete a driver. Because invoices only keep a denormalised driver *snapshot*
        /// (no FK to DRIVER), deleting is safe for history. If the row is still
        /// referenced by a live appointment/invoice column we deactivate instead.
        /// </summary>
        [HttpPost("drivers/delete/{id:int}")]
        public ActionResult<ApiResult<object>> DeleteDriver(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            if (LoadDriverById(conn, id) == null)
                return Ok(new ApiResult<object>(false, "Driver not found", null));

            // Referenced by an invoice header? Keep the row, just deactivate it, so
            // existing invoices still resolve their driver id.
            int refs = SqlMapper.Query<int>(conn,
                "SELECT COUNT(*) FROM dbo.AppointmentInvoices WHERE DeliveryDriverId = @Id",
                new { Id = id }).First();

            try
            {
                if (refs > 0)
                {
                    SqlMapper.Execute(conn,
                        "UPDATE dbo.DRIVER SET IS_ACTIVE = 0 WHERE DRIVER_ID = @Id", new { Id = id });
                    return Ok(new ApiResult<object>(true, null, new { Id = id, Deactivated = true }));
                }

                SqlMapper.Execute(conn, "DELETE FROM dbo.DRIVER WHERE DRIVER_ID = @Id", new { Id = id });
                return Ok(new ApiResult<object>(true, null, new { Id = id, Deactivated = false }));
            }
            catch (Exception ex)
            {
                // FK from some other table we didn't anticipate → fall back to deactivate.
                try
                {
                    SqlMapper.Execute(conn,
                        "UPDATE dbo.DRIVER SET IS_ACTIVE = 0 WHERE DRIVER_ID = @Id", new { Id = id });
                    return Ok(new ApiResult<object>(true, null, new { Id = id, Deactivated = true }));
                }
                catch
                {
                    return Ok(new ApiResult<object>(false, $"Failed to delete driver: {ex.Message}", null));
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // DELIVERY TYPES
        // ═════════════════════════════════════════════════════════════════

        [HttpGet("types")]
        public ActionResult<ApiResult<List<DeliveryTypeDto>>> GetTypes(
            [FromQuery] int? branchId = null,
            [FromQuery] bool includeInactive = false)
        {
            using var conn = sqlConnections.NewByKey("Default");
            return Ok(new ApiResult<List<DeliveryTypeDto>>(
                true, null, LoadDeliveryTypes(conn, branchId, includeInactive)));
        }

        [HttpPost("types")]
        public ActionResult<ApiResult<DeliveryTypeDto>> CreateType([FromBody] SaveDeliveryTypeRequest request)
        {
            var invalid = ValidateType(request);
            if (invalid != null) return Ok(new ApiResult<DeliveryTypeDto>(false, invalid, null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            string code = request.Code.Trim().ToUpperInvariant();

            var clash = SqlMapper.Query<int>(conn, @"
                SELECT COUNT(*) FROM dbo.DeliveryType
                WHERE Code = @Code AND Deleted = 0
                  AND ((@BranchId IS NULL AND BranchId IS NULL) OR BranchId = @BranchId)",
                new { Code = code, request.BranchId }).First();

            if (clash > 0)
                return Ok(new ApiResult<DeliveryTypeDto>(false, $"Code '{code}' already exists", null));

            try
            {
                using var uow = new UnitOfWork(conn);

                if (request.IsDefault)
                    ClearDefaultType(uow.Connection, request.IsDelivery, request.BranchId, exceptId: null);

                int id = SqlMapper.Query<int>(uow.Connection, @"
                    INSERT INTO dbo.DeliveryType (
                        Code, NameEn, NameAr, IsDelivery, IsDefault, IsActive,
                        Ordering, ColorCode, Icon, ChargeOverride, Notes, BranchId,
                        Deleted, CreatedAt
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        @Code, @NameEn, @NameAr, @IsDelivery, @IsDefault, @IsActive,
                        @Ordering, @ColorCode, @Icon, @ChargeOverride, @Notes, @BranchId,
                        0, SYSUTCDATETIME()
                    )",
                    new
                    {
                        Code = code,
                        NameEn = request.NameEn.Trim(),
                        NameAr = request.NameAr.Trim(),
                        request.IsDelivery,
                        request.IsDefault,
                        request.IsActive,
                        request.Ordering,
                        request.ColorCode,
                        request.Icon,
                        request.ChargeOverride,
                        request.Notes,
                        request.BranchId
                    }).First();

                uow.Commit();
                return Ok(new ApiResult<DeliveryTypeDto>(true, null, LoadDeliveryTypeById(conn, id)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryTypeDto>(false, $"Failed to create delivery type: {ex.Message}", null));
            }
        }

        [HttpPost("types/update/{id:int}")]
        public ActionResult<ApiResult<DeliveryTypeDto>> UpdateType(int id, [FromBody] SaveDeliveryTypeRequest request)
        {
            var invalid = ValidateType(request);
            if (invalid != null) return Ok(new ApiResult<DeliveryTypeDto>(false, invalid, null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var existing = LoadDeliveryTypeById(conn, id);
            if (existing == null)
                return Ok(new ApiResult<DeliveryTypeDto>(false, "Delivery type not found", null));

            string code = request.Code.Trim().ToUpperInvariant();

            var clash = SqlMapper.Query<int>(conn, @"
                SELECT COUNT(*) FROM dbo.DeliveryType
                WHERE Code = @Code AND Deleted = 0 AND Id <> @Id
                  AND ((@BranchId IS NULL AND BranchId IS NULL) OR BranchId = @BranchId)",
                new { Code = code, Id = id, request.BranchId }).First();

            if (clash > 0)
                return Ok(new ApiResult<DeliveryTypeDto>(false, $"Code '{code}' already exists", null));

            try
            {
                using var uow = new UnitOfWork(conn);

                if (request.IsDefault)
                    ClearDefaultType(uow.Connection, request.IsDelivery, request.BranchId, exceptId: id);

                SqlMapper.Execute(uow.Connection, @"
                    UPDATE dbo.DeliveryType SET
                        Code           = @Code,
                        NameEn         = @NameEn,
                        NameAr         = @NameAr,
                        IsDelivery     = @IsDelivery,
                        IsDefault      = @IsDefault,
                        IsActive       = @IsActive,
                        Ordering       = @Ordering,
                        ColorCode      = @ColorCode,
                        Icon           = @Icon,
                        ChargeOverride = @ChargeOverride,
                        Notes          = @Notes,
                        BranchId       = @BranchId,
                        UpdatedAt      = SYSUTCDATETIME()
                    WHERE Id = @Id",
                    new
                    {
                        Id = id,
                        Code = code,
                        NameEn = request.NameEn.Trim(),
                        NameAr = request.NameAr.Trim(),
                        request.IsDelivery,
                        request.IsDefault,
                        request.IsActive,
                        request.Ordering,
                        request.ColorCode,
                        request.Icon,
                        request.ChargeOverride,
                        request.Notes,
                        request.BranchId
                    });

                uow.Commit();
                return Ok(new ApiResult<DeliveryTypeDto>(true, null, LoadDeliveryTypeById(conn, id)));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryTypeDto>(false, $"Failed to update delivery type: {ex.Message}", null));
            }
        }

        /// <summary>Soft delete. Refused when it would leave a bucket (pickup/delivery) empty.</summary>
        [HttpPost("types/delete/{id:int}")]
        public ActionResult<ApiResult<object>> DeleteType(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var row = LoadDeliveryTypeById(conn, id);
            if (row == null)
                return Ok(new ApiResult<object>(false, "Delivery type not found", null));

            int siblings = SqlMapper.Query<int>(conn, @"
                SELECT COUNT(*) FROM dbo.DeliveryType
                WHERE Deleted = 0 AND IsActive = 1 AND Id <> @Id AND IsDelivery = @IsDelivery",
                new { Id = id, row.IsDelivery }).First();

            if (siblings == 0)
                return Ok(new ApiResult<object>(false,
                    row.IsDelivery
                        ? "This is the last delivery type — the POS needs at least one."
                        : "This is the last pickup type — the POS needs at least one.",
                    null));

            SqlMapper.Execute(conn, @"
                UPDATE dbo.DeliveryType
                SET Deleted = 1, IsActive = 0, IsDefault = 0, UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @Id", new { Id = id });

            return Ok(new ApiResult<object>(true, null, new { Id = id }));
        }

        // ═════════════════════════════════════════════════════════════════
        // AREA PRICE LIST
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Every area with its price for the branch (priced or not) — the /management grid.</summary>
        [HttpGet("area-charges")]
        public ActionResult<ApiResult<List<AreaDeliveryChargeDto>>> GetAreaCharges(
            [FromQuery] int? branchId = null,
            [FromQuery] string? search = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var list = SqlMapper.Query<AreaDeliveryChargeDto>(conn, @"
                SELECT
                    adc.Id                AS Id,
                    adc.AreaId            AS AreaId,
                    ga.AREA_NAME1         AS AreaNameEn,
                    ga.AREA_NAME2         AS AreaNameAr,
                    ga.GOVERNORATE_ID     AS GovernorateId,
                    g.GOVERNORATE_NAME1   AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2   AS GovernorateNameAr,
                    adc.Charge            AS Charge,
                    adc.BranchId          AS BranchId,
                    b.BRANCH_NAME1        AS BranchName
                FROM dbo.AreaDeliveryCharge adc
                INNER JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = adc.AreaId
                INNER JOIN dbo.GOVERNORATE g       ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                LEFT  JOIN dbo.BRANCH b            ON b.BRANCH_ID = adc.BranchId
                WHERE (@BranchId IS NULL OR adc.BranchId = @BranchId)
                  AND (@Search IS NULL OR ga.AREA_NAME1 LIKE '%' + @Search + '%'
                                       OR ga.AREA_NAME2 LIKE '%' + @Search + '%'
                                       OR g.GOVERNORATE_NAME1 LIKE '%' + @Search + '%'
                                       OR g.GOVERNORATE_NAME2 LIKE '%' + @Search + '%')
                ORDER BY g.GOVERNORATE_NAME1, ga.AREA_NAME1",
                new { BranchId = branchId, Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() })
                .ToList();

            return Ok(new ApiResult<List<AreaDeliveryChargeDto>>(true, null, list));
        }

        /// <summary>Upsert one area price (unique per AreaId + BranchId).</summary>
        [HttpPost("area-charges")]
        public ActionResult<ApiResult<AreaDeliveryChargeDto>> SaveAreaCharge(
            [FromBody] SaveAreaDeliveryChargeRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<AreaDeliveryChargeDto>(false, "Request body is required", null));
            if (request.Charge < 0)
                return Ok(new ApiResult<AreaDeliveryChargeDto>(false, "Charge cannot be negative", null));

            using var conn = sqlConnections.NewByKey("Default");

            var area = SqlMapper.Query(conn,
                "SELECT AREA_ID FROM dbo.GOVERNORATE_AREA WHERE AREA_ID = @Id",
                new { Id = request.AreaId }).FirstOrDefault();
            if (area == null)
                return Ok(new ApiResult<AreaDeliveryChargeDto>(false, "Area not found", null));

            var branch = SqlMapper.Query(conn,
                "SELECT BRANCH_ID FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                new { Id = request.BranchId }).FirstOrDefault();
            if (branch == null)
                return Ok(new ApiResult<AreaDeliveryChargeDto>(false, "Branch not found", null));

            int id = UpsertAreaCharge(conn, request);
            var dto = LoadAreaChargeById(conn, id);
            return Ok(new ApiResult<AreaDeliveryChargeDto>(true, null, dto));
        }

        /// <summary>Save the whole price grid for a branch in one transaction.</summary>
        [HttpPost("area-charges/bulk")]
        public ActionResult<ApiResult<List<AreaDeliveryChargeDto>>> SaveAreaChargesBulk(
            [FromBody] BulkAreaDeliveryChargeRequest request)
        {
            if (request?.Charges == null || request.Charges.Count == 0)
                return Ok(new ApiResult<List<AreaDeliveryChargeDto>>(false, "No charges supplied", null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            try
            {
                using var uow = new UnitOfWork(conn);
                foreach (var c in request.Charges)
                {
                    if (c.Charge < 0)
                        return Ok(new ApiResult<List<AreaDeliveryChargeDto>>(
                            false, $"Area #{c.AreaId}: charge cannot be negative", null));

                    UpsertAreaCharge(uow.Connection,
                        new SaveAreaDeliveryChargeRequest(c.AreaId, c.Charge, request.BranchId));
                }
                uow.Commit();
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<List<AreaDeliveryChargeDto>>(
                    false, $"Failed to save charges: {ex.Message}", null));
            }

            return GetAreaCharges(request.BranchId, null);
        }

        [HttpPost("area-charges/delete/{id:int}")]
        public ActionResult<ApiResult<object>> DeleteAreaCharge(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int rows = SqlMapper.Execute(conn,
                "DELETE FROM dbo.AreaDeliveryCharge WHERE Id = @Id", new { Id = id });

            if (rows == 0)
                return Ok(new ApiResult<object>(false, "Charge not found", null));

            return Ok(new ApiResult<object>(true, null, new { Id = id }));
        }

        [HttpGet("governorates")]
        public ActionResult<ApiResult<List<GovernorateOptionDto>>> GetGovernorates()
        {
            using var conn = sqlConnections.NewByKey("Default");
            var list = SqlMapper.Query<GovernorateOptionDto>(conn, @"
                SELECT GOVERNORATE_ID AS GovernorateId, GOVERNORATE_NAME1 AS NameEn,
                       GOVERNORATE_NAME2 AS NameAr, COLOR_CODE AS ColorCode
                FROM dbo.GOVERNORATE
                ORDER BY GOVERNORATE_NAME1").ToList();
            return Ok(new ApiResult<List<GovernorateOptionDto>>(true, null, list));
        }

        /// <summary>Areas + their price in the branch. Drives the "unpriced areas" view in /management.</summary>
        [HttpGet("areas")]
        public ActionResult<ApiResult<List<AreaOptionDto>>> GetAreas(
            [FromQuery] int? branchId = null,
            [FromQuery] int? governorateId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            return Ok(new ApiResult<List<AreaOptionDto>>(
                true, null, LoadAreaOptions(conn, branchId, governorateId)));
        }

        // ═════════════════════════════════════════════════════════════════
        // CUSTOMER ADDRESSES
        // ═════════════════════════════════════════════════════════════════

        [HttpGet("addresses")]
        public ActionResult<ApiResult<List<DeliveryAddressDto>>> GetAddresses(
            [FromQuery] int customerId,
            [FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var customer = SqlMapper.Query(conn,
                "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<List<DeliveryAddressDto>>(false, "Customer not found", null));

            var list = LoadAddresses(conn, customerId, (Guid)customer.RefGuide, branchId);
            return Ok(new ApiResult<List<DeliveryAddressDto>>(true, null, list));
        }

        [HttpGet("addresses/count")]
        public ActionResult<ApiResult<int>> GetAddressCount([FromQuery] int customerId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            int count = SqlMapper.Query<int>(conn, @"
                SELECT COUNT(*)
                FROM dbo.CUSTOMER_ADRESS ca
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = ca.CUSTOMER_REF
                WHERE c.CUSTOMER_ID = @Id AND ISNULL(ca.IsDeleted, 0) = 0",
                new { Id = customerId }).First();

            return Ok(new ApiResult<int>(true, null, count));
        }

        [HttpPost("addresses")]
        public ActionResult<ApiResult<DeliveryAddressDto>> CreateAddress(
            [FromBody] SaveDeliveryAddressRequest request,
            [FromQuery] int? branchId = null)
        {
            if (request == null)
                return Ok(new ApiResult<DeliveryAddressDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var customer = SqlMapper.Query(conn,
                "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = request.CustomerId }).FirstOrDefault();
            if (customer == null)
                return Ok(new ApiResult<DeliveryAddressDto>(false, "Customer not found", null));

            Guid refGuide = (Guid)customer.RefGuide;

            var area = SqlMapper.Query(conn,
                "SELECT AREA_ID FROM dbo.GOVERNORATE_AREA WHERE AREA_ID = @Id",
                new { Id = request.AreaId }).FirstOrDefault();
            if (area == null)
                return Ok(new ApiResult<DeliveryAddressDto>(false, "Area not found", null));

            try
            {
                using var uow = new UnitOfWork(conn);

                int existingCount = SqlMapper.Query<int>(uow.Connection,
                    "SELECT COUNT(*) FROM dbo.CUSTOMER_ADRESS WHERE CUSTOMER_REF = @Ref AND ISNULL(IsDeleted,0) = 0",
                    new { Ref = refGuide }).First();

                // The first address is always the default — a customer is never left
                // with addresses but no default.
                bool makeDefault = request.MakeDefault || existingCount == 0;

                if (makeDefault)
                    SqlMapper.Execute(uow.Connection,
                        "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0 WHERE CUSTOMER_REF = @Ref",
                        new { Ref = refGuide });

                // CUSTOMER_ADRESS_ID is not an IDENTITY column in this schema.
                int newId = (SqlMapper.Query<int?>(uow.Connection,
                    "SELECT MAX(CUSTOMER_ADRESS_ID) FROM dbo.CUSTOMER_ADRESS").FirstOrDefault() ?? 0) + 1;

                SqlMapper.Execute(uow.Connection, @"
                    INSERT INTO dbo.CUSTOMER_ADRESS (
                        CUSTOMER_ADRESS_ID, CUSTOMER_REF, CREATED_BY, CREATED_DATE, AREA_ID,
                        BLOCK_NO, STREET, AVENUE, BUILDING_NO, FLAT_NO,
                        Floor, NOTE, Location, DEFAULT_ADDRESS, IsDeleted
                    )
                    VALUES (
                        @Id, @Ref, @CreatedBy, SYSUTCDATETIME(), @AreaId,
                        @BlockNo, @Street, @Avenue, @BuildingNo, @FlatNo,
                        @Floor, @Note, @Location, @IsDefault, 0
                    )",
                    new
                    {
                        Id = newId,
                        Ref = refGuide,
                        CreatedBy = CurrentUserId(),
                        request.AreaId,
                        BlockNo = Trim(request.BlockNo),
                        Street = Trim(request.Street),
                        Avenue = Trim(request.Avenue),
                        BuildingNo = Trim(request.BuildingNo),
                        FlatNo = Trim(request.FlatNo),
                        Floor = Trim(request.Floor),
                        Note = Trim(request.Note),
                        Location = Trim(request.Location),
                        IsDefault = makeDefault ? 1 : 0
                    });

                uow.Commit();

                var created = LoadAddresses(conn, request.CustomerId, refGuide, branchId)
                    .FirstOrDefault(a => a.AddressId == newId);
                return Ok(new ApiResult<DeliveryAddressDto>(true, null, created));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryAddressDto>(false, $"Failed to create address: {ex.Message}", null));
            }
        }

        [HttpPost("addresses/update/{addressId:int}")]
        public ActionResult<ApiResult<DeliveryAddressDto>> UpdateAddress(
            int addressId,
            [FromBody] SaveDeliveryAddressRequest request,
            [FromQuery] int? branchId = null)
        {
            if (request == null)
                return Ok(new ApiResult<DeliveryAddressDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var owner = SqlMapper.Query(conn, @"
                SELECT ca.CUSTOMER_REF AS RefGuide, c.CUSTOMER_ID AS CustomerId
                FROM dbo.CUSTOMER_ADRESS ca
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = ca.CUSTOMER_REF
                WHERE ca.CUSTOMER_ADRESS_ID = @Id AND ISNULL(ca.IsDeleted, 0) = 0",
                new { Id = addressId }).FirstOrDefault();

            if (owner == null)
                return Ok(new ApiResult<DeliveryAddressDto>(false, "Address not found", null));

            Guid refGuide = (Guid)owner.RefGuide;
            int customerId = (int)owner.CustomerId;

            if (request.CustomerId != 0 && request.CustomerId != customerId)
                return Ok(new ApiResult<DeliveryAddressDto>(
                    false, "Address does not belong to this customer", null));

            try
            {
                using var uow = new UnitOfWork(conn);

                if (request.MakeDefault)
                    SqlMapper.Execute(uow.Connection,
                        "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0 WHERE CUSTOMER_REF = @Ref",
                        new { Ref = refGuide });

                SqlMapper.Execute(uow.Connection, @"
                    UPDATE dbo.CUSTOMER_ADRESS SET
                        AREA_ID     = @AreaId,
                        BLOCK_NO    = @BlockNo,
                        STREET      = @Street,
                        AVENUE      = @Avenue,
                        BUILDING_NO = @BuildingNo,
                        FLAT_NO     = @FlatNo,
                        Floor       = @Floor,
                        NOTE        = @Note,
                        Location    = @Location
                        " + (request.MakeDefault ? ", DEFAULT_ADDRESS = 1" : "") + @"
                    WHERE CUSTOMER_ADRESS_ID = @Id",
                    new
                    {
                        Id = addressId,
                        request.AreaId,
                        BlockNo = Trim(request.BlockNo),
                        Street = Trim(request.Street),
                        Avenue = Trim(request.Avenue),
                        BuildingNo = Trim(request.BuildingNo),
                        FlatNo = Trim(request.FlatNo),
                        Floor = Trim(request.Floor),
                        Note = Trim(request.Note),
                        Location = Trim(request.Location)
                    });

                EnsureOneDefault(uow.Connection, refGuide);
                uow.Commit();

                var updated = LoadAddresses(conn, customerId, refGuide, branchId)
                    .FirstOrDefault(a => a.AddressId == addressId);
                return Ok(new ApiResult<DeliveryAddressDto>(true, null, updated));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<DeliveryAddressDto>(false, $"Failed to update address: {ex.Message}", null));
            }
        }

        /// <summary>
        /// Soft delete. Deleting the default promotes the next-newest address, so the
        /// customer never ends up with addresses but no default.
        /// </summary>
        [HttpPost("addresses/delete/{addressId:int}")]
        public ActionResult<ApiResult<object>> DeleteAddress(int addressId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var addr = SqlMapper.Query(conn, @"
                SELECT CUSTOMER_REF AS RefGuide, DEFAULT_ADDRESS AS IsDefault
                FROM dbo.CUSTOMER_ADRESS
                WHERE CUSTOMER_ADRESS_ID = @Id AND ISNULL(IsDeleted, 0) = 0",
                new { Id = addressId }).FirstOrDefault();

            if (addr == null)
                return Ok(new ApiResult<object>(false, "Address not found", null));

            Guid refGuide = (Guid)addr.RefGuide;

            try
            {
                using var uow = new UnitOfWork(conn);

                SqlMapper.Execute(uow.Connection, @"
                    UPDATE dbo.CUSTOMER_ADRESS
                    SET IsDeleted = 1, DEFAULT_ADDRESS = 0
                    WHERE CUSTOMER_ADRESS_ID = @Id", new { Id = addressId });

                EnsureOneDefault(uow.Connection, refGuide);
                uow.Commit();

                return Ok(new ApiResult<object>(true, null, new { AddressId = addressId }));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<object>(false, $"Failed to delete address: {ex.Message}", null));
            }
        }

        [HttpPost("addresses/{addressId:int}/make-default")]
        public ActionResult<ApiResult<List<DeliveryAddressDto>>> MakeDefaultAddress(
            int addressId, [FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            var owner = SqlMapper.Query(conn, @"
                SELECT ca.CUSTOMER_REF AS RefGuide, c.CUSTOMER_ID AS CustomerId
                FROM dbo.CUSTOMER_ADRESS ca
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = ca.CUSTOMER_REF
                WHERE ca.CUSTOMER_ADRESS_ID = @Id AND ISNULL(ca.IsDeleted, 0) = 0",
                new { Id = addressId }).FirstOrDefault();

            if (owner == null)
                return Ok(new ApiResult<List<DeliveryAddressDto>>(false, "Address not found", null));

            Guid refGuide = (Guid)owner.RefGuide;
            int customerId = (int)owner.CustomerId;

            try
            {
                using var uow = new UnitOfWork(conn);

                SqlMapper.Execute(uow.Connection,
                    "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0 WHERE CUSTOMER_REF = @Ref",
                    new { Ref = refGuide });

                SqlMapper.Execute(uow.Connection,
                    "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 1 WHERE CUSTOMER_ADRESS_ID = @Id",
                    new { Id = addressId });

                uow.Commit();
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<List<DeliveryAddressDto>>(
                    false, $"Failed to set default address: {ex.Message}", null));
            }

            return Ok(new ApiResult<List<DeliveryAddressDto>>(
                true, null, LoadAddresses(conn, customerId, refGuide, branchId)));
        }

        // ═════════════════════════════════════════════════════════════════
        // Shared resolution logic (also used by PosApiController)
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// The single source of truth for "what does delivery cost for this area".
        /// Returns (0, false) when the area has no price row — callers decide whether
        /// that's a hard error (checkout) or a warning (UI).
        /// </summary>
        public static (decimal Charge, bool HasCharge) ResolveAreaCharge(
            IDbConnection conn, int areaId, int branchId)
        {
            var row = SqlMapper.Query<decimal?>(conn, @"
                SELECT TOP 1 Charge
                FROM dbo.AreaDeliveryCharge
                WHERE AreaId = @AreaId AND BranchId = @BranchId",
                new { AreaId = areaId, BranchId = branchId }).FirstOrDefault();

            return row.HasValue ? (row.Value, true) : (0m, false);
        }

        /// <summary>
        /// Active drivers for a branch, optionally filtered by governorate. A NULL
        /// BRANCH_ID driver is treated as available to every branch (matches the
        /// Home Service query).
        /// </summary>
        public static List<DeliveryDriverDto> LoadDrivers(
            IDbConnection conn, int branchId, int? governorateId)
        {
            return SqlMapper.Query<DeliveryDriverDto>(conn, @"
                SELECT
                    d.DRIVER_ID         AS DriverId,
                    d.DRIVER_NAME       AS DriverName,
                    d.DRIVER_NAME_AR    AS DriverNameAr,
                    d.DRIVER_PHONE      AS DriverPhone,
                    d.DRIVER_ADRESS     AS DriverAddress,
                    d.BRANCH_ID         AS BranchId,
                    d.GOVERNORATE_ID    AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                    CAST(CASE WHEN d.IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive
                FROM dbo.DRIVER d
                INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE d.IS_ACTIVE = 1
                  AND (@BranchId IS NULL OR d.BRANCH_ID = @BranchId OR d.BRANCH_ID IS NULL)
                  AND (@GovernorateId IS NULL OR d.GOVERNORATE_ID = @GovernorateId)
                ORDER BY d.GOVERNORATE_ID, d.DRIVER_NAME",
                new { BranchId = branchId, GovernorateId = governorateId }).ToList();
        }

        /// <summary>Validate a driver + confirm it serves the given governorate. Null on any failure.</summary>
        private static string? ValidateDriver(SaveDeliveryDriverRequest? r)
        {
            if (r == null) return "Request body is required";
            if (string.IsNullOrWhiteSpace(r.DriverName)) return "Driver name is required";
            if (string.IsNullOrWhiteSpace(r.DriverPhone)) return "Driver phone is required";
            if (r.BranchId <= 0) return "Branch is required";
            if (r.GovernorateId <= 0) return "Governorate is required";
            return null;
        }

        private static bool FkExists(IDbConnection conn, string table, string col, int id)
        {
            return SqlMapper.Query<int>(conn,
                $"SELECT COUNT(*) FROM {table} WHERE {col} = @Id", new { Id = id }).First() > 0;
        }

        private static DeliveryDriverDto? LoadDriverById(IDbConnection conn, int id)
        {
            return SqlMapper.Query<DeliveryDriverDto>(conn, @"
                SELECT
                    d.DRIVER_ID         AS DriverId,
                    d.DRIVER_NAME       AS DriverName,
                    d.DRIVER_NAME_AR    AS DriverNameAr,
                    d.DRIVER_PHONE      AS DriverPhone,
                    d.DRIVER_ADRESS     AS DriverAddress,
                    d.BRANCH_ID         AS BranchId,
                    d.GOVERNORATE_ID    AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                    CAST(CASE WHEN d.IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive
                FROM dbo.DRIVER d
                INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE d.DRIVER_ID = @Id", new { Id = id }).FirstOrDefault();
        }

        public static DeliveryDriverDto? ResolveDriver(
            IDbConnection conn, int driverId, int branchId, int governorateId)
        {
            var d = SqlMapper.Query<DeliveryDriverDto>(conn, @"
                SELECT
                    d.DRIVER_ID         AS DriverId,
                    d.DRIVER_NAME       AS DriverName,
                    d.DRIVER_NAME_AR    AS DriverNameAr,
                    d.DRIVER_PHONE      AS DriverPhone,
                    d.DRIVER_ADRESS     AS DriverAddress,
                    d.BRANCH_ID         AS BranchId,
                    d.GOVERNORATE_ID    AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                    CAST(1 AS BIT)      AS IsActive
                FROM dbo.DRIVER d
                INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE d.DRIVER_ID = @Id AND d.IS_ACTIVE = 1
                  AND (d.BRANCH_ID = @BranchId OR d.BRANCH_ID IS NULL)",
                new { Id = driverId, BranchId = branchId }).FirstOrDefault();

            if (d == null || d.GovernorateId != governorateId) return null;
            return d;
        }

        public static List<DeliveryTypeDto> LoadDeliveryTypes(
            IDbConnection conn, int? branchId, bool includeInactive)
        {
            var rows = SqlMapper.Query<DeliveryTypeDto>(conn, @"
                SELECT
                    Id, Code, NameEn, NameAr, IsDelivery, IsDefault, IsActive,
                    Ordering, ColorCode, Icon, ChargeOverride, Notes, BranchId
                FROM dbo.DeliveryType
                WHERE Deleted = 0
                  AND (@IncludeInactive = 1 OR IsActive = 1)
                  AND (BranchId IS NULL OR @BranchId IS NULL OR BranchId = @BranchId)
                ORDER BY Ordering, NameEn",
                new { BranchId = branchId, IncludeInactive = includeInactive ? 1 : 0 }).ToList();

            // A branch-specific row shadows the global row with the same code.
            return rows
                .GroupBy(r => r.Code)
                .Select(g => g.OrderBy(r => r.BranchId == null ? 1 : 0).First())
                .OrderBy(r => r.Ordering).ThenBy(r => r.NameEn)
                .ToList();
        }

        /// <summary>Resolve the address a checkout points at (with its area/governorate names).</summary>
        public static AddressResolveDto? ResolveAddress(IDbConnection conn, int addressId)
        {
            return SqlMapper.Query<AddressResolveDto>(conn, @"
                SELECT
                    ca.CUSTOMER_ADRESS_ID AS CustomerAddressId,
                    ca.CUSTOMER_REF       AS CustomerRef,
                    ca.BLOCK_NO           AS BlockNo,
                    ca.STREET             AS Street,
                    ca.AVENUE             AS Avenue,
                    ca.BUILDING_NO        AS BuildingNo,
                    ca.FLAT_NO            AS FlatNo,
                    ca.Floor              AS Floor,
                    ca.NOTE               AS Note,
                    ca.Location           AS Location,
                    ca.AREA_ID            AS AreaId,
                    ga.AREA_NAME1         AS AreaNameEn,
                    ga.AREA_NAME2         AS AreaNameAr,
                    ga.GOVERNORATE_ID     AS GovernorateId,
                    g.GOVERNORATE_NAME1   AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2   AS GovernorateNameAr
                FROM dbo.CUSTOMER_ADRESS ca
                INNER JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                INNER JOIN dbo.GOVERNORATE g       ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                WHERE ca.CUSTOMER_ADRESS_ID = @Id AND ISNULL(ca.IsDeleted, 0) = 0",
                new { Id = addressId }).FirstOrDefault();
        }

        public static InvoiceDeliveryDto? LoadInvoiceDelivery(IDbConnection conn, int invoiceId)
        {
            return SqlMapper.Query<InvoiceDeliveryDto>(conn, @"
                SELECT
                    DeliveryTypeId, DeliveryTypeCode, DeliveryTypeNameEn, DeliveryTypeNameAr,
                    IsDelivery, DriverId, DriverName, DriverNameAr, DriverPhone,
                    CustomerAddressId, AreaId, AreaNameEn, AreaNameAr,
                    GovernorateId, GovernorateNameEn, GovernorateNameAr,
                    AddressBlock, AddressStreet, AddressAvenue, AddressBuilding,
                    AddressFlat, AddressFloor, AddressNote, AddressLocation,
                    DeliveryCharge, HasDeliveryDate, DeliveryDate, Notes
                FROM dbo.InvoiceDelivery
                WHERE InvoiceId = @Id",
                new { Id = invoiceId }).FirstOrDefault();
        }

        #region Private helpers

        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string? ValidateType(SaveDeliveryTypeRequest? r)
        {
            if (r == null) return "Request body is required";
            if (string.IsNullOrWhiteSpace(r.Code)) return "Code is required";
            if (string.IsNullOrWhiteSpace(r.NameEn)) return "English name is required";
            if (string.IsNullOrWhiteSpace(r.NameAr)) return "Arabic name is required";
            if (r.ChargeOverride.HasValue && r.ChargeOverride.Value < 0) return "Charge override cannot be negative";
            if (!r.IsDelivery && r.ChargeOverride.HasValue && r.ChargeOverride.Value > 0)
                return "A pickup type cannot carry a delivery charge";
            return null;
        }

        private static void ClearDefaultType(IDbConnection conn, bool isDelivery, int? branchId, int? exceptId)
        {
            SqlMapper.Execute(conn, @"
                UPDATE dbo.DeliveryType
                SET IsDefault = 0
                WHERE Deleted = 0
                  AND IsDelivery = @IsDelivery
                  AND ((@BranchId IS NULL AND BranchId IS NULL) OR BranchId = @BranchId)
                  AND (@ExceptId IS NULL OR Id <> @ExceptId)",
                new { IsDelivery = isDelivery, BranchId = branchId, ExceptId = exceptId });
        }

        private static DeliveryTypeDto? LoadDeliveryTypeById(IDbConnection conn, int id)
        {
            return SqlMapper.Query<DeliveryTypeDto>(conn, @"
                SELECT Id, Code, NameEn, NameAr, IsDelivery, IsDefault, IsActive,
                       Ordering, ColorCode, Icon, ChargeOverride, Notes, BranchId
                FROM dbo.DeliveryType
                WHERE Id = @Id AND Deleted = 0", new { Id = id }).FirstOrDefault();
        }

        private static int UpsertAreaCharge(IDbConnection conn, SaveAreaDeliveryChargeRequest req)
        {
            var existing = SqlMapper.Query<int?>(conn,
                "SELECT Id FROM dbo.AreaDeliveryCharge WHERE AreaId = @AreaId AND BranchId = @BranchId",
                new { req.AreaId, req.BranchId }).FirstOrDefault();

            if (existing.HasValue)
            {
                SqlMapper.Execute(conn,
                    "UPDATE dbo.AreaDeliveryCharge SET Charge = @Charge WHERE Id = @Id",
                    new { Id = existing.Value, req.Charge });
                return existing.Value;
            }

            return SqlMapper.Query<int>(conn, @"
                INSERT INTO dbo.AreaDeliveryCharge (AreaId, Charge, BranchId)
                OUTPUT INSERTED.Id
                VALUES (@AreaId, @Charge, @BranchId)",
                new { req.AreaId, req.Charge, req.BranchId }).First();
        }

        private static AreaDeliveryChargeDto? LoadAreaChargeById(IDbConnection conn, int id)
        {
            return SqlMapper.Query<AreaDeliveryChargeDto>(conn, @"
                SELECT
                    adc.Id, adc.AreaId,
                    ga.AREA_NAME1       AS AreaNameEn,
                    ga.AREA_NAME2       AS AreaNameAr,
                    ga.GOVERNORATE_ID   AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                    adc.Charge, adc.BranchId,
                    b.BRANCH_NAME1      AS BranchName
                FROM dbo.AreaDeliveryCharge adc
                INNER JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = adc.AreaId
                INNER JOIN dbo.GOVERNORATE g       ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                LEFT  JOIN dbo.BRANCH b            ON b.BRANCH_ID = adc.BranchId
                WHERE adc.Id = @Id", new { Id = id }).FirstOrDefault();
        }

        private static List<AreaOptionDto> LoadAreaOptions(IDbConnection conn, int? branchId, int? governorateId)
        {
            return SqlMapper.Query<AreaOptionDto>(conn, @"
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
                WHERE (@GovernorateId IS NULL OR a.GOVERNORATE_ID = @GovernorateId)
                ORDER BY g.GOVERNORATE_NAME1, a.AREA_NAME1",
                new { BranchId = branchId, GovernorateId = governorateId }).ToList();
        }

        private static List<DeliveryAddressDto> LoadAddresses(
            IDbConnection conn, int customerId, Guid refGuide, int? branchId)
        {
            return SqlMapper.Query<DeliveryAddressDto>(conn, @"
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
                    CAST(CASE WHEN EXISTS (
                            SELECT 1 FROM dbo.InvoiceDelivery idl
                            WHERE idl.CustomerAddressId = ca.CUSTOMER_ADRESS_ID)
                         OR EXISTS (
                            SELECT 1 FROM dbo.AppointmentHomeService hs
                            WHERE hs.CustomerAddressId = ca.CUSTOMER_ADRESS_ID)
                         THEN 1 ELSE 0 END AS BIT) AS InUse
                FROM dbo.CUSTOMER_ADRESS ca
                LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                LEFT JOIN dbo.GOVERNORATE g       ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                LEFT JOIN dbo.AreaDeliveryCharge adc
                       ON adc.AreaId = ca.AREA_ID AND adc.BranchId = @BranchId
                WHERE ca.CUSTOMER_REF = @Ref AND ISNULL(ca.IsDeleted, 0) = 0
                ORDER BY ca.DEFAULT_ADDRESS DESC, ca.CREATED_DATE DESC",
                new { Ref = refGuide, BranchId = branchId, CustomerId = customerId }).ToList();
        }

        /// <summary>Guarantee exactly one default while the customer still has addresses.</summary>
        private static void EnsureOneDefault(IDbConnection conn, Guid refGuide)
        {
            int defaults = SqlMapper.Query<int>(conn, @"
                SELECT COUNT(*) FROM dbo.CUSTOMER_ADRESS
                WHERE CUSTOMER_REF = @Ref AND ISNULL(IsDeleted,0) = 0 AND DEFAULT_ADDRESS = 1",
                new { Ref = refGuide }).First();

            if (defaults == 1) return;

            if (defaults > 1)
            {
                // Collapse to the newest one.
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0
                    WHERE CUSTOMER_REF = @Ref AND ISNULL(IsDeleted,0) = 0", new { Ref = refGuide });
            }

            SqlMapper.Execute(conn, @"
                UPDATE dbo.CUSTOMER_ADRESS
                SET DEFAULT_ADDRESS = 1
                WHERE CUSTOMER_ADRESS_ID = (
                    SELECT TOP 1 CUSTOMER_ADRESS_ID
                    FROM dbo.CUSTOMER_ADRESS
                    WHERE CUSTOMER_REF = @Ref AND ISNULL(IsDeleted,0) = 0
                    ORDER BY CREATED_DATE DESC, CUSTOMER_ADRESS_ID DESC)",
                new { Ref = refGuide });
        }

        #endregion
    }
}
