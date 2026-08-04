using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class WalletDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ═════════════════════════════════════════════════════════════════
        // SUBS_TYPE (wallet type)
        // ═════════════════════════════════════════════════════════════════

        public record SubsTypeDto(
            int Id,
            string Name,
            double? Value,
            int? DaysCount,
            decimal? DiscountValue,
            double? Count,
            int? Type,
            int? DiscountType,

            // ── UPDATE WALLET FLOW ──
            /// <summary>When true the customer may spend past Count, down to MaxCount.</summary>
            bool AllowOverdraft,
            /// <summary>Hard ceiling on total spend. Only meaningful when AllowOverdraft.</summary>
            int? MaxCount,
            /// <summary>MaxCount - Count. How far below zero the balance may go. 0 when overdraft is off.</summary>
            decimal OverdraftLimit,
            /// <summary>Net after the type's own discount — shown in the type list.</summary>
            decimal NetValue,
            /// <summary>Wallets currently referencing this type. Blocks hard delete.</summary>
            int WalletsInUse
        );

        /// <summary>Create/update payload for a wallet type. Mutable class so partial JSON binds cleanly.</summary>
        public class SubsTypeSaveRequest
        {
            public string Name { get; set; } = "";
            public double? Value { get; set; }
            public int? DaysCount { get; set; }
            public double? Count { get; set; }
            public int? Type { get; set; }
            public int? DiscountType { get; set; }     // 1 = percentage, 2 = fixed
            public decimal? DiscountValue { get; set; }

            // ── UPDATE WALLET FLOW ──
            public bool AllowOverdraft { get; set; }
            public int? MaxCount { get; set; }
        }

        // ═════════════════════════════════════════════════════════════════
        // Subscription (a customer's wallet)
        // ═════════════════════════════════════════════════════════════════

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

            decimal TotalCredit,      // SUM of positive ledger movements
            decimal TotalPaid,        // SUM of non-deleted payment amounts
            string LastActionType,    // 'CREATE' | 'RENEW' | 'UPGRADE' | 'ADJUST'

            // ── UPDATE WALLET FLOW ──
            /// <summary>Snapshot of the type's overdraft flag at purchase time.</summary>
            bool AllowOverdraft,
            /// <summary>Snapshot of the type's MaxCount at purchase time.</summary>
            decimal? MaxCount,
            /// <summary>MaxCount - Count. 0 when overdraft is off.</summary>
            decimal OverdraftLimit,
            /// <summary>Positive number the customer owes the salon (= -CurrentBalance when negative).</summary>
            decimal AmountOwed,
            /// <summary>What can still be spent = CurrentBalance + OverdraftLimit (never negative).</summary>
            decimal AvailableToSpend,
            /// <summary>Settled and shut. A closed wallet can neither be spent nor adjusted again.</summary>
            bool IsClosed,
            DateTime? ClosedAt,
            string? ClosedReason,
            /// <summary>True when the balance is negative — the customer overdrew.</summary>
            bool IsOverdrawn
        );

        // ═════════════════════════════════════════════════════════════════
        // Payments / ledger
        // ═════════════════════════════════════════════════════════════════

        public record SubscriptionPaymentDto(
            int Id,
            int SubscriptionId,
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentTypeNameAr,
            decimal PaymentAmount,
            DateTime PaymentDate,
            string? Notes,
            string ActionType,            // 'CREATE' | 'RENEW' | 'UPGRADE' | 'ADJUST'
            int? PreviousSubTypeId,
            string? PreviousSubTypeName
        );

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

        // ═════════════════════════════════════════════════════════════════
        // Adjust (settlement) — Part 2
        // ═════════════════════════════════════════════════════════════════

        public record WalletAdjustmentDto(
            int Id,
            int SubscriptionId,
            string AdjustType,            // 'COLLECT' | 'REFUND'
            decimal DueAmount,
            decimal SettledAmount,
            decimal WaivedAmount,
            int? PaymentTypeId,
            string? PaymentTypeName,
            string? PaymentTypeNameAr,
            string? RefundMethod,         // 'CASH' | 'LINK'
            string? RefundLink,
            decimal BalanceBefore,
            decimal BalanceAfter,
            bool ClosedWallet,
            string? Notes,
            DateTime AddedDate
        );

        /// <summary>
        /// Everything the Adjust dialog needs before the cashier types anything:
        /// which direction the money flows and how much is on the table.
        /// </summary>
        public record WalletAdjustPreviewDto(
            int SubscriptionId,
            int CustomerId,
            string CustomerName,
            string SubTypeName,
            decimal CurrentBalance,
            /// <summary>'COLLECT' (customer owes) | 'REFUND' (salon owes) | 'NONE' (balance is 0)</summary>
            string Direction,
            decimal DueAmount,
            decimal Count,
            decimal? MaxCount,
            decimal OverdraftLimit,
            bool IsExpired,
            bool IsClosed,
            DateTime EndDate
        );

        /// <summary>
        /// One request shape for both directions. SettledAmount is what actually
        /// moves; WaivedAmount is what is forgiven. They need not add up to
        /// DueAmount — a REFUND may deliberately hand back MORE (a goodwill
        /// top-up), which is why SettledAmount is taken at face value rather
        /// than derived from DueAmount.
        /// </summary>
        public class WalletAdjustRequest
        {
            /// <summary>'COLLECT' | 'REFUND'. Must match the wallet's actual direction.</summary>
            public string AdjustType { get; set; } = "";
            /// <summary>Money that changes hands. 0 is legal (full waiver).</summary>
            public decimal SettledAmount { get; set; }
            /// <summary>Amount written off. 0 is legal.</summary>
            public decimal WaivedAmount { get; set; }
            /// <summary>COLLECT only — how the money came in.</summary>
            public int? PaymentTypeId { get; set; }
            /// <summary>REFUND only — 'CASH' or 'LINK'. Never 'WALLET': the wallet is closing.</summary>
            public string? RefundMethod { get; set; }
            /// <summary>REFUND + LINK only.</summary>
            public string? RefundLink { get; set; }
            /// <summary>Close the wallet after settling. Defaults to true.</summary>
            public bool CloseWallet { get; set; } = true;
            public string? Notes { get; set; }
            public int? BranchId { get; set; }
        }

        public record WalletAdjustResponse(
            int AdjustmentId,
            int SubscriptionId,
            string AdjustType,
            decimal DueAmount,
            decimal SettledAmount,
            decimal WaivedAmount,
            decimal BalanceBefore,
            decimal BalanceAfter,
            bool WalletClosed,
            WalletDetailDto? Wallet
        );

        // ═════════════════════════════════════════════════════════════════
        // Detail / summary
        // ═════════════════════════════════════════════════════════════════

        public record WalletDetailDto(
            SubscriptionDto Subscription,
            List<SubscriptionPaymentDto> Payments,
            List<SubscriptionHistoryDto> History,
            List<WalletAdjustmentDto> Adjustments
        );

        /// <summary>
        /// Quick wallet snapshot for the POS / appointment drawer.
        /// AvailableToSpend already folds in the overdraft allowance, so callers
        /// never have to know the overdraft rules in order to cap an input.
        /// </summary>
        public record CustomerWalletSummaryDto(
            bool HasActiveWallet,
            decimal CurrentBalance,
            int? SubscriptionId,
            string? SubTypeName,
            DateTime? EndDate,

            // ── UPDATE WALLET FLOW ──
            bool AllowOverdraft,
            decimal OverdraftLimit,
            decimal AvailableToSpend,
            decimal AmountOwed,
            bool IsClosed,
            /// <summary>Credit the wallet was sold with. The till shows spend against this.</summary>
            decimal Count,
            /// <summary>Absolute spend ceiling. Null when overdraft is off.</summary>
            decimal? MaxCount
        );

        /// <summary>
        /// Wallet block printed on the invoice and pushed into the WhatsApp
        /// receipt: name, remaining balance, expiry.
        /// </summary>
        public record InvoiceWalletInfoDto(
            int SubscriptionId,
            string SubTypeName,
            decimal CurrentBalance,
            DateTime EndDate,
            bool IsExpired,
            bool IsClosed,
            bool AllowOverdraft,
            decimal? MaxCount,
            decimal OverdraftLimit,
            decimal AmountOwed
        );

        // ═════════════════════════════════════════════════════════════════
        // Create / renew / upgrade
        // ═════════════════════════════════════════════════════════════════

        public record CreateSubscriptionRequest(
            int CustomerId,
            int SubTypeId,
            int BranchId,
            DateTime StartDate,
            int PaymentTypeId,
            decimal? CustomValue,
            decimal? CustomNet,
            string? Notes,
            int? PayerCustomerId,
            string? PayerNote
        );

        public record RenewSubscriptionRequest(
            int PaymentTypeId,
            DateTime? StartDate,
            decimal? CustomValue,
            decimal? CustomNet,
            string? Notes,
            int? PayerCustomerId,
            string? PayerNote
        );

        public record UpgradeSubscriptionRequest(
            int NewSubTypeId,
            int PaymentTypeId,
            DateTime? StartDate,
            decimal? CustomValue,
            decimal? CustomNet,
            string? Notes,
            int? PayerCustomerId,
            string? PayerNote
        );

        /// <summary>
        /// What a renew/upgrade would do to the balance, computed BEFORE the
        /// cashier commits. Part 3 makes this non-obvious — an expired wallet
        /// drops leftover credit but still carries debt forward — so the UI shows
        /// the arithmetic instead of surprising the customer after the fact.
        /// </summary>
        public record RenewPreviewDto(
            decimal CurrentBalance,
            bool IsExpired,
            bool IsClosed,
            /// <summary>Balance carried into the new period. Positive credit is dropped when expired.</summary>
            decimal CarriedBalance,
            /// <summary>Credit dropped because the wallet had expired (0 when nothing was dropped).</summary>
            decimal DroppedCredit,
            /// <summary>Debt carried forward and netted against the new credit (0 when none).</summary>
            decimal CarriedDebt,
            decimal CreditGranted,
            decimal ResultingBalance,
            decimal Net,
            DateTime NewEndDate
        );

        // ═════════════════════════════════════════════════════════════════
        // Deduct
        // ═════════════════════════════════════════════════════════════════

        public record DeductWalletRequest(
            int AppointmentId,
            int SubscriptionId,
            decimal Amount,
            int PaymentTypeId
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
