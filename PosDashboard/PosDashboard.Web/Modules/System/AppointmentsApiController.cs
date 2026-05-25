// Modules/System/Controllers/AppointmentsApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/appointments")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AppointmentsApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public AppointmentsApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        #region Helpers

        private static readonly string[] ValidStatuses =
            { "scheduled", "completed", "cancelled", "no-show" };
        private static readonly string[] ValidServiceTypes =
            { "SALON", "HOME" };
        private static readonly string[] ValidPaymentAs =
            { "DEPOSIT", "FULL" };

        private bool TryParseTime(string? time, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(time)) return false;
            return TimeSpan.TryParseExact(time, @"hh\:mm", null, out result);
        }

        private string GenerateInvoiceNumber()
        {
            var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
            var randomPart = Guid.NewGuid().ToString("N")[..5].ToUpper();
            return $"INV-{datePart}-{randomPart}";
        }

        #endregion

        // =============================================
        // POST /api/appointments — Create Appointment
        // =============================================
        [HttpPost]
        public ActionResult<ApiResult<AppointmentDto>> Create(
            [FromBody] CreateAppointmentRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<AppointmentDto>(false, "Request body is required", null));

            if (!ValidServiceTypes.Contains(request.ServiceType))
                return Ok(new ApiResult<AppointmentDto>(false,
                    "ServiceType must be 'SALON' or 'HOME'", null));

            if (!TryParseTime(request.StartTime, out var startTs))
                return Ok(new ApiResult<AppointmentDto>(false,
                    "StartTime must be in HH:mm format", null));

            if (!TryParseTime(request.EndTime, out var endTs))
                return Ok(new ApiResult<AppointmentDto>(false,
                    "EndTime must be in HH:mm format", null));

            if (endTs <= startTs)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "EndTime must be after StartTime", null));

            if (request.NumberOfPersons < 1)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "NumberOfPersons must be at least 1", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Validate Customer
            var customer = SqlMapper.Query(conn,
                @"SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1 
                  FROM dbo.CUSTOMER 
                  WHERE CUSTOMER_ID = @Id",
                new { Id = request.CustomerId }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResult<AppointmentDto>(false, "Customer not found", null));

            // Validate Staff
            var staff = SqlMapper.Query(conn,
                @"SELECT Id, ArabicName, EnglishName 
                  FROM dbo.STAFF 
                  WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = request.StaffId }).FirstOrDefault();

            if (staff == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Staff not found or not active", null));

            // Validate Item + Unit + get pricing
            var item = SqlMapper.Query(conn, @"
                SELECT 
                    iu.ITEM_ID,
                    i.ITEM_NAME1,
                    i.ITEM_NAME2,
                    iu.UNIT_ID,
                    iu.ITEM_UNIT_PRICE,
                    CAST(iu.ITEM_UNIT_DURATION AS float) AS ITEM_UNIT_DURATION
                FROM dbo.ITEM_UNIT iu
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                WHERE iu.ITEM_ID = @ItemId 
                  AND iu.UNIT_ID = @UnitId 
                  AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                  AND iu.Active = 1",
                new { ItemId = request.ItemId, UnitId = request.UnitId })
                .FirstOrDefault();

            if (item == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Item/Unit not found or not active", null));

            // Validate Branch
            var branch = SqlMapper.Query(conn,
                @"SELECT BRANCH_ID FROM dbo.BRANCH 
                  WHERE BRANCH_ID = @Id 
                  AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { Id = request.BranchId }).FirstOrDefault();

            if (branch == null)
                return Ok(new ApiResult<AppointmentDto>(false, "Branch not found or inactive", null));

            // Calculate pricing
            decimal unitPrice = (decimal)item.ITEM_UNIT_PRICE;
            decimal discountPercent = 0;
            decimal discountedUnitPrice = unitPrice;
            // NumberOfPersons does NOT affect price
            decimal totalPrice = discountedUnitPrice;

            // Check time conflicts
            var conflict = SqlMapper.Query(conn, @"
                SELECT Id FROM dbo.AppointmentData
                WHERE StaffId = @StaffId
                  AND AppointmentDate = @Date
                  AND Status != 'cancelled'
                  AND StartTime < @EndTime
                  AND EndTime > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = request.AppointmentDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).FirstOrDefault();

            if (conflict != null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    $"Time conflict with appointment #{(int)conflict.Id}", null));
            // Check time block conflicts
            var blockConflict = SqlMapper.Query(conn, @"
    SELECT Id, Title, BlockType FROM dbo.StaffTimeBlocks
    WHERE StaffId = @StaffId
      AND BlockDate = @Date
      AND Deleted = 0
      AND IsRecurring = 0
      AND StartTime < @EndTime
      AND EndTime > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = request.AppointmentDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).FirstOrDefault();

            if (blockConflict != null)
            {
                var reason = (string?)blockConflict.Title ?? (string)blockConflict.BlockType;
                return Ok(new ApiResult<AppointmentDto>(false,
                    $"Staff is blocked during this time ({reason})", null));
            }

            // Check recurring block conflicts
            var recurringBlocks = SqlMapper.Query(conn, @"
    SELECT Id, Title, BlockType, RecurrenceRule, RecurringStart, RecurringEnd,
           StartTime, EndTime
    FROM dbo.StaffTimeBlocks
    WHERE StaffId = @StaffId
      AND IsRecurring = 1
      AND Deleted = 0
      AND (RecurringStart IS NULL OR RecurringStart <= @Date)
      AND (RecurringEnd IS NULL OR RecurringEnd >= @Date)
      AND StartTime < @EndTime
      AND EndTime > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = request.AppointmentDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).ToList();

            foreach (var rb in recurringBlocks)
            {
                // Check if recurrence applies to this specific day
                string rule = (string)rb.RecurrenceRule;
                var dayOfWeek = request.AppointmentDate.Date.DayOfWeek;
                bool applies = rule switch
                {
                    "DAILY" => true,
                    "WEEKDAYS" => dayOfWeek >= DayOfWeek.Monday && dayOfWeek <= DayOfWeek.Friday,
                    "WEEKENDS" => dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday,
                    _ when rule.StartsWith("WEEKLY:") =>
                        rule.Substring(7).Split(',').Select(d => d.Trim().ToUpper())
                            .Any(d => d == dayOfWeek.ToString().Substring(0, 3).ToUpper()),
                    _ => false
                };

                if (applies)
                {
                    var reason = (string?)rb.Title ?? (string)rb.BlockType;
                    return Ok(new ApiResult<AppointmentDto>(false,
                        $"Staff has a recurring block during this time ({reason})", null));
                }
            }
            // ── Package session handling ──
            int? customerPackageSessionId = request.CustomerPackageSessionId;
            if (customerPackageSessionId.HasValue)
            {
                var sess = SqlMapper.Query(conn, @"
        SELECT s.Id, s.CustomerPackageId, s.Served, s.Deleted, s.PackageItemId,
               pi.ItemUnitId, iu.ITEM_ID, iu.UNIT_ID,
               cp.CustomerRef, cp.ExpiryDate, cp.IsPaid, cp.Deleted AS CPDeleted,
               c.CUSTOMER_REF_GUIDE AS ApptCustomerRef
        FROM dbo.CustomerPackageSessions s
        INNER JOIN dbo.PackageItems pi ON pi.Id = s.PackageItemId
        INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
        INNER JOIN dbo.CustomerPackages cp ON cp.Id = s.CustomerPackageId
        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = @CustomerId
        WHERE s.Id = @SessionId",
                    new { SessionId = customerPackageSessionId.Value, CustomerId = request.CustomerId })
                    .FirstOrDefault();

                if (sess == null)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package session not found", null));
                if (Convert.ToInt32(sess.Deleted) == 1 || Convert.ToInt32(sess.CPDeleted) == 1)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package session or assignment is deleted", null));
                if (Convert.ToInt32(sess.Served) == 1)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package session already served", null));
                if (Convert.ToInt32(sess.IsPaid) != 1)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package is not paid", null));
                if (((DateTime)sess.ExpiryDate) < DateTime.UtcNow)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package has expired", null));
                if ((Guid)sess.CustomerRef != (Guid)sess.ApptCustomerRef)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package does not belong to this customer", null));
                if ((int)sess.ITEM_ID != request.ItemId || (int)sess.UNIT_ID != request.UnitId)
                    return Ok(new ApiResult<AppointmentDto>(false, "Package session does not match selected service", null));

                // Zero out pricing for package-covered appointment
                unitPrice = 0m;
                discountPercent = 0m;
                discountedUnitPrice = 0m;
                totalPrice = 0m;
            }
            // Insert
            var id = SqlMapper.Query<int>(conn, @"
                INSERT INTO dbo.AppointmentData (
                    BranchId, CustomerId, ItemId, UnitId, StaffId,
                    AppointmentDate, StartTime, EndTime,
                    NumberOfPersons, ServiceType, IsOnlineBooking, Notes,
                    UnitPrice, DiscountPercent, DiscountedUnitPrice, TotalPrice,
                    PaidAmount, PaymentStatus, DepositAmount,
                    Status, CheckoutStatus, CreatedAt
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @BranchId, @CustomerId, @ItemId, @UnitId, @StaffId,
                    @AppointmentDate, @StartTime, @EndTime,
                    @NumberOfPersons, @ServiceType, @IsOnlineBooking, @Notes,
                    @UnitPrice, @DiscountPercent, @DiscountedUnitPrice, @TotalPrice,
                    0, 'NONE', 0,
                    'scheduled', 'open', SYSUTCDATETIME()
                )",
                new
                {
                    request.BranchId,
                    request.CustomerId,
                    request.ItemId,
                    request.UnitId,
                    request.StaffId,
                    AppointmentDate = request.AppointmentDate.Date,
                    StartTime = startTs,
                    EndTime = endTs,
                    request.NumberOfPersons,
                    request.ServiceType,
                    request.IsOnlineBooking,
                    Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                    UnitPrice = unitPrice,
                    DiscountPercent = discountPercent,
                    DiscountedUnitPrice = discountedUnitPrice,
                    TotalPrice = totalPrice
                }).FirstOrDefault();
            // ── Mark session served and link to appointment ──
            if (customerPackageSessionId.HasValue)
            {
                SqlMapper.Execute(conn, @"
        UPDATE dbo.CustomerPackageSessions SET
            Served = 1,
            ServedDate = SYSUTCDATETIME(),
            StaffId = @StaffId,
            AppointmentId = @AppointmentId,
            ModifiedDate = SYSUTCDATETIME()
        WHERE Id = @SessionId",
                    new
                    {
                        StaffId = request.StaffId,
                        AppointmentId = id,
                        SessionId = customerPackageSessionId.Value
                    });

                // Auto-mark the appointment as fully paid (zero price → nothing to collect)
                SqlMapper.Execute(conn, @"
        UPDATE dbo.AppointmentData SET
            PaymentStatus = 'FULL',
            PaidAmount = 0,
            UpdatedAt = SYSUTCDATETIME()
        WHERE Id = @Id",
                    new { Id = id });
            }
            var apt = GetAppointmentById(conn, id);
            return Ok(new ApiResult<AppointmentDto>(true, null, apt));
        }

        // =============================================
        // GET /api/appointments?branchId=1&date=2025-01-15
        // =============================================
        [HttpGet]
        public ActionResult<ApiResult<AppointmentListResponse>> GetAll(
            [FromQuery] int branchId,
            [FromQuery] DateTime? date,
            [FromQuery] DateTime? dateFrom,
            [FromQuery] DateTime? dateTo,
            [FromQuery] int? staffId,
            [FromQuery] string? status)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var where = new List<string> { "a.BranchId = @BranchId" };
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("BranchId", branchId);

            if (date.HasValue)
            {
                where.Add("a.AppointmentDate = @Date");
                parameters.Add("Date", date.Value.Date);
            }
            else
            {
                if (dateFrom.HasValue)
                {
                    where.Add("a.AppointmentDate >= @DateFrom");
                    parameters.Add("DateFrom", dateFrom.Value.Date);
                }
                if (dateTo.HasValue)
                {
                    where.Add("a.AppointmentDate <= @DateTo");
                    parameters.Add("DateTo", dateTo.Value.Date);
                }
            }

            if (staffId.HasValue)
            {
                where.Add("a.StaffId = @StaffId");
                parameters.Add("StaffId", staffId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status) && ValidStatuses.Contains(status))
            {
                where.Add("a.Status = @Status");
                parameters.Add("Status", status);
            }

            var whereClause = string.Join(" AND ", where);

            var appointments = SqlMapper.Query(conn, $@"
                SELECT
                    a.Id,
                    a.BranchId,
                    a.CustomerId,
                    c.CUSTOMER_NAME     AS CustomerName,
                    c.CUSTOMER_PHONE1   AS CustomerPhone,
                    a.ItemId,
                    i.ITEM_NAME1        AS ItemEnName,
                    i.ITEM_NAME2        AS ItemArName,
                    a.UnitId,
                    a.StaffId,
                    s.EnglishName       AS StaffEnName,
                    s.ArabicName        AS StaffArName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS AppointmentDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime, 108), 5)   AS EndTime,
                    a.NumberOfPersons,
                    a.ServiceType,
                    a.IsOnlineBooking,
                    a.Notes,
                    a.UnitPrice,
                    a.DiscountPercent,
                    a.DiscountedUnitPrice,
                    a.TotalPrice,
                    a.PaidAmount,
                    (a.TotalPrice - a.PaidAmount) AS RemainingAmount,
                    a.PaymentStatus,
                    a.DepositAmount,
                    a.VoucherCode,
                    a.Status,
                    a.CheckoutStatus,
                    invRes.InvoiceId    AS InvoiceId,
                    invRes.InvoiceNumber AS InvoiceNumber,
                    a.CreatedAt,
                    (
                        SELECT TOP 1
                            CASE
                                WHEN ISNULL(pt2.OnlinePayment, 0) = 1 THEN 'ONLINE'
                                ELSE 'BRANCH'
                            END
                        FROM dbo.AppointmentPayments ap2
                        LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt2
                            ON pt2.INVOICE_PAYMENT_TYPE_ID = ap2.PaymentTypeId
                        WHERE ap2.AppointmentId = a.Id
                        ORDER BY ap2.PaidAt DESC, ap2.Id DESC
                    ) AS PaymentSource,
                    cps.Id          AS CustomerPackageSessionId,
                    pkg.EnglishName AS PackageName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c    ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM i        ON i.ITEM_ID    = a.ItemId
                INNER JOIN dbo.STAFF s       ON s.Id         = a.StaffId
                -- Resolve invoice in this priority:
                --   1) Direct: AppointmentInvoices.AppointmentId = a.Id (legacy + New Sale lead)
                --   2) Line-based: AppointmentInvoiceLines.AppointmentId = a.Id (New Sale non-lead)
                -- OUTER APPLY guarantees at most one row, so no duplicate appointments.
                OUTER APPLY (
                    SELECT TOP 1 invX.Id AS InvoiceId, invX.InvoiceNumber
                    FROM (
                        SELECT inv1.Id, inv1.InvoiceNumber, 1 AS Priority
                        FROM dbo.AppointmentInvoices inv1
                        WHERE inv1.AppointmentId = a.Id
 
                        UNION ALL
 
                        SELECT inv2.Id, inv2.InvoiceNumber, 2 AS Priority
                        FROM dbo.AppointmentInvoiceLines ail
                        INNER JOIN dbo.AppointmentInvoices inv2
                            ON inv2.Id = ail.InvoiceId
                        WHERE ail.AppointmentId = a.Id
                    ) invX
                    ORDER BY invX.Priority, invX.Id DESC
                ) invRes
                LEFT JOIN dbo.CustomerPackageSessions cps
                    ON cps.AppointmentId = a.Id AND ISNULL(cps.Deleted, 0) = 0
                LEFT JOIN dbo.CustomerPackages cpkg
                    ON cpkg.Id = cps.CustomerPackageId
                LEFT JOIN dbo.Packages pkg
                    ON pkg.Id = cpkg.PackageId
                WHERE {whereClause}
                ORDER BY a.AppointmentDate, a.StartTime",
                    (object)parameters)
                    .Select(MapRowToDto)
                    .ToList();

            var response = new AppointmentListResponse(
                TotalCount: appointments.Count,
                Appointments: appointments
            );

            return Ok(new ApiResult<AppointmentListResponse>(true, null, response));
        }

        // =============================================
        // GET /api/appointments/{id}
        // =============================================
        [HttpGet("{id:int}")]
        public ActionResult<ApiResult<AppointmentDetailDto>> GetById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var apt = GetAppointmentById(conn, id);
            if (apt == null)
                return Ok(new ApiResult<AppointmentDetailDto>(false,
                    "Appointment not found", null));

            var payments = SqlMapper.Query(conn, @"
                SELECT 
                    ap.Id, ap.Amount, ap.PaymentTypeId,
                    pt.INVOICE_PAYMENT_TYPE_NAME1 AS PaymentTypeName,
                    ap.PaymentAs, ap.VoucherCode, ap.PaidAt,
                    ap.IsWalletPayment   
                FROM dbo.AppointmentPayments ap
                LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt 
                    ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
                WHERE ap.AppointmentId = @Id
                ORDER BY ap.PaidAt",
                new { Id = id })
                .Select(p => new AppointmentPaymentDto(
                    Id: (int)p.Id,
                    Amount: (decimal)p.Amount,
                    PaymentTypeId: (int)p.PaymentTypeId,
                    PaymentTypeName: (bool)p.IsWalletPayment ? "Wallet" : (string)(p.PaymentTypeName ?? ""),
                    PaymentAs: (string)p.PaymentAs,
                    VoucherCode: (string?)p.VoucherCode,
                    PaidAt: (DateTime)p.PaidAt
                ))
                .ToList();

            // Load home-service snapshot if present
            HomeServiceSnapshotDto? homeService = null;
            if (apt.ServiceType == "HOME")
            {
                var hs = SqlMapper.Query(conn, @"
            SELECT CustomerAddressId, AreaId, AreaNameEn, AreaNameAr,
                   GovernorateId, GovernorateNameEn, GovernorateNameAr,
                   DriverId, DriverName, DriverPhone,
                   AddressBlock, AddressStreet, AddressAvenue, AddressBuilding,
                   AddressFlat, AddressFloor, AddressNote, AddressLocation
            FROM dbo.AppointmentHomeService
            WHERE AppointmentId = @Id",
                    new { Id = id }).FirstOrDefault();

                if (hs != null)
                {
                    homeService = new HomeServiceSnapshotDto(
                        CustomerAddressId: (int)hs.CustomerAddressId,
                        AreaId: (int)hs.AreaId,
                        AreaNameEn: (string)(hs.AreaNameEn ?? ""),
                        AreaNameAr: (string)(hs.AreaNameAr ?? ""),
                        GovernorateId: (int)hs.GovernorateId,
                        GovernorateNameEn: (string)(hs.GovernorateNameEn ?? ""),
                        GovernorateNameAr: (string)(hs.GovernorateNameAr ?? ""),
                        DriverId: (int)hs.DriverId,
                        DriverName: (string)(hs.DriverName ?? ""),
                        DriverPhone: (string?)hs.DriverPhone,
                        AddressBlock: (string?)hs.AddressBlock,
                        AddressStreet: (string?)hs.AddressStreet,
                        AddressAvenue: (string?)hs.AddressAvenue,
                        AddressBuilding: (string?)hs.AddressBuilding,
                        AddressFlat: (string?)hs.AddressFlat,
                        AddressFloor: (string?)hs.AddressFloor,
                        AddressNote: (string?)hs.AddressNote,
                        AddressLocation: (string?)hs.AddressLocation
                    );
                }
            }

            var detail = new AppointmentDetailDto(apt, payments, homeService);
            return Ok(new ApiResult<AppointmentDetailDto>(true, null, detail));
        }

        // =============================================
        // PUT /api/appointments/{id}
        // =============================================
        [HttpPost("{id:int}/update")]
        public ActionResult<ApiResult<AppointmentDto>> Update(
            int id, [FromBody] UpdateAppointmentRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var existing = SqlMapper.Query(conn,
                "SELECT Id, Status, CheckoutStatus FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (existing == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Appointment not found", null));

            if ((string)existing.CheckoutStatus == "checked_out")
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Cannot edit a checked-out appointment", null));

            var updates = new List<string>();
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("Id", id);

            if (request.CustomerId.HasValue)
            {
                var cust = SqlMapper.Query(conn,
                    "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId.Value }).FirstOrDefault();
                if (cust == null)
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "Customer not found", null));

                updates.Add("CustomerId = @CustomerId");
                parameters.Add("CustomerId", request.CustomerId.Value);
            }

            if (request.StaffId.HasValue)
            {
                var stf = SqlMapper.Query(conn,
                    "SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                    new { Id = request.StaffId.Value }).FirstOrDefault();
                if (stf == null)
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "Staff not found or not active", null));

                updates.Add("StaffId = @StaffId");
                parameters.Add("StaffId", request.StaffId.Value);
            }

            if (request.ItemId.HasValue && request.UnitId.HasValue)
            {
                var itm = SqlMapper.Query(conn, @"
                    SELECT iu.ITEM_UNIT_PRICE AS Price 
                    FROM dbo.ITEM_UNIT iu
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                    WHERE iu.ITEM_ID = @ItemId 
                      AND iu.UNIT_ID = @UnitId 
                      AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                      AND iu.Active = 1",
                    new { ItemId = request.ItemId.Value, UnitId = request.UnitId.Value })
                    .FirstOrDefault();
                if (itm == null)
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "Item/Unit not found or not active", null));

                decimal newPrice = (decimal)itm.Price;
                int persons = request.NumberOfPersons ?? 1;

                updates.Add("ItemId = @ItemId");
                updates.Add("UnitId = @UnitId");
                updates.Add("UnitPrice = @UnitPrice");
                updates.Add("DiscountedUnitPrice = @DiscountedUnitPrice");
                updates.Add("TotalPrice = @TotalPrice");
                parameters.Add("ItemId", request.ItemId.Value);
                parameters.Add("UnitId", request.UnitId.Value);
                parameters.Add("UnitPrice", newPrice);
                parameters.Add("DiscountedUnitPrice", newPrice);
                parameters.Add("TotalPrice", newPrice);
            }

            if (request.AppointmentDate.HasValue)
            {
                updates.Add("AppointmentDate = @AppointmentDate");
                parameters.Add("AppointmentDate", request.AppointmentDate.Value.Date);
            }

            if (!string.IsNullOrWhiteSpace(request.StartTime))
            {
                if (!TryParseTime(request.StartTime, out var st))
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "StartTime must be HH:mm", null));
                updates.Add("StartTime = @StartTime");
                parameters.Add("StartTime", st);
            }

            if (!string.IsNullOrWhiteSpace(request.EndTime))
            {
                if (!TryParseTime(request.EndTime, out var et))
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "EndTime must be HH:mm", null));
                updates.Add("EndTime = @EndTime");
                parameters.Add("EndTime", et);
            }

            if (request.NumberOfPersons.HasValue)
            {
                if (request.NumberOfPersons.Value < 1)
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "NumberOfPersons must be at least 1", null));
                updates.Add("NumberOfPersons = @NumberOfPersons");
                parameters.Add("NumberOfPersons", request.NumberOfPersons.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.ServiceType))
            {
                if (!ValidServiceTypes.Contains(request.ServiceType))
                    return Ok(new ApiResult<AppointmentDto>(false,
                        "ServiceType must be 'SALON' or 'HOME'", null));
                updates.Add("ServiceType = @ServiceType");
                parameters.Add("ServiceType", request.ServiceType);
            }

            if (request.ClearNotes == true)
                updates.Add("Notes = NULL");
            else if (!string.IsNullOrWhiteSpace(request.Notes))
            {
                updates.Add("Notes = @Notes");
                parameters.Add("Notes", request.Notes.Trim());
            }

            if (updates.Count == 0)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "No fields to update", null));

            updates.Add("UpdatedAt = SYSUTCDATETIME()");

            var sql = $"UPDATE dbo.AppointmentData SET {string.Join(", ", updates)} WHERE Id = @Id";
            SqlMapper.Execute(conn, sql, parameters);

            var apt = GetAppointmentById(conn, id);
            return Ok(new ApiResult<AppointmentDto>(true, null, apt));
        }

        // =============================================
        // PATCH /api/appointments/{id}/status
        // =============================================
        [HttpPatch("{id:int}/status")]
        public ActionResult<ApiResult<AppointmentDto>> UpdateStatus(
            int id, [FromBody] UpdateStatusRequest request)
        {
            if (request == null || !ValidStatuses.Contains(request.Status))
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Status must be: scheduled, completed, cancelled, no-show", null));

            using var conn = sqlConnections.NewByKey("Default");

            var exists = SqlMapper.Query(conn,
                "SELECT Id FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (exists == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Appointment not found", null));

            SqlMapper.Execute(conn, @"
                UPDATE dbo.AppointmentData 
                SET Status = @Status, UpdatedAt = SYSUTCDATETIME() 
                WHERE Id = @Id",
                new { Id = id, request.Status });

            var apt = GetAppointmentById(conn, id);
            return Ok(new ApiResult<AppointmentDto>(true, null, apt));
        }

        // =============================================
        // POST /api/appointments/{id}/payments
        // =============================================
        // =============================================
        // POST /api/appointments/{id}/payments
        // =============================================
        [HttpPost("{id:int}/payments")]
        public ActionResult<ApiResult<AppointmentDto>> ApplyPayment(
            int id, [FromBody] ApplyPaymentRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Request body is required", null));

            if (request.Amount <= 0)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Amount must be greater than 0", null));

            if (!ValidPaymentAs.Contains(request.PaymentAs))
                return Ok(new ApiResult<AppointmentDto>(false,
                    "PaymentAs must be 'DEPOSIT' or 'FULL'", null));

            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn, @"
        SELECT Id, TotalPrice, PaidAmount, CheckoutStatus, Status
        FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Appointment not found", null));

            // Block cancelled appointments
            if ((string)apt.Status == "cancelled")
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Cannot apply payment to a cancelled appointment", null));

            decimal totalPrice = (decimal)apt.TotalPrice;
            decimal currentPaid = (decimal)apt.PaidAmount;
            decimal remaining = totalPrice - currentPaid;

            if (remaining <= 0)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Appointment is already fully paid", null));

            if (request.Amount > remaining)
                return Ok(new ApiResult<AppointmentDto>(false,
                    $"Amount exceeds remaining balance of {remaining}", null));

            // Validate payment type
            var pt = SqlMapper.Query(conn,
                @"SELECT INVOICE_PAYMENT_TYPE_ID 
          FROM dbo.INVOICE_PAYMENT_TYPE 
          WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                new { Id = request.PaymentTypeId }).FirstOrDefault();

            if (pt == null)
                return Ok(new ApiResult<AppointmentDto>(false,
                    "Payment type not found", null));

            // Insert payment record
            SqlMapper.Execute(conn, @"
            INSERT INTO dbo.AppointmentPayments 
                (AppointmentId, Amount, PaymentTypeId, PaymentAs, VoucherCode, PaidAt, IsWalletPayment)
            VALUES 
                (@AppointmentId, @Amount, @PaymentTypeId, @PaymentAs, @VoucherCode, SYSUTCDATETIME(), @IsWalletPayment)",
            new
            {
                AppointmentId = id,
                request.Amount,
                request.PaymentTypeId,
                request.PaymentAs,
                VoucherCode = string.IsNullOrWhiteSpace(request.VoucherCode)
                    ? null : request.VoucherCode.Trim(),
                IsWalletPayment = request.IsWalletPayment
            });

            // Update appointment totals
            decimal newPaid = currentPaid + request.Amount;
            decimal newRemaining = totalPrice - newPaid;

            string newPaymentStatus;
            if (newRemaining <= 0)
                newPaymentStatus = "FULL";
            else if (newPaid > 0)
                newPaymentStatus = "DEPOSIT";
            else
                newPaymentStatus = "NONE";

            decimal depositAmount = newPaymentStatus == "DEPOSIT" ? newPaid : 0;

            SqlMapper.Execute(conn, @"
        UPDATE dbo.AppointmentData SET
            PaidAmount = @PaidAmount,
            PaymentStatus = @PaymentStatus,
            DepositAmount = @DepositAmount,
            VoucherCode = COALESCE(@VoucherCode, VoucherCode),
            UpdatedAt = SYSUTCDATETIME()
        WHERE Id = @Id",
                new
                {
                    Id = id,
                    PaidAmount = newPaid,
                    PaymentStatus = newPaymentStatus,
                    DepositAmount = depositAmount,
                    VoucherCode = string.IsNullOrWhiteSpace(request.VoucherCode)
                        ? null : request.VoucherCode.Trim()
                });

            // ✅ NEW: If already checked out, update the existing invoice too
            if ((string)apt.CheckoutStatus == "checked_out")
            {
                SqlMapper.Execute(conn, @"
            UPDATE dbo.AppointmentInvoices SET
                PaidAmount = @PaidAmount,
                RemainingAmount = @RemainingAmount,
                PaymentStatus = @PaymentStatus
            WHERE AppointmentId = @AppointmentId",
                    new
                    {
                        AppointmentId = id,
                        PaidAmount = newPaid,
                        RemainingAmount = newRemaining,
                        PaymentStatus = newPaymentStatus
                    });
            }

            var result = GetAppointmentById(conn, id);
            return Ok(new ApiResult<AppointmentDto>(true, null, result));
        }

        // =============================================
        // POST /api/appointments/{id}/checkout
        // =============================================
        [HttpPost("{id:int}/checkout")]
        public ActionResult<ApiResult<CheckoutResponse>> Checkout(
            int id, [FromBody] CheckoutRequest? request)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn, @"
        SELECT Id, BranchId, CustomerId, TotalPrice, PaidAmount, 
               PaymentStatus, CheckoutStatus
        FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<CheckoutResponse>(false,
                    "Appointment not found", null));

            if ((string)apt.CheckoutStatus == "checked_out")
                return Ok(new ApiResult<CheckoutResponse>(false,
                    "Appointment is already checked out", null));

            // Recalc total to be sure it includes extras
            RecalcAppointmentTotal(conn, id);

            // Re-read after recalc
            apt = SqlMapper.Query(conn, @"
        SELECT Id, BranchId, CustomerId, TotalPrice, PaidAmount, 
               PaymentStatus, CheckoutStatus
        FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            // Update appointment
            SqlMapper.Execute(conn, @"
        UPDATE dbo.AppointmentData SET
            CheckoutStatus = 'checked_out',
            Status = 'completed',
            UpdatedAt = SYSUTCDATETIME()
        WHERE Id = @Id",
                new { Id = id });

            // Create invoice
            var invoiceNumber = GenerateInvoiceNumber();
            decimal totalPrice = (decimal)apt.TotalPrice;
            decimal paidAmount = (decimal)apt.PaidAmount;
            decimal remainingAmount = totalPrice - paidAmount;

            var currency = SqlMapper.Query<string>(conn,
                "SELECT EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                new { Id = (int)apt.BranchId }).FirstOrDefault() ?? "KWD";

            // قبل الـ INSERT بتاع الـ Invoice، أضف ده:
            var lastNonWalletPaymentTypeId = SqlMapper.Query<int?>(conn, @"
                SELECT TOP 1 PaymentTypeId
                FROM dbo.AppointmentPayments
                WHERE AppointmentId = @Id
                  AND IsWalletPayment = 0
                ORDER BY PaidAt DESC",
                            new { Id = id }).FirstOrDefault();

            // بعدين في الـ INSERT:
            var invoiceId = SqlMapper.Query<int>(conn, @"
            INSERT INTO dbo.AppointmentInvoices (
                InvoiceNumber, AppointmentId, BranchId, CustomerId,
                TotalAmount, PaidAmount, RemainingAmount, Currency,
                PaymentTypeId, PaymentStatus, CreatedAt
            )
            OUTPUT INSERTED.Id
            VALUES (
                @InvoiceNumber, @AppointmentId, @BranchId, @CustomerId,
                @TotalAmount, @PaidAmount, @RemainingAmount, @Currency,
                @PaymentTypeId, @PaymentStatus, SYSUTCDATETIME()
            )",
                new
                {
                    InvoiceNumber = invoiceNumber,
                    AppointmentId = id,
                    BranchId = (int)apt.BranchId,
                    CustomerId = (int)apt.CustomerId,
                    TotalAmount = totalPrice,
                    PaidAmount = paidAmount,
                    RemainingAmount = remainingAmount,
                    Currency = currency,
                    PaymentTypeId = lastNonWalletPaymentTypeId ?? request?.PaymentTypeId,
                    PaymentStatus = (string)apt.PaymentStatus
                }).FirstOrDefault();

            // ── Insert AppointmentInvoiceLines for original + checkout extras ──
            // Only the columns guaranteed by the NewSale migration are used.
            // The error is surfaced so schema issues are visible during development.
            try
            {
                // Original service line — ISNULL guards on TIME columns (NOT NULL in schema)
                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.AppointmentInvoiceLines
                        (InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                         UnitPrice, DiscountedUnitPrice, TotalPrice,
                         AppointmentDate, StartTime, EndTime, DurationMinutes, Notes)
                    SELECT
                        @InvoiceId, a.Id, a.ItemId, a.UnitId, a.StaffId,
                        a.DiscountedUnitPrice, a.DiscountedUnitPrice, a.DiscountedUnitPrice,
                        a.AppointmentDate,
                        ISNULL(a.StartTime, '00:00:00'),
                        ISNULL(a.EndTime,   '00:00:00'),
                        0, NULL
                    FROM dbo.AppointmentData a
                    WHERE a.Id = @AppointmentId",
                    new { InvoiceId = invoiceId, AppointmentId = id });

                // Extra checkout-item lines ("Add Service" during checkout)
                // These have no time slot — inherit appointment's StartTime/EndTime
                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.AppointmentInvoiceLines
                        (InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                         UnitPrice, DiscountedUnitPrice, TotalPrice,
                         AppointmentDate, StartTime, EndTime, DurationMinutes, Notes)
                    SELECT
                        @InvoiceId, ci.AppointmentId, ci.ItemId, ci.UnitId, ci.StaffId,
                        ci.DiscountedUnitPrice, ci.DiscountedUnitPrice, ci.TotalPrice,
                        a.AppointmentDate,
                        ISNULL(a.StartTime, '00:00:00'),
                        ISNULL(a.EndTime,   '00:00:00'),
                        0, NULL
                    FROM dbo.AppointmentCheckoutItems ci
                    INNER JOIN dbo.AppointmentData a ON a.Id = ci.AppointmentId
                    WHERE ci.AppointmentId = @AppointmentId",
                    new { InvoiceId = invoiceId, AppointmentId = id });
            }
            catch (Exception lineEx)
            {
                // Checkout invoice already created above — lines are supplementary.
                // Return a warning in the response so the error is visible to the caller.
                return Ok(new ApiResult<CheckoutResponse>(false,
                    $"Checkout succeeded but invoice lines failed: {lineEx.Message}", null));
            }

            var invoice = new InvoiceDto(
                Id: invoiceId,
                InvoiceNumber: invoiceNumber,
                AppointmentId: id,
                TotalAmount: totalPrice,
                PaidAmount: paidAmount,
                RemainingAmount: remainingAmount,
                Currency: currency,
                PaymentTypeId: request?.PaymentTypeId,
                PaymentStatus: (string)apt.PaymentStatus,
                CreatedAt: DateTime.UtcNow
            );

            return Ok(new ApiResult<CheckoutResponse>(true, null,
                new CheckoutResponse(id, invoice)));
        }

        // =============================================
        // DELETE /api/appointments/{id}
        // =============================================
        [HttpDelete("{id:int}")]
        public ActionResult<ApiResult<object>> Delete(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn,
                "SELECT Id, CheckoutStatus FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<object>(false, "Appointment not found", null));

            if ((string)apt.CheckoutStatus == "checked_out")
                return Ok(new ApiResult<object>(false,
                    "Cannot delete a checked-out appointment", null));

            // Delete checkout items first
            SqlMapper.Execute(conn,
                "DELETE FROM dbo.AppointmentCheckoutItems WHERE AppointmentId = @Id",
                new { Id = id });

            // Delete payments
            SqlMapper.Execute(conn,
                "DELETE FROM dbo.AppointmentPayments WHERE AppointmentId = @Id",
                new { Id = id });

            // Delete HomeService
            SqlMapper.Execute(conn,
            "DELETE FROM dbo.AppointmentHomeService WHERE AppointmentId = @Id",
            new { Id = id });

            // Delete appointment
            SqlMapper.Execute(conn,
                "DELETE FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id });

            return Ok(new ApiResult<object>(true, null, new { DeletedId = id }));
        }

        // =============================================
        // POST /api/appointments/{id}/void-invoice
        // =============================================
        [HttpPost("{id:int}/void-invoice")]
        public ActionResult<ApiResult<object>> VoidInvoice(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            using var uow = new UnitOfWork(conn);
            try
            {
                // Load appointment (used as entry point — may be lead or any linked appointment)
                var apt = SqlMapper.Query(conn, @"
                    SELECT Id, BranchId, CheckoutStatus, Status, SaleGroupId
                    FROM dbo.AppointmentData WHERE Id = @Id",
                    new { Id = id }).FirstOrDefault();

                if (apt == null)
                    return Ok(new ApiResult<object>(false, "Appointment not found", null));

                if ((string)apt.CheckoutStatus != "checked_out")
                    return Ok(new ApiResult<object>(false,
                        "Only checked-out appointments can be voided", null));

                // Load invoice — try direct link first (checkout-drawer & legacy),
                // then via AppointmentInvoiceLines (New Sale non-lead appointments).
                var invoice = SqlMapper.Query(conn, @"
                    SELECT TOP 1 inv.Id, inv.IsFullyRefunded,
                           ISNULL(inv.IsVoid, 0) AS IsVoid,
                           inv.TotalRefunded, inv.TotalAmount
                    FROM dbo.AppointmentInvoices inv
                    WHERE inv.AppointmentId = @Id
                    UNION
                    SELECT TOP 1 inv.Id, inv.IsFullyRefunded,
                           ISNULL(inv.IsVoid, 0) AS IsVoid,
                           inv.TotalRefunded, inv.TotalAmount
                    FROM dbo.AppointmentInvoiceLines ail
                    INNER JOIN dbo.AppointmentInvoices inv ON inv.Id = ail.InvoiceId
                    WHERE ail.AppointmentId = @Id",
                    new { Id = id }).FirstOrDefault();

                if (invoice == null)
                    return Ok(new ApiResult<object>(false, "Invoice not found", null));

                if ((bool)invoice.IsVoid)
                    return Ok(new ApiResult<object>(false,
                        "Invoice is already voided", null));

                if ((bool)invoice.IsFullyRefunded)
                    return Ok(new ApiResult<object>(false,
                        "Cannot void an invoice that has already been refunded", null));

                if ((decimal)invoice.TotalRefunded > 0)
                    return Ok(new ApiResult<object>(false,
                        "Cannot void an invoice with partial refunds already processed", null));

                int invoiceId = (int)invoice.Id;

                // ── Collect ALL appointment IDs linked to this invoice ──────────
                // 1) All AppointmentInvoiceLines rows (New Sale + checkout-drawer)
                var lineApptIds = SqlMapper.Query<int>(conn, @"
                    SELECT DISTINCT AppointmentId
                    FROM dbo.AppointmentInvoiceLines
                    WHERE InvoiceId = @InvoiceId",
                    new { InvoiceId = invoiceId }).ToList();

                // 2) The lead appointment (AppointmentInvoices.AppointmentId)
                var leadApptId = (int)SqlMapper.Query<int>(conn,
                    "SELECT AppointmentId FROM dbo.AppointmentInvoices WHERE Id = @Id",
                    new { Id = invoiceId }).FirstOrDefault();

                // Union into one distinct set
                var allApptIds = lineApptIds.Union(new[] { leadApptId })
                                            .Where(x => x > 0)
                                            .Distinct()
                                            .ToList();

                // 3) Also include any other appointments sharing the same SaleGroupId
                //    (extra safety for New Sale groups)
                object saleGroupId = apt.SaleGroupId;
                if (saleGroupId != null && !(saleGroupId is DBNull))
                {
                    var groupIds = SqlMapper.Query<int>(conn, @"
                        SELECT Id FROM dbo.AppointmentData
                        WHERE SaleGroupId = @SaleGroupId",
                        new { SaleGroupId = saleGroupId }).ToList();
                    allApptIds = allApptIds.Union(groupIds).Distinct().ToList();
                }

                // ── 1) Mark invoice as void ──────────────────────────────────────
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.AppointmentInvoices SET IsVoid = 1
                    WHERE Id = @Id",
                    new { Id = invoiceId });

                // ── 2) Mark all AppointmentInvoiceLines as refunded (zero revenue) ─
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.AppointmentInvoiceLines
                    SET IsRefunded = 1, DiscountedUnitPrice = 0, TotalPrice = 0
                    WHERE InvoiceId = @InvoiceId",
                    new { InvoiceId = invoiceId });

                // ── 3) Cancel ALL linked appointments ────────────────────────────
                foreach (var apptId in allApptIds)
                {
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentData
                        SET Status         = 'cancelled',
                            CheckoutStatus = 'open',
                            DiscountedUnitPrice = 0,
                            UpdatedAt      = SYSUTCDATETIME()
                        WHERE Id = @ApptId",
                        new { ApptId = apptId });
                }

                // ── 4) Zero out checkout extra items so Staff Performance is clean ─
                if (allApptIds.Count > 0)
                {
                    SqlMapper.Execute(conn, @"
                        UPDATE dbo.AppointmentCheckoutItems
                        SET IsRefunded = 1, DiscountedUnitPrice = 0, TotalPrice = 0
                        WHERE AppointmentId IN @ApptIds",
                        new { ApptIds = allApptIds });
                }

                uow.Commit();

                return Ok(new ApiResult<object>(true, null, new
                {
                    VoidedInvoiceId = invoiceId,
                    CancelledApptIds = allApptIds
                }));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<object>(false,
                    $"Void failed (rolled back): {ex.Message}", null));
            }
        }

        // =============================================
        // GET /api/appointments/{id}/invoice
        // =============================================
        [HttpGet("{id:int}/invoice")]
        public ActionResult<ApiResult<DetailedInvoiceDto>> GetInvoice(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            // 1) Legacy / normal checkout path:
            //    invoice is directly attached to this appointment.
            var invoice = SqlMapper.Query(conn, @"
            SELECT TOP 1
                inv.Id, inv.InvoiceNumber, inv.AppointmentId,
                inv.TotalAmount, inv.PaidAmount, inv.RemainingAmount,
                inv.Currency, inv.PaymentTypeId, inv.PaymentStatus, inv.CreatedAt,
                inv.PackageOfferName, inv.PackageOfferPrice,
                ISNULL(inv.IsFullyRefunded, 0)     AS IsFullyRefunded,
                ISNULL(inv.IsPartiallyRefunded, 0) AS IsPartiallyRefunded
            FROM dbo.AppointmentInvoices inv
            WHERE inv.AppointmentId = @Id
            ORDER BY inv.Id DESC",
                new { Id = id }).FirstOrDefault();

            // 2) New Sale path:
            //    non-lead appointments do not have AppointmentInvoices.AppointmentId = @Id.
            //    They are linked to the shared lead invoice through AppointmentInvoiceLines.
            if (invoice == null)
            {
                invoice = SqlMapper.Query(conn, @"
                SELECT TOP 1
                    inv.Id, inv.InvoiceNumber, inv.AppointmentId,
                    inv.TotalAmount, inv.PaidAmount, inv.RemainingAmount,
                    inv.Currency, inv.PaymentTypeId, inv.PaymentStatus, inv.CreatedAt,
                    inv.PackageOfferName, inv.PackageOfferPrice,
                    ISNULL(inv.IsFullyRefunded, 0)     AS IsFullyRefunded,
                    ISNULL(inv.IsPartiallyRefunded, 0) AS IsPartiallyRefunded
                FROM dbo.AppointmentInvoiceLines ail
                INNER JOIN dbo.AppointmentInvoices inv ON inv.Id = ail.InvoiceId
                WHERE ail.AppointmentId = @Id
                ORDER BY ail.Id",
                    new { Id = id }).FirstOrDefault();
            }

            if (invoice == null)
                return Ok(new ApiResult<DetailedInvoiceDto>(false,
                    "Invoice not found for this appointment", null));

            int invoiceId = (int)invoice.Id;
            var lineItems = new List<InvoiceLineItemDto>();

            // Prefer AppointmentInvoiceLines when present.
            // This is the New Sale invoice shape: one shared invoice with multiple appointment lines.
            var saleLines = SqlMapper.Query(conn, @"
        SELECT 
            ail.Id                    AS Id,
            ail.AppointmentId         AS AppointmentId,
            ail.ItemId                AS ItemId,
            ail.StaffId               AS StaffId,
            ISNULL(ail.IsRefunded, 0) AS IsRefunded,
            i.ITEM_NAME1              AS ItemName,
            c.CUSTOMER_NAME           AS CustomerName,
            s.EnglishName             AS StaffName,
            1                         AS Quantity,
            ail.DiscountedUnitPrice   AS UnitPrice,
            ail.TotalPrice            AS TotalPrice
        FROM dbo.AppointmentInvoiceLines ail
        INNER JOIN dbo.ITEM i
            ON i.ITEM_ID = ail.ItemId
        INNER JOIN dbo.STAFF s
            ON s.Id = ail.StaffId
        INNER JOIN dbo.AppointmentData a
            ON a.Id = ail.AppointmentId
        INNER JOIN dbo.CUSTOMER c
            ON c.CUSTOMER_ID = a.CustomerId
        WHERE ail.InvoiceId = @InvoiceId
        ORDER BY ail.Id",
                new { InvoiceId = invoiceId }).ToList();

            if (saleLines.Count > 0)
            {
                // Mark the actual lead invoice appointment as original, not simply the first row.
                int leadAppointmentId = (int)invoice.AppointmentId;

                foreach (var sl in saleLines)
                {
                    int lineAppointmentId = (int)sl.AppointmentId;

                    lineItems.Add(new InvoiceLineItemDto(
                        Id: (int)sl.Id,
                        AppointmentId: (int)sl.AppointmentId,
                        ItemId: (int)sl.ItemId,
                        StaffId: (int)sl.StaffId,
                        IsRefunded: (bool)sl.IsRefunded,
                        ItemName: (string)(sl.ItemName ?? ""),
                        CustomerName: (string)(sl.CustomerName ?? ""),
                        StaffName: (string)(sl.StaffName ?? ""),
                        Quantity: (int)sl.Quantity,
                        UnitPrice: (decimal)sl.UnitPrice,
                        TotalPrice: (decimal)sl.TotalPrice,
                        IsOriginal: lineAppointmentId == leadAppointmentId
                    ));
                }
            }
            else
            {
                // Legacy path:
                // original appointment + checkout extra items.
                var originalItem = SqlMapper.Query(conn, @"
            SELECT 
                i.ITEM_NAME1 AS ItemName,
                c.CUSTOMER_NAME AS CustomerName,
                s.EnglishName AS StaffName,
                a.NumberOfPersons AS Quantity,
                a.DiscountedUnitPrice AS UnitPrice,
                a.DiscountedUnitPrice AS TotalPrice
            FROM dbo.AppointmentData a
            INNER JOIN dbo.ITEM i
                ON i.ITEM_ID = a.ItemId
            INNER JOIN dbo.CUSTOMER c
                ON c.CUSTOMER_ID = a.CustomerId
            INNER JOIN dbo.STAFF s
                ON s.Id = a.StaffId
            WHERE a.Id = @Id",
                    new { Id = (int)invoice.AppointmentId }).FirstOrDefault();

                if (originalItem != null)
                {
                    lineItems.Add(new InvoiceLineItemDto(
                        Id: null,
                        AppointmentId: (int)invoice.AppointmentId,
                        ItemId: null,
                        StaffId: null,
                        IsRefunded: false,
                        ItemName: (string)(originalItem.ItemName ?? ""),
                        CustomerName: (string)(originalItem.CustomerName ?? ""),
                        StaffName: (string)(originalItem.StaffName ?? ""),
                        Quantity: (int)originalItem.Quantity,
                        UnitPrice: (decimal)originalItem.UnitPrice,
                        TotalPrice: (decimal)originalItem.TotalPrice,
                        IsOriginal: true));
                }

                var extraItems = SqlMapper.Query(conn, @"
            SELECT 
                ci.Id                  AS Id,
                ci.ItemId              AS ItemId,
                ci.StaffId             AS StaffId,
                ISNULL(ci.IsRefunded, 0) AS IsRefunded,
                i.ITEM_NAME1 AS ItemName,
                c.CUSTOMER_NAME AS CustomerName,
                s.EnglishName AS StaffName,
                ci.NumberOfPersons AS Quantity,
                ci.DiscountedUnitPrice AS UnitPrice,
                ci.TotalPrice
            FROM dbo.AppointmentCheckoutItems ci
            INNER JOIN dbo.ITEM i
                ON i.ITEM_ID = ci.ItemId
            INNER JOIN dbo.CUSTOMER c
                ON c.CUSTOMER_ID = ci.CustomerId
            INNER JOIN dbo.STAFF s
                ON s.Id = ci.StaffId
            WHERE ci.AppointmentId = @Id
            ORDER BY ci.CreatedAt",
                    new { Id = (int)invoice.AppointmentId }).ToList();

                foreach (var extra in extraItems)
                {
                    lineItems.Add(new InvoiceLineItemDto(
                        Id: (int)extra.Id,
                        AppointmentId: (int)invoice.AppointmentId,
                        ItemId: (int)extra.ItemId,
                        StaffId: (int)extra.StaffId,
                        IsRefunded: (bool)extra.IsRefunded,
                        ItemName: (string)(extra.ItemName ?? ""),
                        CustomerName: (string)(extra.CustomerName ?? ""),
                        StaffName: (string)(extra.StaffName ?? ""),
                        Quantity: (int)extra.Quantity,
                        UnitPrice: (decimal)extra.UnitPrice,
                        TotalPrice: (decimal)extra.TotalPrice,
                        IsOriginal: false));
                }
            }
            // أضف query للـ payments قبل بناء الـ dto
            var invoicePayments = SqlMapper.Query(conn, @"
    SELECT 
        ap.Id,
        ap.Amount,
        ap.PaymentTypeId,
        pt.INVOICE_PAYMENT_TYPE_NAME1 AS PaymentTypeName,
        ap.PaymentAs,
        ap.VoucherCode,
        ap.PaidAt,
        ap.IsWalletPayment
    FROM dbo.AppointmentPayments ap
    LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt 
        ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
    WHERE ap.AppointmentId = @AppointmentId
    ORDER BY ap.PaidAt",
                new { AppointmentId = (int)invoice.AppointmentId })
                .Select(p => new AppointmentPaymentDetailDto(
                    Id: (int)p.Id,
                    Amount: (decimal)p.Amount,
                    PaymentTypeId: (int)p.PaymentTypeId,
                    PaymentTypeName: (bool)p.IsWalletPayment ? "Wallet" : (string)(p.PaymentTypeName ?? ""),
                    PaymentAs: (string)p.PaymentAs,
                    VoucherCode: (string?)p.VoucherCode,
                    PaidAt: (DateTime)p.PaidAt
                ))
                .ToList();

            // ── Refund lines for this invoice ─────────────────────────
            // Also join via RefundTransactionLines → AppointmentInvoiceLines → InvoiceId
            // to catch any legacy records where InvoiceId wasn't stored directly.
            var refundLines = SqlMapper.Query(conn, @"
                SELECT DISTINCT
                       rt.Id AS RefundTransactionId,
                       rt.RefundType,
                       rt.RefundAmount AS Amount,
                       rt.ProcessedAt,
                       rt.CancellationReason
                FROM dbo.RefundTransactions rt
                WHERE rt.Deleted = 0
                  AND (
                    rt.InvoiceId = @InvoiceId
                    OR rt.Id IN (
                        SELECT DISTINCT rtl.RefundTransactionId
                        FROM dbo.RefundTransactionLines rtl
                        INNER JOIN dbo.AppointmentInvoiceLines ail
                            ON ail.Id = rtl.InvoiceLineId
                        WHERE ail.InvoiceId = @InvoiceId
                    )
                  )
                ORDER BY rt.ProcessedAt",
                new { InvoiceId = invoiceId })
                .Select(r => new RefundLineDto(
                    RefundTransactionId: (int)r.RefundTransactionId,
                    RefundType: (string)r.RefundType,
                    Amount: (decimal)r.Amount,
                    ProcessedAt: (DateTime)r.ProcessedAt,
                    CancellationReason: r.CancellationReason is DBNull ? null : (string?)r.CancellationReason
                )).ToList();

            decimal totalRefunded = refundLines.Sum(r => r.Amount);
            bool isFullyRefunded = invoice.IsFullyRefunded != null && Convert.ToBoolean(invoice.IsFullyRefunded);
            bool isPartiallyRefunded = invoice.IsPartiallyRefunded != null && Convert.ToBoolean(invoice.IsPartiallyRefunded);

            var dto = new DetailedInvoiceDto(
                Id: invoiceId,
                InvoiceNumber: (string)invoice.InvoiceNumber,
                AppointmentId: (int)invoice.AppointmentId,
                TotalAmount: (decimal)invoice.TotalAmount,
                PaidAmount: (decimal)invoice.PaidAmount,
                RemainingAmount: (decimal)invoice.RemainingAmount,
                Currency: (string)invoice.Currency,
                PaymentTypeId: (int?)invoice.PaymentTypeId,
                PaymentStatus: (string)invoice.PaymentStatus,
                CreatedAt: (DateTime)invoice.CreatedAt,
                LineItems: lineItems,
                Payments: invoicePayments,
                PackageOfferName: invoice.PackageOfferName is DBNull || invoice.PackageOfferName == null
                    ? null : (string?)invoice.PackageOfferName,
                PackageOfferPrice: invoice.PackageOfferPrice is DBNull || invoice.PackageOfferPrice == null
                    ? null : (decimal?)invoice.PackageOfferPrice,
                TotalRefunded: totalRefunded,
                IsFullyRefunded: isFullyRefunded,
                IsPartiallyRefunded: isPartiallyRefunded,
                RefundLines: refundLines
            );

            return Ok(new ApiResult<DetailedInvoiceDto>(true, null, dto));
        }


        #region Private Helpers

        private AppointmentDto? GetAppointmentById(IDbConnection conn, int id)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT 
                    a.Id,
                    a.BranchId,
                    a.CustomerId,
                    c.CUSTOMER_NAME     AS CustomerName,
                    c.CUSTOMER_PHONE1   AS CustomerPhone,
                    a.ItemId,
                    i.ITEM_NAME1        AS ItemEnName,
                    i.ITEM_NAME2        AS ItemArName,
                    a.UnitId,
                    a.StaffId,
                    s.EnglishName       AS StaffEnName,
                    s.ArabicName        AS StaffArName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS AppointmentDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime, 108), 5) AS EndTime,
                    a.NumberOfPersons,
                    a.ServiceType,
                    a.IsOnlineBooking,
                    a.Notes,
                    a.UnitPrice,
                    a.DiscountPercent,
                    a.DiscountedUnitPrice,
                    a.TotalPrice,
                    a.PaidAmount,
                    (a.TotalPrice - a.PaidAmount) AS RemainingAmount,
                    a.PaymentStatus,
                    a.DepositAmount,
                    a.VoucherCode,
                    a.Status,
                    a.CheckoutStatus,
                    invRes.InvoiceId     AS InvoiceId,
                    invRes.InvoiceNumber AS InvoiceNumber,
                    a.CreatedAt,
                    (
                        SELECT TOP 1
                            CASE
                                WHEN ISNULL(pt2.OnlinePayment, 0) = 1 THEN 'ONLINE'
                                ELSE 'BRANCH'
                            END
                        FROM dbo.AppointmentPayments ap2
                        LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt2
                            ON pt2.INVOICE_PAYMENT_TYPE_ID = ap2.PaymentTypeId
                        WHERE ap2.AppointmentId = a.Id
                        ORDER BY ap2.PaidAt DESC, ap2.Id DESC
                    ) AS PaymentSource,
                    cps.Id   AS CustomerPackageSessionId,
                    pkg.EnglishName AS PackageName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c
                    ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM i
                    ON i.ITEM_ID = a.ItemId
                INNER JOIN dbo.STAFF s
                    ON s.Id = a.StaffId

                -- Resolve invoice in this priority:
                -- 1) Direct invoice: legacy checkout + New Sale lead appointment
                -- 2) Line-based invoice: New Sale non-lead appointments
                OUTER APPLY (
                    SELECT TOP 1
                        invX.Id AS InvoiceId,
                        invX.InvoiceNumber
                    FROM (
                        SELECT
                            inv1.Id,
                            inv1.InvoiceNumber,
                            1 AS Priority
                        FROM dbo.AppointmentInvoices inv1
                        WHERE inv1.AppointmentId = a.Id

                        UNION ALL

                        SELECT
                            inv2.Id,
                            inv2.InvoiceNumber,
                            2 AS Priority
                        FROM dbo.AppointmentInvoiceLines ail
                        INNER JOIN dbo.AppointmentInvoices inv2
                            ON inv2.Id = ail.InvoiceId
                        WHERE ail.AppointmentId = a.Id
                    ) invX
                    ORDER BY invX.Priority, invX.Id DESC
                ) invRes

                LEFT JOIN dbo.CustomerPackageSessions cps
                    ON cps.AppointmentId = a.Id
                   AND ISNULL(cps.Deleted, 0) = 0
                LEFT JOIN dbo.CustomerPackages cpkg
                    ON cpkg.Id = cps.CustomerPackageId
                LEFT JOIN dbo.Packages pkg
                    ON pkg.Id = cpkg.PackageId
                WHERE a.Id = @Id",
                        new { Id = id }).FirstOrDefault();

            if (row == null) return null;
            return MapRowToDto(row);
        }

        private static AppointmentDto MapRowToDto(dynamic row)
        {
            return new AppointmentDto(
                Id: (int)row.Id,
                BranchId: (int)row.BranchId,
                CustomerId: (int)row.CustomerId,
                CustomerName: (string)(row.CustomerName ?? ""),
                CustomerPhone: (string)(row.CustomerPhone ?? ""),
                ItemId: (int)row.ItemId,
                ItemEnName: (string)(row.ItemEnName ?? ""),
                ItemArName: (string)(row.ItemArName ?? ""),
                UnitId: (int)row.UnitId,
                StaffId: (int)row.StaffId,
                StaffEnName: (string)(row.StaffEnName ?? ""),
                StaffArName: (string)(row.StaffArName ?? ""),
                AppointmentDate: (string)row.AppointmentDate,
                StartTime: (string)row.StartTime,
                EndTime: (string)row.EndTime,
                NumberOfPersons: (int)row.NumberOfPersons,
                ServiceType: (string)row.ServiceType,
                IsOnlineBooking: (bool)row.IsOnlineBooking,
                Notes: (string?)row.Notes,
                UnitPrice: (decimal)row.UnitPrice,
                DiscountPercent: (decimal)row.DiscountPercent,
                DiscountedUnitPrice: (decimal)row.DiscountedUnitPrice,
                TotalPrice: (decimal)row.TotalPrice,
                PaidAmount: (decimal)row.PaidAmount,
                RemainingAmount: (decimal)row.RemainingAmount,
                PaymentStatus: (string)row.PaymentStatus,
                DepositAmount: (decimal)row.DepositAmount,
                VoucherCode: (string?)row.VoucherCode,
                Status: (string)row.Status,
                CheckoutStatus: (string)row.CheckoutStatus,
                InvoiceId: (int?)row.InvoiceId,
                InvoiceNumber: (string?)row.InvoiceNumber,
                CreatedAt: (DateTime)row.CreatedAt,
                PaymentSource: (string?)row.PaymentSource,
                CustomerPackageSessionId: (int?)row.CustomerPackageSessionId,
                PackageName: (string?)row.PackageName
            );
        }

        #endregion



        // =============================================
        // POST /api/appointments/{id}/checkout-items — Add extra service to checkout
        // =============================================
        [HttpPost("{id:int}/checkout-items")]
        public ActionResult<ApiResult<CheckoutSummaryDto>> AddCheckoutItem(
         int id, [FromBody] AddCheckoutItemRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn,
                "SELECT Id, CustomerId, NumberOfPersons, CheckoutStatus FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Appointment not found", null));

            if ((string)apt.CheckoutStatus == "checked_out")
                return Ok(new ApiResult<CheckoutSummaryDto>(false,
                    "Cannot add items to a checked-out appointment", null));

            int customerId = (int)apt.CustomerId;
            int numberOfPersons = (int)apt.NumberOfPersons;

            var staff = SqlMapper.Query(conn,
                "SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = request.StaffId }).FirstOrDefault();

            if (staff == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Staff not found or not active", null));

            var item = SqlMapper.Query(conn, @"
        SELECT iu.ITEM_UNIT_PRICE AS Price
        FROM dbo.ITEM_UNIT iu
        INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
        WHERE iu.ITEM_ID = @ItemId 
          AND iu.UNIT_ID = @UnitId 
          AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
          AND iu.Active = 1",
                new { ItemId = request.ItemId, UnitId = request.UnitId }).FirstOrDefault();

            if (item == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false,
                    "Item/Unit not found or not active", null));

            decimal unitPrice = (decimal)item.Price;
            // Price is NOT multiplied by numberOfPersons
            decimal totalPrice = unitPrice;

            SqlMapper.Execute(conn, @"
        INSERT INTO dbo.AppointmentCheckoutItems (
            AppointmentId, ItemId, UnitId, CustomerId, StaffId,
            UnitPrice, DiscountPercent, DiscountedUnitPrice,
            NumberOfPersons, TotalPrice, CreatedAt
        )
        VALUES (
            @AppointmentId, @ItemId, @UnitId, @CustomerId, @StaffId,
            @UnitPrice, 0, @UnitPrice,
            @NumberOfPersons, @TotalPrice, SYSUTCDATETIME()
        )",
                new
                {
                    AppointmentId = id,
                    request.ItemId,
                    request.UnitId,
                    CustomerId = customerId,
                    request.StaffId,
                    UnitPrice = unitPrice,
                    NumberOfPersons = numberOfPersons,
                    TotalPrice = totalPrice
                });

            RecalcAppointmentTotal(conn, id);

            var summary = GetCheckoutSummary(conn, id);
            return Ok(new ApiResult<CheckoutSummaryDto>(true, null, summary));
        }

        // =============================================
        // DELETE /api/appointments/{id}/checkout-items/{itemId}
        // =============================================
        [HttpDelete("{id:int}/checkout-items/{itemId:int}")]
        public ActionResult<ApiResult<CheckoutSummaryDto>> RemoveCheckoutItem(int id, int itemId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn,
                "SELECT Id, CheckoutStatus FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Appointment not found", null));

            if ((string)apt.CheckoutStatus == "checked_out")
                return Ok(new ApiResult<CheckoutSummaryDto>(false,
                    "Cannot remove items from a checked-out appointment", null));

            var exists = SqlMapper.Query(conn,
                "SELECT Id FROM dbo.AppointmentCheckoutItems WHERE Id = @ItemId AND AppointmentId = @AptId",
                new { ItemId = itemId, AptId = id }).FirstOrDefault();

            if (exists == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Checkout item not found", null));

            SqlMapper.Execute(conn,
                "DELETE FROM dbo.AppointmentCheckoutItems WHERE Id = @ItemId",
                new { ItemId = itemId });

            RecalcAppointmentTotal(conn, id);

            var summary = GetCheckoutSummary(conn, id);
            return Ok(new ApiResult<CheckoutSummaryDto>(true, null, summary));
        }

        // =============================================
        // GET /api/appointments/{id}/checkout-summary
        // =============================================
        [HttpGet("{id:int}/checkout-summary")]
        public ActionResult<ApiResult<CheckoutSummaryDto>> GetCheckoutSummary(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var apt = SqlMapper.Query(conn,
                "SELECT Id FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<CheckoutSummaryDto>(false, "Appointment not found", null));

            var summary = GetCheckoutSummary(conn, id);
            return Ok(new ApiResult<CheckoutSummaryDto>(true, null, summary));
        }

        #region Checkout Helpers

        private void RecalcAppointmentTotal(IDbConnection conn, int appointmentId)
        {
            // Original appointment price = DiscountedUnitPrice (NOT multiplied by persons)
            var originalPrice = SqlMapper.Query<decimal>(conn,
                "SELECT DiscountedUnitPrice FROM dbo.AppointmentData WHERE Id = @Id",
                new { Id = appointmentId }).FirstOrDefault();

            // Sum of extra items (each already stores TotalPrice = UnitPrice, not multiplied)
            var extrasTotal = SqlMapper.Query<decimal?>(conn,
                "SELECT SUM(TotalPrice) FROM dbo.AppointmentCheckoutItems WHERE AppointmentId = @Id",
                new { Id = appointmentId }).FirstOrDefault() ?? 0;

            var grandTotal = originalPrice + extrasTotal;

            SqlMapper.Execute(conn, @"
        UPDATE dbo.AppointmentData 
        SET TotalPrice = @GrandTotal, UpdatedAt = SYSUTCDATETIME()
        WHERE Id = @Id",
                new { Id = appointmentId, GrandTotal = grandTotal });
        }

        private CheckoutSummaryDto? GetCheckoutSummary(IDbConnection conn, int appointmentId)
        {
            var apt = GetAppointmentById(conn, appointmentId);
            if (apt == null) return null;

            var extraItems = SqlMapper.Query(conn, @"
        SELECT 
            ci.Id,
            ci.AppointmentId,
            ci.ItemId,
            i.ITEM_NAME1 AS ItemEnName,
            i.ITEM_NAME2 AS ItemArName,
            ci.UnitId,
            ci.CustomerId,
            c.CUSTOMER_NAME AS CustomerName,
            c.CUSTOMER_PHONE1 AS CustomerPhone,
            CAST(CASE WHEN c.CUSTOMER_IS_BLOCK = 1 THEN 1 ELSE 0 END AS BIT) AS CustomerHasAlert,
            c.CUSTOMER_BLOCK_REASON AS CustomerAlertNote,
            ci.StaffId,
            s.EnglishName AS StaffEnName,
            s.ArabicName AS StaffArName,
            ci.UnitPrice,
            ci.DiscountPercent,
            ci.DiscountedUnitPrice,
            ci.NumberOfPersons,
            ci.TotalPrice
        FROM dbo.AppointmentCheckoutItems ci
        INNER JOIN dbo.ITEM i ON i.ITEM_ID = ci.ItemId
        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = ci.CustomerId
        INNER JOIN dbo.STAFF s ON s.Id = ci.StaffId
        WHERE ci.AppointmentId = @Id
        ORDER BY ci.CreatedAt",
                new { Id = appointmentId })
                .Select(row => new CheckoutItemDto(
                    Id: (int)row.Id,
                    AppointmentId: (int)row.AppointmentId,
                    ItemId: (int)row.ItemId,
                    ItemEnName: (string)(row.ItemEnName ?? ""),
                    ItemArName: (string)(row.ItemArName ?? ""),
                    UnitId: (int)row.UnitId,
                    CustomerId: (int)row.CustomerId,
                    CustomerName: (string)(row.CustomerName ?? ""),
                    CustomerPhone: (string)(row.CustomerPhone ?? ""),
                    CustomerHasAlert: (bool)row.CustomerHasAlert,
                    CustomerAlertNote: (string?)row.CustomerAlertNote,
                    StaffId: (int)row.StaffId,
                    StaffEnName: (string)(row.StaffEnName ?? ""),
                    StaffArName: (string)(row.StaffArName ?? ""),
                    UnitPrice: (decimal)row.UnitPrice,
                    DiscountPercent: (decimal)row.DiscountPercent,
                    DiscountedUnitPrice: (decimal)row.DiscountedUnitPrice,
                    NumberOfPersons: (int)row.NumberOfPersons,
                    TotalPrice: (decimal)row.TotalPrice
                ))
                .ToList();

            // Payment history
            var payments = SqlMapper.Query(conn, @"
        SELECT 
            ap.Id,
            ap.Amount,
            ap.PaymentTypeId,
            pt.INVOICE_PAYMENT_TYPE_NAME1 AS PaymentTypeName,
            ap.PaymentAs,
            ap.VoucherCode,
            ap.PaidAt,
            ap.IsWalletPayment
        FROM dbo.AppointmentPayments ap
        LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt 
            ON pt.INVOICE_PAYMENT_TYPE_ID = ap.PaymentTypeId
        WHERE ap.AppointmentId = @Id
        ORDER BY ap.PaidAt",
                new { Id = appointmentId })
                .Select(p => new AppointmentPaymentDetailDto(
                    Id: (int)p.Id,
                    Amount: (decimal)p.Amount,
                    PaymentTypeId: (int)p.PaymentTypeId,
                    PaymentTypeName: (bool)p.IsWalletPayment ? "Wallet" : (string)(p.PaymentTypeName ?? ""),
                    PaymentAs: (string)p.PaymentAs,
                    VoucherCode: (string?)p.VoucherCode,
                    PaidAt: (DateTime)p.PaidAt
                ))
                .ToList();

            var extrasTotal = extraItems.Sum(x => x.TotalPrice);
            var grandTotal = apt.TotalPrice;
            var grandPaid = apt.PaidAmount;
            var grandRemaining = Math.Max(0, grandTotal - grandPaid);

            return new CheckoutSummaryDto(apt, extraItems, payments, grandTotal, grandPaid, grandRemaining);
        }

        #endregion
    }
}