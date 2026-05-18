// Modules/System/Models/MyFatoorahDtos.cs

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosDashboard.Web.Modules.System.Models
{
    public class MyFatoorahDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Initiate Payment =====
        public record InitiatePaymentRequest(
            int AppointmentId,
            string? CustomerEmail,
            decimal? DepositAmount   // ← NEW: null means charge full remaining
        );

        public record InitiatePaymentResponse(
            int TransactionId,
            string InvoiceURL,
            string InvoiceId,
            decimal Amount,
            string Currency
        );

        // ===== Payment Callback =====
        public record PaymentCallbackResult(
            int TransactionId,
            int AppointmentId,
            bool IsSuccess,
            string Status,
            string? PaymentMethod,
            decimal Amount,
            string Currency,
            string? InvoiceNumber,
            string? PdfUrl
        );

        // ===== Transaction Status =====
        public record TransactionStatusDto(
            int Id,
            int AppointmentId,
            string? InvoiceId,
            string? InvoiceURL,
            decimal Amount,
            string Currency,
            string Status,
            string? PaymentMethod,
            DateTime? TransactionDate,
            string? PdfInvoiceUrl,
            bool WhatsAppSent,
            DateTime CreatedAt
        );

        // ===== MyFatoorah API Models =====
        public class MFSendPaymentRequest
        {
            public string NotificationOption { get; set; } = "LNK";
            public decimal InvoiceValue { get; set; }
            public string CustomerName { get; set; } = "";
            public string? CustomerEmail { get; set; }
            public string? CustomerMobile { get; set; }
            public string? MobileCountryCode { get; set; }
            public string DisplayCurrencyIso { get; set; } = "KWD";
            public string CallBackUrl { get; set; } = "";
            public string ErrorUrl { get; set; } = "";
            public string Language { get; set; } = "AR";
            public string? CustomerReference { get; set; }
            public List<MFInvoiceItem>? InvoiceItems { get; set; }
        }

        public class MFExecutePaymentRequest
        {
            public decimal InvoiceValue { get; set; }
            public string PaymentMethodId { get; set; } = "0";
            public string CustomerName { get; set; } = "";
            public string? CustomerEmail { get; set; }
            public string? CustomerMobile { get; set; }
            public string? MobileCountryCode { get; set; }
            public string DisplayCurrencyIso { get; set; } = "KWD";
            public string CallBackUrl { get; set; } = "";
            public string ErrorUrl { get; set; } = "";
            public string Language { get; set; } = "AR";
            public string? CustomerReference { get; set; }
            public List<MFInvoiceItem>? InvoiceItems { get; set; }
        }

        public class MFInvoiceItem
        {
            public string ItemName { get; set; } = "";
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
        }

        public class MFSendPaymentResponse
        {
            public bool IsSuccess { get; set; }
            public string? Message { get; set; }
            public MFSendPaymentData? Data { get; set; }
            public MFValidationError[]? ValidationErrors { get; set; }
        }

        public class MFSendPaymentData
        {
            public int InvoiceId { get; set; }
            public string? InvoiceURL { get; set; }
        }

        public class MFValidationError
        {
            public string? Name { get; set; }
            public string? Error { get; set; }
        }

        public class MFGetPaymentStatusRequest
        {
            public string Key { get; set; } = "";
            public string KeyType { get; set; } = "PaymentId";
        }

        public class MFPaymentStatusResponse
        {
            public bool IsSuccess { get; set; }
            public MFPaymentStatusData? Data { get; set; }
        }

        public class MFPaymentStatusData
        {
            public int InvoiceId { get; set; }
            public string? InvoiceStatus { get; set; }

            [JsonConverter(typeof(FlexibleDecimalConverter))]
            public decimal? InvoiceValue { get; set; }

            public string? CustomerName { get; set; }
            public string? CustomerMobile { get; set; }
            public string? InvoiceReference { get; set; }
            public string? CustomerReference { get; set; }
            public List<MFInvoiceTransaction>? InvoiceTransactions { get; set; }
        }

        public class MFInvoiceTransaction
        {
            public string? PaymentId { get; set; }
            public string? PaymentGateway { get; set; }
            public string? TransactionStatus { get; set; }

            [JsonConverter(typeof(FlexibleDecimalConverter))]
            public decimal? PaidCurrencyValue { get; set; }

            public string? PaidCurrency { get; set; }
            public string? TransactionDate { get; set; }
        }
    }
}

public class FlexibleDecimalConverter : System.Text.Json.Serialization.JsonConverter<decimal?>
{
    public override decimal? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.Number:
                return reader.GetDecimal();
            case System.Text.Json.JsonTokenType.String:
                var str = reader.GetString();
                if (string.IsNullOrWhiteSpace(str)) return null;
                if (decimal.TryParse(str, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var result))
                    return result;
                return null;
            case System.Text.Json.JsonTokenType.Null:
                return null;
            default:
                return null;
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, decimal? value,
        JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}