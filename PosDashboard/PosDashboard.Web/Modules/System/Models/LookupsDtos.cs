using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class LookupsDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Branch (common info returned with every lookup) =====
        public record BranchInfoDto(
            int BranchId,
            int CompanyId,
            string BranchName1,
            string BranchName2,
            string? BranchAddress,
            string? BranchPhone,
            string ArabicCurrencyName,
            string EnglishCurrencyName,
            int RoundOfDigits,
            decimal? TaxValue
        );
        public record CompanyInfoDto(
            int CompanyId,
            string CompanyName1,
            string CompanyName2,
            string? CompanyLogo,
            string? CompanyPhone,
            string? CompanyAddress1,
            string? CompanyAddress2,
            string? Footer,
            string? Footer1,
            string? Footer2,
            string? Footer3,
            string? Footer4,
            string? Footer5
        );
        public record UpdateCompanyInfoRequest(
            string CompanyName1,
            string CompanyName2,
            string? CompanyLogo,
            string? CompanyPhone,
            string? CompanyAddress1,
            string? CompanyAddress2,
            string? Footer,
            string? Footer1,
            string? Footer2,
            string? Footer3,
            string? Footer4,
            string? Footer5
        );
        // ===== Services (Items + Units + AppointmentCategory + Category) scoped to Branch =====
        public record ServiceLookupDto(
            // Branch
            int? BranchId,
            string? BranchName1,
            string? BranchName2,
            string ArabicCurrencyName,
            string EnglishCurrencyName,
            int RoundOfDigits,
            string? BranchPhone,

            // Appointment Category
            int AppointmentCategoryId,
            string AppointmentCategoryNameEn,
            string AppointmentCategoryNameAr,

            // Category
            int CategoryId,
            string CategoryNameEn,
            string CategoryNameAr,

            // Item
            int ItemId,
            string ItemEnName,
            string ItemArName,
            int? ItemIsActive,
            string? ItemDocumentName,

            // ItemUnit
            int ItemUnitId,

            // Unit
            int UnitId,
            string UnitEnName,
            string UnitArName,

            // Pricing
            decimal ItemUnitPrice,
            double? ItemUnitDuration,
            decimal MinimumPrice,
            bool UnitActive
        );

        // ===== Appointment Categories (branch scoped via branch info) =====
        public record AppointmentCategoryDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int Id,
            string ArabicName,
            string EnglishName,
            bool Deleted,
            bool IsMakeup,
            bool IsPackage,
            decimal Deposit,
            string? DocumentName
        );

        // ===== Categories (branch scoped via branch info) =====
        public record CategoryDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int CategoryId,
            string CategoryName1,
            int CategoryIsActive,
            int? ParentCategory,
            int CategoryOrdering,
            string? DocumentName
        );

        // ===== Customers (has BranchId) =====
        public record CustomerDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int CustomerId,
            string CustomerName,
            string CustomerPhone1,
            string? CustomerPhone2,
            int? CustomerIsBlock,
            string? CustomerBlockReason,
            string? CustomerNote,
            string NotificationLang,
            bool HasRefundHistory,         
            DateTime? LastRefundDate,     
            decimal TotalRefundAmount      
        );

        // ===== Invoice Payment Types (branch scoped via branch info) =====
        public record InvoicePaymentTypeDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int Id,
            string Name1,
            string Name2,
            decimal? Rate,
            bool Treasury,
            bool? Loyalty,
            bool Reservation,
            bool OnlinePayment,
            string? DocumentName
        );

        // ===== Staff (has BranchId) =====
        public record StaffDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int Id,
            string ArabicName,
            string EnglishName,
            string? Mobile,
            bool Active,
            string? DocumentName
        );

        // Staff with categories
        public record StaffWithCategoriesDto(
            int? BranchId,
            string? BranchName1,
            string? BranchName2,

            int Id,
            string ArabicName,
            string EnglishName,
            string? Mobile,
            bool Active,
            List<int> CategoryIds
        );

        // ===== Branch list =====
        public record BranchDto(
            int BranchId,
            int CompanyId,
            string BranchName1,
            string BranchName2,
            int? BranchIsActive,
            string? BranchAddress,
            string? BranchPhone,
            decimal? TaxValue,
            string ArabicCurrencyName,
            string EnglishCurrencyName,
            int RoundOfDigits,
            string? ColorCode,
            string? Email,
            string? WhatsappMobile
        );

        public record AppointmentSettingsDto(
            int StartHour,
            int EndHour,
            int SlotDuration,
            int TimeZoneOffset
        );


        // ===== Create Customer =====
        public record CreateCustomerRequest(
            string CustomerName,
            string CustomerPhone1,
            string? CustomerPhone2,
            DateTime? BirthDate,
            int? CustomerIsBlock,
            string? CustomerBlockReason,
            int BranchId,
            string? NotificationLang         
        );

        public record CreateCustomerResponse(
            int CustomerId,
            string CustomerName,
            string CustomerPhone1,
            string? CustomerPhone2,
            DateTime? BirthDate,
            int? CustomerIsBlock,
            string? CustomerBlockReason,
            DateTime CustomerCreatedDate,
            int BranchId,
            Guid CustomerRefGuide,
            string NotificationLang          
        );
    }
}
