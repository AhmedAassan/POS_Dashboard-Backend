// PosDashboard.Web.Modules.System.Models/AdminCrudDtos.cs
using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AdminCrudDtos
    {
        // ════════════════════════════════════════════════════════════
        // SHARED
        // ════════════════════════════════════════════════════════════

        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record PagedResult<T>(
            List<T> Items,
            int TotalCount,
            int Page,
            int PageSize,
            int TotalPages
        );

        // ════════════════════════════════════════════════════════════
        // BRANCH
        // ════════════════════════════════════════════════════════════

        public record BranchListDto(
            int BranchId,
            int CompanyId,
            string BranchName1,
            string BranchName2,
            int? BranchIsActive,
            string? BranchAddress,
            string? BranchPhone,
            string? ColorCode,
            decimal? TaxValue,
            string ArabicCurrencyName,
            string EnglishCurrencyName,
            int RoundOfDigits,
            string? Email,
            string? WhatsappMobile,
            DateTime CreatedOn
        );

        public class BranchCreateRequest
        {
            public int CompanyId { get; set; }
            public string BranchName1 { get; set; } = "";
            public string BranchName2 { get; set; } = "";
            public int? BranchIsActive { get; set; } = 1;
            public string? BranchAddress { get; set; }
            public string? BranchPhone { get; set; }
            public string? ColorCode { get; set; }
            public decimal? TaxValue { get; set; }
            public string ArabicCurrencyName { get; set; } = "د.ك";
            public string EnglishCurrencyName { get; set; } = "KWD";
            public int RoundOfDigits { get; set; } = 3;
            public string? Email { get; set; }
            public string? WhatsappMobile { get; set; }
            public string? EnjazatikToken { get; set; }
            public string? InvoiceCodePrefix { get; set; } = "B";
        }

        public class BranchUpdateRequest : BranchCreateRequest
        {
            public int BranchId { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // APPOINTMENT CATEGORIES
        // ════════════════════════════════════════════════════════════

        public record AppointmentCategoryListDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? Notes,
            bool IsMakeup,
            bool IsPackage,
            decimal Deposit,
            string? DocumentName,
            bool Deleted,
            DateTime AddedDate,
            DateTime? ModifiedDate
        );

        public class AppointmentCategoryCreateRequest
        {
            public string ArabicName { get; set; } = "";
            public string EnglishName { get; set; } = "";
            public string? Notes { get; set; }
            public bool IsMakeup { get; set; } = false;
            public bool IsPackage { get; set; } = false;
            public decimal Deposit { get; set; } = 0;
            public string? DocumentName { get; set; }
        }

        public class AppointmentCategoryUpdateRequest : AppointmentCategoryCreateRequest
        {
            public int Id { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // CATEGORY
        // ════════════════════════════════════════════════════════════

        public record CategoryListDto(
            int CategoryId,
            string CategoryName1,
            string CategoryName2,
            int? ParentCategory,
            string? ParentName1,
            int CategoryOrdering,
            int CategoryIsActive,
            string? CategoryColor,
            int? CategoryLevel,
            string? DocumentName
        );

        public class CategoryCreateRequest
        {
            public string CategoryName1 { get; set; } = "";
            public string CategoryName2 { get; set; } = "";
            public int? ParentCategory { get; set; }
            public int CategoryOrdering { get; set; } = 0;
            public int CategoryIsActive { get; set; } = 1;
            public string? CategoryColor { get; set; }
            public string? DocumentName { get; set; }
        }

        public class CategoryUpdateRequest : CategoryCreateRequest
        {
            public int CategoryId { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // UNIT
        // ════════════════════════════════════════════════════════════

        public record UnitDto(
            int UnitId,
            string UnitName1,
            string UnitName2,
            int Order
        );

        // ════════════════════════════════════════════════════════════
        // ITEM
        // ════════════════════════════════════════════════════════════

        public record ItemListDto(
            int ItemId,
            string ItemName1,
            string ItemName2,
            int ItemCategoryId,
            string? CategoryName1,
            int? AppointmentCategoryId,
            string? AppointmentCategoryName,
            int ItemType,
            string? ItemCode,
            int? ItemIsActive,
            string? DocumentName,
            bool ECommerce,
            string? Description,
            decimal? CostPrice,
            decimal Balance,
            DateTime AddedDate,
            // ItemUnits summary
            List<ItemUnitSummaryDto> Units
        );

        public record ItemUnitSummaryDto(
            int ItemUnitId,
            int UnitId,
            string UnitName1,
            string UnitName2,
            string? Barcode,
            decimal ItemUnitPrice,
            decimal ItemUnitFactor,
            double? ItemUnitDuration,
            decimal MinimumPrice,
            decimal Deposit,
            bool Active,
            int? BranchId,
            string? BranchName
        );

        public class ItemCreateRequest
        {
            public string ItemName1 { get; set; } = "";
            public string ItemName2 { get; set; } = "";
            public int ItemCategoryId { get; set; }
            public int? AppointmentCategoryId { get; set; }
            public int ItemType { get; set; } = 1;
            public string? ItemCode { get; set; }
            public int ItemIsActive { get; set; } = 1;
            public string? DocumentName { get; set; }
            public bool ECommerce { get; set; } = true;
            public string? Description { get; set; }
            public decimal? CostPrice { get; set; }
            public List<ItemUnitCreateRequest> Units { get; set; } = new();
        }

        public class ItemUpdateRequest : ItemCreateRequest
        {
            public int ItemId { get; set; }
        }

        public class ItemUnitCreateRequest
        {
            public int UnitId { get; set; }
            public string? Barcode { get; set; }
            public decimal ItemUnitFactor { get; set; } = 1;
            public decimal ItemUnitPrice { get; set; }
            public float ItemUnitPoint { get; set; } = 0;
            public double? ItemUnitDuration { get; set; }
            public decimal MinimumPrice { get; set; } = 0;
            public decimal Deposit { get; set; } = 0;
            public bool Active { get; set; } = true;
            public int? BranchId { get; set; }
        }

        public class ItemUnitUpdateRequest : ItemUnitCreateRequest
        {
            public int ItemUnitId { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // STAFF
        // ════════════════════════════════════════════════════════════

        public record StaffListDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? Mobile,
            decimal Salary,
            decimal? Commission,
            int BranchId,
            string? BranchName,
            bool Active,
            bool IsAppointment,
            bool IsMakeupArtist,
            string? DocumentName,
            string? EmployeeCode,
            DateTime AddedDate
        );

        public record StaffDetailDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? Mobile,
            decimal Salary,
            decimal? Commission,
            int BranchId,
            string? BranchName,
            bool Active,
            bool IsAppointment,
            bool IsMakeupArtist,
            string? DocumentName,
            string? EmployeeCode,
            DateTime? ServiceEndDate,
            decimal FixedAmount,
            // Only when IsMakeupArtist = true
            List<StaffItemDto> StaffItems
        );

        public record StaffItemDto(
            int Id,
            int StaffId,
            int ItemUnitId,
            string? ItemName1,
            string? ItemName2,
            string? UnitName1,
            string? UnitName2,
            decimal Price,
            string? Notes,
            bool Deleted
        );

        public class StaffCreateRequest
        {
            public string ArabicName { get; set; } = "";
            public string EnglishName { get; set; } = "";
            public string? Mobile { get; set; }
            public decimal Salary { get; set; } = 0;
            public decimal? Commission { get; set; }
            public int BranchId { get; set; }
            public bool Active { get; set; } = true;
            public bool IsAppointment { get; set; } = false;
            public bool IsMakeupArtist { get; set; } = false;
            public string? DocumentName { get; set; }
            public string? EmployeeCode { get; set; }
            public DateTime? ServiceEndDate { get; set; }
            public decimal FixedAmount { get; set; } = 0;
        }

        public class StaffUpdateRequest : StaffCreateRequest
        {
            public int Id { get; set; }
        }

        public class StaffItemCreateRequest
        {
            public int StaffId { get; set; }
            public int ItemUnitId { get; set; }
            public decimal Price { get; set; }
            public string? Notes { get; set; }
        }

        public class StaffItemUpdateRequest : StaffItemCreateRequest
        {
            public int Id { get; set; }
        }

        // ════════════════════════════════════════════════════════════
        // CUSTOMER
        // ════════════════════════════════════════════════════════════

        public record CustomerListDto(
            int CustomerId,
            string CustomerName,
            string CustomerPhone1,
            string? CustomerPhone2,
            int BranchId,
            string? BranchName,
            int? CustomerIsBlock,
            string? CustomerBlockReason,
            string? CustomerNote,
            DateTime? BirthDate,
            decimal LoyaltyBalance,
            decimal MembershipBalance,
            decimal UnpaidSales,
            bool HasRefundHistory,
            string NotificationLang,
            DateTime CustomerCreatedDate
        );

        public class CustomerCreateRequest
        {
            public string CustomerName { get; set; } = "";
            public string CustomerPhone1 { get; set; } = "";
            public string? CustomerPhone2 { get; set; }
            public DateTime? BirthDate { get; set; }
            public int? CustomerIsBlock { get; set; } = 0;
            public string? CustomerBlockReason { get; set; }
            public string? CustomerNote { get; set; }
            public int BranchId { get; set; }
            public string NotificationLang { get; set; } = "ar";
        }

        public class CustomerUpdateRequest
        {
            public int CustomerId { get; set; }
            public string CustomerName { get; set; } = "";
            public string CustomerPhone1 { get; set; } = "";
            public string? CustomerPhone2 { get; set; }
            public DateTime? BirthDate { get; set; }
            public int? CustomerIsBlock { get; set; } = 0;
            public string? CustomerBlockReason { get; set; }
            public string? CustomerNote { get; set; }
            public string NotificationLang { get; set; } = "ar";
        }
    }
}