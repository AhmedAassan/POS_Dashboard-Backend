// Modules/System/Controllers/WhatsAppApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WhatsAppDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/whatsapp")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WhatsAppApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IHttpClientFactory httpClientFactory;

        // Token is SERVER-SIDE ONLY — never exposed to frontend
        private const string EnjazatikUrl = "https://business.enjazatik.com/api/v1/send-message";
        

        private readonly IConfiguration _configuration;

        public WhatsAppApiController(
            ISqlConnections sqlConnections,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            this.httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // =============================================
        // GET /api/whatsapp/template
        // =============================================
        [HttpGet("template")]
        public ActionResult<ApiResult<WhatsAppTemplateDto>> GetTemplate()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var row = conn.Query<dynamic>(@"
                SELECT TOP 1 HeaderText, FooterText, IsEnabled
                FROM dbo.WHATSAPP_CONFIG
                ORDER BY Id").FirstOrDefault();

            if (row == null)
            {
                return Ok(new ApiResult<WhatsAppTemplateDto>(true, null,
                    new WhatsAppTemplateDto("", "", false)));
            }

            var dto = new WhatsAppTemplateDto(
                Header: (string?)row.HeaderText ?? "",
                Footer: (string?)row.FooterText ?? "",
                Enabled: (bool)row.IsEnabled
            );

            return Ok(new ApiResult<WhatsAppTemplateDto>(true, null, dto));
        }

        // =============================================
        // PUT /api/whatsapp/template
        // =============================================
        [HttpPost("template")]
        public ActionResult<ApiResult<WhatsAppTemplateDto>> UpdateTemplate(
            [FromBody] UpdateWhatsAppTemplateRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<WhatsAppTemplateDto>(false,
                    "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Check if config row exists
            var exists = conn.Query<int>(
                "SELECT COUNT(*) FROM dbo.WHATSAPP_CONFIG").FirstOrDefault();

            if (exists > 0)
            {
                var updates = new List<string>();
                var parameters = new Dapper.DynamicParameters();

                if (request.Header != null)
                {
                    updates.Add("HeaderText = @Header");
                    parameters.Add("Header", request.Header);
                }

                if (request.Footer != null)
                {
                    updates.Add("FooterText = @Footer");
                    parameters.Add("Footer", request.Footer);
                }

                if (request.Enabled.HasValue)
                {
                    updates.Add("IsEnabled = @Enabled");
                    parameters.Add("Enabled", request.Enabled.Value);
                }

                updates.Add("UpdatedAt = SYSUTCDATETIME()");

                var sql = $"UPDATE dbo.WHATSAPP_CONFIG SET {string.Join(", ", updates)} WHERE Id = (SELECT TOP 1 Id FROM dbo.WHATSAPP_CONFIG)";
                SqlMapper.Execute(conn, sql, parameters);
            }
            else
            {
                // Insert new row
                SqlMapper.Execute(conn, @"
                    INSERT INTO dbo.WHATSAPP_CONFIG 
                        (HeaderText, FooterText, InstanceId, IsEnabled, UpdatedAt)
                    VALUES 
                        (@Header, @Footer, '51d2e384a1ef86b', @Enabled, SYSUTCDATETIME())",
                    new
                    {
                        Header = request.Header ?? "",
                        Footer = request.Footer ?? "",
                        Enabled = request.Enabled ?? true
                    });
            }

            // Return updated
            var updated = new WhatsAppTemplateDto(
                Header: request.Header ?? "",
                Footer: request.Footer ?? "",
                Enabled: request.Enabled ?? true
            );

            return Ok(new ApiResult<WhatsAppTemplateDto>(true, null, updated));
        }

        // =============================================
        // POST /api/whatsapp/send-appointment-confirmation
        // =============================================
        [HttpPost("send-appointment-confirmation")]
        public async Task<ActionResult<ApiResult<SendWhatsAppResponse>>> SendAppointmentConfirmation(
            [FromBody] int appointmentId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            // Load config
            var config = conn.Query<dynamic>(@"
                SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
                FROM dbo.WHATSAPP_CONFIG
                ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, "", "WhatsApp sending is disabled")));
            }

            // Load appointment + customer lang
            var apt = conn.Query<dynamic>(@"
                    SELECT 
                        a.Id,
                        c.CUSTOMER_NAME     AS CustomerName,
                        c.CUSTOMER_PHONE1   AS CustomerPhone,
                        ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
                        i.ITEM_NAME2        AS ItemArName,
                        i.ITEM_NAME1        AS ItemEnName,
                        s.ArabicName        AS StaffArName,
                        s.EnglishName       AS StaffEnName,
                        CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS AppointmentDate,
                        LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                        LEFT(CONVERT(VARCHAR(8), a.EndTime, 108), 5) AS EndTime,
                        a.NumberOfPersons,
                        a.ServiceType,
                        a.DiscountedUnitPrice,
                        a.Notes,
                        b.ArabicCurrencyName,
                        b.EnglishCurrencyName,
                        cps.Id AS CustomerPackageSessionId,
                        pkg.EnglishName AS PackageEnName,
                        pkg.ArabicName AS PackageArName,
                        ISNULL((
                            SELECT COUNT(*) 
                            FROM dbo.CustomerPackageSessions s2
                            WHERE s2.CustomerPackageId = cps.CustomerPackageId
                              AND ISNULL(s2.Deleted, 0) = 0
                              AND ISNULL(s2.Served, 0) = 0
                        ), 0) AS RemainingSessionsInPackage,
                        ISNULL((
                            SELECT COUNT(*) 
                            FROM dbo.CustomerPackageSessions s3
                            WHERE s3.CustomerPackageId = cps.CustomerPackageId
                              AND ISNULL(s3.Deleted, 0) = 0
                              AND ISNULL(s3.Served, 0) = 1
                        ), 0) AS ServedSessionsInPackage,
                        ISNULL((
                            SELECT COUNT(*) 
                            FROM dbo.CustomerPackageSessions s4
                            WHERE s4.CustomerPackageId = cps.CustomerPackageId
                              AND ISNULL(s4.Deleted, 0) = 0
                        ), 0) AS TotalSessionsInPackage
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                    INNER JOIN dbo.ITEM i     ON i.ITEM_ID = a.ItemId
                    INNER JOIN dbo.STAFF s    ON s.Id = a.StaffId
                    LEFT JOIN dbo.BRANCH b    ON b.BRANCH_ID = a.BranchId
                    LEFT JOIN dbo.CustomerPackageSessions cps 
                        ON cps.AppointmentId = a.Id AND ISNULL(cps.Deleted, 0) = 0
                    LEFT JOIN dbo.CustomerPackages cpkg ON cpkg.Id = cps.CustomerPackageId
                    LEFT JOIN dbo.Packages pkg ON pkg.Id = cpkg.PackageId
                    WHERE a.Id = @Id",
                    new { Id = appointmentId }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Appointment not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string customerLang = (string)apt.CustomerLang;

            string? packageName = customerLang == "en"
    ? (string?)apt.PackageEnName
    : (string?)apt.PackageArName;

            int remainingSessions = apt.CustomerPackageSessionId != null
                ? (int)apt.RemainingSessionsInPackage
                : 0;
            int servedSessions = apt.CustomerPackageSessionId != null
                ? (int)apt.ServedSessionsInPackage
                : 0;
            int totalSessions = apt.CustomerPackageSessionId != null
                ? (int)apt.TotalSessionsInPackage
                : 0;

            // Build message based on customer language
            string message;
            if (customerLang == "en")
            {
                message = BuildEnglishMessage(
                    header: header,
                    footer: footer,
                    customerName: (string)apt.CustomerName,
                    serviceName: (string)(apt.ItemEnName ?? apt.ItemArName ?? ""),
                    staffName: (string)(apt.StaffEnName ?? apt.StaffArName ?? ""),
                    dateStr: (string)apt.AppointmentDate,
                    startTime: (string)apt.StartTime,
                    endTime: (string)apt.EndTime,
                    persons: (int)apt.NumberOfPersons,
                    serviceType: (string)apt.ServiceType,
                    price: (decimal)apt.DiscountedUnitPrice,
                    currency: (string)(apt.EnglishCurrencyName ?? apt.ArabicCurrencyName ?? "KWD"),
                    notes: (string?)apt.Notes,
                    packageName: packageName,
                    remainingSessions: remainingSessions,
                    servedSessions: servedSessions,
                    totalSessions: totalSessions
                );
            }
            else
            {
                message = BuildArabicMessage(
                    header: header,
                    footer: footer,
                    customerName: (string)apt.CustomerName,
                    serviceName: (string)(apt.ItemArName ?? apt.ItemEnName ?? ""),
                    staffName: (string)(apt.StaffArName ?? apt.StaffEnName ?? ""),
                    dateStr: (string)apt.AppointmentDate,
                    startTime: (string)apt.StartTime,
                    endTime: (string)apt.EndTime,
                    persons: (int)apt.NumberOfPersons,
                    serviceType: (string)apt.ServiceType,
                    price: (decimal)apt.DiscountedUnitPrice,
                    currency: (string)(apt.ArabicCurrencyName ?? apt.EnglishCurrencyName ?? "د.ك"),
                    notes: (string?)apt.Notes,
                    packageName: packageName,
                    remainingSessions: remainingSessions,
                    servedSessions: servedSessions,
                    totalSessions: totalSessions
                );
            }

            var phone = NormalizePhone((string)apt.CustomerPhone);

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

                var payload = new
                {
                    instance_id = instanceId,
                    message = message,
                    number = phone
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(EnjazatikUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(true, phone, null)));
                }
                else
                {
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(false, phone,
                            $"API error: {response.StatusCode} — {responseBody}")));
                }
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, phone, $"Send failed: {ex.Message}")));
            }
        }

        // POST /api/whatsapp/send-payment-link
        [HttpPost("send-payment-link")]
        public async Task<ActionResult<ApiResult<SendWhatsAppResponse>>> SendPaymentLink(
            [FromBody] SendPaymentLinkRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Load config
            var config = conn.Query<dynamic>(@"
        SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
        FROM dbo.WHATSAPP_CONFIG
        ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, "", "WhatsApp sending is disabled")));

            // Load appointment
            var apt = conn.Query<dynamic>(@"
        SELECT 
            a.Id,
            c.CUSTOMER_NAME     AS CustomerName,
            c.CUSTOMER_PHONE1   AS CustomerPhone,
            ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
            i.ITEM_NAME2        AS ItemArName,
            i.ITEM_NAME1        AS ItemEnName,
            s.ArabicName        AS StaffArName,
            s.EnglishName       AS StaffEnName,
            CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS AppointmentDate,
            LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
            LEFT(CONVERT(VARCHAR(8), a.EndTime, 108), 5) AS EndTime,
            a.NumberOfPersons,
            a.ServiceType,
            a.DiscountedUnitPrice,
            a.Notes,
            b.ArabicCurrencyName,
            b.EnglishCurrencyName
        FROM dbo.AppointmentData a
        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
        INNER JOIN dbo.ITEM i     ON i.ITEM_ID = a.ItemId
        INNER JOIN dbo.STAFF s    ON s.Id = a.StaffId
        LEFT JOIN dbo.BRANCH b    ON b.BRANCH_ID = a.BranchId
        WHERE a.Id = @Id",
                new { Id = request.AppointmentId }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Appointment not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string customerLang = (string)apt.CustomerLang;
            string paymentLink = request.PaymentLink;

            string message;
            if (customerLang == "en")
            {
                message = BuildEnglishMessageWithPaymentLink(
                    header: header,
                    footer: footer,
                    customerName: (string)apt.CustomerName,
                    serviceName: (string)(apt.ItemEnName ?? apt.ItemArName ?? ""),
                    staffName: (string)(apt.StaffEnName ?? apt.StaffArName ?? ""),
                    dateStr: (string)apt.AppointmentDate,
                    startTime: (string)apt.StartTime,
                    endTime: (string)apt.EndTime,
                    persons: (int)apt.NumberOfPersons,
                    serviceType: (string)apt.ServiceType,
                    price: (decimal)apt.DiscountedUnitPrice,
                    currency: (string)(apt.EnglishCurrencyName ?? apt.ArabicCurrencyName ?? "KWD"),
                    notes: (string?)apt.Notes,
                    paymentLink: paymentLink
                );
            }
            else
            {
                message = BuildArabicMessageWithPaymentLink(
                    header: header,
                    footer: footer,
                    customerName: (string)apt.CustomerName,
                    serviceName: (string)(apt.ItemArName ?? apt.ItemEnName ?? ""),
                    staffName: (string)(apt.StaffArName ?? apt.StaffEnName ?? ""),
                    dateStr: (string)apt.AppointmentDate,
                    startTime: (string)apt.StartTime,
                    endTime: (string)apt.EndTime,
                    persons: (int)apt.NumberOfPersons,
                    serviceType: (string)apt.ServiceType,
                    price: (decimal)apt.DiscountedUnitPrice,
                    currency: (string)(apt.ArabicCurrencyName ?? apt.EnglishCurrencyName ?? "د.ك"),
                    notes: (string?)apt.Notes,
                    paymentLink: paymentLink
                );
            }

            var phone = NormalizePhone((string)apt.CustomerPhone);

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

                var payload = new
                {
                    instance_id = instanceId,
                    message = message,
                    number = phone
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(EnjazatikUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(true, phone, null)));
                }
                else
                {
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(false, phone,
                            $"API error: {response.StatusCode}")));
                }
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, phone, $"Send failed: {ex.Message}")));
            }
        }

        // POST /api/whatsapp/send-package-assignment
        [HttpPost("send-package-assignment")]
        public async Task<ActionResult<ApiResult<SendWhatsAppResponse>>> SendPackageAssignment(
            [FromBody] SendPackageAssignmentRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // Load config
            var config = conn.Query<dynamic>(@"
        SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
        FROM dbo.WHATSAPP_CONFIG
        ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, "", "WhatsApp sending is disabled")));

            // Load assignment details
            var assignment = conn.Query<dynamic>(@"
        SELECT 
            cp.Id,
            c.CUSTOMER_NAME         AS CustomerName,
            c.CUSTOMER_PHONE1       AS CustomerPhone,
            ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
            p.EnglishName           AS PackageEnName,
            p.ArabicName            AS PackageArName,
            ISNULL(cp.Amount, 0)    AS Amount,
            cp.ExpiryDate,
            cp.AddedDate,
            b.ArabicCurrencyName,
            b.EnglishCurrencyName,
            ISNULL((
                SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                WHERE s.CustomerPackageId = cp.Id
                  AND ISNULL(s.Deleted, 0) = 0
            ), 0) AS TotalSessions,
            ISNULL((
                SELECT COUNT(*) FROM dbo.CustomerPackageSessions s
                WHERE s.CustomerPackageId = cp.Id
                  AND ISNULL(s.Deleted, 0) = 0
                  AND ISNULL(s.Served, 0) = 0
            ), 0) AS RemainingSessions
        FROM dbo.CustomerPackages cp
        INNER JOIN dbo.Packages p ON p.Id = cp.PackageId
        INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
        LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = 1
        WHERE cp.Id = @Id AND ISNULL(cp.Deleted, 0) = 0",
                new { Id = request.CustomerPackageId }).FirstOrDefault();

            if (assignment == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Assignment not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string customerLang = (string)assignment.CustomerLang;

            string packageName = customerLang == "en"
                ? (string)(assignment.PackageEnName ?? "")
                : (string)(assignment.PackageArName ?? "");

            decimal amount = (decimal)assignment.Amount;
            int totalSessions = (int)assignment.TotalSessions;
            int remainingSessions = (int)assignment.RemainingSessions;
            DateTime expiryDate = (DateTime)assignment.ExpiryDate;
            string currency = customerLang == "en"
                ? (string)(assignment.EnglishCurrencyName ?? "KWD")
                : (string)(assignment.ArabicCurrencyName ?? "د.ك");

            string message = customerLang == "en"
                ? BuildPackageAssignmentEnglishMessage(
                    header, footer,
                    customerName: (string)assignment.CustomerName,
                    packageName: packageName,
                    amount: amount,
                    currency: currency,
                    totalSessions: totalSessions,
                    remainingSessions: remainingSessions,
                    expiryDate: expiryDate)
                : BuildPackageAssignmentArabicMessage(
                    header, footer,
                    customerName: (string)assignment.CustomerName,
                    packageName: packageName,
                    amount: amount,
                    currency: currency,
                    totalSessions: totalSessions,
                    remainingSessions: remainingSessions,
                    expiryDate: expiryDate);

            var phone = NormalizePhone((string)assignment.CustomerPhone);

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

                var payload = new { instance_id = instanceId, message, number = phone };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(EnjazatikUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(true, phone, null)));
                else
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(false, phone,
                            $"API error: {response.StatusCode}")));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, phone, $"Send failed: {ex.Message}")));
            }
        }


        // POST /api/whatsapp/send-sale-confirmation
        [HttpPost("send-sale-confirmation")]
        public async Task<ActionResult<ApiResult<SendWhatsAppResponse>>> SendSaleConfirmation(
            [FromBody] int invoiceId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var config = conn.Query<dynamic>(@"
        SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
        FROM dbo.WHATSAPP_CONFIG
        ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, "", "WhatsApp sending is disabled")));
            }

            var invoice = conn.Query<dynamic>(@"
        SELECT  inv.Id, inv.InvoiceNumber, inv.AppointmentId,
                inv.TotalAmount, inv.PaidAmount, inv.RemainingAmount,
                inv.Currency, inv.PaymentStatus, inv.CreatedAt,
                a.AppointmentDate,
                a.CustomerId,
                c.CUSTOMER_NAME    AS CustomerName,
                c.CUSTOMER_PHONE1  AS CustomerPhone,
                ISNULL(c.NotificationLang, 'ar') AS Lang,
                b.ArabicCurrencyName,
                b.EnglishCurrencyName
        FROM    dbo.AppointmentInvoices inv
        INNER JOIN dbo.AppointmentData a ON a.Id = inv.AppointmentId
        INNER JOIN dbo.CUSTOMER c        ON c.CUSTOMER_ID = a.CustomerId
        LEFT  JOIN dbo.BRANCH b          ON b.BRANCH_ID = a.BranchId
        WHERE inv.Id = @Id",
                new { Id = invoiceId }).FirstOrDefault();

            if (invoice == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Invoice not found", null));

            // Pull all sale lines if available; otherwise fall back to the lead appointment alone.
            var lines = conn.Query<dynamic>(@"
        SELECT  ail.DiscountedUnitPrice  AS Price,
                ail.StartTime, ail.EndTime,
                i.ITEM_NAME1 AS ItemEn, i.ITEM_NAME2 AS ItemAr,
                s.EnglishName AS StaffEn, s.ArabicName AS StaffAr
        FROM    dbo.AppointmentInvoiceLines ail
        INNER JOIN dbo.ITEM  i ON i.ITEM_ID = ail.ItemId
        INNER JOIN dbo.STAFF s ON s.Id = ail.StaffId
        WHERE   ail.InvoiceId = @InvoiceId
        ORDER BY ail.Id",
                new { InvoiceId = invoiceId }).ToList();

            if (lines.Count == 0)
            {
                // Legacy / single-appointment invoice — synthesize one line.
                lines = conn.Query<dynamic>(@"
            SELECT  a.DiscountedUnitPrice AS Price,
                    a.StartTime, a.EndTime,
                    i.ITEM_NAME1 AS ItemEn, i.ITEM_NAME2 AS ItemAr,
                    s.EnglishName AS StaffEn, s.ArabicName AS StaffAr
            FROM    dbo.AppointmentData a
            INNER JOIN dbo.ITEM  i ON i.ITEM_ID = a.ItemId
            INNER JOIN dbo.STAFF s ON s.Id = a.StaffId
            WHERE   a.Id = @Id",
                    new { Id = (int)invoice.AppointmentId }).ToList();
            }

            string lang = (string)invoice.Lang;
            string currency = lang == "en"
                ? (string)(invoice.EnglishCurrencyName ?? invoice.ArabicCurrencyName ?? "KWD")
                : (string)(invoice.ArabicCurrencyName ?? invoice.EnglishCurrencyName ?? "د.ك");

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }

            decimal rem = (decimal)invoice.RemainingAmount;
            string pdfUrl = $"{Request.Scheme}://{Request.Host}/api/myfatoorah/invoice-pdf/{(int)invoice.AppointmentId}";
            string services = string.Join(lang == "en" ? ", " : "، ",
                lines.Select(l => lang == "en"
                    ? (string)(l.ItemEn ?? l.ItemAr ?? "")
                    : (string)(l.ItemAr ?? l.ItemEn ?? "")));

            if (lang == "en")
            {
                sb.AppendLine(rem > 0 ? "✅ Deposit received successfully" : "✅ Payment received successfully");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"Service: {services}");
                sb.AppendLine($"Paid: {currency} {((decimal)invoice.PaidAmount):F2}");
                sb.AppendLine($"Total: {currency} {((decimal)invoice.TotalAmount):F2}");
                if (rem > 0) sb.AppendLine($"Remaining: {currency} {rem:F2}");
                sb.AppendLine($"📄 Invoice: {pdfUrl}");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine(rem > 0 ? "Thank you! The remaining balance is due on arrival." : "Thank you!");
            }
            else
            {
                sb.AppendLine(rem > 0 ? "✅ تم استلام الدفعة المقدمة بنجاح" : "✅ تم استلام الدفع بنجاح");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"الخدمة: {services}");
                sb.AppendLine($"المبلغ المدفوع: {((decimal)invoice.PaidAmount):F2} {currency}");
                sb.AppendLine($"السعر الكلي: {((decimal)invoice.TotalAmount):F2} {currency}");
                if (rem > 0) sb.AppendLine($"المتبقي: {rem:F2} {currency}");
                sb.AppendLine($"📄 الفاتورة: {pdfUrl}");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine(rem > 0 ? "شكراً لكم! المبلغ المتبقي يُسدَّد عند الحضور." : "شكراً لكم!");
            }

            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }

            string message = sb.ToString();
            string phone = NormalizePhone((string)invoice.CustomerPhone);

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

                var payload = new { instance_id = instanceId, message, number = phone };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = await client.PostAsync(EnjazatikUrl, content);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                        new SendWhatsAppResponse(true, phone, null)));

                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, phone, $"API error: {resp.StatusCode} — {body}")));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, phone, $"Send failed: {ex.Message}")));
            }
        }

        // POST /api/whatsapp/send-session-served
        [HttpPost("send-session-served")]
        public async Task<ActionResult<ApiResult<SendWhatsAppResponse>>> SendSessionServed(
            [FromBody] int customerPackageSessionId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var config = conn.Query<dynamic>(@"
        SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
        FROM dbo.WHATSAPP_CONFIG ORDER BY Id").FirstOrDefault();
            if (config == null || !(bool)config.IsEnabled)
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null,
                    new SendWhatsAppResponse(false, "", "WhatsApp sending is disabled")));

            var info = conn.Query<dynamic>(@"
        SELECT 
            c.CUSTOMER_NAME   AS CustomerName,
            c.CUSTOMER_PHONE1 AS CustomerPhone,
            ISNULL(c.NotificationLang,'ar') AS Lang,
            p.ArabicName  AS PkgAr, p.EnglishName AS PkgEn,
            (SELECT COUNT(*) FROM dbo.CustomerPackageSessions x
               WHERE x.CustomerPackageId = cp.Id AND ISNULL(x.Deleted,0)=0) AS Total,
            (SELECT COUNT(*) FROM dbo.CustomerPackageSessions x
               WHERE x.CustomerPackageId = cp.Id AND ISNULL(x.Deleted,0)=0 AND ISNULL(x.Served,0)=1) AS Used,
            (SELECT COUNT(*) FROM dbo.CustomerPackageSessions x
               WHERE x.CustomerPackageId = cp.Id AND ISNULL(x.Deleted,0)=0 AND ISNULL(x.Served,0)=0) AS Remaining
        FROM dbo.CustomerPackageSessions s
        INNER JOIN dbo.CustomerPackages cp ON cp.Id = s.CustomerPackageId
        INNER JOIN dbo.Packages p          ON p.Id  = cp.PackageId
        INNER JOIN dbo.CUSTOMER c          ON c.CUSTOMER_REF_GUIDE = cp.CustomerRef
        WHERE s.Id = @Id",
                new { Id = customerPackageSessionId }).FirstOrDefault();

            if (info == null)
                return Ok(new ApiResult<SendWhatsAppResponse>(false, "Session not found", null));

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";
            string lang = (string)info.Lang;
            string pkg = lang == "en" ? (string)(info.PkgEn ?? "") : (string)(info.PkgAr ?? "");
            int total = (int)info.Total, used = (int)info.Used, remaining = (int)info.Remaining;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
            if (lang == "en")
            {
                sb.AppendLine("✅ A session from your package has been used");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"Package: {pkg}");
                sb.AppendLine($"Used sessions: {used} of {total}");
                sb.AppendLine($"Remaining sessions: {remaining}");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("Thank you for your visit!");
            }
            else
            {
                sb.AppendLine("✅ تم استخدام جلسة من باقتك");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"الباقة: {pkg}");
                sb.AppendLine($"الجلسات المستخدمة: {used} من {total}");
                sb.AppendLine($"الجلسات المتبقية: {remaining}");
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine("شكراً لزيارتكم!");
            }
            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }

            string message = sb.ToString();
            string phone = NormalizePhone((string)info.CustomerPhone);

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");
                var payload = new { instance_id = instanceId, message, number = phone };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(EnjazatikUrl, content);
                if (resp.IsSuccessStatusCode)
                    return Ok(new ApiResult<SendWhatsAppResponse>(true, null, new SendWhatsAppResponse(true, phone, null)));
                var body = await resp.Content.ReadAsStringAsync();
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null, new SendWhatsAppResponse(false, phone, $"API error: {resp.StatusCode} — {body}")));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<SendWhatsAppResponse>(true, null, new SendWhatsAppResponse(false, phone, $"Send failed: {ex.Message}")));
            }
        }

        #region Private Helpers

        private static string BuildPackageAssignmentArabicMessage(
            string header, string footer,
            string customerName, string packageName,
            decimal amount, string currency,
            int totalSessions, int remainingSessions,
            DateTime expiryDate)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("🎁 *تأكيد الباقة*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"📦 *الباقة:* {packageName}");
            sb.AppendLine($"💰 *القيمة:* {amount:F2} {currency}");
            sb.AppendLine($"🎯 *إجمالي الجلسات:* {totalSessions}");
            sb.AppendLine($"🔄 *الجلسات المتاحة:* {remainingSessions}");
            sb.AppendLine($"📅 *تاريخ الانتهاء:* {FormatDateArabic(expiryDate.ToString("yyyy-MM-dd"))}");
            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("✨ تم تفعيل باقتك بنجاح!");
            sb.AppendLine("📞 للحجز أو الاستفسار تواصل معنا في أي وقت.");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }

        private static string BuildPackageAssignmentEnglishMessage(
            string header, string footer,
            string customerName, string packageName,
            decimal amount, string currency,
            int totalSessions, int remainingSessions,
            DateTime expiryDate)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("🎁 *Package Confirmation*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"📦 *Package:* {packageName}");
            sb.AppendLine($"💰 *Value:* {currency} {amount:F2}");
            sb.AppendLine($"🎯 *Total Sessions:* {totalSessions}");
            sb.AppendLine($"🔄 *Sessions Available:* {remainingSessions}");
            sb.AppendLine($"📅 *Valid Until:* {FormatDateEnglish(expiryDate.ToString("yyyy-MM-dd"))}");
            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine("✨ Your package has been activated successfully!");
            sb.AppendLine("📞 Contact us anytime to book your sessions.");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }
        private static string BuildArabicMessageWithPaymentLink(
string header, string footer,
string customerName, string serviceName, string staffName,
string dateStr, string startTime, string endTime,
int persons, string serviceType,
decimal price, string currency, string? notes,
string paymentLink)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("📋 *تأكيد الموعد*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"*الخدمة:* {serviceName}");
            sb.AppendLine($"*المختص/ة:* {staffName}");
            sb.AppendLine($"*التاريخ:* {FormatDateArabic(dateStr)}");
            sb.AppendLine($"*الوقت:* {FormatTimeArabic(startTime)} – {FormatTimeArabic(endTime)}");

            if (serviceType == "HOME")
                sb.AppendLine("*النوع:* خدمة منزلية");
            else
                sb.AppendLine("*النوع:* في الصالون");

            if (persons > 1)
                sb.AppendLine($"👥 *عدد الأشخاص:* {persons}");

            sb.AppendLine($"*السعر:* {price:F2} {currency}");

            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine();
                sb.AppendLine($"📝 *ملاحظات:* {notes}");
            }

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine("💳 *رابط الدفع الإلكتروني:*");
            sb.AppendLine(paymentLink);
            sb.AppendLine();
            sb.AppendLine("⚡ يرجى إتمام الدفع قبل موعدكم");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }

        private static string BuildEnglishMessageWithPaymentLink(
    string header, string footer,
    string customerName, string serviceName, string staffName,
    string dateStr, string startTime, string endTime,
    int persons, string serviceType,
    decimal price, string currency, string? notes,
    string paymentLink)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("📋 *Appointment Confirmation*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"*Service:* {serviceName}");
            sb.AppendLine($"*Specialist:* {staffName}");
            sb.AppendLine($"*Date:* {FormatDateEnglish(dateStr)}");
            sb.AppendLine($"*Time:* {FormatTimeEnglish(startTime)} – {FormatTimeEnglish(endTime)}");

            if (serviceType == "HOME")
                sb.AppendLine("*Type:* Home Service");
            else
                sb.AppendLine("*Type:* Salon");

            if (persons > 1)
                sb.AppendLine($"👥 *Persons:* {persons}");

            sb.AppendLine($"*Price:* {currency} {price:F2}");

            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine();
                sb.AppendLine($"📝 *Notes:* {notes}");
            }

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine("💳 *Payment Link:*");
            sb.AppendLine(paymentLink);
            sb.AppendLine();
            sb.AppendLine("⚡ Please complete payment before your appointment");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }
        private static string BuildArabicMessage(
    string header, string footer,
    string customerName, string serviceName, string staffName,
    string dateStr, string startTime, string endTime,
    int persons, string serviceType,
    decimal price, string currency, string? notes, string? packageName,
    int remainingSessions = 0, int servedSessions = 0, int totalSessions = 0)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("📋 *تأكيد الموعد*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"*الخدمة:* {serviceName}");
            sb.AppendLine($"*المختص/ة:* {staffName}");
            sb.AppendLine($"*التاريخ:* {FormatDateArabic(dateStr)}");
            sb.AppendLine($"*الوقت:* {FormatTimeArabic(startTime)} – {FormatTimeArabic(endTime)}");

            if (serviceType == "HOME")
                sb.AppendLine("*النوع:* خدمة منزلية");
            else
                sb.AppendLine("*النوع:* في الصالون");

            if (persons > 1)
                sb.AppendLine($"👥 *عدد الأشخاص:* {persons}");

            if (!string.IsNullOrWhiteSpace(packageName))
            {
                sb.AppendLine();
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"📦 *الباقة:* {packageName}");
                sb.AppendLine($"✅ *هذا الموعد مغطى بالباقة — مدفوع مسبقاً*");
                if (totalSessions > 0)
                {
                    sb.AppendLine($"📊 *الجلسات:* {servedSessions} من {totalSessions} مستخدمة");
                    sb.AppendLine($"🔄 *الجلسات المتبقية:* {remainingSessions}");
                }
            }
            else
            {
                sb.AppendLine($"*السعر:* {price:F2} {currency}");
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine();
                sb.AppendLine($"📝 *ملاحظات:* {notes}");
            }

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }

        private static string BuildEnglishMessage(
    string header, string footer,
    string customerName, string serviceName, string staffName,
    string dateStr, string startTime, string endTime,
    int persons, string serviceType,
    decimal price, string currency, string? notes, string? packageName,
    int remainingSessions = 0, int servedSessions = 0, int totalSessions = 0)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(header))
            {
                sb.AppendLine(header);
                sb.AppendLine();
            }

            sb.AppendLine("📋 *Appointment Confirmation*");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
            sb.AppendLine($"*Service:* {serviceName}");
            sb.AppendLine($"*Specialist:* {staffName}");
            sb.AppendLine($"*Date:* {FormatDateEnglish(dateStr)}");
            sb.AppendLine($"*Time:* {FormatTimeEnglish(startTime)} – {FormatTimeEnglish(endTime)}");

            if (serviceType == "HOME")
                sb.AppendLine("*Type:* Home Service");
            else
                sb.AppendLine("*Type:* Salon");

            if (persons > 1)
                sb.AppendLine($"👥 *Persons:* {persons}");

            if (!string.IsNullOrWhiteSpace(packageName))
            {
                sb.AppendLine();
                sb.AppendLine("━━━━━━━━━━━━━━━━━━");
                sb.AppendLine($"📦 *Package:* {packageName}");
                sb.AppendLine($"✅ *This appointment is covered by your package — Pre-paid*");
                if (totalSessions > 0)
                {
                    sb.AppendLine($"📊 *Sessions:* {servedSessions} of {totalSessions} used");
                    sb.AppendLine($"🔄 *Sessions Remaining:* {remainingSessions}");
                }
            }
            else
            {
                sb.AppendLine($"*Price:* {currency} {price:F2}");
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                sb.AppendLine();
                sb.AppendLine($"📝 *Notes:* {notes}");
            }

            sb.AppendLine();
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                sb.AppendLine();
                sb.AppendLine(footer);
            }

            return sb.ToString();
        }

        private static string FormatDateArabic(string dateStr)
        {
            if (!DateTime.TryParse(dateStr, out var dt))
                return dateStr;

            var dayNames = new[]
            {
                "الأحد", "الإثنين", "الثلاثاء", "الأربعاء",
                "الخميس", "الجمعة", "السبت"
            };

            var monthNames = new[]
            {
                "", "يناير", "فبراير", "مارس", "أبريل",
                "مايو", "يونيو", "يوليو", "أغسطس",
                "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"
            };

            return $"{dayNames[(int)dt.DayOfWeek]}، {dt.Day} {monthNames[dt.Month]} {dt.Year}";
        }

        private static string FormatTimeArabic(string time)
        {
            var parts = time.Split(':');
            if (parts.Length < 2) return time;

            if (!int.TryParse(parts[0], out int h)) return time;
            if (!int.TryParse(parts[1], out int m)) return time;

            var period = h >= 12 ? "مساءً" : "صباحاً";
            var displayH = h > 12 ? h - 12 : h == 0 ? 12 : h;

            return $"{displayH}:{m:D2} {period}";
        }

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";

            var cleaned = new string(phone.Where(char.IsDigit).ToArray());

            if (cleaned.StartsWith("0"))
                cleaned = "965" + cleaned.Substring(1);

            if (cleaned.Length == 8)
                cleaned = "965" + cleaned;

            return cleaned;
        }

        private static string FormatDateEnglish(string dateStr)
        {
            if (!DateTime.TryParse(dateStr, out var dt))
                return dateStr;

            return dt.ToString("dddd, MMMM d, yyyy",
                CultureInfo.GetCultureInfo("en-US"));
        }

        private static string FormatTimeEnglish(string time)
        {
            var parts = time.Split(':');
            if (parts.Length < 2) return time;

            if (!int.TryParse(parts[0], out int h)) return time;
            if (!int.TryParse(parts[1], out int m)) return time;

            var period = h >= 12 ? "PM" : "AM";
            var displayH = h > 12 ? h - 12 : h == 0 ? 12 : h;

            return $"{displayH}:{m:D2} {period}";
        }

        #endregion
    }
}