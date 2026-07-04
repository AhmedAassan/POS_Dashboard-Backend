// Modules/System/Controllers/AppointmentOfferApiController.cs
//
// POST /api/appointments/package-offer
//
// Books a "Package OFFER" as a multi-service appointment. This is the
// appointment-side twin of NewSaleApiController's offer path:
//
//   • one OFFER package  ->  several services
//   • each service has its OWN staff (chosen by the manager)
//   • each service is placed INDEPENDENTLY on the calendar (next free slot
//     per staff), exactly like New Sale
//   • the total is ALWAYS the package price
//
// The single difference from New Sale: payment is NOT required. The booking
// is created regardless. An optional up-front deposit may be collected.
//
// Storage model (mirrors New Sale's consolidated invoice):
//   • N AppointmentData rows, one per service, sharing one SaleGroupId.
//       - Status        = 'scheduled'      (services not performed yet)
//       - CheckoutStatus = 'checked_out'   (the invoice already exists, so the
//                                            normal Checkout endpoint won't
//                                            create a duplicate, and ApplyPayment
//                                            keeps the invoice in sync)
//       - LEAD row carries the money: TotalPrice = package price,
//         PaidAmount = deposit, DepositAmount, PaymentStatus.
//       - the other rows are zero-priced (TotalPrice = 0, PaymentStatus 'FULL').
//   • one AppointmentInvoices row holding the package total + offer metadata.
//   • one AppointmentInvoiceLines row per service.
//   • one AppointmentPayments row on the lead row when a deposit is taken.
//
// To collect the remaining balance later, the existing
// POST /api/appointments/{leadId}/payments endpoint works unchanged.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using PosDashboard.Web.Modules.System.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

using AppointmentDtoModel = PosDashboard.Web.Modules.System.Models.AppointmentDtos.AppointmentDto;
using OfferDtos = PosDashboard.Web.Modules.System.Models.AppointmentOfferDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/appointments/package-offer")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AppointmentOfferApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        private static readonly string[] ValidServiceTypes = { "SALON", "HOME" };

        public AppointmentOfferApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // =========================================================================
        // POST /api/appointments/package-offer
        // =========================================================================
        [HttpPost]
        public ActionResult<OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>> Create(
            [FromBody] OfferDtos.AppointmentOfferRequest request)
        {
            try
            {
                // -------- Basic shape validation --------
                if (request == null)
                    return Fail("Request body is required");

                if (request.PackageOfferId <= 0)
                    return Fail("PackageOfferId is required");

                if (request.Lines == null || request.Lines.Count == 0)
                    return Fail("At least one service line is required");

                var serviceType = string.IsNullOrWhiteSpace(request.ServiceType)
                    ? "SALON"
                    : request.ServiceType.ToUpperInvariant();

                if (!ValidServiceTypes.Contains(serviceType))
                    return Fail("ServiceType must be 'SALON' or 'HOME'");

                var numberOfPersons = request.NumberOfPersons < 1 ? 1 : request.NumberOfPersons;

                // -------- Open connection --------
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open)
                    conn.Open();

                // -------- Validate branch --------
                var branch = SqlMapper.Query(conn,
                    @"SELECT BRANCH_ID, EnglishCurrencyName, ArabicCurrencyName
                      FROM dbo.BRANCH
                      WHERE BRANCH_ID = @Id
                        AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                    new { Id = request.BranchId }).FirstOrDefault();

                if (branch == null)
                    return Fail("Branch not found or inactive");

                string currency = (string?)branch.EnglishCurrencyName ?? "KWD";

                // -------- Validate customer --------
                var customer = SqlMapper.Query(conn,
                    @"SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1
                      FROM dbo.CUSTOMER
                      WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();

                if (customer == null)
                    return Fail("Customer not found");

                // -------- Validate the OFFER package + load its price --------
                var pkg = SqlMapper.Query(conn, @"
                    SELECT Id, EnglishName, ArabicName, Amount
                    FROM dbo.Packages
                    WHERE Id = @Id AND ISNULL(OFFER,0) = 1 AND ISNULL(Deleted,0) = 0",
                    new { Id = request.PackageOfferId }).FirstOrDefault();

                if (pkg == null) return Fail("Package OFFER not found");

                string packageOfferName = (string)(pkg.EnglishName ?? "");
                decimal grandTotal = (decimal)pkg.Amount;

                // -------- Load the package items (the services it contains) --------
                var pkgItems = SqlMapper.Query(conn, @"
                    SELECT
                        pi.ItemUnitId,
                        iu.ITEM_ID                                     AS ItemId,
                        iu.UNIT_ID                                     AS UnitId,
                        iu.ITEM_UNIT_PRICE                             AS ItemPrice,
                        CAST(iu.ITEM_UNIT_DURATION AS float)           AS Duration,
                        i.ITEM_NAME1                                   AS ItemEnName,
                        i.ITEM_NAME2                                   AS ItemArName
                    FROM dbo.PackageItems pi
                    INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = pi.ItemUnitId
                    INNER JOIN dbo.ITEM      i  ON i.ITEM_ID        = iu.ITEM_ID
                    WHERE pi.PackageId = @PackageId
                      AND ISNULL(pi.Deleted, 0) = 0
                    ORDER BY pi.Id",
                    new { PackageId = request.PackageOfferId }).ToList();

                if (pkgItems.Count == 0)
                    return Fail("Package OFFER has no services");

                // -------- System slot config --------
                var settings = SqlMapper.Query<(string Key, string Value)>(conn,
                    @"SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
                      FROM dbo.SYSTEM_SETTING
                      WHERE SETTING_KEY IN (
                          'calendarStartHour',
                          'calendarEndHour',
                          'AppointmentDuration',
                          'timeZoneOffset'
                      )")
                    .ToDictionary(x => x.Key, x => x.Value);

                int startHour =
                    settings.TryGetValue("calendarStartHour", out var sh) &&
                    int.TryParse(sh, out var shi) ? shi : 10;

                int endHour =
                    settings.TryGetValue("calendarEndHour", out var eh) &&
                    int.TryParse(eh, out var ehi) ? ehi : 22;

                int slot =
                    settings.TryGetValue("AppointmentDuration", out var sd) &&
                    int.TryParse(sd, out var sdi) ? sdi : 5;

                int tzOffset =
                    settings.TryGetValue("timeZoneOffset", out var tz) &&
                    int.TryParse(tz, out var tzi) ? tzi : 3;

                if (slot < 1) slot = 5;
                if (endHour <= startHour)
                    return Fail("System calendar hours are misconfigured");

                // -------- Resolve appointment date and initial starting slot --------
                var apptDate = (request.AppointmentDate ?? LocalNow(tzOffset).Date).Date;
                TimeSpan startTs;

                if (!string.IsNullOrWhiteSpace(request.StartTime))
                {
                    if (!TimeSpan.TryParseExact(
                            request.StartTime!.Trim(), @"hh\:mm", null, out startTs))
                        return Fail("StartTime must be in HH:mm format");
                }
                else
                {
                    startTs = ResolveNearestSlot(
                        LocalNow(tzOffset), apptDate, startHour, endHour, slot);
                }

                if (startTs >= TimeSpan.FromHours(endHour))
                    return Fail($"Calendar end hour reached ({endHour:00}:00). " +
                                "Pick a different start time or move to the next day.");
                if (startTs >= TimeSpan.FromDays(1))
                    return Fail("Start time cannot be 24:00 or later.");
                if (startTs < TimeSpan.FromHours(startHour))
                    startTs = TimeSpan.FromHours(startHour);

                // -------- Build one resolved line per package item (with chosen staff) --------
                var resolvedLines = new List<ResolvedLine>(pkgItems.Count);

                for (int i = 0; i < pkgItems.Count; i++)
                {
                    var pkItem = pkgItems[i];
                    int itemUnitId = (int)pkItem.ItemUnitId;

                    // Find the staff the manager picked for THIS package service.
                    var lineReq = request.Lines.FirstOrDefault(l => l.ItemUnitId == itemUnitId);
                    if (lineReq == null)
                        return Fail(
                            $"No staff assigned for service '{(string)(pkItem.ItemEnName ?? "")}'.");

                    var staff = SqlMapper.Query(conn,
                        @"SELECT Id, ArabicName, EnglishName
                          FROM dbo.STAFF
                          WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                        new { Id = lineReq.StaffId }).FirstOrDefault();

                    if (staff == null)
                        return Fail(
                            $"Service #{i + 1}: staff not found or not active");

                    int duration = ResolveDuration(
                        lineReq.DurationMinutes, (double?)pkItem.Duration, slot);

                    resolvedLines.Add(new ResolvedLine
                    {
                        Index = i,
                        ItemId = (int)pkItem.ItemId,
                        UnitId = (int)pkItem.UnitId,
                        StaffId = lineReq.StaffId,
                        ItemEnName = (string?)pkItem.ItemEnName ?? "",
                        ItemArName = (string?)pkItem.ItemArName ?? "",
                        StaffEnName = (string?)staff.EnglishName ?? "",
                        StaffArName = (string?)staff.ArabicName ?? "",
                        DurationMinutes = duration,
                        // Per-line price is informational only; the package price is the total.
                        UnitPrice = (decimal)pkItem.ItemPrice,
                        SalePrice = 0m,
                        Notes = string.IsNullOrWhiteSpace(lineReq.Notes)
                                ? null : lineReq.Notes.Trim()
                    });
                }

                // -------- Assign each line its own slot (next free per staff) --------
                var warnings = new List<OfferDtos.AppointmentOfferLineWarning>();
                var scheduleCursor = startTs;

                for (int i = 0; i < resolvedLines.Count; i++)
                {
                    var rl = resolvedLines[i];

                    var availableStart = FindNextAvailableStartForLine(
                        conn,
                        staffId: rl.StaffId,
                        apptDate: apptDate,
                        desiredStart: scheduleCursor,
                        durationMinutes: rl.DurationMinutes);

                    rl.StartTime = availableStart;
                    rl.EndTime = availableStart + TimeSpan.FromMinutes(rl.DurationMinutes);

                    if (rl.EndTime >= TimeSpan.FromDays(1))
                        return Fail(
                            $"Service #{rl.Index + 1}: no available slot before midnight " +
                            "for this staff member on this day.");

                    if (rl.EndTime > TimeSpan.FromHours(endHour))
                        warnings.Add(new OfferDtos.AppointmentOfferLineWarning(
                            rl.Index,
                            $"Service #{rl.Index + 1} ends after calendar end ({endHour:00}:00)."));

                    scheduleCursor = rl.EndTime;
                }

                // -------- Same-staff overlap guard inside this booking --------
                for (int i = 0; i < resolvedLines.Count; i++)
                    for (int j = i + 1; j < resolvedLines.Count; j++)
                    {
                        var a = resolvedLines[i];
                        var b = resolvedLines[j];
                        if (a.StaffId != b.StaffId) continue;
                        if (a.StartTime < b.EndTime && a.EndTime > b.StartTime)
                            return Fail(
                                $"Services #{i + 1} and #{j + 1} overlap for the same staff " +
                                "after auto-scheduling. Please assign different staff.");
                    }

                // -------- Validate the (optional) deposit --------
                decimal deposit = 0m;
                int? depositPtId = null;
                bool depositIsWallet = false;
                string? depositVoucher = null;

                if (request.Deposit != null)
                {
                    deposit = request.Deposit.Amount;
                    if (deposit <= 0m)
                        return Fail("Deposit amount must be greater than 0 (omit Deposit for no payment).");
                    if (deposit - grandTotal > 0.0001m)
                        return Fail(
                            $"Deposit ({deposit:F3}) cannot exceed the package price ({grandTotal:F3}).");

                    var pt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE
                          WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = request.Deposit.PaymentTypeId }).FirstOrDefault();

                    if (pt == null)
                        return Fail($"Payment type #{request.Deposit.PaymentTypeId} not found");

                    depositPtId = request.Deposit.PaymentTypeId;
                    depositIsWallet = request.Deposit.IsWalletPayment;
                    depositVoucher = string.IsNullOrWhiteSpace(request.Deposit.VoucherCode)
                        ? null : request.Deposit.VoucherCode.Trim();
                }

                decimal paidTotal = deposit;
                decimal remaining = grandTotal - paidTotal;

                string paymentStatus =
                    paidTotal <= 0.0001m ? "NONE" :
                    Math.Abs(remaining) <= 0.0001m ? "FULL" :
                    "DEPOSIT";

                string leadPaymentAs = paymentStatus == "FULL" ? "FULL" : "DEPOSIT";
                decimal leadDepositAmount = paymentStatus == "DEPOSIT" ? paidTotal : 0m;

                // -------- Atomic transaction --------
                try
                {
                    using var uow = new UnitOfWork(conn);

                    var saleGroupId = Guid.NewGuid();
                    var apptIds = new List<int>(resolvedLines.Count);

                    // 1) AppointmentData — one row per service.
                    //    Money lives on the LEAD (first) row; the rest are zero-priced.
                    for (int idx = 0; idx < resolvedLines.Count; idx++)
                    {
                        var rl = resolvedLines[idx];
                        bool isLead = idx == 0;

                        decimal rowTotal = isLead ? grandTotal : 0m;
                        decimal rowPaid = isLead ? paidTotal : 0m;
                        decimal rowDeposit = isLead ? leadDepositAmount : 0m;
                        string rowPayStatus = isLead ? paymentStatus : "FULL";

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
                                @UnitPrice, 0, @DiscountedUnitPrice, @TotalPrice,
                                @PaidAmount, @PaymentStatus, @DepositAmount,
                                'scheduled', 'checked_out', SYSUTCDATETIME(), @SaleGroupId
                            )",
                            new
                            {
                                BranchId = request.BranchId,
                                CustomerId = request.CustomerId,
                                rl.ItemId,
                                rl.UnitId,
                                rl.StaffId,
                                AppointmentDate = apptDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                NumberOfPersons = numberOfPersons,
                                ServiceType = serviceType,
                                Notes = rl.Notes ?? request.Notes,
                                UnitPrice = rl.UnitPrice,
                                DiscountedUnitPrice = rowTotal,
                                TotalPrice = rowTotal,
                                PaidAmount = rowPaid,
                                PaymentStatus = rowPayStatus,
                                DepositAmount = rowDeposit,
                                SaleGroupId = saleGroupId
                            }).First();

                        apptIds.Add(newId);
                    }

                    int leadApptId = apptIds[0];
                    string invoiceNumber = InvoiceNumberService.Next(uow.Connection, InvoiceNumberService.PrefixInvoice);

                    // 2) Shared invoice (carries the package total + offer metadata).
                    //    PaymentTypeId is null when no deposit was taken.
                    int invoiceId = SqlMapper.Query<int>(uow.Connection, @"
                        INSERT INTO dbo.AppointmentInvoices (
                            InvoiceNumber, AppointmentId, BranchId, CustomerId,
                            TotalAmount, PaidAmount, RemainingAmount, Currency,
                            PaymentTypeId, PaymentStatus, CreatedAt,
                            PackageOfferId, PackageOfferName, PackageOfferPrice
                        )
                        OUTPUT INSERTED.Id
                        VALUES (
                            @InvoiceNumber, @AppointmentId, @BranchId, @CustomerId,
                            @TotalAmount, @PaidAmount, @RemainingAmount, @Currency,
                            @PaymentTypeId, @PaymentStatus, SYSUTCDATETIME(),
                            @PackageOfferId, @PackageOfferName, @PackageOfferPrice
                        )",
                        new
                        {
                            InvoiceNumber = invoiceNumber,
                            AppointmentId = leadApptId,
                            BranchId = request.BranchId,
                            CustomerId = request.CustomerId,
                            TotalAmount = grandTotal,
                            PaidAmount = paidTotal,
                            RemainingAmount = remaining,
                            Currency = currency,
                            PaymentTypeId = depositPtId,
                            PaymentStatus = paymentStatus,
                            PackageOfferId = request.PackageOfferId,
                            PackageOfferName = packageOfferName,
                            PackageOfferPrice = grandTotal
                        }).First();

                    // 3) Invoice lines — one per service.
                    for (int idx = 0; idx < resolvedLines.Count; idx++)
                    {
                        var rl = resolvedLines[idx];
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.AppointmentInvoiceLines (
                                InvoiceId, AppointmentId, ItemId, UnitId, StaffId,
                                UnitPrice, DiscountedUnitPrice, TotalPrice,
                                AppointmentDate, StartTime, EndTime, DurationMinutes, Notes
                            )
                            VALUES (
                                @InvoiceId, @AppointmentId, @ItemId, @UnitId, @StaffId,
                                @UnitPrice, 0, 0,
                                @AppointmentDate, @StartTime, @EndTime,
                                @DurationMinutes, @Notes
                            )",
                            new
                            {
                                InvoiceId = invoiceId,
                                AppointmentId = apptIds[idx],
                                rl.ItemId,
                                rl.UnitId,
                                rl.StaffId,
                                rl.UnitPrice,
                                AppointmentDate = apptDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                rl.DurationMinutes,
                                rl.Notes
                            });
                    }

                    // 4) Deposit payment row (on the lead appointment).
                    if (paidTotal > 0m && depositPtId.HasValue)
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
                                AppointmentId = leadApptId,
                                Amount = paidTotal,
                                PaymentTypeId = depositPtId.Value,
                                PaymentAs = leadPaymentAs,
                                VoucherCode = depositVoucher,
                                IsWalletPayment = depositIsWallet
                            });
                    }

                    uow.Commit();

                    // -------- After commit: read back the rows --------
                    var apptDtos = apptIds
                        .Select(id => GetAppointmentById(conn, id))
                        .Where(d => d != null)
                        .Select(d => d!)
                        .ToList();

                    var response = new OfferDtos.AppointmentOfferResponse(
                        InvoiceId: invoiceId,
                        InvoiceNumber: invoiceNumber,
                        LeadAppointmentId: leadApptId,
                        SaleGroupId: saleGroupId,
                        AppointmentIds: apptIds,
                        Appointments: apptDtos,
                        TotalAmount: grandTotal,
                        PaidAmount: paidTotal,
                        RemainingAmount: remaining,
                        PaymentStatus: paymentStatus,
                        Currency: currency,
                        PackageOfferId: request.PackageOfferId,
                        PackageOfferName: packageOfferName,
                        PackageOfferPrice: grandTotal,
                        Warnings: warnings);

                    return Ok(new OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>(
                        true, null, response));
                }
                catch (Exception ex)
                {
                    return Ok(new OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>(
                        false, $"Failed to create offer booking: {ex.Message}", null));
                }
            }
            catch (Exception ex)
            {
                return Ok(new OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>(
                    false, $"Offer booking failed before transaction: {ex.Message}", null));
            }
        }

        // =========================================================================
        // Private helpers (parallel to NewSaleApiController)
        // =========================================================================

        private ActionResult<OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>>
            Fail(string error) =>
            Ok(new OfferDtos.ApiResult<OfferDtos.AppointmentOfferResponse>(false, error, null));

        private static DateTime LocalNow(int tzOffsetHours) =>
            DateTime.UtcNow.AddHours(tzOffsetHours);

        private static TimeSpan ResolveNearestSlot(
            DateTime localNow, DateTime apptDate,
            int startHour, int endHour, int slotMinutes)
        {
            if (apptDate.Date != localNow.Date)
                return TimeSpan.FromHours(startHour);

            int totalMins = localNow.Hour * 60 + localNow.Minute;
            int snapped = ((totalMins + slotMinutes - 1) / slotMinutes) * slotMinutes;
            int minOfStart = startHour * 60;
            int minOfEnd = endHour * 60;

            if (snapped < minOfStart) snapped = minOfStart;
            if (snapped > minOfEnd) snapped = minOfEnd;

            return TimeSpan.FromMinutes(snapped);
        }

        /// <summary>
        /// First TimeSpan free for the staff from desiredStart, skipping existing
        /// appointments and non-recurring time blocks.
        /// </summary>
        private static TimeSpan FindNextAvailableStartForLine(
            IDbConnection conn,
            int staffId,
            DateTime apptDate,
            TimeSpan desiredStart,
            int durationMinutes)
        {
            var existingAppts = SqlMapper.Query(conn, @"
                SELECT StartTime, EndTime
                FROM dbo.AppointmentData
                WHERE StaffId         = @StaffId
                  AND AppointmentDate = @Date
                  AND Status         != 'cancelled'
                ORDER BY StartTime",
                new { StaffId = staffId, Date = apptDate.Date })
                .Select(r => (Start: (TimeSpan)r.StartTime, End: (TimeSpan)r.EndTime))
                .ToList();

            var blockSlots = SqlMapper.Query(conn, @"
                SELECT StartTime, EndTime
                FROM dbo.StaffTimeBlocks
                WHERE StaffId     = @StaffId
                  AND BlockDate    = @Date
                  AND Deleted      = 0
                  AND IsRecurring  = 0
                ORDER BY StartTime",
                new { StaffId = staffId, Date = apptDate.Date })
                .Select(r => (Start: (TimeSpan)r.StartTime, End: (TimeSpan)r.EndTime))
                .ToList();

            var allOccupied = existingAppts
                .Concat(blockSlots)
                .OrderBy(s => s.Start)
                .ToList();

            var dur = TimeSpan.FromMinutes(durationMinutes);
            var cursor = desiredStart;

            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var s in allOccupied)
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

        private static int ResolveDuration(
            int? lineOverride, double? itemUnitDuration, int slotMinutes)
        {
            if (lineOverride.HasValue && lineOverride.Value > 0)
                return lineOverride.Value;
            if (itemUnitDuration.HasValue && itemUnitDuration.Value > 0)
                return (int)Math.Ceiling(itemUnitDuration.Value);
            return Math.Max(slotMinutes, 5);
        }



        private static AppointmentDtoModel? GetAppointmentById(IDbConnection conn, int id)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT
                    a.Id, a.BranchId, a.CustomerId,
                    c.CUSTOMER_NAME   AS CustomerName,
                    c.CUSTOMER_PHONE1 AS CustomerPhone,
                    a.ItemId,
                    i.ITEM_NAME1      AS ItemEnName,
                    i.ITEM_NAME2      AS ItemArName,
                    a.UnitId, a.StaffId,
                    s.EnglishName     AS StaffEnName,
                    s.ArabicName      AS StaffArName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120)   AS AppointmentDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5) AS EndTime,
                    a.NumberOfPersons, a.ServiceType, a.IsOnlineBooking, a.Notes,
                    a.UnitPrice, a.DiscountPercent, a.DiscountedUnitPrice,
                    a.TotalPrice, a.PaidAmount,
                    (a.TotalPrice - a.PaidAmount) AS RemainingAmount,
                    a.PaymentStatus, a.DepositAmount, a.VoucherCode,
                    a.Status, a.CheckoutStatus,
                    inv.Id           AS InvoiceId,
                    inv.InvoiceNumber,
                    a.CreatedAt,
                    (
                        SELECT TOP 1
                            CASE WHEN ISNULL(pt2.OnlinePayment, 0) = 1
                                 THEN 'ONLINE' ELSE 'BRANCH' END
                        FROM dbo.AppointmentPayments ap2
                        LEFT JOIN dbo.INVOICE_PAYMENT_TYPE pt2
                            ON pt2.INVOICE_PAYMENT_TYPE_ID = ap2.PaymentTypeId
                        WHERE ap2.AppointmentId = a.Id
                        ORDER BY ap2.PaidAt DESC, ap2.Id DESC
                    ) AS PaymentSource,
                    cps.Id          AS CustomerPackageSessionId,
                    pkg.EnglishName AS PackageName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c   ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM     i   ON i.ITEM_ID     = a.ItemId
                INNER JOIN dbo.STAFF    s   ON s.Id          = a.StaffId
                LEFT JOIN dbo.AppointmentInvoices     inv  ON inv.AppointmentId  = a.Id
                LEFT JOIN dbo.CustomerPackageSessions cps  ON cps.AppointmentId  = a.Id
                                                           AND ISNULL(cps.Deleted, 0) = 0
                LEFT JOIN dbo.CustomerPackages        cpkg ON cpkg.Id = cps.CustomerPackageId
                LEFT JOIN dbo.Packages                pkg  ON pkg.Id  = cpkg.PackageId
                WHERE a.Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (row == null) return null;

            return new AppointmentDtoModel(
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
                PackageName: (string?)row.PackageName);
        }

        // ===== Internal model =====
        private sealed class ResolvedLine
        {
            public int Index { get; set; }
            public int ItemId { get; set; }
            public int UnitId { get; set; }
            public int StaffId { get; set; }
            public string ItemEnName { get; set; } = "";
            public string ItemArName { get; set; } = "";
            public string StaffEnName { get; set; } = "";
            public string StaffArName { get; set; } = "";
            public int DurationMinutes { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal SalePrice { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string? Notes { get; set; }
        }
    }
}