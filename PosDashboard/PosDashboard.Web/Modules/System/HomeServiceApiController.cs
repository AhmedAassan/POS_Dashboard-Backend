using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using static PosDashboard.Web.Modules.System.Models.HomeServiceDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/home-service")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class HomeServiceApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public HomeServiceApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/home-service/context?customerId=1&branchId=1
        // One-shot load: addresses + drivers + governorates + areas
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("context")]
        public ActionResult<ApiResult<HomeServiceContextDto>> GetContext(
            [FromQuery] int customerId,
            [FromQuery] int branchId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            // 1) Resolve customer ref guide
            var customer = conn.Query<dynamic>(
                @"SELECT CUSTOMER_REF_GUIDE AS RefGuide
                  FROM dbo.CUSTOMER
                  WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<HomeServiceContextDto>(
                    false, "Customer not found", null));

            Guid refGuide = (Guid)customer.RefGuide;

            // 2) Load addresses for this customer
            var addresses = LoadAddressesByRef(conn, refGuide);

            var defaultAddress = addresses.FirstOrDefault(a => a.IsDefault)
                                 ?? addresses.FirstOrDefault();

            // 3) Load all governorates
            var governorates = conn.Query<GovernorateDto>(@"
                SELECT
                    GOVERNORATE_ID  AS GovernorateId,
                    GOVERNORATE_NAME1 AS NameEn,
                    GOVERNORATE_NAME2 AS NameAr,
                    COLOR_CODE      AS ColorCode
                FROM dbo.GOVERNORATE
                ORDER BY GOVERNORATE_ID")
                .ToList();

            // 4) Load all areas
            var areas = conn.Query<AreaDto>(@"
                SELECT
                    a.AREA_ID          AS AreaId,
                    a.AREA_NAME1       AS NameEn,
                    a.AREA_NAME2       AS NameAr,
                    a.GOVERNORATE_ID   AS GovernorateId,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAr
                FROM dbo.GOVERNORATE_AREA a
                INNER JOIN dbo.GOVERNORATE g
                    ON g.GOVERNORATE_ID = a.GOVERNORATE_ID
                ORDER BY a.GOVERNORATE_ID, a.AREA_ID")
                .ToList();

            // 5) Load drivers (all active for this branch)
            int? defaultGovernorateId = defaultAddress?.GovernorateId;

            var allDrivers = LoadDrivers(conn, branchId, null);
            var driversForContext = allDrivers.Select(d => d with
            {
                IsPreferred = defaultGovernorateId.HasValue
                    && d.GovernorateId == defaultGovernorateId.Value
            }).ToList();

            DriverDto? preferredDriver = defaultGovernorateId.HasValue
                ? driversForContext.FirstOrDefault(d =>
                    d.GovernorateId == defaultGovernorateId.Value && d.IsActive)
                : null;

            var ctx = new HomeServiceContextDto(
                Addresses: addresses,
                DefaultAddress: defaultAddress,
                Drivers: driversForContext,
                PreferredDriver: preferredDriver,
                Governorates: governorates,
                Areas: areas
            );

            return Ok(new ApiResult<HomeServiceContextDto>(true, null, ctx));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/home-service/addresses?customerId=1
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("addresses")]
        public ActionResult<ApiResult<List<CustomerAddressDto>>> GetAddresses(
            [FromQuery] int customerId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var customer = conn.Query<dynamic>(
                "SELECT CUSTOMER_REF_GUIDE AS RefGuide FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<List<CustomerAddressDto>>(
                    false, "Customer not found", null));

            var addresses = LoadAddressesByRef(conn, (Guid)customer.RefGuide);
            return Ok(new ApiResult<List<CustomerAddressDto>>(true, null, addresses));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/home-service/addresses
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("addresses")]
        public ActionResult<ApiResult<CustomerAddressDto>> CreateAddress(
            [FromBody] CreateCustomerAddressRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<CustomerAddressDto>(
                    false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Validate customer + get ref guide
            var customer = conn.Query<dynamic>(
                @"SELECT CUSTOMER_REF_GUIDE AS RefGuide
                  FROM dbo.CUSTOMER
                  WHERE CUSTOMER_ID = @Id",
                new { Id = request.CustomerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<CustomerAddressDto>(
                    false, "Customer not found", null));

            Guid refGuide = (Guid)customer.RefGuide;

            // Validate area
            var area = conn.Query<dynamic>(
                @"SELECT a.AREA_ID, a.AREA_NAME1, a.AREA_NAME2,
                         a.GOVERNORATE_ID,
                         g.GOVERNORATE_NAME1, g.GOVERNORATE_NAME2
                  FROM dbo.GOVERNORATE_AREA a
                  INNER JOIN dbo.GOVERNORATE g
                      ON g.GOVERNORATE_ID = a.GOVERNORATE_ID
                  WHERE a.AREA_ID = @AreaId",
                new { AreaId = request.AreaId }).FirstOrDefault();

            if (area == null)
                return Ok(new ApiResult<CustomerAddressDto>(
                    false, "Area not found", null));

            // If MakeDefault → clear existing default
            if (request.MakeDefault)
            {
                conn.Execute(
                    @"UPDATE dbo.CUSTOMER_ADRESS
                      SET DEFAULT_ADDRESS = 0
                      WHERE CUSTOMER_REF = @Ref",
                    new { Ref = refGuide });
            }

            // Check if this is first address (auto-make default)
            var existingCount = conn.Query<int>(
                "SELECT COUNT(*) FROM dbo.CUSTOMER_ADRESS WHERE CUSTOMER_REF = @Ref",
                new { Ref = refGuide }).FirstOrDefault();

            bool isDefault = request.MakeDefault || existingCount == 0;

            // Generate next ID
            var maxId = conn.Query<int?>(
                "SELECT MAX(CUSTOMER_ADRESS_ID) FROM dbo.CUSTOMER_ADRESS")
                .FirstOrDefault();
            int newId = (maxId ?? 0) + 1;

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!int.TryParse(userIdClaim, out var createdBy))
            {
                return Ok(new ApiResult<CustomerAddressDto>(
                    false, "Could not resolve current user id", null));
            }

            conn.Execute(@"
                INSERT INTO dbo.CUSTOMER_ADRESS (
                    CUSTOMER_ADRESS_ID, CUSTOMER_REF, CREATED_BY, CREATED_DATE, AREA_ID,
                    BLOCK_NO, STREET, AVENUE, BUILDING_NO, FLAT_NO,
                    Floor, NOTE, Location, DEFAULT_ADDRESS
                )
                VALUES (
                    @Id, @Ref, @CreatedBy, SYSUTCDATETIME(), @AreaId,
                    @BlockNo, @Street, @Avenue, @BuildingNo, @FlatNo,
                    @Floor, @Note, @Location, @IsDefault
                )",
                new
                {
                    Id = newId,
                    Ref = refGuide,
                    CreatedBy = createdBy,
                    AreaId = request.AreaId,
                    BlockNo = request.BlockNo,
                    Street = request.Street,
                    Avenue = request.Avenue,
                    BuildingNo = request.BuildingNo,
                    FlatNo = request.FlatNo,
                    Floor = request.Floor,
                    Note = request.Note,
                    Location = request.Location,
                    IsDefault = isDefault ? 1 : 0
                });

            var created = LoadAddressById(conn, newId);
            return Ok(new ApiResult<CustomerAddressDto>(true, null, created));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/home-service/drivers?governorateId=1&branchId=1
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("drivers")]
        public ActionResult<ApiResult<List<DriverDto>>> GetDrivers(
            [FromQuery] int? governorateId,
            [FromQuery] int branchId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var drivers = LoadDrivers(conn, branchId, governorateId);
            return Ok(new ApiResult<List<DriverDto>>(true, null, drivers));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/home-service/appointment/{appointmentId}
        // Load existing home-service snapshot for edit flow
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("appointment/{appointmentId:int}")]
        public ActionResult<ApiResult<HomeServiceDto>> GetForAppointment(int appointmentId)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var hs = LoadHomeService(conn, appointmentId);

            if (hs == null)
                return Ok(new ApiResult<HomeServiceDto>(
                    false, "No home-service data for this appointment", null));

            return Ok(new ApiResult<HomeServiceDto>(true, null, hs));
        }

        // ─────────────────────────────────────────────────────────────────
        // PUT /api/home-service/appointment/{appointmentId}
        // Create or update home-service snapshot
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("appointment/{appointmentId:int}")]
        public ActionResult<ApiResult<HomeServiceDto>> SaveForAppointment(
            int appointmentId,
            [FromBody] SaveHomeServiceRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<HomeServiceDto>(
                    false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Validate appointment exists
            var apt = conn.Query<dynamic>(@"
                    SELECT Id, CustomerId
                    FROM dbo.AppointmentData
                    WHERE Id = @Id",
                    new { Id = appointmentId }).FirstOrDefault();
            if (apt == null)
                return Ok(new ApiResult<HomeServiceDto>(
                    false, "Appointment not found", null));
            if (apt.CustomerId == null)
            {
                return Ok(new ApiResult<HomeServiceDto>(
                    false,
                    "Appointment customer is missing",
                    null));
            }
            var customer = conn.Query<dynamic>(@"
                SELECT CUSTOMER_REF_GUIDE AS RefGuide
                FROM dbo.CUSTOMER
                WHERE CUSTOMER_ID = @CustomerId",
                new { CustomerId = (int)apt.CustomerId }).FirstOrDefault();

                        if (customer == null)
                        {
                            return Ok(new ApiResult<HomeServiceDto>(
                                false,
                                "Appointment customer not found",
                                null));
                        }

            Guid appointmentCustomerRef = (Guid)customer.RefGuide;

            // Validate address
            var addr = conn.Query<AddressLookupDto>(@"
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
            INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
            WHERE ca.CUSTOMER_ADRESS_ID = @Id",
            new { Id = request.CustomerAddressId }).FirstOrDefault();

            if (addr == null)
                return Ok(new ApiResult<HomeServiceDto>(
                    false, "Customer address not found", null));

            if (addr.CustomerRef != appointmentCustomerRef)
            {
                return Ok(new ApiResult<HomeServiceDto>(
                    false,
                    "Selected address does not belong to the appointment customer",
                    null));
            }
            // Validate driver
            var driver = conn.Query<DriverLookupDto>(@"
            SELECT
                DRIVER_ID      AS DriverId,
                DRIVER_NAME    AS DriverName,
                DRIVER_NAME_AR AS DriverNameAr,
                DRIVER_PHONE   AS DriverPhone,
                GOVERNORATE_ID AS GovernorateId,
                CAST(CASE WHEN IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive
            FROM dbo.DRIVER
            WHERE DRIVER_ID = @Id AND IS_ACTIVE = 1",
            new { Id = request.DriverId }).FirstOrDefault();

            if (driver == null)
                return Ok(new ApiResult<HomeServiceDto>(
                    false, "Driver not found or not active", null));
            if (driver.GovernorateId != addr.GovernorateId)
            {
                return Ok(new ApiResult<HomeServiceDto>(
                    false,
                    "Driver does not belong to the selected address governorate",
                    null));
            }

            // Upsert
            var existing = conn.Query<dynamic>(
                "SELECT Id FROM dbo.AppointmentHomeService WHERE AppointmentId = @Id",
                new { Id = appointmentId }).FirstOrDefault();

            if (existing != null)
            {
                conn.Execute(@"
                    UPDATE dbo.AppointmentHomeService SET
                        CustomerAddressId = @AddressId,
                        AreaId            = @AreaId,
                        GovernorateId     = @GovId,
                        DriverId          = @DriverId,
                        AddressBlock      = @Block,
                        AddressStreet     = @Street,
                        AddressAvenue     = @Avenue,
                        AddressBuilding   = @Building,
                        AddressFlat       = @Flat,
                        AddressFloor      = @Floor,
                        AddressNote       = @Note,
                        AddressLocation   = @Location,
                        AreaNameEn        = @AreaNameEn,
                        AreaNameAr        = @AreaNameAr,
                        GovernorateNameEn = @GovNameEn,
                        GovernorateNameAr = @GovNameAr,
                        DriverName        = @DriverName,
                        DriverPhone       = @DriverPhone,
                        UpdatedAt         = SYSUTCDATETIME()
                    WHERE AppointmentId = @AppointmentId",
                    BuildUpsertParams(appointmentId, request, addr, driver));
            }
            else
            {
                conn.Execute(@"
                    INSERT INTO dbo.AppointmentHomeService (
                        AppointmentId, CustomerAddressId, AreaId, GovernorateId, DriverId,
                        AddressBlock, AddressStreet, AddressAvenue, AddressBuilding,
                        AddressFlat, AddressFloor, AddressNote, AddressLocation,
                        AreaNameEn, AreaNameAr, GovernorateNameEn, GovernorateNameAr,
                        DriverName, DriverPhone, CreatedAt
                    )
                    VALUES (
                        @AppointmentId, @AddressId, @AreaId, @GovId, @DriverId,
                        @Block, @Street, @Avenue, @Building,
                        @Flat, @Floor, @Note, @Location,
                        @AreaNameEn, @AreaNameAr, @GovNameEn, @GovNameAr,
                        @DriverName, @DriverPhone, SYSUTCDATETIME()
                    )",
                    BuildUpsertParams(appointmentId, request, addr, driver));
            }

            var hs = LoadHomeService(conn, appointmentId);
            return Ok(new ApiResult<HomeServiceDto>(true, null, hs));
        }

        // ─────────────────────────────────────────────────────────────────
        // DELETE /api/home-service/appointment/{appointmentId}
        // Called when switching service type HOME -> SALON
        // ─────────────────────────────────────────────────────────────────
        [HttpDelete("appointment/{appointmentId:int}")]
        public ActionResult<ApiResult<object>> DeleteForAppointment(int appointmentId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            conn.Execute(
                "DELETE FROM dbo.AppointmentHomeService WHERE AppointmentId = @Id",
                new { Id = appointmentId });

            return Ok(new ApiResult<object>(true, null, new { AppointmentId = appointmentId }));
        }



        [HttpPost("drivers")]
        public ActionResult<ApiResult<DriverDto>> CreateDriver([FromBody] CreateDriverRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<DriverDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var gov = conn.Query<dynamic>(@"
        SELECT GOVERNORATE_ID, GOVERNORATE_NAME1, GOVERNORATE_NAME2
        FROM dbo.GOVERNORATE
        WHERE GOVERNORATE_ID = @Id",
                new { Id = request.GovernorateId }).FirstOrDefault();

            if (gov == null)
                return Ok(new ApiResult<DriverDto>(false, "Governorate not found", null));

            var maxId = conn.Query<int?>("SELECT MAX(DRIVER_ID) FROM dbo.DRIVER").FirstOrDefault();
            int newId = (maxId ?? 0) + 1;

            conn.Execute(@"
        INSERT INTO dbo.DRIVER (
            DRIVER_ID,
            DRIVER_NAME,
            DRIVER_ADRESS,
            DRIVER_PHONE,
            IS_ACTIVE,
            DRIVER_NAME_AR,
            BRANCH_ID,
            GOVERNORATE_ID
        )
        VALUES (
            @DriverId,
            @DriverName,
            @DriverAddress,
            @DriverPhone,
            @IsActive,
            @DriverNameAr,
            @BranchId,
            @GovernorateId
        )",
                new
                {
                    DriverId = newId,
                    DriverName = request.DriverName,
                    DriverAddress = request.DriverAddress ?? "",
                    DriverPhone = request.DriverPhone,
                    IsActive = request.IsActive ? 1 : 0,
                    DriverNameAr = request.DriverNameAr ?? request.DriverName,
                    BranchId = request.BranchId,
                    GovernorateId = request.GovernorateId
                });

            var created = conn.Query<DriverDto>(@"
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
            CAST(CASE WHEN d.IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive,
            CAST(1 AS BIT) AS IsPreferred
        FROM dbo.DRIVER d
        INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
        WHERE d.DRIVER_ID = @Id",
                new { Id = newId }).FirstOrDefault();

            return Ok(new ApiResult<DriverDto>(true, null, created));
        }

        #region Private Helpers

        private List<CustomerAddressDto> LoadAddressesByRef(IDbConnection conn, Guid refGuide)
        {
            return conn.Query<CustomerAddressDto>(@"
                SELECT
                    ca.CUSTOMER_ADRESS_ID   AS AddressId,
                    ca.CUSTOMER_REF         AS CustomerRef,
                    ca.AREA_ID              AS AreaId,
                    ga.AREA_NAME1           AS AreaNameEn,
                    ga.AREA_NAME2           AS AreaNameAr,
                    ga.GOVERNORATE_ID       AS GovernorateId,
                    g.GOVERNORATE_NAME1     AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2     AS GovernorateNameAr,
                    ca.BLOCK_NO             AS BlockNo,
                    ca.STREET               AS Street,
                    ca.AVENUE               AS Avenue,
                    ca.BUILDING_NO          AS BuildingNo,
                    ca.FLAT_NO              AS FlatNo,
                    ca.Floor                AS Floor,
                    ca.NOTE                 AS Note,
                    ca.Location             AS Location,
                    CAST(CASE WHEN ca.DEFAULT_ADDRESS = 1 THEN 1 ELSE 0 END AS BIT)
                                            AS IsDefault
                FROM dbo.CUSTOMER_ADRESS ca
                LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                WHERE ca.CUSTOMER_REF = @Ref
                ORDER BY ca.DEFAULT_ADDRESS DESC, ca.CREATED_DATE DESC",
                new { Ref = refGuide }).ToList();
        }

        private CustomerAddressDto? LoadAddressById(IDbConnection conn, int addressId)
        {
            return conn.Query<CustomerAddressDto>(@"
                SELECT
                    ca.CUSTOMER_ADRESS_ID   AS AddressId,
                    ca.CUSTOMER_REF         AS CustomerRef,
                    ca.AREA_ID              AS AreaId,
                    ga.AREA_NAME1           AS AreaNameEn,
                    ga.AREA_NAME2           AS AreaNameAr,
                    ga.GOVERNORATE_ID       AS GovernorateId,
                    g.GOVERNORATE_NAME1     AS GovernorateNameEn,
                    g.GOVERNORATE_NAME2     AS GovernorateNameAr,
                    ca.BLOCK_NO             AS BlockNo,
                    ca.STREET               AS Street,
                    ca.AVENUE               AS Avenue,
                    ca.BUILDING_NO          AS BuildingNo,
                    ca.FLAT_NO              AS FlatNo,
                    ca.Floor                AS Floor,
                    ca.NOTE                 AS Note,
                    ca.Location             AS Location,
                    CAST(CASE WHEN ca.DEFAULT_ADDRESS = 1 THEN 1 ELSE 0 END AS BIT)
                                            AS IsDefault
                FROM dbo.CUSTOMER_ADRESS ca
                LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                WHERE ca.CUSTOMER_ADRESS_ID = @Id",
                new { Id = addressId }).FirstOrDefault();
        }

        private List<DriverDto> LoadDrivers(
            IDbConnection conn, int branchId, int? governorateId)
        {
            return conn.Query<DriverDto>(@"
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
                    CAST(CASE WHEN d.IS_ACTIVE = 1 THEN 1 ELSE 0 END AS BIT)
                                        AS IsActive,
                    CAST(0 AS BIT)      AS IsPreferred
                FROM dbo.DRIVER d
                INNER JOIN dbo.GOVERNORATE g
                    ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE d.IS_ACTIVE = 1
                  AND (@BranchId IS NULL OR d.BRANCH_ID = @BranchId OR d.BRANCH_ID IS NULL)
                  AND (@GovernorateId IS NULL OR d.GOVERNORATE_ID = @GovernorateId)
                ORDER BY d.GOVERNORATE_ID, d.DRIVER_NAME",
                new { BranchId = branchId, GovernorateId = governorateId }).ToList();
        }

        private HomeServiceDto? LoadHomeService(IDbConnection conn, int appointmentId)
        {
            return conn.Query<HomeServiceDto>(@"
                SELECT
                    hs.Id,
                    hs.AppointmentId,
                    hs.CustomerAddressId,
                    hs.AreaId,
                    hs.AreaNameEn,
                    hs.AreaNameAr,
                    hs.GovernorateId,
                    hs.GovernorateNameEn,
                    hs.GovernorateNameAr,
                    hs.DriverId,
                    hs.DriverName,
                    hs.DriverPhone,
                    hs.AddressBlock,
                    hs.AddressStreet,
                    hs.AddressAvenue,
                    hs.AddressBuilding,
                    hs.AddressFlat,
                    hs.AddressFloor,
                    hs.AddressNote,
                    hs.AddressLocation
                FROM dbo.AppointmentHomeService hs
                WHERE hs.AppointmentId = @Id",
                new { Id = appointmentId }).FirstOrDefault();
        }

        private static object BuildUpsertParams(
            int appointmentId,
            SaveHomeServiceRequest req,
            AddressLookupDto addr,
            DriverLookupDto driver)
        {
            return new
            {
                AppointmentId = appointmentId,
                AddressId = req.CustomerAddressId,
                AreaId = addr.AreaId,
                GovId = addr.GovernorateId,
                DriverId = req.DriverId,
                Block = addr.BlockNo,
                Street = addr.Street,
                Avenue = addr.Avenue,
                Building = addr.BuildingNo,
                Flat = addr.FlatNo,
                Floor = addr.Floor,
                Note = addr.Note,
                Location = addr.Location,
                AreaNameEn = addr.AreaNameEn,
                AreaNameAr = addr.AreaNameAr,
                GovNameEn = addr.GovernorateNameEn,
                GovNameAr = addr.GovernorateNameAr,
                DriverName = driver.DriverName,
                DriverPhone = driver.DriverPhone
            };
        }

        #endregion
    }
}