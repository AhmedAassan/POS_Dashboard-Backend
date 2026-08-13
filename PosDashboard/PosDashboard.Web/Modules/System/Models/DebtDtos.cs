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
        // Invoice list — the /orders table (3 tabs: unpaid / paid / wallet)
        // =====================================================================

        /// <summary>
        /// One payment method that contributed to an invoice, already aggregated
        /// (all Cash rows collapse into one Cash entry). Wallet is a flag rather
        /// than a payment type id, because a wallet deduction is booked against a
        /// normal payment type with IsWalletPayment = 1 — exactly how the POS,
        /// the dashboard and the settle endpoint all record it.
        /// </summary>
        public record InvoicePaymentMethodDto(
            int PaymentTypeId,
            string NameEn,
            string NameAr,
            decimal Amount,
            bool IsWallet
        );

        /// <summary>
        /// One row of the orders table. Denormalised on purpose: the grid must be
        /// sortable and filterable without N+1 lookups per row.
        ///
        /// The trailing block (PaymentStatus onwards) is only populated for the
        /// paid/wallet tabs. The unpaid tab leaves it at its defaults, so nothing
        /// pays for data it does not render.
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
            string? Notes,

            // ── Paid / wallet tabs only ───────────────────────────────────────
            string? PaymentStatus = null,
            bool IsDeferred = false,
            /// <summary>Set when this invoice started as debt and was later collected.</summary>
            DateTime? SettledAt = null,
            /// <summary>When the money actually arrived: SettledAt for a collected
            /// debt, CreatedAt for a paid-at-the-counter sale. UTC.</summary>
            DateTime? PaidAt = null,
            decimal WalletPaidAmount = 0m,
            decimal OtherPaidAmount = 0m,
            /// <summary>Every fils came out of the wallet — no cash, no card.</summary>
            bool IsFullyWalletPaid = false,
            decimal TotalRefunded = 0m,
            /// <summary>Days since the money arrived (the paid-tab counterpart of AgeDays).</summary>
            int PaidAgeDays = 0,
            List<InvoicePaymentMethodDto>? PaymentMethods = null
        );

        /// <summary>
        /// Totals for the current filter — drives the summary cards. Which fields
        /// carry meaning depends on the tab: the debt figures belong to 'unpaid',
        /// the paid figures to 'paid' and 'wallet'.
        /// </summary>
        public record DebtSummaryDto(
            int InvoiceCount,
            decimal TotalDebt,
            int CustomerCount,
            decimal DeliveryDebt,
            decimal PickupDebt,
            decimal OverdueDebt,          // older than OverdueDays
            int OverdueDays,
            string Currency,

            // ── Paid / wallet tabs ────────────────────────────────────────────
            decimal TotalPaid = 0m,
            decimal WalletPaid = 0m,
            decimal OtherPaid = 0m,
            decimal TotalRefunded = 0m,
            /// <summary>Invoices in the current filter that used the wallet at all.</summary>
            int WalletInvoiceCount = 0
        );

        public record DebtInvoiceListDto(
            PagedResult<DebtInvoiceDto> Page,
            DebtSummaryDto Summary,
            int TzOffset,
            /// <summary>Echoed back so the client can ignore a stale response.</summary>
            string Tab = "unpaid"
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
