// Modules/System/Models/PosDtos.cs
//
// Self-contained DTOs for the standalone POS screen (api/pos).
//
// The POS flow is a "sell now / pay now" counter sale. It is mechanically
// similar to New Sale, but it is ALWAYS hidden from the calendar
// (ShowOnCalendar = 0) and is organised around CATEGORY (not
// AppointmentCategory). It still produces a real invoice, records all
// payments (wallet + split), and (best-effort) sends a WhatsApp receipt.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class PosDtos
    {
        // Generic API result — same shape as the rest of the project.
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // =====================================================================
        // 1) Catalog (one bootstrap call for the whole POS screen)
        //    GET /api/pos/catalog?branchId=
        // =====================================================================

        public record PosBranchDto(
            int BranchId,
            int CompanyId,
            string BranchName1,
            string BranchName2,
            string? BranchPhone,
            string CurrencyEn,
            string CurrencyAr,
            int RoundOfDigits,
            decimal? TaxValue
        );

        public record PosCategoryDto(
            int CategoryId,
            string CategoryNameEn,
            string CategoryNameAr,
            int Ordering,
            string? DocumentName
        );

        public record PosServiceDto(
            int ItemId,
            string ItemNameEn,
            string ItemNameAr,
            int CategoryId,
            string CategoryNameEn,
            string CategoryNameAr,
            int AppointmentCategoryId,
            int ItemUnitId,
            int UnitId,
            string UnitNameEn,
            string UnitNameAr,
            decimal Price,
            decimal MinimumPrice,
            double DurationMinutes,
            string? ImageUrl,
            string? DocumentName,
            bool IsActive
        );

        public record PosStaffDto(
            int StaffId,
            string NameEn,
            string NameAr,
            string? Mobile,
            bool Active,
            string? EmployeeCode
        );

        public record PosPaymentTypeDto(
            int PaymentTypeId,
            string NameEn,
            string NameAr,
            bool IsWallet,
            bool OnlinePayment
        );

        // One service inside an OFFER package (keyed by ItemUnitId, the same
        // identifier the offer checkout lines use).
        public record PosOfferItemDto(
            int ItemUnitId,
            int ItemId,
            string ItemNameEn,
            string ItemNameAr,
            int UnitId,
            string UnitNameEn,
            string UnitNameAr,
            decimal ItemPrice,          // catalog price (before the package discount)
            double DurationMinutes
        );

        // An OFFER package the cashier can add to the cart.
        public record PosOfferDto(
            int PackageOfferId,
            string NameEn,
            string NameAr,
            decimal Amount,             // the fixed package price
            int NoOfDays,
            decimal TotalRealValue,     // sum of item catalog prices
            decimal Savings,            // TotalRealValue - Amount (>= 0)
            List<PosOfferItemDto> Items
        );

        public record PosCatalogDto(
            PosBranchDto Branch,
            List<PosCategoryDto> Categories,
            List<PosServiceDto> Services,
            List<PosStaffDto> Staff,
            List<PosPaymentTypeDto> PaymentTypes,
            List<PosOfferDto> Offers
        );

        // =====================================================================
        // 2) Checkout (create the POS sale)
        //    POST /api/pos/checkout
        // =====================================================================

        public record PosCheckoutLineRequest(
            int ItemId,
            int UnitId,
            int? StaffId,                  // null / 0 = NO staff -> generates a printable label (Phase 1)
            int? DurationMinutes,          // optional override
            decimal? UnitPriceOverride,    // optional sale-only price (discount). Never written to master.
            string? Notes
        );

        public record PosSplitPaymentRequest(
            int PaymentTypeId,
            decimal Amount,
            string? VoucherCode
        );

        public record PosPaymentsRequest(
            int? WalletSubscriptionId,
            decimal? WalletAmount,
            int? WalletPaymentTypeId,
            List<PosSplitPaymentRequest>? Splits
        );

        // One chosen service inside a selected package instance.
        // ItemUnitId must belong to PackageOfferId; StaffId is per-service.
        public record PosCheckoutPackageLineRequest(
            int ItemUnitId,
            int? StaffId,                  // null / 0 = NO staff -> generates a printable label (Phase 1)
            int? DurationMinutes,
            string? Notes
        );

        // One package instance added to the cart. The same package can appear
        // multiple times (each becomes its own group).
        public record PosCheckoutPackageRequest(
            int PackageOfferId,
            List<PosCheckoutPackageLineRequest> Lines
        );

        // Ticket-level discount. Applies to STANDALONE SERVICES only — never to
        // OFFER packages. Type is "percentage" (0..100) or "fixed" (a currency
        // amount, capped at the services subtotal). Value <= 0 or a missing
        // Discount object means "no discount".
        public record PosDiscountRequest(
            string Type,        // "percentage" | "fixed"
            decimal Value       // percent (0..100) OR fixed money amount
        );

        public record PosCheckoutRequest(
            int BranchId,
            int CustomerId,
            string? Notes,
            List<PosCheckoutLineRequest> Lines,          // standalone extra services
            PosPaymentsRequest? Payments,
            bool SendWhatsApp,
            List<PosCheckoutPackageRequest>? Packages = null,  // selected OFFER packages
            PosDiscountRequest? Discount = null,               // ticket discount on services
                                                               // Customer discount code (CARD-######). When present it OVERRIDES the manual
                                                               // Discount above and is applied to the SERVICES subtotal only (never OFFER
                                                               // packages). The code is validated + redeemed atomically inside checkout.
            string? DiscountCode = null
        );

        // =====================================================================
        // Printable service label (Phase 1)
        // One label per un-staffed service line: QR + 8-digit barcode + the
        // service name/price/date/time + its order (LabelNumber) in the invoice.
        // =====================================================================
        public record PosLabelDto(
            int LabelId,
            int InvoiceId,
            int AppointmentId,
            int? InvoiceLineId,
            int LabelNumber,            // 1-based order of the service within the invoice
            int ItemId,
            string ServiceName,
            string ServiceNameAr,
            decimal Price,
            string Currency,
            string Barcode,             // 8-digit numeric (also what the QR encodes)
            string QrPayload,
            DateTime CreatedAt,         // sale date/time printed on the label
            bool IsAssigned,
            int? AssignedStaffId,
            string? AssignedStaffName
        );

        // ---- Phase 2: assign a staff member to one or more service labels ----
        // Each item pairs a label with the staff it should go to (per-label staff).
        // StaffCode is an alternative to StaffId for the anonymous phone-camera flow:
        // when StaffId is 0/omitted, the server resolves the code within the label's branch.
        public record PosAssignLabelItem(
            int LabelId,
            int StaffId,
            string? StaffCode = null
        );

        public record PosAssignLabelsRequest(
            List<PosAssignLabelItem> Assignments
        );

        public record PosAssignLabelsResponse(
            int AssignedCount,
            int SkippedCount,            // labels that were already assigned / not found
            List<PosLabelDto> Labels     // the resulting (now assigned) labels
        );

        public record PosCheckoutResponse(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            Guid SaleGroupId,
            List<int> AppointmentIds,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            decimal WalletDeductedAmount,
            string PaymentStatus,          // always 'FULL' for POS
            string Currency,
            bool WhatsAppSent,
            string? WhatsAppError,
            string? InvoicePdfUrl,
            List<PosLabelDto> Labels,      // un-staffed service labels (empty when every line had a staff)
            decimal SubTotal = 0m,         // services + offers BEFORE the ticket discount
            decimal DiscountAmount = 0m,   // money deducted by the ticket discount (0 = none)
            string? DiscountCode = null,   // the redeemed CARD-###### (null = none)
            int? DiscountCodeId = null     // its id (null = none)
        );

        // =====================================================================
        // 3) Receipt (everything the receipt/invoice dialog needs)
        //    GET /api/pos/receipt/{invoiceId}
        // =====================================================================

        public record PosReceiptLineDto(
            int? Id,
            int AppointmentId,
            int ItemId,
            int? StaffId,               // null = no staff yet (un-staffed POS line)
            string ItemName,
            string ItemNameAr,
            string StaffName,           // "" when un-staffed
            string StaffNameAr,
            int Quantity,
            decimal UnitPrice,          // charged unit price AFTER the ticket discount (revenue)
            decimal TotalPrice,         // charged line total AFTER the ticket discount (revenue)
            int? PackageOfferId,
            string? PackageOfferName,
            Guid? PackageGroupId,
            decimal OriginalUnitPrice = 0m  // listed unit price BEFORE the ticket discount (display)
        );

        public record PosReceiptPaymentDto(
            int PaymentTypeId,
            string PaymentTypeName,
            string PaymentTypeNameAr,
            decimal Amount,
            bool IsWallet
        );

        public record PosCompanyInfoDto(
            string CompanyName1,
            string CompanyName2,
            string? CompanyLogo,
            string? CompanyPhone,
            string? CompanyAddress1,
            string? Footer,
            string? Footer1,
            string? Footer2
        );

        public record PosReceiptDto(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            DateTime CreatedAt,
            string CustomerName,
            string CustomerPhone,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string Currency,
            string CurrencyAr,
            string PaymentStatus,
            List<PosReceiptLineDto> Lines,
            List<PosReceiptPaymentDto> Payments,
            PosCompanyInfoDto? Company,
            string? InvoicePdfUrl,
            List<PosLabelDto> Labels,      // labels for this invoice (empty when none)
            decimal SubTotal = 0m,         // services + offers BEFORE the ticket discount
            string? DiscountType = null,   // "percentage" | "fixed" | null (no discount)
            decimal? DiscountValue = null, // the raw entered value (10 => 10% / 5.000 => fixed)
            decimal DiscountAmount = 0m,   // money deducted by the ticket discount (0 = none)
            int TzOffset = 0               // branch timezone offset (hours); CreatedAt is UTC
        );
    }
}
