using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class WalletDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== SUBS_TYPE =====
        public record SubsTypeDto(
            int Id,
            string Name,
            double? Value,
            int? DaysCount,
            decimal? DiscountValue,
            double? Count,
            int? Type,
            int? DiscountType
        );

        // ===== Subscription (wallet purchase record) =====
        public record SubscriptionDto(
            int Id,
            Guid Guid,
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            int SubTypeId,
            string SubTypeName,
            decimal Value,
            int? DiscountType,
            decimal? DiscountValue,
            decimal Net,
            decimal? Count,
            DateTime StartDate,
            DateTime EndDate,
            decimal? DaysCount,
            int BranchId,
            int IsPaid,
            DateTime AddedDate,
            decimal CurrentBalance,
            bool IsExpired,
            bool IsActive,
            int? PayerCustomerId,
            string? PayerCustomerName,
            string? PayerNote,

            // ── PHASE 2 ──
            decimal TotalCredit,      // SUM of all positive ledger movements (credits granted over lifetime)
            decimal TotalPaid,        // SUM of all non-deleted payment amounts
            string LastActionType     // 'CREATE' | 'RENEW' | 'UPGRADE' — from most recent payment row
        );

        // ===== Subscription Payment =====
        public record SubscriptionPaymentDto(
            int Id,
            int SubscriptionId,
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentTypeNameAr,
            decimal PaymentAmount,
            DateTime PaymentDate,
            string? Notes,
            // ── PHASE 2 ──
            string ActionType,            // 'CREATE' | 'RENEW' | 'UPGRADE'
            int? PreviousSubTypeId,       // only populated for UPGRADE rows
            string? PreviousSubTypeName   // joined from SUBS_TYPE
        );
        // ===== Renew Wallet Request =====
        public record RenewSubscriptionRequest(
            int PaymentTypeId,
            DateTime? StartDate,          // optional; defaults to today. End date extends from this.
            decimal? CustomValue,         // override SUBS_TYPE.VALUE if provided
            decimal? CustomNet,           // override calculated net if provided
            string? Notes,
            int? PayerCustomerId,
            string? PayerNote
        );

        // ===== Upgrade Wallet Request =====
        public record UpgradeSubscriptionRequest(
            int NewSubTypeId,             // must differ from current SubTypeId
            int PaymentTypeId,
            DateTime? StartDate,
            decimal? CustomValue,
            decimal? CustomNet,
            string? Notes,
            int? PayerCustomerId,
            string? PayerNote
        );
        // ===== Wallet History (ledger) =====
        public record SubscriptionHistoryDto(
            int Id,
            int? SubscriptionId,
            int RefType,
            string RefTypeLabel,
            decimal Amount,
            decimal Balance,
            DateTime AddedDate,
            int? InvoiceId
        );

        // ===== Wallet Detail (full view) =====
        public record WalletDetailDto(
            SubscriptionDto Subscription,
            List<SubscriptionPaymentDto> Payments,
            List<SubscriptionHistoryDto> History
        );

        // ===== Customer Wallet Summary (for appointment drawer) =====
        public record CustomerWalletSummaryDto(
            bool HasActiveWallet,
            decimal CurrentBalance,
            int? SubscriptionId,
            string? SubTypeName,
            DateTime? EndDate
        );

        // ===== Create Wallet Request =====
        // ===== Create Wallet Request =====
        public record CreateSubscriptionRequest(
            int CustomerId,
            int SubTypeId,
            int BranchId,
            DateTime StartDate,
            int PaymentTypeId,
            decimal? CustomValue,        // override SUBS_TYPE.VALUE if provided
            decimal? CustomNet,          // override calculated net if provided
            string? Notes,
            // "Who paid?" - optional payer (different from wallet owner)
            int? PayerCustomerId,        // nullable; if null, owner paid themselves
            string? PayerNote            // optional note e.g. "Season's greetings"
        );

        // ===== Deduct Wallet (use wallet balance on appointment) =====
        public record DeductWalletRequest(
            int AppointmentId,
            int SubscriptionId,
            decimal Amount,
            int PaymentTypeId           // the "Wallet" payment type ID
        );

        public record DeductWalletResponse(
            int AppointmentId,
            int SubscriptionId,
            decimal DeductedAmount,
            decimal RemainingWalletBalance,
            decimal AppointmentPaidAmount,
            decimal AppointmentRemainingAmount,
            string AppointmentPaymentStatus
        );
    }
}