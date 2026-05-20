// Modules/System/Models/RefundDtos.cs

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class RefundDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ── Request ──────────────────────────────────────────────

        public record RefundRequest(
            int InvoiceId,
            int BranchId,
            /// <summary>'CASH' | 'LINK' | 'WALLET'</summary>
            string RefundType,
            /// <summary>null = refund entire invoice; populated = partial refund per line</summary>
            List<RefundLineRequest>? Lines,
            string? CancellationReason,
            string? CustomerComment,
            /// <summary>Only for RefundType='LINK'</summary>
            string? RefundLink
        );

        public record RefundLineRequest(
            /// <summary>Provide InvoiceLineId when invoice has AppointmentInvoiceLines rows</summary>
            int? InvoiceLineId,
            /// <summary>Always required</summary>
            int AppointmentId,
            int ItemId,
            int StaffId,
            decimal RefundAmount
        );

        // ── Response ─────────────────────────────────────────────

        public record RefundResponse(
            int RefundTransactionId,
            string RefundType,
            decimal RefundAmount,
            string InvoiceNumber,
            bool AppointmentsCancelled,
            bool WalletCredited,
            int? WalletSubscriptionId
        );

        // ── Refund line detail (for history view) ────────────────

        public record RefundTransactionLineDto(
            int Id,
            int? InvoiceLineId,
            int AppointmentId,
            int ItemId,
            string ItemName,
            int StaffId,
            string StaffName,
            decimal RefundedAmount,
            bool IsFullyRefunded
        );

        // ── Full refund transaction detail (for invoice/history) ─

        public record RefundTransactionDto(
            int Id,
            int InvoiceId,
            string InvoiceNumber,
            int BranchId,
            int CustomerId,
            string CustomerName,
            string RefundType,
            decimal RefundAmount,
            string? CancellationReason,
            string? CustomerComment,
            string? RefundLink,
            string Status,
            DateTime ProcessedAt,
            List<RefundTransactionLineDto> Lines
        );

        // ── History item (for customer profile panel) ────────────

        public record CustomerRefundHistoryDto(
            int RefundTransactionId,
            string InvoiceNumber,
            DateTime ProcessedAt,
            string RefundType,
            decimal RefundAmount,
            string? CancellationReason,
            string? CustomerComment
        );

        // ── Dashboard refund summary card ────────────────────────

        public record RefundSummaryDto(
            int TotalRefunds,
            decimal TotalRefundAmount,
            int CashRefunds,
            int LinkRefunds,
            int WalletRefunds
        );
    }
}
