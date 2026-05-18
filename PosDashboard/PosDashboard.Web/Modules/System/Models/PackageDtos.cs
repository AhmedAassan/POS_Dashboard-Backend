using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class PackageDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Master =====
        public record PackageItemDefDto(
            int Id,
            int ItemUnitId,
            int ItemId,
            string ItemEnName,
            string ItemArName,
            int UnitId,
            string UnitEnName,
            string UnitArName,
            decimal ItemPrice,
            bool Active
        );

        public record PackageMasterDto(
            int Id,
            int BranchId,
            string EnglishName,
            string ArabicName,
            decimal Amount,
            int NoOfDays,
            bool Active,
            int TotalSessions,
            decimal TotalRealValue,
            decimal Savings,
            DateTime AddedDate,
            List<PackageItemDefDto> Items
        );

        public record PackageItemDefInput(
            int ItemUnitId
        );

        public record CreatePackageRequest(
            string EnglishName,
            string ArabicName,
            decimal Amount,
            int NoOfDays,
            bool Active,
            List<PackageItemDefInput> Items
        );

        public record UpdatePackageRequest(
            string? EnglishName,
            string? ArabicName,
            decimal? Amount,
            int? NoOfDays,
            bool? Active,
            List<PackageItemDefInput>? Items
        );

        // ===== Assignment =====
        public record AssignPackageRequest(
            int PackageId,
            int CustomerId,
            int PaymentTypeId,
            decimal? CustomAmount,
            string? Notes
        );

        public record CustomerPackageSessionDto(
            int Id,
            int CustomerPackageId,
            int PackageItemId,
            int ItemUnitId,
            int ItemId,
            string ItemEnName,
            string ItemArName,
            int UnitId,
            int? StaffId,
            string? StaffEnName,
            string? StaffArName,
            decimal ItemPrice,
            decimal ItemPriceInPackage,
            bool Served,
            DateTime? ServedDate,
            int? AppointmentId,
            string? Notes
        );

        public record CustomerPackagePaymentDto(
            int Id,
            int CustomerPackageId,
            int PaymentTypeId,
            string PaymentTypeName,
            decimal PaymentAmount,
            bool IsDeposit,
            string? Notes,
            DateTime AddedDate
        );

        public record CustomerPackageDto(
            int Id,
            int PackageId,
            string PackageEnName,
            string PackageArName,
            int CustomerId,
            string CustomerName,
            string CustomerPhone,
            decimal Amount,
            decimal PaidAmount,
            decimal RemainingAmount,
            bool IsPaid,
            int BranchId,
            DateTime AddedDate,
            DateTime ExpiryDate,
            bool IsExpired,
            int TotalSessions,
            int ServedSessions,
            int RemainingSessions,
            bool IsCompleted,
            string Status,
            bool IsOnline
        );

        public record CustomerPackageDetailDto(
            CustomerPackageDto Assignment,
            List<CustomerPackageSessionDto> Sessions,
            List<CustomerPackagePaymentDto> Payments
        );

        public record ServeSessionRequest(
            int CustomerPackageSessionId,
            int StaffId,
            string? Notes
        );

        public record EligiblePackageSessionDto(
            int CustomerPackageSessionId,
            int CustomerPackageId,
            int PackageId,
            string PackageEnName,
            string PackageArName,
            DateTime ExpiryDate,
            int RemainingInPackage,
            int ItemUnitId,      // ← NEW
            int ItemId,          // ← NEW
            int UnitId,          // ← NEW
            string ItemEnName,   // ← NEW
            string ItemArName    // ← NEW
        );

        public record PagedResult<T>(
            bool Success,
            string? Error,
            T? Data,
            int TotalCount,
            int Page,
            int PageSize
        );
    }
}