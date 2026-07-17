// Modules/System/Controllers/AppointmentMultiApiController.cs
//
// POST /api/appointments/multi
//
// Creates a multi-service appointment: several services, each with its own
// staff, each placed independently on the calendar (next free slot, with the
// same cascade + conflict-skip logic as the offer flow).
//
// Storage model (deliberately identical to a single-service appointment so the
// existing checkout / invoice / dashboard pipeline keeps working unchanged):
//   • N AppointmentData rows sharing one SaleGroupId.
//       - Status         = 'scheduled'
//       - CheckoutStatus = 'open'      (NO invoice yet — created later at checkout)
//       - each row keeps its OWN real price.
//   • Optional deposit collected NOW is allocated across the rows (greedy: fill
//     row 1, overflow to row 2, ...). Every row that receives money gets:
//       - PaidAmount / DepositAmount / PaymentStatus set, and
//       - an AppointmentPayments row (so the dashboard counts the cash and the
//         "PendingFromDeposits" KPI tracks the remainder — same as a single
//         appointment deposit).
//
// Revenue recognition therefore matches a normal appointment: the deposit is
// real cash now; the service revenue is recognised at checkout.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

using AppointmentDtoModel = PosDashboard.Web.Modules.System.Models.AppointmentDtos.AppointmentDto;
using MultiDtos = PosDashboard.Web.Modules.System.Models.AppointmentMultiDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/appointments/multi")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AppointmentMultiApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private static readonly string[] ValidServiceTypes = { "SALON", "HOME" };

        public AppointmentMultiApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        [HttpPost]
        public ActionResult<MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>> Create(
            [FromBody] MultiDtos.AppointmentMultiRequest request)
        {
            try
            {
                if (request == null) return Fail("Request body is required");
                if (request.Lines == null || request.Lines.Count == 0)
                    return Fail("At least one service line is required");

                var serviceType = string.IsNullOrWhiteSpace(request.ServiceType)
                    ? "SALON" : request.ServiceType.ToUpperInvariant();
                if (!ValidServiceTypes.Contains(serviceType))
                    return Fail("ServiceType must be 'SALON' or 'HOME'");

                var numberOfPersons = request.NumberOfPersons < 1 ? 1 : request.NumberOfPersons;

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                // -------- Branch / customer --------
                var branch = SqlMapper.Query(conn,
                    @"SELECT BRANCH_ID, EnglishCurrencyName
                      FROM dbo.BRANCH
                      WHERE BRANCH_ID = @Id
                        AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                    new { Id = request.BranchId }).FirstOrDefault();
                if (branch == null) return Fail("Branch not found or inactive");
                string currency = (string?)branch.EnglishCurrencyName ?? "KWD";

                var customer = SqlMapper.Query(conn,
                    @"SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();
                if (customer == null) return Fail("Customer not found");

                // -------- Slot config --------
                var settings = SqlMapper.Query<(string Key, string Value)>(conn,
                    @"SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
                      FROM dbo.SYSTEM_SETTING
                      WHERE SETTING_KEY IN
                        ('calendarStartHour','calendarEndHour','AppointmentDuration','timeZoneOffset')")
                    .ToDictionary(x => x.Key, x => x.Value);

                int startHour = settings.TryGetValue("calendarStartHour", out var sh) && int.TryParse(sh, out var shi) ? shi : 10;
                int endHour = settings.TryGetValue("calendarEndHour", out var eh) && int.TryParse(eh, out var ehi) ? ehi : 22;
                int slot = settings.TryGetValue("AppointmentDuration", out var sd) && int.TryParse(sd, out var sdi) ? sdi : 5;
                int tzOffset = settings.TryGetValue("timeZoneOffset", out var tz) && int.TryParse(tz, out var tzi) ? tzi : 3;
                if (slot < 1) slot = 5;
                if (endHour <= startHour) return Fail("System calendar hours are misconfigured");

                // -------- Date + initial start --------
                var apptDate = (request.AppointmentDate ?? LocalNow(tzOffset).Date).Date;
                TimeSpan startTs;
                if (!string.IsNullOrWhiteSpace(request.StartTime))
                {
                    if (!TimeSpan.TryParseExact(request.StartTime!.Trim(), @"hh\:mm", null, out startTs))
                        return Fail("StartTime must be in HH:mm format");
                }
                else
                {
                    startTs = ResolveNearestSlot(LocalNow(tzOffset), apptDate, startHour, endHour, slot);
                }
                if (startTs >= TimeSpan.FromHours(endHour))
                    return Fail($"Calendar end hour reached ({endHour:00}:00).");
                if (startTs < TimeSpan.FromHours(startHour))
                    startTs = TimeSpan.FromHours(startHour);

                // -------- Resolve each line (real price + duration + staff) --------
                var resolved = new List<ResolvedLine>(request.Lines.Count);
                for (int i = 0; i < request.Lines.Count; i++)
                {
                    var ln = request.Lines[i];

                    var item = SqlMapper.Query(conn, @"
                        SELECT iu.ITEM_ID, i.ITEM_NAME1, i.ITEM_NAME2,
                               iu.UNIT_ID, iu.ITEM_UNIT_PRICE,
                               CAST(iu.ITEM_UNIT_DURATION AS float) AS ITEM_UNIT_DURATION
                        FROM dbo.ITEM_UNIT iu
                        INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                        WHERE iu.ITEM_ID = @ItemId AND iu.UNIT_ID = @UnitId
                          AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                          AND iu.Active = 1",
                        new { ln.ItemId, ln.UnitId }).FirstOrDefault();
                    if (item == null)
                        return Fail($"Service #{i + 1}: item/unit not found or inactive");

                    var staff = SqlMapper.Query(conn,
                        @"SELECT Id FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                        new { Id = ln.StaffId }).FirstOrDefault();
                    if (staff == null)
                        return Fail($"Service #{i + 1}: staff not found or not active");

                    int duration = ResolveDuration(ln.DurationMinutes, (double?)item.ITEM_UNIT_DURATION, slot);

                    // Sale-only price override (mirrors New Sale); null/negative => real price.
                    decimal linePrice = ln.UnitPriceOverride.HasValue && ln.UnitPriceOverride.Value >= 0m
                        ? ln.UnitPriceOverride.Value
                        : (decimal)item.ITEM_UNIT_PRICE;

                    // Optional explicit per-line start time.
                    TimeSpan? explicitStart = null;
                    if (!string.IsNullOrWhiteSpace(ln.StartTime))
                    {
                        if (!TimeSpan.TryParseExact(ln.StartTime!.Trim(), @"hh\:mm", null, out var lineStart))
                            return Fail($"Service #{i + 1}: StartTime must be in HH:mm format");
                        explicitStart = lineStart;
                    }

                    resolved.Add(new ResolvedLine
                    {
                        Index = i,
                        ItemId = (int)item.ITEM_ID,
                        UnitId = (int)item.UNIT_ID,
                        StaffId = ln.StaffId,
                        UnitPrice = linePrice,
                        DurationMinutes = duration,
                        Notes = string.IsNullOrWhiteSpace(ln.Notes) ? null : ln.Notes.Trim(),
                        ExplicitStart = explicitStart
                    });
                }

                // -------- Place each line --------
                // A line with an explicit StartTime is honored exactly (no shift and
                // no same-staff overlap check — booking two services at the same time
                // for one staff is intentional). Lines without a time cascade to the
                // next free slot as before.
                var warnings = new List<MultiDtos.AppointmentMultiLineWarning>();
                var cursor = startTs;
                foreach (var rl in resolved)
                {
                    if (rl.ExplicitStart.HasValue)
                    {
                        rl.StartTime = rl.ExplicitStart.Value;
                    }
                    else
                    {
                        rl.StartTime = FindNextAvailableStartForLine(
                            conn, rl.StaffId, apptDate, cursor, rl.DurationMinutes);
                    }
                    rl.EndTime = rl.StartTime + TimeSpan.FromMinutes(rl.DurationMinutes);

                    if (rl.EndTime >= TimeSpan.FromDays(1))
                        return Fail($"Service #{rl.Index + 1}: no slot available before midnight for this staff.");
                    if (rl.EndTime > TimeSpan.FromHours(endHour))
                        warnings.Add(new MultiDtos.AppointmentMultiLineWarning(
                            rl.Index, $"Service #{rl.Index + 1} ends after calendar end ({endHour:00}:00)."));

                    cursor = rl.EndTime;
                }

                decimal grandTotal = resolved.Sum(r => r.UnitPrice);

                // -------- Validate deposit --------
                decimal deposit = 0m;
                int? depositPtId = null;
                bool depositIsWallet = false;
                string? depositVoucher = null;
                if (request.Deposit != null)
                {
                    deposit = request.Deposit.Amount;
                    if (deposit <= 0m)
                        return Fail("Deposit amount must be greater than 0 (omit Deposit to book without paying).");
                    if (deposit - grandTotal > 0.0001m)
                        return Fail($"Deposit ({deposit:F3}) cannot exceed the total ({grandTotal:F3}).");

                    var pt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE
                          WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = request.Deposit.PaymentTypeId }).FirstOrDefault();
                    if (pt == null) return Fail($"Payment type #{request.Deposit.PaymentTypeId} not found");

                    depositPtId = request.Deposit.PaymentTypeId;
                    depositIsWallet = request.Deposit.IsWalletPayment;
                    depositVoucher = string.IsNullOrWhiteSpace(request.Deposit.VoucherCode)
                        ? null : request.Deposit.VoucherCode.Trim();
                }

                // -------- Greedy-allocate the deposit across the lines --------
                decimal toAllocate = deposit;
                foreach (var rl in resolved)
                {
                    if (toAllocate <= 0m) break;
                    var take = Math.Min(toAllocate, rl.UnitPrice);
                    rl.PaidAmount = take;
                    toAllocate -= take;
                }

                decimal paidTotal = deposit;
                decimal remaining = grandTotal - paidTotal;
                string paymentStatus =
                    paidTotal <= 0.0001m ? "NONE" :
                    Math.Abs(remaining) <= 0.0001m ? "FULL" : "DEPOSIT";

                // -------- Transaction --------
                try
                {
                    using var uow = new UnitOfWork(conn);
                    var saleGroupId = Guid.NewGuid();
                    var apptIds = new List<int>(resolved.Count);

                    foreach (var rl in resolved)
                    {
                        string rowStatus =
                            rl.PaidAmount <= 0.0001m ? "NONE" :
                            Math.Abs(rl.UnitPrice - rl.PaidAmount) <= 0.0001m ? "FULL" : "DEPOSIT";

                        int newId = SqlMapper.Query<int>(uow.Connection, @"
                            INSERT INTO dbo.AppointmentData (
                                BranchId, CustomerId, ItemId, UnitId, StaffId,
                                AppointmentDate, StartTime, EndTime,
                                NumberOfPersons, ServiceType, IsOnlineBooking, Notes,
                                UnitPrice, DiscountPercent, DiscountedUnitPrice, TotalPrice,
                                PaidAmount, PaymentStatus, DepositAmount,
                                Status, CheckoutStatus, CreatedAt, SaleGroupId
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                @BranchId, @CustomerId, @ItemId, @UnitId, @StaffId,
                                @AppointmentDate, @StartTime, @EndTime,
                                @NumberOfPersons, @ServiceType, 0, @Notes,
                                @UnitPrice, 0, @UnitPrice, @UnitPrice,
                                @PaidAmount, @PaymentStatus, @DepositAmount,
                                'scheduled', 'open', SYSUTCDATETIME(), @SaleGroupId
                            )",
                            new
                            {
                                request.BranchId,
                                request.CustomerId,
                                rl.ItemId,
                                rl.UnitId,
                                rl.StaffId,
                                AppointmentDate = apptDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                NumberOfPersons = numberOfPersons,
                                ServiceType = serviceType,
                                Notes = rl.Notes ?? request.Notes,
                                rl.UnitPrice,
                                rl.PaidAmount,
                                PaymentStatus = rowStatus,
                                DepositAmount = rl.PaidAmount,  // amount paid up-front for this row
                                SaleGroupId = saleGroupId
                            }).First();

                        rl.AppointmentId = newId;
                        apptIds.Add(newId);

                        // Record the cash so the dashboard counts it.
                        if (rl.PaidAmount > 0m && depositPtId.HasValue)
                        {
                            SqlMapper.Execute(uow.Connection, @"
                                INSERT INTO dbo.AppointmentPayments (
                                    AppointmentId, Amount, PaymentTypeId,
                                    PaymentAs, VoucherCode, PaidAt, IsWalletPayment
                                )
                                VALUES (
                                    @AppointmentId, @Amount, @PaymentTypeId,
                                    @PaymentAs, @VoucherCode, SYSUTCDATETIME(), @IsWalletPayment
                                )",
                                new
                                {
                                    AppointmentId = newId,
                                    Amount = rl.PaidAmount,
                                    PaymentTypeId = depositPtId.Value,
                                    PaymentAs = rowStatus == "FULL" ? "FULL" : "DEPOSIT",
                                    VoucherCode = depositVoucher,
                                    IsWalletPayment = depositIsWallet
                                });
                        }
                    }

                    uow.Commit();

                    var dtos = apptIds.Select(id => GetAppointmentById(conn, id))
                                      .Where(d => d != null).Select(d => d!).ToList();

                    var response = new MultiDtos.AppointmentMultiResponse(
                        SaleGroupId: saleGroupId,
                        LeadAppointmentId: apptIds[0],
                        AppointmentIds: apptIds,
                        Appointments: dtos,
                        TotalAmount: grandTotal,
                        PaidAmount: paidTotal,
                        RemainingAmount: remaining,
                        PaymentStatus: paymentStatus,
                        Currency: currency,
                        Warnings: warnings);

                    return Ok(new MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>(true, null, response));
                }
                catch (Exception ex)
                {
                    return Ok(new MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>(
                        false, $"Failed to create multi-service appointment: {ex.Message}", null));
                }
            }
            catch (Exception ex)
            {
                return Ok(new MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>(
                    false, $"Multi-service booking failed before transaction: {ex.Message}", null));
            }
        }

        // ===== Helpers (parallel to the offer controller) =====

        private ActionResult<MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>> Fail(string error) =>
            Ok(new MultiDtos.ApiResult<MultiDtos.AppointmentMultiResponse>(false, error, null));

        private static DateTime LocalNow(int tzOffsetHours) => DateTime.UtcNow.AddHours(tzOffsetHours);

        private static TimeSpan ResolveNearestSlot(
            DateTime localNow, DateTime apptDate, int startHour, int endHour, int slotMinutes)
        {
            if (apptDate.Date != localNow.Date) return TimeSpan.FromHours(startHour);
            int totalMins = localNow.Hour * 60 + localNow.Minute;
            int snapped = ((totalMins + slotMinutes - 1) / slotMinutes) * slotMinutes;
            int minOfStart = startHour * 60, minOfEnd = endHour * 60;
            if (snapped < minOfStart) snapped = minOfStart;
            if (snapped > minOfEnd) snapped = minOfEnd;
            return TimeSpan.FromMinutes(snapped);
        }

        private static TimeSpan FindNextAvailableStartForLine(
            IDbConnection conn, int staffId, DateTime apptDate,
            TimeSpan desiredStart, int durationMinutes)
        {
            var occupied = SqlMapper.Query(conn, @"
                SELECT StartTime, EndTime FROM dbo.AppointmentData
                WHERE StaffId = @StaffId AND AppointmentDate = @Date AND Status != 'cancelled'
                UNION ALL
                SELECT StartTime, EndTime FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId AND BlockDate = @Date AND Deleted = 0 AND IsRecurring = 0",
                new { StaffId = staffId, Date = apptDate.Date })
                .Select(r => (Start: (TimeSpan)r.StartTime, End: (TimeSpan)r.EndTime))
                .OrderBy(s => s.Start).ToList();

            var dur = TimeSpan.FromMinutes(durationMinutes);
            var cursor = desiredStart;
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var s in occupied)
                {
                    if (cursor < s.End && cursor + dur > s.Start)
                    {
                        cursor = s.End;
                        moved = true;
                    }
                }
            }
            return cursor;
        }

        private static int ResolveDuration(int? lineOverride, double? itemUnitDuration, int slotMinutes)
        {
            if (lineOverride.HasValue && lineOverride.Value > 0) return lineOverride.Value;
            if (itemUnitDuration.HasValue && itemUnitDuration.Value > 0) return (int)Math.Ceiling(itemUnitDuration.Value);
            return Math.Max(slotMinutes, 5);
        }

        private static AppointmentDtoModel? GetAppointmentById(IDbConnection conn, int id)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT
                    a.Id, a.BranchId, a.CustomerId,
                    c.CUSTOMER_NAME AS CustomerName, c.CUSTOMER_PHONE1 AS CustomerPhone,
                    a.ItemId, i.ITEM_NAME1 AS ItemEnName, i.ITEM_NAME2 AS ItemArName,
                    a.UnitId, a.StaffId, s.EnglishName AS StaffEnName, s.ArabicName AS StaffArName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120)   AS AppointmentDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5) AS EndTime,
                    a.NumberOfPersons, a.ServiceType, a.IsOnlineBooking, a.Notes,
                    a.UnitPrice, a.DiscountPercent, a.DiscountedUnitPrice,
                    a.TotalPrice, a.PaidAmount,
                    (a.TotalPrice - a.PaidAmount) AS RemainingAmount,
                    a.PaymentStatus, a.DepositAmount, a.VoucherCode,
                    a.Status, a.CheckoutStatus,
                    inv.Id AS InvoiceId, inv.InvoiceNumber, a.CreatedAt,
                    (
                        SELECT TOP 1 CASE WHEN ISNULL(pt2.OnlinePayment,0)=1 THEN 'ONLINE' ELSE 'BRANCH' END
                        FROM dbo.AppointmentPayments ap2
                        LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt2 ON pt2.INVOICE_PAYMENT_TYPE_ID = ap2.PaymentTypeId
                        WHERE ap2.AppointmentId = a.Id ORDER BY ap2.PaidAt DESC, ap2.Id DESC
                    ) AS PaymentSource,
                    cps.Id AS CustomerPackageSessionId, pkg.EnglishName AS PackageName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM     i ON i.ITEM_ID     = a.ItemId
                INNER JOIN dbo.STAFF    s ON s.Id          = a.StaffId
                LEFT JOIN dbo.AppointmentInvoices     inv  ON inv.AppointmentId = a.Id
                LEFT JOIN dbo.CustomerPackageSessions cps  ON cps.AppointmentId = a.Id AND ISNULL(cps.Deleted,0)=0
                LEFT JOIN dbo.CustomerPackages        cpkg ON cpkg.Id = cps.CustomerPackageId
                LEFT JOIN dbo.Packages                pkg  ON pkg.Id  = cpkg.PackageId
                WHERE a.Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (row == null) return null;

            return new AppointmentDtoModel(
                Id: (int)row.Id, BranchId: (int)row.BranchId, CustomerId: (int)row.CustomerId,
                CustomerName: (string)(row.CustomerName ?? ""), CustomerPhone: (string)(row.CustomerPhone ?? ""),
                ItemId: (int)row.ItemId, ItemEnName: (string)(row.ItemEnName ?? ""), ItemArName: (string)(row.ItemArName ?? ""),
                UnitId: (int)row.UnitId, StaffId: (int)row.StaffId,
                StaffEnName: (string)(row.StaffEnName ?? ""), StaffArName: (string)(row.StaffArName ?? ""),
                AppointmentDate: (string)row.AppointmentDate, StartTime: (string)row.StartTime, EndTime: (string)row.EndTime,
                NumberOfPersons: (int)row.NumberOfPersons, ServiceType: (string)row.ServiceType,
                IsOnlineBooking: (bool)row.IsOnlineBooking, Notes: (string?)row.Notes,
                UnitPrice: (decimal)row.UnitPrice, DiscountPercent: (decimal)row.DiscountPercent,
                DiscountedUnitPrice: (decimal)row.DiscountedUnitPrice, TotalPrice: (decimal)row.TotalPrice,
                PaidAmount: (decimal)row.PaidAmount, RemainingAmount: (decimal)row.RemainingAmount,
                PaymentStatus: (string)row.PaymentStatus, DepositAmount: (decimal)row.DepositAmount,
                VoucherCode: (string?)row.VoucherCode, Status: (string)row.Status, CheckoutStatus: (string)row.CheckoutStatus,
                InvoiceId: (int?)row.InvoiceId, InvoiceNumber: (string?)row.InvoiceNumber, CreatedAt: (DateTime)row.CreatedAt,
                PaymentSource: (string?)row.PaymentSource,
                CustomerPackageSessionId: (int?)row.CustomerPackageSessionId, PackageName: (string?)row.PackageName);
        }

        private sealed class ResolvedLine
        {
            public int Index { get; set; }
            public int ItemId { get; set; }
            public int UnitId { get; set; }
            public int StaffId { get; set; }
            public decimal UnitPrice { get; set; }
            public int DurationMinutes { get; set; }
            public string? Notes { get; set; }
            public TimeSpan? ExplicitStart { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public decimal PaidAmount { get; set; }
            public int AppointmentId { get; set; }
        }
    }
}