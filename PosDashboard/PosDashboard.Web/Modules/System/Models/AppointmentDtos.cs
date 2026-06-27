// Modules/System/Models/AppointmentDtos.cs

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AppointmentDtos
    {
        // ===== Generic API result (reuse if you already have one) =====
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Create =====
        public record CreateAppointmentRequest(
            int BranchId,
            int CustomerId,
            int ItemId,
            int UnitId,
            int StaffId,
            DateTime AppointmentDate,
            string StartTime,       // "HH:mm"
            string EndTime,         // "HH:mm"
            int NumberOfPersons,
            string ServiceType,     // "SALON" or "HOME"
            bool IsOnlineBooking,
            string? Notes,
            int? CustomerPackageSessionId // ←  (optional; when set, price is zeroed)
        );

        // ===== Update =====
        public record UpdateAppointmentRequest(
            int? CustomerId,
            int? ItemId,
            int? UnitId,
            int? StaffId,
            DateTime? AppointmentDate,
            string? StartTime,
            string? EndTime,
            int? NumberOfPersons,
            string? ServiceType,
            string? Notes,
            bool? ClearNotes
        );

        // ===== Update Status =====
        public record UpdateStatusRequest(
            string Status    // 'scheduled','completed','cancelled','no-show'
        );

        // ===== Apply Payment =====
        public record ApplyPaymentRequest(
            decimal Amount,
            int PaymentTypeId,
            string PaymentAs,       // 'DEPOSIT' or 'FULL'
            string? VoucherCode,
            bool IsWalletPayment
        );

        // ===== Checkout (confirm sale) =====
        public record CheckoutRequest(
            int? PaymentTypeId      // optional: last payment type used
        );

        // ===== Response DTOs =====
        public record AppointmentDto(
            int Id,
            int BranchId,
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            int ItemId,
            string ItemEnName,
            string ItemArName,
            int UnitId,
            int StaffId,
            string StaffEnName,
            string StaffArName,
            string AppointmentDate,
            string StartTime,
            string EndTime,
            int NumberOfPersons,
            string ServiceType,
            bool IsOnlineBooking,
            string? Notes,
            decimal UnitPrice,
            decimal DiscountPercent,
            decimal DiscountedUnitPrice,
            decimal TotalPrice,
            decimal PaidAmount,
            decimal RemainingAmount,
            string PaymentStatus,
            decimal DepositAmount,
            string? VoucherCode,
            string Status,
            string CheckoutStatus,
            int? InvoiceId,
            string? InvoiceNumber,
            DateTime CreatedAt,
            string? PaymentSource,   // ← NEW: 'ONLINE' | 'BRANCH' | null
            int? CustomerPackageSessionId,
            string? PackageName //(populated from JOIN when session linked)
        );

        public record AppointmentListResponse(
            int TotalCount,
            List<AppointmentDto> Appointments
        );

        public record AppointmentPaymentDto(
            int Id,
            decimal Amount,
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentAs,
            string? VoucherCode,
            DateTime PaidAt
        );

        public record AppointmentDetailDto(
            AppointmentDto Appointment,
            List<AppointmentPaymentDto> Payments,
            HomeServiceSnapshotDto? HomeService
        );
        public record HomeServiceSnapshotDto(
            int CustomerAddressId,
            int AreaId,
            string AreaNameEn,
            string AreaNameAr,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            int DriverId,
            string DriverName,
            string? DriverPhone,
            string? AddressBlock,
            string? AddressStreet,
            string? AddressAvenue,
            string? AddressBuilding,
            string? AddressFlat,
            string? AddressFloor,
            string? AddressNote,
            string? AddressLocation
        );
        public record InvoiceDto(
            int Id,
            string InvoiceNumber,
            int AppointmentId,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string Currency,
            int? PaymentTypeId,
            string PaymentStatus,
            DateTime CreatedAt
        );

        public record CheckoutResponse(
            int AppointmentId,
            InvoiceDto Invoice
        );

        // ===== Checkout Items =====
        public record AddCheckoutItemRequest(
            int ItemId,
            int UnitId,
            int StaffId
        );

        public record RemoveCheckoutItemRequest(
            int CheckoutItemId
        );

        public record CheckoutItemDto(
            int Id,
            int AppointmentId,
            int ItemId,
            string ItemEnName,
            string ItemArName,
            int UnitId,
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            bool CustomerHasAlert,
            string? CustomerAlertNote,
            int StaffId,
            string StaffEnName,
            string StaffArName,
            decimal UnitPrice,
            decimal DiscountPercent,
            decimal DiscountedUnitPrice,
            int NumberOfPersons,
            decimal TotalPrice
        );

        // Updated checkout detail with items
        public record CheckoutSummaryDto(
            AppointmentDto Appointment,
            List<CheckoutItemDto> ExtraItems,
            List<AppointmentPaymentDetailDto> Payments,
            decimal GrandTotal,
            decimal GrandPaid,
            decimal GrandRemaining
        );

        // Updated checkout request to handle multi-item
        public record MultiItemCheckoutRequest(
            int? PaymentTypeId
        );

        // Updated invoice to include line items
        public record InvoiceLineItemDto(
            // Refund-critical IDs — null for legacy (non-AppointmentInvoiceLines) rows
            int? Id,
            int? AppointmentId,
            int? ItemId,
            int? StaffId,
            bool IsRefunded,
            // Display fields
            string ItemName,
            string ItemNameAr,
            string CustomerName,
            string StaffName,
            string StaffNameAr,
            int Quantity,
            decimal UnitPrice,
            decimal TotalPrice,
            bool IsOriginal,
            // POS multi-package (OFFER) grouping — lines that share a PackageGroupId
            // render as one package block (fixed price). Null = standalone extra line.
            int? PackageOfferId = null,
            string? PackageOfferName = null,
            Guid? PackageGroupId = null
        );

        /// <summary>One refund transaction linked to this invoice</summary>
        public record RefundLineDto(
            int RefundTransactionId,
            string RefundType,        // CASH | LINK | WALLET
            decimal Amount,
            DateTime ProcessedAt,
            string? CancellationReason
        );

        /// <summary>A printable POS service label attached to this invoice (Phase 1).</summary>
        public record InvoiceLabelDto(
            int LabelId,
            int InvoiceId,
            int AppointmentId,
            int? InvoiceLineId,
            int LabelNumber,
            int ItemId,
            string ServiceName,
            string ServiceNameAr,
            decimal Price,
            string Currency,
            string Barcode,
            string QrPayload,
            DateTime CreatedAt,
            bool IsAssigned,
            int? AssignedStaffId,
            string? AssignedStaffName
        );

        public record DetailedInvoiceDto(
            int Id,
            string InvoiceNumber,
            int AppointmentId,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string Currency,
            int? PaymentTypeId,
            string PaymentStatus,
            DateTime CreatedAt,
            List<InvoiceLineItemDto> LineItems,
            List<AppointmentPaymentDetailDto> Payments,
            string? PackageOfferName,
            decimal? PackageOfferPrice,
            // Refund summary
            decimal TotalRefunded,
            bool IsFullyRefunded,
            bool IsPartiallyRefunded,
            List<RefundLineDto> RefundLines,
            // POS service labels (un-staffed services). Empty for normal invoices.
            List<InvoiceLabelDto>? Labels = null
        );

        /*split payment history*/
        public record AppointmentPaymentDetailDto(
            int Id,
            decimal Amount,
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentTypeNameAr,
            string PaymentAs,
            string? VoucherCode,
            DateTime PaidAt
        );

        // =====================================================================
        // ===== Edit Transaction (change staff per line + payment methods) =====
        // =====================================================================

        /// <summary>One editable service line in the edit-transaction dialog.</summary>
        public record EditInvoiceLineDto(
            string LineType,        // "SALE_LINE" | "APPOINTMENT" | "CHECKOUT_ITEM"
            int? LineId,            // AppointmentInvoiceLines.Id / AppointmentCheckoutItems.Id (null for legacy original)
            int AppointmentId,      // appointment that owns the line (drives calendar + staff performance)
            int ItemId,
            string ItemName,
            string ItemNameAr,
            int StaffId,
            string StaffName,
            string StaffNameAr,
            bool IsRefunded
        );

        /// <summary>One editable (FULL, non-wallet) payment in the dialog.</summary>
        public record EditInvoicePaymentDto(
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentTypeNameAr,
            decimal Amount
        );

        /// <summary>Everything the edit dialog needs to render.</summary>
        public record EditInvoiceInfoDto(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            string Currency,
            bool CanEdit,
            string? LockReason,
            decimal EditableTotal,     // sum of FULL non-wallet payments — must be preserved on save
            decimal WalletAmount,      // read-only: wallet portion can't be changed here
            List<EditInvoiceLineDto> Lines,
            List<EditInvoicePaymentDto> Payments
        );

        // ----- Save request -----
        public record EditInvoiceStaffChange(
            string LineType,        // "SALE_LINE" | "APPOINTMENT" | "CHECKOUT_ITEM"
            int? LineId,
            int AppointmentId,
            int NewStaffId
        );
        public record EditInvoicePaymentInput(
            int PaymentTypeId,
            decimal Amount,
            string? VoucherCode
        );
        public record EditInvoiceRequest(
            List<EditInvoiceStaffChange>? StaffChanges,
            List<EditInvoicePaymentInput>? Payments   // null => leave payments untouched
        );
    }
}