// Modules/System/Controllers/MyFatoorahApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.MyFatoorahDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/myfatoorah")]
    public class MyFatoorahApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration configuration;

        public MyFatoorahApiController(
            ISqlConnections sqlConnections,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            this.httpClientFactory = httpClientFactory;
            this.configuration = configuration;
        }

        // =============================================
        // POST /api/myfatoorah/initiate-payment
        // =============================================
        [HttpPost("initiate-payment")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<ActionResult<ApiResult<InitiatePaymentResponse>>> InitiatePayment(
            [FromBody] InitiatePaymentRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<InitiatePaymentResponse>(false, "Request required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var config = LoadConfig(conn);
            if (config == null || !(bool)config.IsEnabled)
                return Ok(new ApiResult<InitiatePaymentResponse>(false,
                    "MyFatoorah is not enabled", null));

            var apt = conn.Query<dynamic>(@"
                SELECT 
                    a.Id, a.BranchId, a.TotalPrice, a.PaidAmount,
                    a.DiscountedUnitPrice, a.Notes,
                    c.CUSTOMER_NAME AS CustomerName,
                    c.CUSTOMER_PHONE1 AS CustomerPhone,
                    ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
                    i.ITEM_NAME1 AS ItemEnName,
                    i.ITEM_NAME2 AS ItemArName,
                    b.EnglishCurrencyName,
                    b.ArabicCurrencyName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                INNER JOIN dbo.ITEM i ON i.ITEM_ID = a.ItemId
                LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = a.BranchId
                WHERE a.Id = @Id",
                new { Id = request.AppointmentId }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResult<InitiatePaymentResponse>(false,
                    "Appointment not found", null));

            decimal totalPrice = (decimal)apt.TotalPrice;
            decimal paidAmount = (decimal)apt.PaidAmount;
            decimal remaining = totalPrice - paidAmount;

            if (remaining <= 0)
                return Ok(new ApiResult<InitiatePaymentResponse>(false,
                    "Appointment is already fully paid", null));

            // ── NEW: honour the requested deposit amount ──
            decimal amountToCharge;
            if (request.DepositAmount.HasValue && request.DepositAmount.Value > 0)
            {
                // Clamp to remaining so we never overcharge
                amountToCharge = Math.Min(request.DepositAmount.Value, remaining);
            }
            else
            {
                amountToCharge = remaining;   // default: charge full remaining
            }

            if (amountToCharge <= 0)
                return Ok(new ApiResult<InitiatePaymentResponse>(false,
                    "Deposit amount must be greater than 0", null));

            string customerName = (string)apt.CustomerName;
            string customerPhone = (string)apt.CustomerPhone;
            string customerLang = (string)apt.CustomerLang;
            string currency = (string)(apt.EnglishCurrencyName ?? "KWD");
            string itemName = customerLang == "en"
                ? (string)(apt.ItemEnName ?? apt.ItemArName ?? "Service")
                : (string)(apt.ItemArName ?? apt.ItemEnName ?? "خدمة");

            string baseUrl = $"{Request.Scheme}://{Request.Host}";
            string callbackUrl = (string?)config.CallbackUrl
                ?? $"{baseUrl}/api/myfatoorah/callback";
            string errorUrl = (string?)config.ErrorUrl
                ?? $"{baseUrl}/api/myfatoorah/callback";

            string apiKey = (string)config.ApiKey;
            string apiBaseUrl = (string)config.ApiBaseUrl;

            var mfRequestObj = new Dictionary<string, object>
            {
                { "NotificationOption", "LNK" },
                { "InvoiceValue", amountToCharge },   // ← use deposit amount
                { "CustomerName", customerName },
                { "DisplayCurrencyIso", currency },
                { "CallBackUrl", callbackUrl },
                { "ErrorUrl", errorUrl },
                { "Language", customerLang == "en" ? "EN" : "AR" },
                { "CustomerReference", $"APT-{request.AppointmentId}" }
            };

            var (countryCode, mobileNumber) = SplitPhone(customerPhone);
            if (!string.IsNullOrWhiteSpace(mobileNumber) && mobileNumber.Length >= 8)
            {
                mfRequestObj["CustomerMobile"] = mobileNumber;
                mfRequestObj["MobileCountryCode"] = countryCode;
            }

            if (!string.IsNullOrWhiteSpace(request.CustomerEmail) &&
                request.CustomerEmail.Contains("@"))
            {
                mfRequestObj["CustomerEmail"] = request.CustomerEmail;
            }

            // Label clarifies deposit vs full in the invoice line item
            bool isDeposit = amountToCharge < remaining;
            string lineItemName = isDeposit
                ? $"{itemName} (Deposit)"
                : itemName;

            mfRequestObj["InvoiceItems"] = new[]
            {
                new Dictionary<string, object>
                {
                    { "ItemName", lineItemName },
                    { "Quantity", 1 },
                    { "UnitPrice", amountToCharge }
                }
            };

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var json = JsonSerializer.Serialize(mfRequestObj);

                Debug.WriteLine($"[MyFatoorah] URL: {apiBaseUrl}/v2/SendPayment");
                Debug.WriteLine($"[MyFatoorah] Body: {json}");

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{apiBaseUrl}/v2/SendPayment", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                Debug.WriteLine($"[MyFatoorah] Response ({response.StatusCode}): {responseBody}");

                var mfResponse = JsonSerializer.Deserialize<MFSendPaymentResponse>(
                    responseBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (mfResponse == null || !mfResponse.IsSuccess || mfResponse.Data == null)
                {
                    var errorMsg = mfResponse?.Message ?? "Unknown error";
                    if (mfResponse?.ValidationErrors != null && mfResponse.ValidationErrors.Length > 0)
                    {
                        var details = string.Join("; ",
                            mfResponse.ValidationErrors.Select(e => $"{e.Name}: {e.Error}"));
                        errorMsg = $"{errorMsg} [{details}]";
                    }
                    return Ok(new ApiResult<InitiatePaymentResponse>(false,
                        $"MyFatoorah: {errorMsg}", null));
                }

                // Save transaction — store amountToCharge (not full remaining)
                var txId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.MYFATOORAH_TRANSACTIONS (
                        AppointmentId, InvoiceId, InvoiceURL, Amount, Currency,
                        Status, CustomerName, CustomerPhone, CustomerEmail,
                        RawResponse, CreatedAt
                    )
                    OUTPUT INSERTED.Id
                    VALUES (
                        @AppointmentId, @InvoiceId, @InvoiceURL, @Amount, @Currency,
                        'pending', @CustomerName, @CustomerPhone, @CustomerEmail,
                        @RawResponse, SYSUTCDATETIME()
                    )",
                    new
                    {
                        AppointmentId = request.AppointmentId,
                        InvoiceId = mfResponse.Data.InvoiceId.ToString(),
                        InvoiceURL = mfResponse.Data.InvoiceURL,
                        Amount = amountToCharge,          // ← deposit amount stored
                        Currency = currency,
                        CustomerName = customerName,
                        CustomerPhone = customerPhone,
                        CustomerEmail = request.CustomerEmail,
                        RawResponse = responseBody
                    }).FirstOrDefault();

                return Ok(new ApiResult<InitiatePaymentResponse>(true, null,
                    new InitiatePaymentResponse(
                        TransactionId: txId,
                        InvoiceURL: mfResponse.Data.InvoiceURL!,
                        InvoiceId: mfResponse.Data.InvoiceId.ToString(),
                        Amount: amountToCharge,           // ← return deposit amount
                        Currency: currency
                    )));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<InitiatePaymentResponse>(false,
                    $"Failed: {ex.Message}", null));
            }
        }

        // =============================================
        // GET /api/myfatoorah/callback?paymentId=xxx
        // =============================================
        [HttpGet("callback")]
        [AllowAnonymous]
        public async Task<IActionResult> PaymentCallback([FromQuery] string paymentId)
        {
            if (string.IsNullOrWhiteSpace(paymentId))
                return RedirectToFrontend("failed", 0, 0, "missing_payment_id");

            using var conn = sqlConnections.NewByKey("Default");
            var config = LoadConfig(conn);
            if (config == null)
                return RedirectToFrontend("failed", 0, 0, "config_missing");

            string apiKey = (string)config.ApiKey;
            string apiBaseUrl = (string)config.ApiBaseUrl;

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var statusRequestObj = new Dictionary<string, string>
                {
                    { "Key", paymentId },
                    { "KeyType", "PaymentId" }
                };

                var json = JsonSerializer.Serialize(statusRequestObj);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"{apiBaseUrl}/v2/GetPaymentStatus", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                Debug.WriteLine($"[MyFatoorah Callback] PaymentId: {paymentId}");
                Debug.WriteLine($"[MyFatoorah Callback] HTTP Status: {response.StatusCode}");
                Debug.WriteLine($"[MyFatoorah Callback] Response: {responseBody}");

                if (!response.IsSuccessStatusCode)
                {
                    return RedirectToFrontend("failed", 0, 0,
                        $"myfatoorah_http_{(int)response.StatusCode}");
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };

                var statusResponse = JsonSerializer.Deserialize<MFPaymentStatusResponse>(
                    responseBody, jsonOptions);

                if (statusResponse?.Data == null)
                {
                    Debug.WriteLine($"[MyFatoorah Callback] Data is null. Full response: {responseBody}");
                    return RedirectToFrontend("failed", 0, 0, "verification_data_null");
                }

                var invoiceData = statusResponse.Data;

                Debug.WriteLine($"[MyFatoorah Callback] InvoiceStatus: {invoiceData.InvoiceStatus}");
                Debug.WriteLine($"[MyFatoorah Callback] InvoiceId: {invoiceData.InvoiceId}");

                if (invoiceData.InvoiceTransactions != null)
                {
                    foreach (var tx in invoiceData.InvoiceTransactions)
                    {
                        Debug.WriteLine($"[MyFatoorah Callback] Transaction: Status={tx.TransactionStatus}, " +
                            $"Gateway={tx.PaymentGateway}, PaymentId={tx.PaymentId}, " +
                            $"Amount={tx.PaidCurrencyValue}, Currency={tx.PaidCurrency}");
                    }
                }

                bool isPaid = invoiceData.InvoiceStatus == "Paid";
                string txStatus = isPaid ? "paid" : "failed";

                string? paymentMethod = null;
                string? failReason = null;

                if (invoiceData.InvoiceTransactions != null)
                {
                    var successTx = invoiceData.InvoiceTransactions
                        .FirstOrDefault(t => t.TransactionStatus == "Succss" ||
                                             t.TransactionStatus == "Success");
                    paymentMethod = successTx?.PaymentGateway;

                    if (!isPaid)
                    {
                        var failedTx = invoiceData.InvoiceTransactions
                            .LastOrDefault(t => t.TransactionStatus != "Succss" &&
                                               t.TransactionStatus != "Success");
                        failReason = failedTx?.TransactionStatus;
                        Debug.WriteLine($"[MyFatoorah Callback] Failed TX status: {failReason}");
                    }
                }

                var mfInvoiceId = invoiceData.InvoiceId.ToString();
                var txRow = conn.Query<dynamic>(@"
                    SELECT Id, AppointmentId, Amount, Currency
                    FROM dbo.MYFATOORAH_TRANSACTIONS
                    WHERE InvoiceId = @InvoiceId",
                    new { InvoiceId = mfInvoiceId }).FirstOrDefault();

                if (txRow == null)
                {
                    Debug.WriteLine($"[MyFatoorah Callback] No transaction found for InvoiceId: {mfInvoiceId}");

                    var allPending = conn.Query<dynamic>(@"
                        SELECT Id, InvoiceId, AppointmentId, Status 
                        FROM dbo.MYFATOORAH_TRANSACTIONS 
                        WHERE Status = 'pending'
                        ORDER BY Id DESC").ToList();

                    foreach (var p in allPending)
                    {
                        Debug.WriteLine($"[MyFatoorah Callback] Pending TX: Id={p.Id}, InvoiceId={p.InvoiceId}, AptId={p.AppointmentId}");
                    }

                    return RedirectToFrontend("failed", 0, 0,
                        $"transaction_not_found_for_invoice_{mfInvoiceId}");
                }

                int txId = (int)txRow.Id;
                int appointmentId = (int)txRow.AppointmentId;
                decimal amount = (decimal)txRow.Amount;  // ← this is the deposit amount stored

                // Update transaction
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.MYFATOORAH_TRANSACTIONS SET
                        PaymentId = @PaymentId,
                        Status = @Status,
                        PaymentMethod = @PaymentMethod,
                        TransactionDate = SYSUTCDATETIME(),
                        RawResponse = @RawResponse,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE Id = @Id",
                    new
                    {
                        Id = txId,
                        PaymentId = paymentId,
                        Status = txStatus,
                        PaymentMethod = paymentMethod,
                        RawResponse = responseBody
                    });

                if (isPaid)
                {
                    var apt = conn.Query<dynamic>(@"
                        SELECT TotalPrice, PaidAmount, BranchId, CustomerId 
                        FROM dbo.AppointmentData 
                        WHERE Id = @Id",
                        new { Id = appointmentId }).FirstOrDefault();

                    if (apt != null)
                    {
                        decimal newPaid = (decimal)apt.PaidAmount + amount;
                        decimal total = (decimal)apt.TotalPrice;
                        decimal newRemaining = total - newPaid;

                        // Correct PaymentAs: DEPOSIT if still has remaining, FULL if fully paid
                        string paymentStatus = newRemaining <= 0 ? "FULL" : "DEPOSIT";
                        string paymentAs = newRemaining <= 0 ? "FULL" : "DEPOSIT";

                        SqlMapper.Execute(conn, @"
                            UPDATE dbo.AppointmentData SET
                                PaidAmount = @PaidAmount,
                                PaymentStatus = @PaymentStatus,
                                DepositAmount = CASE WHEN @PaymentStatus = 'DEPOSIT' 
                                    THEN @PaidAmount ELSE DepositAmount END,
                                UpdatedAt = SYSUTCDATETIME()
                            WHERE Id = @Id",
                            new
                            {
                                Id = appointmentId,
                                PaidAmount = newPaid,
                                PaymentStatus = paymentStatus
                            });

                        var onlinePaymentTypeId = conn.Query<int?>(@"
                            SELECT TOP 1 INVOICE_PAYMENT_TYPE_ID 
                            FROM dbo.INVOICE_PAYMENT_TYPE 
                            WHERE OnlinePayment = 1").FirstOrDefault();

                        if (onlinePaymentTypeId == null)
                        {
                            onlinePaymentTypeId = conn.Query<int>(@"
                                DECLARE @NextId INT = (SELECT ISNULL(MAX(INVOICE_PAYMENT_TYPE_ID), 0) + 1 
                                                       FROM dbo.INVOICE_PAYMENT_TYPE);
                                INSERT INTO dbo.INVOICE_PAYMENT_TYPE 
                                    (INVOICE_PAYMENT_TYPE_ID, INVOICE_PAYMENT_TYPE_NAME1, 
                                     INVOICE_PAYMENT_TYPE_NAME2, INVOICE_PAYMENT_TYPE_RATE,
                                     Treasury, Loyalty, Reservation, OnlinePayment)
                                VALUES 
                                    (@NextId, 'Online Payment', N'دفع إلكتروني', 0, 0, 0, 0, 1);
                                SELECT @NextId;")
                                .FirstOrDefault();
                        }

                        SqlMapper.Execute(conn, @"
                            INSERT INTO dbo.AppointmentPayments 
                                (AppointmentId, Amount, PaymentTypeId, PaymentAs, 
                                 VoucherCode, PaidAt, IsWalletPayment)  
                            VALUES 
                                (@AppointmentId, @Amount, @PaymentTypeId,
                                 @PaymentAs, NULL, SYSUTCDATETIME(), 0)", 
                            new
                            {
                                AppointmentId = appointmentId,
                                Amount = amount,
                                PaymentTypeId = onlinePaymentTypeId!.Value,
                                PaymentAs = paymentAs
                            });

                        // ← أضف ده: لو FULL، اعمل Invoice وعمل checkout
                        if (paymentAs == "FULL")
                        {
                            var existingInvoice = conn.Query<int?>(@"
                                SELECT TOP 1 Id FROM dbo.AppointmentInvoices 
                                WHERE AppointmentId = @Id",
                                new { Id = appointmentId }).FirstOrDefault();

                            if (existingInvoice == null)
                            {
                                // جيب الـ currency من الـ Branch
                                var invoiceCurrency = conn.Query<string>(@"
                                SELECT EnglishCurrencyName FROM dbo.BRANCH 
                                WHERE BRANCH_ID = @Id",
                                    new { Id = (int)apt.BranchId }).FirstOrDefault() ?? "KWD";

                                var invoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{appointmentId}";

                                conn.Execute(@"
                                INSERT INTO dbo.AppointmentInvoices (
                                    InvoiceNumber, AppointmentId, BranchId, CustomerId,
                                    TotalAmount, PaidAmount, RemainingAmount, Currency,
                                    PaymentTypeId, PaymentStatus, CreatedAt
                                )
                                VALUES (
                                    @InvoiceNumber, @AppointmentId, @BranchId, @CustomerId,
                                    @TotalAmount, @PaidAmount, 0, @Currency,
                                    @PaymentTypeId, 'FULL', SYSUTCDATETIME()
                                )",
                                    new
                                    {
                                        InvoiceNumber = invoiceNumber,
                                        AppointmentId = appointmentId,
                                        BranchId = (int)apt.BranchId,
                                        CustomerId = (int)apt.CustomerId,
                                        TotalAmount = amount,
                                        PaidAmount = amount,
                                        Currency = invoiceCurrency,
                                        PaymentTypeId = onlinePaymentTypeId!.Value
                                    });
                            }

                            // عمل checkout للـ appointment
                            conn.Execute(@"
                        UPDATE dbo.AppointmentData SET
                            CheckoutStatus = 'checked_out',
                            Status = 'completed',
                            UpdatedAt = SYSUTCDATETIME()
                        WHERE Id = @Id AND CheckoutStatus != 'checked_out'",
                                                new { Id = appointmentId });
                        }

                        try
                        {
                            GenerateAndStorePdf(conn, appointmentId, txId, paymentMethod);
                        }
                        catch (Exception pdfEx)
                        {
                            Debug.WriteLine($"[PDF Error] {pdfEx.Message}");
                        }

                        try
                        {
                            _ = SendWhatsAppInvoiceAsync(conn, appointmentId, txId);
                        }
                        catch (Exception waEx)
                        {
                            Debug.WriteLine($"[WhatsApp Error] {waEx.Message}");
                        }
                    }
                }

                return RedirectToFrontend(txStatus, appointmentId, txId,
                    isPaid ? null : (failReason ?? "payment_not_completed"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyFatoorah Callback Error] {ex}");
                return RedirectToFrontend("failed", 0, 0, ex.Message);
            }
        }

        private IActionResult RedirectToFrontend(string status, int appointmentId,
            int transactionId, string? error)
        {
            var queryParams = new List<string> { $"status={status}" };

            if (appointmentId > 0)
                queryParams.Add($"appointmentId={appointmentId}");
            if (transactionId > 0)
                queryParams.Add($"transactionId={transactionId}");
            if (!string.IsNullOrWhiteSpace(error))
                queryParams.Add($"reason={Uri.EscapeDataString(error)}");

            var query = string.Join("&", queryParams);
            var frontendUrls = configuration
            .GetSection("FrontendBaseUrls")
            .Get<string[]>();

            var frontendBaseUrl = frontendUrls?.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(frontendBaseUrl))
                return Redirect($"{frontendBaseUrl}/payment/result?{query}");

            return Redirect($"/payment/result?{query}");
        }

        // =============================================
        // GET /api/myfatoorah/transaction/{id}
        // =============================================
        [HttpGet("transaction/{id:int}")]
        [AllowAnonymous]
        public ActionResult<ApiResult<TransactionStatusDto>> GetTransaction(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var tx = conn.Query<TransactionStatusDto>(@"
                SELECT 
                    Id, AppointmentId, InvoiceId, InvoiceURL,
                    Amount, Currency, Status, PaymentMethod,
                    TransactionDate, PdfInvoiceUrl, WhatsAppSent, CreatedAt
                FROM dbo.MYFATOORAH_TRANSACTIONS
                WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (tx == null)
                return Ok(new ApiResult<TransactionStatusDto>(false,
                    "Transaction not found", null));

            return Ok(new ApiResult<TransactionStatusDto>(true, null, tx));
        }

        // =============================================
        // GET /api/myfatoorah/invoice-pdf/{appointmentId}
        // =============================================
        [HttpGet("invoice-pdf/{appointmentId:int}")]
        [AllowAnonymous]
        public ActionResult GetInvoicePdf(int appointmentId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var pdf = conn.Query<dynamic>(@"
                SELECT TOP 1 FileName, PdfData
                FROM dbo.INVOICE_PDF
                WHERE AppointmentId = @Id
                ORDER BY CreatedAt DESC",
                new { Id = appointmentId }).FirstOrDefault();

            if (pdf == null)
                return NotFound("PDF not found");

            return File((byte[])pdf.PdfData, "application/pdf", (string)pdf.FileName);
        }

        [HttpGet("test-connection")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<ActionResult<ApiResult<object>>> TestConnection()
        {
            using var conn = sqlConnections.NewByKey("Default");
            var config = LoadConfig(conn);

            if (config == null)
                return Ok(new ApiResult<object>(false, "No config found", null));

            string apiKey = (string)config.ApiKey;
            string apiBaseUrl = (string)config.ApiBaseUrl;

            try
            {
                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var testRequest = new
                {
                    NotificationOption = "LNK",
                    InvoiceValue = 0.100m,
                    CustomerName = "Test Customer",
                    DisplayCurrencyIso = "KWD",
                    CallBackUrl = $"{Request.Scheme}://{Request.Host}/api/myfatoorah/callback",
                    ErrorUrl = $"{Request.Scheme}://{Request.Host}/api/myfatoorah/callback",
                    Language = "EN"
                };

                var json = JsonSerializer.Serialize(testRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"{apiBaseUrl}/v2/SendPayment", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                return Ok(new ApiResult<object>(true, null, new
                {
                    StatusCode = (int)response.StatusCode,
                    ApiBaseUrl = apiBaseUrl,
                    IsTestMode = (bool)config.IsTestMode,
                    RequestSent = json,
                    Response = responseBody
                }));
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<object>(false, ex.Message, new
                {
                    ApiBaseUrl = apiBaseUrl,
                    ExceptionType = ex.GetType().Name
                }));
            }
        }

        #region Private Helpers

        private static (string countryCode, string mobileNumber) SplitPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return ("", "");

            var cleaned = new string(phone.Where(char.IsDigit).ToArray());

            if (cleaned.StartsWith("965") && cleaned.Length >= 11)
                return ("+965", cleaned.Substring(3));
            if (cleaned.StartsWith("20") && cleaned.Length >= 12)
                return ("+20", cleaned.Substring(2));
            if (cleaned.StartsWith("966") && cleaned.Length >= 12)
                return ("+966", cleaned.Substring(3));
            if (cleaned.StartsWith("971") && cleaned.Length >= 12)
                return ("+971", cleaned.Substring(3));
            if (cleaned.StartsWith("973") && cleaned.Length >= 11)
                return ("+973", cleaned.Substring(3));
            if (cleaned.StartsWith("0") && cleaned.Length == 9)
                return ("+965", cleaned.Substring(1));
            if (cleaned.Length == 8)
                return ("+965", cleaned);
            if (cleaned.Length > 8)
            {
                if (cleaned.Length <= 10)
                    return ("+965", cleaned);
                return ($"+{cleaned.Substring(0, 3)}", cleaned.Substring(3));
            }

            return ("+965", cleaned);
        }

        private dynamic? LoadConfig(IDbConnection conn)
        {
            return conn.Query<dynamic>(@"
                SELECT TOP 1 ApiKey, ApiBaseUrl, IsTestMode, IsEnabled,
                    CallbackUrl, ErrorUrl
                FROM dbo.MYFATOORAH_CONFIG
                ORDER BY Id").FirstOrDefault();
        }

        private void GenerateAndStorePdf(IDbConnection conn, int appointmentId,
            int transactionId, string? paymentMethod)
        {
            try
            {
                var apt = conn.Query<dynamic>(@"
                    SELECT 
                        a.Id, a.TotalPrice, a.PaidAmount, a.Notes,
                        a.DiscountedUnitPrice, a.NumberOfPersons,
                        c.CUSTOMER_NAME AS CustomerName,
                        c.CUSTOMER_PHONE1 AS CustomerPhone,
                        i.ITEM_NAME1 AS ItemEnName,
                        i.ITEM_NAME2 AS ItemArName,
                        s.EnglishName AS StaffEnName,
                        s.ArabicName AS StaffArName,
                        b.EnglishCurrencyName,
                        b.ArabicCurrencyName,
                        inv.InvoiceNumber,
                        inv.PaidAmount AS InvPaidAmount,
                        inv.TotalAmount AS InvTotalAmount,
                        inv.RemainingAmount AS InvRemainingAmount
                    FROM dbo.AppointmentData a
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = a.ItemId
                    INNER JOIN dbo.STAFF s ON s.Id = a.StaffId
                    LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = a.BranchId
                    LEFT JOIN dbo.AppointmentInvoices inv ON inv.AppointmentId = a.Id
                    WHERE a.Id = @Id",
                    new { Id = appointmentId }).FirstOrDefault();

                if (apt == null) return;
                // Retrieve company data from the database
                var companyInfo = conn.Query<dynamic>(@"
                SELECT TOP 1 
                    COMPANY_NAME1, COMPANY_NAME2,
                    COMPANY_ADRESS1, COMPANY_ADRESS2,
                    COMPANY_PHONE, COMPANY_LOGO,
                    FOOTER, FOOTER1, FOOTER2, FOOTER3, FOOTER4, FOOTER5
                FROM dbo.COMPANY
                ORDER BY COMPANY_ID").FirstOrDefault();
                string invoiceNumber = (string?)apt.InvoiceNumber
                    ?? $"INV-{DateTime.UtcNow:yyyyMMdd}-{appointmentId}";
                string currency = (string)(apt.EnglishCurrencyName ?? "KWD");
                string currencyAr = (string)(apt.ArabicCurrencyName ?? "د.ك");

                var pdfData = new InvoicePdfData
                {
                    InvoiceNumber = invoiceNumber,
                    InvoiceDate = DateTime.UtcNow,
                    CustomerName = (string)apt.CustomerName,
                    CustomerPhone = (string)apt.CustomerPhone,
                    Currency = currency,
                    CurrencyAr = currencyAr,
                    TotalAmount = (decimal)apt.TotalPrice,
                    PaidAmount = (decimal)apt.PaidAmount,
                    RemainingAmount = Math.Max(0, (decimal)apt.TotalPrice - (decimal)apt.PaidAmount),
                    PaymentMethod = paymentMethod ?? "Online Payment",
                    PaymentStatus = (decimal)apt.PaidAmount >= (decimal)apt.TotalPrice ? "Paid" : "Deposit Paid",
                    Notes = (string?)apt.Notes,

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

                    LineItems = new List<InvoiceLineData>
                    {
                        new InvoiceLineData
                        {
                            ItemName  = (string)(apt.ItemEnName ?? apt.ItemArName ?? "Service"),
                            StaffName = (string)(apt.StaffEnName ?? apt.StaffArName ?? ""),
                            Quantity  = 1,
                            UnitPrice  = (decimal)apt.DiscountedUnitPrice,
                            TotalPrice = (decimal)apt.DiscountedUnitPrice
                        }
                    }
                };

                var extras = conn.Query<dynamic>(@"
                    SELECT 
                        i.ITEM_NAME1 AS ItemName,
                        s.EnglishName AS StaffName,
                        ci.DiscountedUnitPrice AS UnitPrice,
                        ci.TotalPrice
                    FROM dbo.AppointmentCheckoutItems ci
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = ci.ItemId
                    INNER JOIN dbo.STAFF s ON s.Id = ci.StaffId
                    WHERE ci.AppointmentId = @Id",
                    new { Id = appointmentId }).ToList();

                foreach (var extra in extras)
                {
                    pdfData.LineItems.Add(new InvoiceLineData
                    {
                        ItemName = (string)(extra.ItemName ?? ""),
                        StaffName = (string)(extra.StaffName ?? ""),
                        Quantity = 1,
                        UnitPrice = (decimal)extra.UnitPrice,
                        TotalPrice = (decimal)extra.TotalPrice
                    });
                }

                byte[] pdfBytes = PdfInvoiceService.GenerateInvoicePdf(pdfData);
                string fileName = $"Invoice_{invoiceNumber}_{appointmentId}.pdf";

                var pdfId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.INVOICE_PDF 
                        (AppointmentId, TransactionId, FileName, PdfData, CreatedAt)
                    OUTPUT INSERTED.Id
                    VALUES 
                        (@AppointmentId, @TransactionId, @FileName, @PdfData, SYSUTCDATETIME())",
                    new
                    {
                        AppointmentId = appointmentId,
                        TransactionId = transactionId,
                        FileName = fileName,
                        PdfData = pdfBytes
                    }).FirstOrDefault();

                string baseUrl = $"{Request.Scheme}://{Request.Host}";
                string pdfUrl = $"{baseUrl}/api/myfatoorah/invoice-pdf/{appointmentId}";

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.MYFATOORAH_TRANSACTIONS SET
                        PdfInvoiceUrl = @PdfUrl
                    WHERE Id = @Id",
                    new { Id = transactionId, PdfUrl = pdfUrl });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PDF generation failed: {ex.Message}");
            }
        }

        private async Task SendWhatsAppInvoiceAsync(IDbConnection conn,
            int appointmentId, int transactionId)
        {
            try
            {
                var waConfig = conn.Query<dynamic>(@"
                    SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
                    FROM dbo.WHATSAPP_CONFIG
                    ORDER BY Id").FirstOrDefault();

                if (waConfig == null || !(bool)waConfig.IsEnabled) return;

                var info = conn.Query<dynamic>(@"
                    SELECT 
                        c.CUSTOMER_NAME AS CustomerName,
                        c.CUSTOMER_PHONE1 AS CustomerPhone,
                        ISNULL(c.NotificationLang, 'ar') AS CustomerLang,
                        t.Amount, t.Currency, t.PaymentMethod, t.PdfInvoiceUrl,
                        i.ITEM_NAME1 AS ItemEnName,
                        i.ITEM_NAME2 AS ItemArName,
                        b.ArabicCurrencyName,
                        a.TotalPrice,
                        a.PaidAmount,
                        (a.TotalPrice - a.PaidAmount) AS RemainingAmount
                    FROM dbo.MYFATOORAH_TRANSACTIONS t
                    INNER JOIN dbo.AppointmentData a ON a.Id = t.AppointmentId
                    INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = a.ItemId
                    LEFT JOIN dbo.BRANCH b ON b.BRANCH_ID = a.BranchId
                    WHERE t.Id = @Id",
                    new { Id = transactionId }).FirstOrDefault();

                if (info == null) return;

                string customerLang = (string)info.CustomerLang;
                string customerPhone = NormalizePhone((string)info.CustomerPhone);
                string instanceId = (string?)waConfig.InstanceId ?? "51d2e384a1ef86b";
                string pdfUrl = (string?)info.PdfInvoiceUrl ?? "";
                decimal paidAmount = (decimal)info.Amount;     // deposit paid now
                decimal totalPrice = (decimal)info.TotalPrice;
                decimal remaining = Math.Max(0, (decimal)info.RemainingAmount);
                bool isDepositOnly = remaining > 0;

                string message;
                if (customerLang == "en")
                {
                    message = isDepositOnly
                        ? $@"✅ *Deposit Received Successfully*
━━━━━━━━━━━━━━━━━━

👤 *Client:* {(string)info.CustomerName}
💇 *Service:* {(string)(info.ItemEnName ?? info.ItemArName)}
💰 *Deposit Paid:* {(string)info.Currency} {paidAmount:F2}
📊 *Total Price:* {(string)info.Currency} {totalPrice:F2}
⏳ *Remaining:* {(string)info.Currency} {remaining:F2}
💳 *Method:* {(string?)info.PaymentMethod ?? "Online"}

📄 *Invoice:* {pdfUrl}

━━━━━━━━━━━━━━━━━━
Thank you for your deposit! Remaining balance to be paid at the salon. 🙏"
                        : $@"✅ *Payment Received Successfully*
━━━━━━━━━━━━━━━━━━

👤 *Client:* {(string)info.CustomerName}
💇 *Service:* {(string)(info.ItemEnName ?? info.ItemArName)}
💰 *Amount Paid:* {(string)info.Currency} {paidAmount:F2}
💳 *Method:* {(string?)info.PaymentMethod ?? "Online"}

📄 *Invoice:* {pdfUrl}

━━━━━━━━━━━━━━━━━━
Thank you for your payment! 🙏";
                }
                else
                {
                    string currencyAr = (string)(info.ArabicCurrencyName ?? info.Currency ?? "د.ك");
                    message = isDepositOnly
                        ? $@"✅ *تم استلام الدفعة المقدمة بنجاح*
━━━━━━━━━━━━━━━━━━

👤 *العميل:* {(string)info.CustomerName}
💇 *الخدمة:* {(string)(info.ItemArName ?? info.ItemEnName)}
💰 *المبلغ المدفوع:* {paidAmount:F2} {currencyAr}
📊 *السعر الكلي:* {totalPrice:F2} {currencyAr}
⏳ *المتبقي:* {remaining:F2} {currencyAr}
💳 *طريقة الدفع:* {(string?)info.PaymentMethod ?? "دفع إلكتروني"}

📄 *الفاتورة:* {pdfUrl}

━━━━━━━━━━━━━━━━━━
شكراً لكم! المبلغ المتبقي يُسدَّد عند الحضور. 🙏"
                        : $@"✅ *تم استلام الدفعة بنجاح*
━━━━━━━━━━━━━━━━━━

👤 *العميل:* {(string)info.CustomerName}
💇 *الخدمة:* {(string)(info.ItemArName ?? info.ItemEnName)}
💰 *المبلغ المدفوع:* {paidAmount:F2} {currencyAr}
💳 *طريقة الدفع:* {(string?)info.PaymentMethod ?? "دفع إلكتروني"}

📄 *الفاتورة:* {pdfUrl}

━━━━━━━━━━━━━━━━━━
شكراً لكم على الدفع! 🙏";
                }

                var httpClient = httpClientFactory.CreateClient();
                string waToken = configuration["WhatsApp:ApiKey"] ?? "";
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", waToken);

                var payload = new
                {
                    instance_id = instanceId,
                    message = message,
                    number = customerPhone
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await httpClient.PostAsync(
                    "https://business.enjazatik.com/api/v1/send-message", content);

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.MYFATOORAH_TRANSACTIONS SET WhatsAppSent = 1 
                    WHERE Id = @Id",
                    new { Id = transactionId });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WhatsApp invoice send failed: {ex.Message}");
            }
        }

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var cleaned = new string(phone.Where(char.IsDigit).ToArray());
            if (cleaned.StartsWith("0")) cleaned = "965" + cleaned.Substring(1);
            if (cleaned.Length == 8) cleaned = "965" + cleaned;
            return cleaned;
        }

        #endregion
    }
}