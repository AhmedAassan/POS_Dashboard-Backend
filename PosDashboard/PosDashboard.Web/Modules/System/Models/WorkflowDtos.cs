// Modules/System/Models/WorkflowDtos.cs
// Contracts for the Order (Invoice) Workflow controller — api/workflow.
// PascalCase mirrors the TypeScript in workflow.api.ts exactly, so nothing is
// remapped on the wire — same convention as DebtDtos / PosDtos.
// The one idea worth stating up front: WORKFLOW STATE AND PAYMENT STATE ARE
// INDEPENDENT. An order can be Completed and still owe money; a wallet-paid
// order owes nothing the moment it is created. Every DTO below carries both,
// and nothing here ever infers one from the other.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class WorkflowDtos
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
        // Statuses
        // =====================================================================

        // The seven states an invoice can be in. Strings rather than an enum on
        // the wire because they are stored as strings, read in SQL and shown in
        // deep links — an int would need a translation table in three places.
        public static class Status
        {
            public const string Pending = "Pending";
            public const string Processing = "Processing";
            public const string Ready = "Ready";
            public const string OutForDelivery = "OutForDelivery";
            public const string Delivered = "Delivered";
            public const string Completed = "Completed";
            public const string Cancelled = "Cancelled";

            public static readonly string[] All =
            {
                Pending, Processing, Ready, OutForDelivery, Delivered, Completed, Cancelled
            };

            // Nothing moves out of these.
            public static readonly string[] Terminal = { Completed, Cancelled };

            // Only reachable by an order that is actually being delivered.
            public static readonly string[] DeliveryOnly = { OutForDelivery, Delivered };

            // Sort order for the board columns and the "rank" comparisons.
            public static int Rank(string? s) => s switch
            {
                Pending => 0,
                Processing => 1,
                Ready => 2,
                OutForDelivery => 3,
                Delivered => 4,
                Completed => 5,
                Cancelled => 6,
                _ => -1
            };
        }

        // One status, described well enough that the client never needs
        // its own copy of the labels, colours or ordering.
        public record WorkflowStatusDto(
            string Code,
            string NameEn,
            string NameAr,
            string Icon,
            string ColorCode,
            int Ordering,
            bool IsTerminal,
            bool IsDeliveryOnly
        );

        // =====================================================================
        // Config — one bootstrap call for the whole page
        // =====================================================================

        public record WorkflowSettingsDto(
            bool Enabled,
            bool RequireDriver,
            bool AllowSkipStages,
            bool PromptPaymentOnComplete,
            int StalePendingMinutes,
            // Mirrors debt.enabled — decides whether "collect now" is offered.
            bool DebtEnabled,
            // Mirrors delivery.enabled — with delivery off the two delivery
            // stages never appear at all.
            bool DeliveryEnabled
        );

        public record WorkflowConfigDto(
            WorkflowSettingsDto Settings,
            PosDtos.PosBranchDto Branch,
            List<WorkflowStatusDto> Statuses,
            List<DeliveryDtos.DeliveryDriverDto> Drivers,
            List<PosDtos.PosPaymentTypeDto> PaymentTypes,
            List<DeliveryDtos.AreaOptionDto> Areas,
            List<DeliveryDtos.GovernorateOptionDto> Governorates,
            DebtDtos.DebtSettingsDto DebtSettings,
            int TzOffset,
            // Live count per status under the CURRENT filter — the board
            // column headers and the tab badges read from this.
            Dictionary<string, int> Counts
        );

        // =====================================================================
        // The list / board row
        // =====================================================================

        public record WorkflowOrderDto(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            int BranchId,
            DateTime CreatedAt,

            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            string? CustomerPhone2,

            // ── Money ────────────────────────────────────────────────────────
            decimal SubTotal,
            decimal DiscountAmount,
            decimal DeliveryCharge,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string Currency,
            string? PaymentStatus,
            bool IsDeferred,
            // When the invoice was settled in full. NULL = still owing.
            DateTime? PaidAt,
            DateTime? SettledAt,
            // Nothing left to collect — covers cash-at-till AND wallet.
            bool IsPaid,
            // The wallet covered it outright, so completion must never
            // ask for money. This is the flag the UI checks, not IsPaid.
            bool IsWalletPaid,
            decimal WalletPaidAmount,

            // ── Fulfilment ───────────────────────────────────────────────────
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

            // ── Workflow ─────────────────────────────────────────────────────
            string WorkflowStatus,
            DateTime? WorkflowStatusAt,
            DateTime? ProcessingAt,
            DateTime? ReadyAt,
            DateTime? OutForDeliveryAt,
            DateTime? DeliveredAt,
            DateTime? CompletedAt,
            DateTime? CancelledAt,
            string? WorkflowCancelReason,
            // Minutes in the current stage — the ageing pill.
            int MinutesInStage,
            // Past StalePendingMinutes and not terminal.
            bool IsStale,
            // The single legal next step, or NULL at a fork / terminal.
            string? NextStatus,

            // ── Content ──────────────────────────────────────────────────────
            int ItemCount,
            string? ServicesSummary,
            string? Notes,
            int CommentCount,
            int AttachmentCount,
            // Newest comment, so the row can show it without a second call.
            string? LastCommentText,
            string? LastCommentBy,
            DateTime? LastCommentAt,

            bool IsVoid,
            int AgeDays
        );

        public record WorkflowSummaryDto(
            int OrderCount,
            decimal TotalValue,
            decimal OutstandingValue,
            int UnpaidCount,
            // Done, but the money never arrived. The number this page exists to surface.
            int CompletedUnpaidCount,
            decimal CompletedUnpaidValue,
            int DeliveryCount,
            int PickupCount,
            int StaleCount,
            string Currency,
            Dictionary<string, int> CountByStatus,
            Dictionary<string, decimal> ValueByStatus
        );

        public record WorkflowListDto(
            PagedResult<WorkflowOrderDto> Page,
            WorkflowSummaryDto Summary,
            int TzOffset,
            string? Status
        );

        // The board: every column, each with its own capped slice of rows.
        public record WorkflowBoardColumnDto(
            string Status,
            int TotalCount,
            decimal TotalValue,
            List<WorkflowOrderDto> Items,
            // TotalCount exceeded the per-column cap — the UI says "+37 more".
            bool HasMore
        );

        public record WorkflowBoardDto(
            List<WorkflowBoardColumnDto> Columns,
            WorkflowSummaryDto Summary,
            int TzOffset
        );

        // =====================================================================
        // Timeline — events + comments, already merged and ordered
        // =====================================================================

        public record WorkflowAttachmentDto(
            int Id,
            int InvoiceId,
            int? CommentId,
            string FileName,
            string FileUrl,
            string? ContentType,
            long? FileSize,
            bool IsImage,
            string? UserName,
            DateTime CreatedAt
        );

        public record WorkflowCommentDto(
            int Id,
            int InvoiceId,
            int? EventId,
            string? Stage,
            string? CommentText,
            bool IsInternal,
            int? UserId,
            string? UserName,
            DateTime CreatedAt,
            DateTime? EditedAt,
            List<WorkflowAttachmentDto> Attachments
        );

        public record WorkflowEventDto(
            int Id,
            int InvoiceId,
            string? FromStatus,
            string ToStatus,
            int? DriverId,
            string? DriverName,
            string? DriverNameAr,
            decimal? RemainingAmount,
            bool WasPaid,
            string? Note,
            int? UserId,
            string? UserName,
            DateTime CreatedAt,
            int? SecondsInPrevious
        );

        // One entry in the merged timeline. Kind tells the client which of the
        // two payloads is populated — a discriminated union in the only shape
        // JSON can express.
        public record WorkflowTimelineEntryDto(
            string Kind,                        // "event" | "comment"
            DateTime CreatedAt,
            WorkflowEventDto? Event,
            WorkflowCommentDto? Comment
        );

        public record WorkflowDetailDto(
            WorkflowOrderDto Order,
            List<WorkflowTimelineEntryDto> Timeline,
            // Every transition this user may perform right now, already
            // resolved server-side. The client renders buttons; it never decides
            // what is legal.
            List<WorkflowTransitionOptionDto> Allowed,
            List<WorkflowOrderLineDto> Lines,
            int TzOffset
        );

        public record WorkflowOrderLineDto(
            int AppointmentId,
            string ServiceName,
            decimal UnitPrice,
            int Quantity,
            decimal LineTotal,
            bool IsRefunded
        );

        public record WorkflowTransitionOptionDto(
            string ToStatus,
            string NameEn,
            string NameAr,
            string Icon,
            // The transition cannot proceed without a driver.
            bool RequiresDriver,
            // Money is still owed and this is a completing step — the
            // client should offer collection before (or alongside) the move.
            bool SuggestsPayment,
            // Jumping ahead rather than stepping. Confirm before firing.
            bool IsSkip,
            bool IsDestructive
        );

        // =====================================================================
        // Requests
        // =====================================================================

        public record TransitionRequest(
            string ToStatus,
            int? DriverId = null,
            string? Note = null,
            // Ids returned by /upload, attached to the note comment.
            List<int>? AttachmentIds = null,
            // Set by the client after the user confirms a skip.
            bool ConfirmSkip = false,
            // Completing an unpaid order on purpose. Without it the
            // server refuses so "I forgot to take the money" cannot happen silently.
            bool AllowUnpaid = false
        );

        public record BulkTransitionRequest(
            List<int> InvoiceIds,
            string ToStatus,
            int? DriverId = null,
            string? Note = null,
            bool ConfirmSkip = false,
            bool AllowUnpaid = false
        );

        public record BulkTransitionResultDto(
            int Succeeded,
            int Failed,
            List<BulkTransitionFailureDto> Failures
        );

        public record BulkTransitionFailureDto(
            int InvoiceId,
            string? InvoiceNumber,
            string Reason
        );

        public record AssignDriverRequest(
            int DriverId,
            string? Note = null
        );

        public record AddCommentRequest(
            string? CommentText = null,
            List<int>? AttachmentIds = null,
            bool IsInternal = true
        );

        public record TransitionResultDto(
            int InvoiceId,
            string InvoiceNumber,
            string FromStatus,
            string ToStatus,
            DateTime At,
            decimal RemainingAmount,
            bool IsPaid,
            // Completed with money still owed — the client shows the
            // "collect later" reminder rather than a plain success toast.
            bool CompletedUnpaid,
            WorkflowOrderDto Order
        );

        // =====================================================================
        // Query
        // =====================================================================

        public record WorkflowQuery(
            string? Status = null,
            int? BranchId = null,
            string? Search = null,
            int? CustomerId = null,
            int? DriverId = null,
            int? AreaId = null,
            int? GovernorateId = null,
            string? OrderType = null,
            string? PaymentState = null,
            DateTime? DateFrom = null,
            DateTime? DateTo = null,
            bool OnlyStale = false,
            string? SortBy = "stage",
            string? SortDir = "desc",
            int Page = 1,
            int PageSize = 25
        );
    }
}
