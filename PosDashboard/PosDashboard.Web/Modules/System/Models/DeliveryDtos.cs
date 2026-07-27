// Modules/System/Models/DeliveryDtos.cs
//
// Contracts for the Delivery flow:
//   • BusinessSetting     — generic system flags (/settings page)
//   • DeliveryType        — Pickup / Delivery (/settings page)
//   • AreaDeliveryCharge  — per-area price list (/management page)
//   • CustomerAddress     — full CRUD (POS delivery step + /management page)
//   • DeliveryContext     — one-shot bootstrap for the POS delivery dialog
//
// The POS never trusts a client-supplied charge: it is always recomputed from
// AreaDeliveryCharge (or DeliveryType.ChargeOverride) on the server.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class DeliveryDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // =====================================================================
        // Business settings
        // =====================================================================

        public record BusinessSettingDto(
            int Id,
            string SettingKey,
            string? SettingValue,
            string ValueType,          // bool | int | decimal | string | json
            string Category,
            string DisplayNameEn,
            string DisplayNameAr,
            string? DescriptionEn,
            string? DescriptionAr,
            int? BranchId,
            bool IsEditable,
            int Ordering,
            DateTime? UpdatedAt
        );

        public record BusinessSettingGroupDto(
            string Category,
            List<BusinessSettingDto> Settings
        );

        public record UpdateBusinessSettingRequest(
            string SettingKey,
            string? SettingValue,
            int? BranchId = null
        );

        public record UpdateBusinessSettingsRequest(
            List<UpdateBusinessSettingRequest> Settings
        );

        // Compact, cache-friendly view the POS bootstraps from.
        public record DeliverySettingsDto(
            bool Enabled,
            bool DateEnabled,
            bool DateDefaultOn,
            int DefaultLeadDays
        );

        // =====================================================================
        // Delivery type
        // =====================================================================

        public record DeliveryTypeDto(
            int Id,
            string Code,
            string NameEn,
            string NameAr,
            bool IsDelivery,
            bool IsDefault,
            bool IsActive,
            int Ordering,
            string? ColorCode,
            string? Icon,
            decimal? ChargeOverride,
            string? Notes,
            int? BranchId
        );

        public record SaveDeliveryTypeRequest(
            string Code,
            string NameEn,
            string NameAr,
            bool IsDelivery,
            bool IsDefault,
            bool IsActive,
            int Ordering,
            string? ColorCode,
            string? Icon,
            decimal? ChargeOverride,
            string? Notes,
            int? BranchId
        );

        // =====================================================================
        // Area delivery charge
        // =====================================================================

        public record AreaDeliveryChargeDto(
            int Id,
            int AreaId,
            string AreaNameEn,
            string AreaNameAr,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            decimal Charge,
            int BranchId,
            string? BranchName
        );

        public record SaveAreaDeliveryChargeRequest(
            int AreaId,
            decimal Charge,
            int BranchId
        );

        /// <summary>Bulk price-list editing from the /management grid.</summary>
        public record BulkAreaDeliveryChargeRequest(
            int BranchId,
            List<SaveAreaDeliveryChargeRequest> Charges
        );

        // =====================================================================
        // Customer address (full CRUD — the POS delivery step and /management
        // both drive this)
        // =====================================================================

        public record DeliveryAddressDto(
            int AddressId,
            int CustomerId,
            Guid CustomerRef,
            int? AreaId,
            string? AreaNameEn,
            string? AreaNameAr,
            int? GovernorateId,
            string? GovernorateNameEn,
            string? GovernorateNameAr,
            string? BlockNo,
            string? Street,
            string? Avenue,
            string? BuildingNo,
            string? FlatNo,
            string? Floor,
            string? Note,
            string? Location,
            bool IsDefault,
            /// <summary>Delivery price for this address in the requested branch (0 when the area is unpriced).</summary>
            decimal DeliveryCharge,
            /// <summary>False when the area has no AreaDeliveryCharge row — the UI warns instead of silently charging 0.</summary>
            bool HasCharge,
            /// <summary>True when the address is referenced by an invoice/appointment, so it can only be soft-deleted.</summary>
            bool InUse = false
        );

        public record SaveDeliveryAddressRequest(
            int CustomerId,
            int AreaId,
            string? BlockNo,
            string? Street,
            string? Avenue,
            string? BuildingNo,
            string? FlatNo,
            string? Floor,
            string? Note,
            string? Location,
            bool MakeDefault
        );

        // =====================================================================
        // Driver (delivery only) — a driver serves ONE governorate, exactly like
        // Home Service. The POS filters drivers to the chosen address's governorate.
        // =====================================================================

        public record DeliveryDriverDto(
            int DriverId,
            string DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            string? DriverAddress,
            int? BranchId,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            bool IsActive
        );

        public record SaveDeliveryDriverRequest(
            string DriverName,
            string? DriverNameAr,
            string DriverPhone,
            string? DriverAddress,
            int BranchId,
            int GovernorateId,
            bool IsActive
        );

        // =====================================================================
        // POS bootstrap for the delivery dialog
        // =====================================================================

        public record DeliveryContextDto(
            DeliverySettingsDto Settings,
            List<DeliveryTypeDto> DeliveryTypes,
            DeliveryTypeDto? DefaultDeliveryType,   // IsDelivery=1 default
            DeliveryTypeDto? DefaultPickupType,     // IsDelivery=0 default
            List<DeliveryAddressDto> Addresses,
            DeliveryAddressDto? DefaultAddress,
            List<GovernorateOptionDto> Governorates,
            List<AreaOptionDto> Areas,
            List<DeliveryDriverDto> Drivers,
            /// <summary>Suggested delivery date = branch-local today + Settings.DefaultLeadDays.</summary>
            DateTime SuggestedDeliveryDate
        );

        public record GovernorateOptionDto(
            int GovernorateId,
            string NameEn,
            string NameAr,
            string? ColorCode
        );

        public record AreaOptionDto(
            int AreaId,
            string NameEn,
            string NameAr,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            decimal Charge,
            bool HasCharge
        );

        public record AreaChargeLookupDto(
            int AreaId,
            int BranchId,
            decimal Charge,
            bool HasCharge
        );

        // =====================================================================
        // Invoice delivery snapshot (written by POS checkout, read by receipt)
        // =====================================================================

        public record InvoiceDeliveryDto(
            int DeliveryTypeId,
            string DeliveryTypeCode,
            string DeliveryTypeNameEn,
            string DeliveryTypeNameAr,
            bool IsDelivery,
            int? DriverId,
            string? DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            int? CustomerAddressId,
            int? AreaId,
            string? AreaNameEn,
            string? AreaNameAr,
            int? GovernorateId,
            string? GovernorateNameEn,
            string? GovernorateNameAr,
            string? AddressBlock,
            string? AddressStreet,
            string? AddressAvenue,
            string? AddressBuilding,
            string? AddressFlat,
            string? AddressFloor,
            string? AddressNote,
            string? AddressLocation,
            decimal DeliveryCharge,
            bool HasDeliveryDate,
            DateTime? DeliveryDate,
            string? Notes
        );

        // Internal lookup used while resolving a checkout.
        public record AddressResolveDto(
            int CustomerAddressId,
            Guid CustomerRef,
            string? BlockNo,
            string? Street,
            string? Avenue,
            string? BuildingNo,
            string? FlatNo,
            string? Floor,
            string? Note,
            string? Location,
            int AreaId,
            string AreaNameEn,
            string AreaNameAr,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr
        );
    }
}
