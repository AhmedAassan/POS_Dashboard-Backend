// Modules/System/Models/DiscountCodeDtos.cs
//
// Self-contained DTOs for the Customer Discount Codes feature (api/discount-codes).
//
// Flow
// ----
//   1) Templates   – reusable discount definitions (Name, Type, Amount, MaxUses,
//                    ExpiresAfterDays). CRUD from the "Discount Templates" screen.
//   2) Assign      – POST /assign generates a unique CARD-###### code for a
//                    customer and (best-effort) sends it over WhatsApp using the
//                    same HeaderText/FooterText config the receipts use.
//   3) Redeem      – POS looks a code up (GET /lookup), applies its value to the
//                    SERVICES subtotal only (never OFFER packages), and records a
//                    redemption row when the sale is committed.
//
// Field names are PascalCase to mirror the C# <-> TS contract used everywhere
// else in the project (nothing is remapped on the wire).

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class DiscountCodeDtos
    {
        // Generic API result — same shape as the rest of the project.
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // =====================================================================
        // 1) Templates
        // =====================================================================

        public record DiscountTemplateDto(
            int Id,
            string Name,
            string DiscountType,          // 'value' | 'percentage'
            decimal DiscountAmount,
            int MaxUses,
            int? ExpiresAfterDays,
            int? BranchId,
            bool IsActive,
            DateTime CreatedAt,
            int GeneratedCount,           // how many codes were generated from it
            int UsedCount                 // total redemptions across its codes
        );

        public record CreateDiscountTemplateRequest(
            string? Name,
            string? DiscountType,
            decimal DiscountAmount,
            int? MaxUses,
            int? ExpiresAfterDays,
            int? BranchId,
            bool? IsActive
        );

        public record UpdateDiscountTemplateRequest(
            string? Name,
            string? DiscountType,
            decimal? DiscountAmount,
            int? MaxUses,
            int? ExpiresAfterDays,
            bool? IsActive
        );

        // =====================================================================
        // 2) Assign (generate + WhatsApp)
        // =====================================================================

        public record AssignDiscountCodeRequest(
            int TemplateId,
            int CustomerId,
            bool SendWhatsApp = true
        );

        public record AssignDiscountCodeResponse(
            int DiscountCodeId,
            string Code,                  // CARD-######
            int TemplateId,
            string TemplateName,
            string DiscountType,
            decimal DiscountAmount,
            int MaxUses,
            DateTime? ExpiresAt,
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            bool WhatsAppSent,
            string? WhatsAppError
        );

        // =====================================================================
        // 3) Lookup (POS) — validate a typed code before checkout
        // =====================================================================

        public record DiscountCodeLookupDto(
            int DiscountCodeId,
            string Code,
            string DiscountType,          // 'value' | 'percentage'
            decimal DiscountAmount,
            int MaxUses,
            int UsedCount,
            int RemainingUses,
            DateTime? ExpiresAt,
            bool IsExpired,
            bool IsUsable,
            int AssignedCustomerId,
            string AssignedCustomerName,
            string? Reason                // why not usable (when IsUsable = false)
        );

        // A customer's currently-usable code (drives the POS "this client has a
        // discount code" alert). Null Data = no active code.
        public record CustomerActiveCodeDto(
            int DiscountCodeId,
            string Code,
            string DiscountType,
            decimal DiscountAmount,
            int RemainingUses,
            DateTime? ExpiresAt
        );

        // =====================================================================
        // 4) Discount Codes screen (list + filter)
        // =====================================================================

        public record DiscountCodeListItemDto(
            int DiscountCodeId,
            string Code,
            int TemplateId,
            string TemplateName,
            string DiscountType,
            decimal DiscountAmount,
            int MaxUses,
            int UsedCount,
            int RemainingUses,
            string Status,                // 'not_used' | 'used' | 'partially_used' | 'expired'
            DateTime? ExpiresAt,
            bool IsExpired,

            // Who assigned + when
            int AssignedCustomerId,
            string AssignedCustomerName,
            string AssignedCustomerPhone,
            int? AssignedByUserId,
            string? AssignedByUserName,
            DateTime AssignedAt,

            // Who took it + when (latest redemption; a shared code may be spent by
            // a friend, so this can differ from the assigned customer)
            int? RedeemedByCustomerId,
            string? RedeemedByCustomerName,
            DateTime? RedeemedAt,

            // Invoice snapshot of the latest redemption
            int? InvoiceId,
            int? InvoiceAppointmentId,    // lead appointment id → drives the invoice-pdf link
            string? InvoiceNumber,
            decimal? InvoiceValue,
            decimal? RedeemedDiscountAmount
        );

        public record DiscountCodeCountsDto(
            int Total,
            int NotUsed,
            int Used,
            int PartiallyUsed,
            int Expired
        );

        public record DiscountCodeListResponse(
            List<DiscountCodeListItemDto> Items,
            DiscountCodeCountsDto Counts
        );

        // Full redemption history for a single code (invoice drill-down).
        public record DiscountCodeRedemptionDto(
            int Id,
            int? InvoiceId,
            int? InvoiceAppointmentId,
            string? InvoiceNumber,
            decimal? InvoiceValue,
            decimal DiscountAmount,
            int? RedeemedByCustomerId,
            string? RedeemedByCustomerName,
            int? RedeemedByUserId,
            string? RedeemedByUserName,
            DateTime RedeemedAt
        );
    }
}
