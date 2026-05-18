// Modules/System/Models/HomeServiceDtos.cs
using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class HomeServiceDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Customer Address =====
        public record CustomerAddressDto(
            int AddressId,
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
            bool IsDefault
        );

        public record CreateCustomerAddressRequest(
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

        // ===== Governorate / Area =====
        public record GovernorateDto(
            int GovernorateId,
            string NameEn,
            string NameAr,
            string? ColorCode
        );

        public record AreaDto(
            int AreaId,
            string NameEn,
            string NameAr,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr
        );

        // ===== Driver =====
        public record DriverDto(
            int DriverId,
            string DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            string? DriverAddress,
            int? BranchId,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            bool IsActive,
            bool IsPreferred
        );

        // ===== Home Service Snapshot =====
        public record HomeServiceDto(
            int Id,
            int AppointmentId,
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

        // ===== Save Home Service (create or update) =====
        public record SaveHomeServiceRequest(
            int CustomerAddressId,
            int DriverId
        );

        // ===== Home Context (one-shot load for the dialog) =====
        public record HomeServiceContextDto(
            List<CustomerAddressDto> Addresses,
            CustomerAddressDto? DefaultAddress,
            List<DriverDto> Drivers,
            DriverDto? PreferredDriver,
            List<GovernorateDto> Governorates,
            List<AreaDto> Areas
        );

        // ===== Internal lookup DTOs for SaveForAppointment =====
        public record AddressLookupDto(
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

        public record DriverLookupDto(
            int DriverId,
            string DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            int GovernorateId,
            bool IsActive
        );
        public record CreateDriverRequest(
            string DriverName,
            string? DriverNameAr,
            string DriverPhone,
            string? DriverAddress,
            int BranchId,
            int GovernorateId,
            bool IsActive
        );
        public record CreateDriverResponse(
            int DriverId,
            string DriverName,
            string? DriverNameAr,
            string? DriverPhone,
            string? DriverAddress,
            int BranchId,
            int GovernorateId,
            string GovernorateNameEn,
            string GovernorateNameAr,
            bool IsActive,
            bool IsPreferred
        );
    }
}