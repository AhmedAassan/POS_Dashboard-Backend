// Modules/System/Models/DebtDtos.cs
//
// DTOs for the Deferred Payment (Debt) flow — api/debt.
//
// A debt invoice is just an AppointmentInvoices row with IsDeferred = 1 and
// SettledAt = NULL. Nothing here introduces a parallel invoice model: these
// records are projections over the existing tables plus the three settlement
// tables added by 001_deferred_payment_flow.sql.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class DebtDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record PagedResult<T>(
            List<T> Items,
            int TotalCount,
            int Page,
            int PageSize,
            int TotalPages
        );

        // =====================================================================
        // Config — one bootstrap call for the /orders page and the POS
        // =====================================================================

        public record DebtSettingsDto(
            bool Enabled,
            bool AllowSettlementDiscount,
            decimal CustomerLimit          // 0 = no limit
        );

        public record DebtConfigDto(
            DebtSettingsDto Settings,
            PosDtos.PosBranchDto Branch,
            List<PosDtos.PosPaymentTypeDto> PaymentTypes,
            List<DeliveryDtos.DeliveryTypeDto> DeliveryTypes,
            List<DeliveryDtos.DeliveryDriverDto> Drivers,
            List<DeliveryDtos.AreaOptionDto> Areas,
            List<DeliveryDtos.GovernorateOptionDto> Governorates,
            int TzOffset
        );

        // =====================================================================
        // Unpaid (debt) invoice list — the /orders table
        // =====================================================================

        /// <summary>
        /// One row of the debt table. Denormalised on purpose: the grid must be
        /// sortable and filterable without N+1 lookups per row.
        /// </summary>
        public record DebtInvoiceDto(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            int BranchId,
            DateTime CreatedAt,            // UTC — the client shifts by TzOffset

            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            string? CustomerPhone2,

            decimal SubTotal,              // before the sale-time discount
            decimal DiscountAmount,        // sale-time discount
            decimal DeliveryCharge,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,       // what is still owed
            string Currency,

            // Delivery context (null for a counter sale)
            bool IsDelivery,
            int? DeliveryTypeId,
            string? DeliveryTypeNameEn,
            string? DeliveryTypeNameAr,
            int? DriverId,
            string? DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            int? AreaId,
            string? AreaNameEn,
            string? AreaNameAr,
            int? GovernorateId,
            string? GovernorateNameEn,
            string? GovernorateNameAr,
            string? AddressSummary,
            DateTime? DeliveryDate,

            int ItemCount,
            string? ServicesSummary,       // "Haircut, Beard trim +2"
            int AgeDays,                   // how long this debt has been open
            string? Notes
        );

        /// <summary>Totals for the current filter — drives the summary cards.</summary>
        public record DebtSummaryDto(
            int InvoiceCount,
            decimal TotalDebt,
            int CustomerCount,
            decimal DeliveryDebt,
            decimal PickupDebt,
            decimal OverdueDebt,          // older than OverdueDays
            int OverdueDays,
            string Currency
        );

        public record DebtInvoiceListDto(
            PagedResult<DebtInvoiceDto> Page,
            DebtSummaryDto Summary,
            int TzOffset
        );

        // =====================================================================
        // Customer debt
        // =====================================================================

        public record CustomerDebtSummaryDto(
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            decimal TotalDebt,
            int InvoiceCount,
            DateTime? OldestInvoiceAt,
            DateTime? LastPaymentAt,
            string Currency
        );

        // =====================================================================
        // Customer history (the "Customer History" dialog)
        // =====================================================================

        public record CustomerHistoryHeaderDto(
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            string? CustomerPhone2,
            int BranchId,
            string? BranchName,
            DateTime? CreatedDate,
            bool IsBlocked,
            string? Note,

            decimal TotalDebt,
            int DebtInvoiceCount,
            DateTime? LastPaymentAt,

            decimal LifetimeSpend,
            int TotalInvoices,
            int TotalBookings,
            decimal WalletBalance,
            bool HasActiveWallet,
            int AddressCount,
            string Currency
        );

        public record CustomerInvoiceRowDto(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            DateTime CreatedAt,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string PaymentStatus,
            bool IsDeferred,
            DateTime? SettledAt,
            bool IsDelivery,
            string? DeliveryTypeNameEn,
            string? DeliveryTypeNameAr,
            string? DriverName,
            string? AreaNameEn,
            string? AreaNameAr,
            int ItemCount,
            string? ServicesSummary,
            decimal TotalRefunded,
            string Currency
        );

        public record CustomerSubscriptionRowDto(
            int SubscriptionId,
            string SubTypeName,
            decimal Value,
            decimal Net,
            decimal CurrentBalance,
            DateTime StartDate,
            DateTime EndDate,
            bool IsPaid,
            bool IsExpired,
            bool IsWallet
        );

        public record CustomerWalletTxRowDto(
            int Id,
            int SubscriptionId,
            DateTime AddedDate,
            decimal Amount,           // negative = spent
            decimal Balance,
            int RefType,              // 1 = sale deduction, 0 = top-up
            int? InvoiceId,
            string? InvoiceNumber
        );

        public record CustomerBookingRowDto(
            int AppointmentId,
            DateTime AppointmentDate,
            string? StartTime,
            string? EndTime,
            string ItemNameEn,
            string ItemNameAr,
            string? StaffNameEn,
            string? StaffNameAr,
            string Status,
            string CheckoutStatus,
            string PaymentStatus,
            decimal TotalPrice,
            decimal PaidAmount,
            bool IsOnlineBooking
        );

        public record CustomerSettlementRowDto(
            int SettlementId,
            string SettlementNumber,
            DateTime SettledAt,
            int InvoiceCount,
            decimal TotalBefore,
            decimal DiscountAmount,
            decimal TotalCollected,
            string? DriverName,
            string PaymentSummary,     // "Cash 12.000 · KNET 5.000"
            string? Notes
        );

        public record CustomerHistoryDto(
            CustomerHistoryHeaderDto Header,
            PagedResult<CustomerInvoiceRowDto> Invoices,
            List<CustomerSubscriptionRowDto> Subscriptions,
            List<CustomerWalletTxRowDto> WalletTransactions,
            PagedResult<CustomerBookingRowDto> Bookings,
            List<CustomerSettlementRowDto> Settlements,
            List<DeliveryDtos.DeliveryAddressDto> Addresses,
            int TzOffset
        );

        // =====================================================================
        // Settlement (التحصيل)
        // =====================================================================

        public record DebtSplitPaymentRequest(
            int PaymentTypeId,
            decimal Amount,
            string? VoucherCode
        );

        public record DebtPaymentsRequest(
            int? WalletSubscriptionId,
            decimal? WalletAmount,
            int? WalletPaymentTypeId,
            List<DebtSplitPaymentRequest>? Splits
        );

        /// <summary>
        /// Settlement discount. Only honoured when the whole selection is
        /// collected in ONE go (which is exactly what this endpoint does), and
        /// only when 'debt.allowSettlementDiscount' is on.
        /// </summary>
        public record DebtDiscountRequest(
            string Type,        // "percentage" | "fixed"
            decimal Value
        );

        public record SettleDebtRequest(
            int BranchId,
            List<int> InvoiceIds,
            DebtPaymentsRequest? Payments,
            DebtDiscountRequest? Discount = null,
            /// <summary>Set when the collection was made by a driver rather than at the counter.</summary>
            int? DriverId = null,
            string? Notes = null,
            bool SendWhatsApp = false
        );

        public record SettledInvoiceDto(
            int InvoiceId,
            string InvoiceNumber,
            decimal AmountBefore,
            decimal DiscountShare,
            decimal AmountCollected,
            decimal NewTotalAmount
        );

        public record SettleDebtResponse(
            int SettlementId,
            string SettlementNumber,
            DateTime SettledAt,
            int InvoiceCount,
            decimal TotalBefore,
            decimal DiscountAmount,
            decimal TotalCollected,
            decimal WalletDeductedAmount,
            string Currency,
            List<SettledInvoiceDto> Invoices,
            bool WhatsAppSent,
            string? WhatsAppError
        );
    }
}
