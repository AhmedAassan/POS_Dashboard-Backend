// Modules/System/Controllers/NewSaleApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PosDashboard.Web.Modules.System.Services;   // InvoicePdfData + PdfInvoiceService
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
using AppointmentDtoModel = PosDashboard.Web.Modules.System.Models.AppointmentDtos.AppointmentDto;
using NewSaleDtos = PosDashboard.Web.Modules.System.Models.NewSaleDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/new-sale")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class NewSaleApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration _configuration;
        private const string EnjazatikUrl = "https://business.enjazatik.com/api/v1/send-message";

        private static readonly string[] ValidServiceTypes = { "SALON", "HOME" };

        public NewSaleApiController(
            ISqlConnections sqlConnections,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            this.httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // =========================================================================
        // POST /api/new-sale
        // =========================================================================
        [HttpPost]
        public async Task<ActionResult<NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>>> Create(
            [FromBody] NewSaleDtos.NewSaleRequest request)
        {
            try
            {
                // -------- Basic shape validation --------
                if (request == null)
                    return Fail("Request body is required");

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

                // -------- Validate customer --------
                var customer = SqlMapper.Query(conn,
                    @"SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1,
                             CUSTOMER_REF_GUIDE AS RefGuide,
                             ISNULL(NotificationLang, 'ar') AS Lang
                      FROM dbo.CUSTOMER
                      WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();

                if (customer == null)
                    return Fail("Customer not found");

                string currency = (string?)branch.EnglishCurrencyName ?? "KWD";

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

                // -------- Resolve sale date and initial starting slot --------
                var saleDate = (request.SaleDate ?? LocalNow(tzOffset).Date).Date;
                TimeSpan startTs;

                if (!string.IsNullOrWhiteSpace(request.StartTime))
                {
                    if (!TimeSpan.TryParseExact(
                            request.StartTime!.Trim(), @"HH\:mm", null, out startTs))
                        return Fail("StartTime must be in HH:mm format");
                }
                else
                {
                    startTs = ResolveNearestSlot(
                        LocalNow(tzOffset), saleDate, startHour, endHour, slot);
                }

                if (startTs >= TimeSpan.FromHours(endHour))
                    return Fail($"Calendar end hour reached ({endHour:00}:00). " +
                                "Pick a different start time or move to the next day.");
                if (startTs >= TimeSpan.FromDays(1))
                    return Fail("Start time cannot be 24:00 or later.");
                if (startTs < TimeSpan.FromHours(startHour))
                    startTs = TimeSpan.FromHours(startHour);

                // -------- Resolve / validate sale lines --------
                var resolvedLines = new List<ResolvedLine>(request.Lines.Count);

                for (int i = 0; i < request.Lines.Count; i++)
                {
                    var line = request.Lines[i];

                    if (line == null)
                        return Fail($"Line #{i + 1} is null");

                    var staff = SqlMapper.Query(conn,
                        @"SELECT Id, ArabicName, EnglishName
                          FROM dbo.STAFF
                          WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                        new { Id = line.StaffId }).FirstOrDefault();

                    if (staff == null)
                        return Fail($"Line #{i + 1}: staff not found or not active");

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
                        new { ItemId = line.ItemId, UnitId = line.UnitId })
                        .FirstOrDefault();

                    if (item == null)
                        return Fail($"Line #{i + 1}: item/unit not found or not active");

                    int duration = ResolveDuration(
                        line.DurationMinutes,
                        (double?)item.ITEM_UNIT_DURATION,
                        slot);

                    decimal masterPrice = (decimal)item.ITEM_UNIT_PRICE;
                    decimal salePrice = line.UnitPriceOverride.HasValue
                        ? Math.Max(0m, line.UnitPriceOverride.Value)
                        : masterPrice;

                    resolvedLines.Add(new ResolvedLine
                    {
                        Index = i,
                        ItemId = line.ItemId,
                        UnitId = line.UnitId,
                        StaffId = line.StaffId,
                        ItemEnName = (string?)item.ITEM_NAME1 ?? "",
                        ItemArName = (string?)item.ITEM_NAME2 ?? "",
                        StaffEnName = (string?)staff.EnglishName ?? "",
                        StaffArName = (string?)staff.ArabicName ?? "",
                        DurationMinutes = duration,
                        UnitPrice = masterPrice,
                        SalePrice = salePrice,
                        Notes = string.IsNullOrWhiteSpace(line.Notes)
                                           ? null : line.Notes.Trim()
                    });
                }

                // -------- Assign start/end times avoiding existing conflicts --------
                // New Sale لا يُرفض بسبب تعارض — يبحث عن أول slot متاح تلقائياً
                var warnings = new List<NewSaleDtos.NewSaleLineWarning>();
                var endHourMins = endHour * 60;
                var scheduleCursor = startTs;

                for (int i = 0; i < resolvedLines.Count; i++)
                {
                    var rl = resolvedLines[i];

                    // أول slot متاح للـ staff ابتداءً من الـ cursor
                    var availableStart = FindNextAvailableStartForLine(
                        conn,
                        staffId: rl.StaffId,
                        saleDate: saleDate,
                        desiredStart: scheduleCursor,
                        durationMinutes: rl.DurationMinutes,
                        endHourMinutes: endHourMins);

                    rl.StartTime = availableStart;
                    rl.EndTime = availableStart + TimeSpan.FromMinutes(rl.DurationMinutes);

                    // حماية من تجاوز منتصف الليل أو نهاية اليوم
                    if (rl.EndTime >= TimeSpan.FromDays(1))
                        return Fail(
                            $"Line #{rl.Index + 1}: no available slot before midnight " +
                            "for this staff member on this day.");

                    if (rl.EndTime > TimeSpan.FromHours(endHour))
                        return Fail(
                            $"Line #{rl.Index + 1}: no available slot before calendar end " +
                            $"({endHour:00}:00) for this staff member.");

                    // الـ line التالية تبدأ من نهاية الـ line الحالية
                    scheduleCursor = rl.EndTime;
                }

                // -------- Intra-sale same-staff overlap check --------
                // (بعد تعديل الأوقات — حماية من حالة اثنين بنفس الـ staff في نفس الـ sale)
                for (int i = 0; i < resolvedLines.Count; i++)
                {
                    for (int j = i + 1; j < resolvedLines.Count; j++)
                    {
                        var a = resolvedLines[i];
                        var b = resolvedLines[j];
                        if (a.StaffId != b.StaffId) continue;
                        if (a.StartTime < b.EndTime && a.EndTime > b.StartTime)
                            return Fail(
                                $"Lines #{i + 1} and #{j + 1} still overlap for the same staff " +
                                "after auto-scheduling. Please assign different staff.");
                    }
                }

                // -------- Validate payments --------
                decimal grandTotal;
                int? packageOfferId = request.PackageOfferId;
                string? packageOfferName = null;
                decimal? packageOfferPrice = null;

                if (packageOfferId.HasValue)
                {
                    var pkg = SqlMapper.Query(conn, @"
                    SELECT Id, EnglishName, ArabicName, Amount 
                    FROM dbo.Packages 
                    WHERE Id = @Id AND ISNULL(OFFER,0) = 1 AND ISNULL(Deleted,0) = 0",
                        new { Id = packageOfferId.Value }).FirstOrDefault();

                    if (pkg == null) return Fail("Package OFFER not found");

                    packageOfferName = (string)(pkg.EnglishName ?? "");
                    packageOfferPrice = (decimal)pkg.Amount;
                    grandTotal = packageOfferPrice.Value; // Price from the package
                }
                else
                {
                    grandTotal = resolvedLines.Sum(x => x.SalePrice); // Regular price
                }

                decimal walletAmount = 0m;
                int? walletSubId = null;
                int? walletPtId = null;
                decimal walletBalanceBefore = 0m;
                Guid walletCustomerRef = Guid.Empty;
                decimal splitsTotal = 0m;

                var splits = request.Payments?.Splits
                    ?? new List<NewSaleDtos.NewSaleSplitPaymentRequest>();

                if (request.Payments?.WalletAmount.HasValue == true &&
                    request.Payments.WalletAmount.Value > 0)
                {
                    walletAmount = request.Payments.WalletAmount.Value;
                    walletSubId = request.Payments.WalletSubscriptionId;
                    walletPtId = request.Payments.WalletPaymentTypeId;

                    if (walletSubId == null)
                        return Fail("WalletAmount given but WalletSubscriptionId is missing");
                    if (walletPtId == null)
                        return Fail("WalletAmount given but WalletPaymentTypeId is missing");

                    var sub = SqlMapper.Query(conn, @"
                        SELECT s.Id, s.CustomerRef, s.EndDate,
                               ISNULL(s.Deleted, 0) AS Deleted,
                               ISNULL(s.IsPaid, 0)  AS IsPaid,
                               ISNULL((
                                   SELECT TOP 1 sh.Balance
                                   FROM dbo.SubscriptionsHistory sh
                                   WHERE sh.SubscriptionId = s.Id AND sh.Deleted = 0
                                   ORDER BY sh.Id DESC
                               ), 0) AS CurrentBalance
                        FROM dbo.Subscriptions s
                        WHERE s.Id = @Id",
                        new { Id = walletSubId.Value }).FirstOrDefault();

                    if (sub == null || (int)sub.Deleted == 1)
                        return Fail("Wallet subscription not found");
                    if ((int)sub.IsPaid != 1)
                        return Fail("Wallet subscription is not paid");
                    if ((DateTime)sub.EndDate < DateTime.UtcNow)
                        return Fail("Wallet subscription has expired");
                    if ((Guid)sub.CustomerRef != (Guid)customer.RefGuide)
                        return Fail("Wallet subscription does not belong to this customer");

                    walletBalanceBefore = (decimal)sub.CurrentBalance;
                    walletCustomerRef = (Guid)sub.CustomerRef;

                    if (walletBalanceBefore < walletAmount)
                        return Fail(
                            $"Insufficient wallet balance. Available: {walletBalanceBefore:F3}");

                    var walletPt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE
                          WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = walletPtId.Value }).FirstOrDefault();

                    if (walletPt == null)
                        return Fail("Wallet payment type not found");
                }

                foreach (var sp in splits)
                {
                    if (sp.Amount <= 0)
                        return Fail("Split payment amount must be greater than 0");

                    var pt = SqlMapper.Query(conn,
                        @"SELECT INVOICE_PAYMENT_TYPE_ID FROM dbo.INVOICE_PAYMENT_TYPE
                          WHERE INVOICE_PAYMENT_TYPE_ID = @Id",
                        new { Id = sp.PaymentTypeId }).FirstOrDefault();

                    if (pt == null)
                        return Fail($"Payment type #{sp.PaymentTypeId} not found");

                    splitsTotal += sp.Amount;
                }

                decimal paidTotal = walletAmount + splitsTotal;

                if (paidTotal - grandTotal > 0.0001m)
                    return Fail(
                        $"Payment total ({paidTotal:F3}) exceeds sale total ({grandTotal:F3})");

                if (Math.Abs(paidTotal - grandTotal) > 0.0001m)
                    return Fail(
                        $"New Sale must be fully paid. Total: {grandTotal:F3}, Paid: {paidTotal:F3}");

                string paymentStatus = "FULL";
                int currentUserId = ResolveCurrentUserId();

                // -------- Atomic transaction --------
                try
                {
                    using var uow = new UnitOfWork(conn);

                    var saleGroupId = Guid.NewGuid();
                    var apptIds = new List<int>(resolvedLines.Count);

                    // 1) AppointmentData — one row per line
                    for (int idx = 0; idx < resolvedLines.Count; idx++)
                    {
                        var rl = resolvedLines[idx];

                        int newId = SqlMapper.Query<int>(uow.Connection, @"
                            INSERT INTO dbo.AppointmentData (
                                BranchId, CustomerId, ItemId, UnitId, StaffId,
                                AppointmentDate, StartTime, EndTime,
                                NumberOfPersons, ServiceType, IsOnlineBooking, Notes,
                                UnitPrice, DiscountPercent, DiscountedUnitPrice, TotalPrice,
                                PaidAmount, PaymentStatus, DepositAmount,
                                Status, CheckoutStatus, CreatedAt, SaleGroupId,
                                ShowOnCalendar
                            )
                            OUTPUT INSERTED.Id
                            VALUES (
                                @BranchId, @CustomerId, @ItemId, @UnitId, @StaffId,
                                @AppointmentDate, @StartTime, @EndTime,
                                @NumberOfPersons, @ServiceType, 0, @Notes,
                                @UnitPrice, @DiscountPercent, @DiscountedUnitPrice, @TotalPrice,
                                @PaidAmount, @PaymentStatus, @DepositAmount,
                                'completed', 'checked_out', SYSUTCDATETIME(), @SaleGroupId,
                                @ShowOnCalendar
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
                                NumberOfPersons = numberOfPersons,
                                ServiceType = serviceType,
                                Notes = rl.Notes ?? request.Notes,
                                UnitPrice = rl.UnitPrice,
                                DiscountPercent = ComputeDiscountPercent(
                                                          rl.UnitPrice, rl.SalePrice),
                                DiscountedUnitPrice = rl.SalePrice,
                                TotalPrice = rl.SalePrice,
                                PaidAmount = rl.SalePrice,
                                PaymentStatus = "FULL",
                                DepositAmount = 0m,
                                SaleGroupId = saleGroupId,
                                ShowOnCalendar = request.AddOnCalendar ? 1 : 0
                            }).First();

                        apptIds.Add(newId);
                    }

                    int leadApptId = apptIds[0];
                    string invoiceNumber = InvoiceNumberService.Next(uow.Connection, InvoiceNumberService.PrefixInvoice);

                    int? invoicePaymentTypeId =
                        (int?)splits.FirstOrDefault()?.PaymentTypeId ?? walletPtId;

                    if (invoicePaymentTypeId == null)
                        return Fail(
                            "Payment type is required for a fully paid New Sale");

                    // 2) Shared invoice
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
                            RemainingAmount = 0m,
                            Currency = currency,
                            PaymentTypeId = invoicePaymentTypeId,
                            PaymentStatus = paymentStatus,
                            PackageOfferId = packageOfferId,
                            PackageOfferName = packageOfferName,
                            PackageOfferPrice = packageOfferPrice
                        }).First();

                    // 3) Invoice lines
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
                                @UnitPrice, @DiscountedUnitPrice, @TotalPrice,
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
                                DiscountedUnitPrice = rl.SalePrice,
                                TotalPrice = rl.SalePrice,
                                AppointmentDate = saleDate,
                                StartTime = rl.StartTime,
                                EndTime = rl.EndTime,
                                rl.DurationMinutes,
                                rl.Notes
                            });
                    }

                    // 4) Wallet payment + ledger
                    if (walletAmount > 0 && walletSubId.HasValue && walletPtId.HasValue)
                    {
                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.AppointmentPayments (
                                AppointmentId, Amount, PaymentTypeId,
                                PaymentAs, VoucherCode, PaidAt, IsWalletPayment
                            )
                            VALUES (
                                @AppointmentId, @Amount, @PaymentTypeId,
                                'FULL', NULL, SYSUTCDATETIME(), 1
                            )",
                            new
                            {
                                AppointmentId = leadApptId,
                                Amount = walletAmount,
                                PaymentTypeId = walletPtId.Value
                            });

                        decimal newBalance = walletBalanceBefore - walletAmount;

                        SqlMapper.Execute(uow.Connection, @"
                            INSERT INTO dbo.SubscriptionsHistory (
                                CustomerRef, RefType, InvoiceId, SubscriptionId,
                                Amount, Balance, AddedBy, AddedDate, Deleted
                            )
                            VALUES (
                                @CustomerRef, 1, NULL, @SubscriptionId,
                                @Amount, @Balance, @AddedBy, @AddedDate, 0
                            )",
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
                                AppointmentId, Amount, PaymentTypeId,
                                PaymentAs, VoucherCode, PaidAt
                            )
                            VALUES (
                                @AppointmentId, @Amount, @PaymentTypeId,
                                'FULL', @VoucherCode, SYSUTCDATETIME()
                            )",
                            new
                            {
                                AppointmentId = leadApptId,
                                sp.Amount,
                                sp.PaymentTypeId,
                                VoucherCode = string.IsNullOrWhiteSpace(sp.VoucherCode)
                                    ? null : sp.VoucherCode.Trim()
                            });
                    }

                    uow.Commit();
                    // ولّد وخزّن PDF الفاتورة عشان لينك invoice-pdf يشتغل (البيع المباشر مش بيعدّي على MyFatoorah)
                    try { GenerateAndStoreInvoicePdf(conn, leadApptId); }
                    catch (Exception ex) { Debug.WriteLine($"[NewSale PDF] {ex.Message}"); }
                    // -------- After commit: read back + WhatsApp --------
                    var apptDtos = apptIds
                        .Select(id => GetAppointmentById(conn, id))
                        .Where(d => d != null)
                        .Select(d => d!)
                        .ToList();

                    bool waSent = false;
                    string? waErr = null;

                    if (request.SendWhatsApp)
                    {
                        try
                        {
                            (waSent, waErr) = await SendSaleConfirmationAsync(
                                conn,
                                appointmentId: leadApptId,
                                customerName: (string)customer.CUSTOMER_NAME,
                                customerPhone: (string)customer.CUSTOMER_PHONE1,
                                customerLang: (string)customer.Lang,
                                saleDate: saleDate,
                                currency: currency,
                                currencyAr: (string?)branch.ArabicCurrencyName ?? currency,
                                invoiceNumber: invoiceNumber,
                                grandTotal: grandTotal,
                                paidTotal: paidTotal,
                                walletAmount: walletAmount,
                                lines: resolvedLines);
                        }
                        catch (Exception ex)
                        {
                            waSent = false;
                            waErr = $"Send failed: {ex.Message}";
                        }
                    }

                    var response = new NewSaleDtos.NewSaleResponse(
                        SaleId: invoiceId,
                        InvoiceId: invoiceId,
                        InvoiceNumber: invoiceNumber,
                        LeadAppointmentId: leadApptId,
                        SaleGroupId: saleGroupId,
                        AppointmentIds: apptIds,
                        Appointments: apptDtos,
                        TotalAmount: grandTotal,
                        PaidAmount: paidTotal,
                        RemainingAmount: 0m,
                        WalletDeductedAmount: walletAmount,
                        PaymentStatus: paymentStatus,
                        Currency: currency,
                        WhatsAppSent: waSent,
                        WhatsAppError: waErr,
                        Warnings: warnings,
                        PackageOfferId: packageOfferId,
                        PackageOfferName: packageOfferName,
                        PackageOfferPrice: packageOfferPrice);

                    return Ok(new NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>(
                        true, null, response));
                }
                catch (Exception ex)
                {
                    return Ok(new NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>(
                        false, $"Failed to create sale: {ex.Message}", null));
                }
            }
            catch (Exception ex)
            {
                return Ok(new NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>(
                    false,
                    $"New Sale failed before transaction: {ex.Message}",
                    null));
            }
        }

        // =========================================================================
        // Private helpers
        // =========================================================================

        private ActionResult<NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>>
            Fail(string error) =>
            Ok(new NewSaleDtos.ApiResult<NewSaleDtos.NewSaleResponse>(false, error, null));

        private static DateTime LocalNow(int tzOffsetHours) =>
            DateTime.UtcNow.AddHours(tzOffsetHours);

        private static TimeSpan ResolveNearestSlot(
            DateTime localNow, DateTime saleDate,
            int startHour, int endHour, int slotMinutes)
        {
            if (saleDate.Date != localNow.Date)
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
        /// يرجع أول TimeSpan متاح للـ staff ابتداءً من desiredStart،
        /// متجنباً كل الـ appointments الموجودة والـ time blocks.
        /// </summary>
        private static TimeSpan FindNextAvailableStartForLine(
            IDbConnection conn,
            int staffId,
            DateTime saleDate,
            TimeSpan desiredStart,
            int durationMinutes,
            int endHourMinutes)
        {
            var existingAppts = SqlMapper.Query(conn, @"
                SELECT StartTime, EndTime
                FROM dbo.AppointmentData
                WHERE StaffId        = @StaffId
                  AND AppointmentDate = @Date
                  AND Status         != 'cancelled'
                ORDER BY StartTime",
                new { StaffId = staffId, Date = saleDate.Date })
                .Select(r => (Start: (TimeSpan)r.StartTime, End: (TimeSpan)r.EndTime))
                .ToList();

            var blockSlots = SqlMapper.Query(conn, @"
                SELECT StartTime, EndTime
                FROM dbo.StaffTimeBlocks
                WHERE StaffId    = @StaffId
                  AND BlockDate   = @Date
                  AND Deleted     = 0
                  AND IsRecurring = 0
                ORDER BY StartTime",
                new { StaffId = staffId, Date = saleDate.Date })
                .Select(r => (Start: (TimeSpan)r.StartTime, End: (TimeSpan)r.EndTime))
                .ToList();

            var allOccupied = existingAppts
                .Concat(blockSlots)
                .OrderBy(s => s.Start)
                .ToList();

            var dur = TimeSpan.FromMinutes(durationMinutes);
            var cursor = desiredStart;

            // keep pushing cursor forward until no conflict remains
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var slot in allOccupied)
                {
                    if (cursor < slot.End && cursor + dur > slot.Start)
                    {
                        cursor = slot.End;
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

        private static decimal ComputeDiscountPercent(decimal master, decimal sale)
        {
            if (master <= 0m || sale >= master) return 0m;
            return Math.Round((master - sale) / master * 100m, 2);
        }



        private int ResolveCurrentUserId()
        {
            var idClaim =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                User.FindFirst("sub")?.Value ??
                User.FindFirst("UserId")?.Value;
            return int.TryParse(idClaim, out var id) ? id : 0;
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

        private async Task<(bool Sent, string? Error)> SendSaleConfirmationAsync(
            IDbConnection conn,
            int appointmentId,
            string customerName, string customerPhone, string customerLang,
            DateTime saleDate, string currency, string currencyAr,
            string invoiceNumber,
            decimal grandTotal, decimal paidTotal, decimal walletAmount,
            List<ResolvedLine> lines)
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
            ? BuildSaleMessageEn(header, footer, customerName, saleDate,
                                 invoiceNumber, lines, grandTotal,
                                 paidTotal, walletAmount, currency, pdfUrl)
            : BuildSaleMessageAr(header, footer, customerName, saleDate,
                                 invoiceNumber, lines, grandTotal,
                                 paidTotal, walletAmount, currencyAr, pdfUrl);

            string phone = NormalizePhone(customerPhone);

            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

            var payload = new { instance_id = instanceId, message, number = phone };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(EnjazatikUrl, content);
            if (resp.IsSuccessStatusCode) return (true, null);

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"API error: {resp.StatusCode} — {body}");
        }

        private static string BuildSaleMessageEn(
            string header, string footer, string customerName, DateTime saleDate,
            string invoiceNumber, List<ResolvedLine> lines,
            decimal total, decimal paid, decimal walletAmount, string currency, string pdfUrl)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }

            decimal rem = Math.Max(0m, total - paid);
            string services = string.Join(", ", lines.Select(l => l.ItemEnName));

            sb.AppendLine(rem > 0 ? "✅ Deposit received successfully" : "✅ Payment received successfully");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"Service: {services}");
            sb.AppendLine($"Paid: {currency} {paid:F2}");
            sb.AppendLine($"Total: {currency} {total:F2}");
            if (rem > 0) sb.AppendLine($"Remaining: {currency} {rem:F2}");
            sb.AppendLine($"📄 Invoice: {pdfUrl}");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine(rem > 0 ? "Thank you! The remaining balance is due on arrival." : "Thank you!");

            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string BuildSaleMessageAr(
            string header, string footer, string customerName, DateTime saleDate,
            string invoiceNumber, List<ResolvedLine> lines,
            decimal total, decimal paid, decimal walletAmount, string currencyAr,
            string pdfUrl)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }

            decimal rem = Math.Max(0m, total - paid);
            string services = string.Join("، ",
                lines.Select(l => string.IsNullOrWhiteSpace(l.ItemArName) ? l.ItemEnName : l.ItemArName));

            sb.AppendLine(rem > 0 ? "✅ تم استلام الدفعة المقدمة بنجاح" : "✅ تم استلام الدفع بنجاح");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"الخدمة: {services}");
            sb.AppendLine($"المبلغ المدفوع: {paid:F2} {currencyAr}");
            sb.AppendLine($"السعر الكلي: {total:F2} {currencyAr}");
            if (rem > 0) sb.AppendLine($"المتبقي: {rem:F2} {currencyAr}");
            sb.AppendLine($"📄 الفاتورة: {pdfUrl}");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine(rem > 0 ? "شكراً لكم! المبلغ المتبقي يُسدَّد عند الحضور." : "شكراً لكم!");

            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string FormatTimeEn(TimeSpan t)
        {
            int h = t.Hours, m = t.Minutes;
            string period = h >= 12 ? "PM" : "AM";
            int dh = h > 12 ? h - 12 : h == 0 ? 12 : h;
            return $"{dh}:{m:D2} {period}";
        }

        private static string FormatTimeAr(TimeSpan t)
        {
            int h = t.Hours, m = t.Minutes;
            string period = h >= 12 ? "مساءً" : "صباحاً";
            int dh = h > 12 ? h - 12 : h == 0 ? 12 : h;
            return $"{dh}:{m:D2} {period}";
        }

        private static string FormatDateAr(DateTime dt)
        {
            string[] days = { "الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
            string[] months = { "","يناير","فبراير","مارس","أبريل","مايو","يونيو",
                                 "يوليو","أغسطس","سبتمبر","أكتوبر","نوفمبر","ديسمبر" };
            return $"{days[(int)dt.DayOfWeek]}، {dt.Day} {months[dt.Month]} {dt.Year}";
        }
        private void GenerateAndStoreInvoicePdf(IDbConnection conn, int appointmentId, string? paymentMethod = null)
        {
            try
            {
                var head = conn.Query<dynamic>(@"
            SELECT TOP 1
                inv.Id              AS InvoiceId,
                inv.InvoiceNumber   AS InvoiceNumber,
                inv.TotalAmount     AS TotalAmount,
                inv.PaidAmount      AS PaidAmount,
                inv.RemainingAmount AS RemainingAmount,
                c.CUSTOMER_NAME     AS CustomerName,
                c.CUSTOMER_PHONE1   AS CustomerPhone,
                a.Notes             AS Notes,
                b.EnglishCurrencyName, b.ArabicCurrencyName
            FROM dbo.AppointmentInvoices inv
            INNER JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
            INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
            LEFT  JOIN dbo.BRANCH b          ON b.BRANCH_ID   = a.BranchId
            WHERE inv.AppointmentId = @Id",
                    new { Id = appointmentId }).FirstOrDefault();

                if (head == null) return;

                var companyInfo = conn.Query<dynamic>(@"
            SELECT TOP 1
                COMPANY_NAME1, COMPANY_NAME2, COMPANY_ADRESS1, COMPANY_PHONE, COMPANY_LOGO,
                FOOTER, FOOTER1, FOOTER2
            FROM dbo.COMPANY ORDER BY COMPANY_ID").FirstOrDefault();

                int invoiceId = (int)head.InvoiceId;
                string currency = (string)(head.EnglishCurrencyName ?? "KWD");
                string currencyAr = (string)(head.ArabicCurrencyName ?? "د.ك");
                string invoiceNumber = (string?)head.InvoiceNumber
                    ?? $"INV-{DateTime.UtcNow:yyyyMMdd}-{appointmentId}";
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

                // كل سطور البيع من AppointmentInvoiceLines (يدعم تعدد الخدمات)
                var lines = conn.Query<dynamic>(@"
            SELECT
                i.ITEM_NAME1 AS ItemEn, i.ITEM_NAME2 AS ItemAr,
                s.EnglishName AS StaffEn, s.ArabicName AS StaffAr,
                ail.DiscountedUnitPrice AS UnitPrice,
                ail.TotalPrice          AS TotalPrice
            FROM dbo.AppointmentInvoiceLines ail
            INNER JOIN dbo.ITEM  i ON i.ITEM_ID = ail.ItemId
            INNER JOIN dbo.STAFF s ON s.Id      = ail.StaffId
            WHERE ail.InvoiceId = @InvoiceId AND ISNULL(ail.IsRefunded, 0) = 0
            ORDER BY ail.Id",
                    new { InvoiceId = invoiceId }).ToList();

                foreach (var ln in lines)
                {
                    pdfData.LineItems.Add(new InvoiceLineData
                    {
                        ItemName = (string)(ln.ItemEn ?? ln.ItemAr ?? "Service"),
                        StaffName = (string)(ln.StaffEn ?? ln.StaffAr ?? ""),
                        Quantity = 1,
                        UnitPrice = (decimal)ln.UnitPrice,
                        TotalPrice = (decimal)ln.TotalPrice
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
                Debug.WriteLine($"[NewSale PDF] {ex.Message}");
            }
        }
        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("0")) digits = "965" + digits.Substring(1);
            if (digits.Length == 8) digits = "965" + digits;
            return digits;
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