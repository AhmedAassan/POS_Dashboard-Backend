// Modules/System/Models/NewSaleDtos.cs

using System;
using System.Collections.Generic;
using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;

namespace PosDashboard.Web.Modules.System.Models
{
    public class NewSaleDtos
    {
        // Generic API result — kept identical in shape to the rest of the project
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Request =====

        public record NewSaleLineRequest(
            int ItemId,
            int UnitId,
            int StaffId,
            int? DurationMinutes,        // optional override; falls back to ITEM_UNIT_DURATION → AppointmentDuration
            decimal? UnitPriceOverride,      // optional sale-only price (NEVER written back to ITEM_UNIT)
            string? Notes
        );

        public record NewSaleSplitPaymentRequest(
            int PaymentTypeId,
            decimal Amount,
            string? VoucherCode
        );

        public record NewSalePaymentsRequest(
            int? WalletSubscriptionId,   // optional
            decimal? WalletAmount,           // optional, must be ≤ wallet balance and ≤ remaining
            int? WalletPaymentTypeId,    // payment-type id used to record the wallet deduction
            List<NewSaleSplitPaymentRequest>? Splits   // any number of non-wallet entries
        );

        public record NewSaleRequest(
            int BranchId,
            int CustomerId,
            DateTime? SaleDate,              // optional, defaults to "today" (server local)
            string? StartTime,             // optional "HH:mm"; if null → backend snaps to nearest valid slot
            string ServiceType,           // "SALON" | "HOME"   (defaults handled in controller)
            int NumberOfPersons,
            string? Notes,
            List<NewSaleLineRequest> Lines,
            NewSalePaymentsRequest? Payments,
            bool SendWhatsApp
        );

        // ===== Response =====

        public record NewSaleLineWarning(
            int LineIndex,
            string Message
        );

        public record NewSaleResponse(
            int SaleId,                // = InvoiceId for now
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            Guid SaleGroupId,
            List<int> AppointmentIds,
            List<AppointmentDto> Appointments,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            decimal WalletDeductedAmount,
            string PaymentStatus,         // 'NONE' | 'DEPOSIT' | 'FULL'
            string Currency,
            bool WhatsAppSent,
            string? WhatsAppError,
            List<NewSaleLineWarning> Warnings
        );
    }
}