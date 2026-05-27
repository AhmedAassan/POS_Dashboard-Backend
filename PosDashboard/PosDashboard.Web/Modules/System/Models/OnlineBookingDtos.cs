// Modules/Online/Models/OnlineBookingDtos.cs
using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class OnlineBookingDtos
    {
        // ═══════════════════════════════════════════════════════════
        // GENERIC WRAPPER — matches existing backend pattern
        // ═══════════════════════════════════════════════════════════
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record ApiResultBilingual<T>(
            bool Success,
            string? Error,
            T? Data,
            string? MsgAR,
            string? MsgEN
        );

        // ═══════════════════════════════════════════════════════════
        // ── AUTH
        // ═══════════════════════════════════════════════════════════

        /// POST /api/online/auth/request-otp
        public class RequestOtpDto
        {
            public string MobileNo { get; set; } = "";
        }

        /// POST /api/online/auth/verify-otp
        public class VerifyOtpDto
        {
            public string MobileNo { get; set; } = "";
            public string OtpCode { get; set; } = "";
        }

        /// POST /api/online/auth/register  (Step 2 — after OTP verified)
        public class RegisterDto
        {
            public string MobileNo { get; set; } = "";
            public string FullName { get; set; } = "";
            public string Email { get; set; } = "";
            public string Gender { get; set; } = "Male";
            public int BranchId { get; set; }
            public string Password { get; set; } = "";
            public string ConfirmPassword { get; set; } = "";
            public string NotificationLang { get; set; } = "ar";
        }

        /// POST /api/online/auth/login
        public class LoginDto
        {
            public string Mobile { get; set; } = "";
            public string Password { get; set; } = "";
        }

        /// Response for login + register
        public record LoginResponseDto(
            bool Status,
            string Token,
            string RefreshToken,
            string ExpiryTime,
            OnlineUserDto? User
        );

        /// POST /api/online/auth/refresh-token
        public record RefreshTokenDto(string RefreshToken);

        // ═══════════════════════════════════════════════════════════
        // ── USER / PROFILE
        // ═══════════════════════════════════════════════════════════

        /// Returned inside LoginResponse and GET /api/online/profile
        public record OnlineUserDto(
            int CustomerId,
            string FullName,
            string? Email,
            string CustomerPhone,
            string? Gender,
            int BranchId,
            string CustomerRef,         // CUSTOMER_REF_GUIDE as string
            string NotificationLang     // "ar" | "en"
        );

        /// PUT /api/online/profile
        public class UpdateProfileDto
        {
            public string? FullName { get; set; }
            public string? Email { get; set; }
            public string? Gender { get; set; }
            public string? NotificationLang { get; set; }
        }

        /// POST /api/online/profile/change-password
        public class ChangePasswordDto
        {
            public string CurrentPassword { get; set; } = "";
            public string NewPassword { get; set; } = "";
            public string ConfirmNewPassword { get; set; } = "";
        }

        // ═══════════════════════════════════════════════════════════
        // ── CONFIG / LOOKUPS
        // ═══════════════════════════════════════════════════════════

        /// Branch (simplified for website)
        public record OnlineBranchDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? Phone,
            string? Address
        );

        /// AppointmentCategory — NO Deposit here (Deposit is on ITEM_UNIT)
        public record OnlineAppointmentCategoryDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? DocumentName,
            bool IsMakeup,
            bool IsPackage
        );

        /// Full config payload — one call loads everything the website needs
        public record OnlineConfigDto(
            List<OnlineBranchDto> Branches,
            List<OnlineAppointmentCategoryDto> AppointmentCategories,
            string CurrencySymbol,
            string? ShopLogoUrl,
            string? ShopNameEN,
            string? ShopNameAR,
            string? StaffImageBaseUrl,
            string? CategoryImageBaseUrl,
            string? ProductImageBaseUrl
        );

        /// Area (for addresses)
        public record OnlineAreaDto(
            int AreaId,
            string ArabicName,
            string EnglishName,
            int GovernorateId,
            string GovernorateNameAR,
            string GovernorateNameEN
        );

        // ═══════════════════════════════════════════════════════════
        // ── AVAILABILITY
        // ═══════════════════════════════════════════════════════════

        /// Staff member (with live availability flag)
        public record OnlineStaffDto(
            int Id,
            string ArabicName,
            string EnglishName,
            string? DocumentName,
            bool IsAvailable
        );

        /// Service item with Deposit info
        /// Price = ITEM_UNIT.ITEM_UNIT_PRICE  (or StaffItems.Price for makeup)
        /// Deposit: 0 = full payment, > 0 = deposit amount
        public record OnlineServiceItemDto(
            int ItemUnitId,
            int ItemId,
            string ArabicName,
            string EnglishName,
            decimal Price,
            decimal Deposit,
            double? Duration,
            string? DocumentName,
            string? Notes         
        );

        /// POST /api/online/availability/time-slots
        public class TimeSlotsRequestDto
        {
            public int StaffId { get; set; }
            public string Date { get; set; } = "";
            public List<int> ItemUnitIds { get; set; } = new();
        }

        // ═══════════════════════════════════════════════════════════
        // ── BOOKING
        // ═══════════════════════════════════════════════════════════

        /// POST /api/online/bookings
        public class SubmitOnlineBookingDto
        {
            public int BranchId { get; set; }
            public int AppointmentCategoryId { get; set; }
            public int StaffId { get; set; }
            public string BookingDate { get; set; } = "";
            public string StartTime { get; set; } = "";
            public string ServiceType { get; set; } = "SALON";
            public int NumberOfPersons { get; set; } = 1;
            public List<int> ItemUnitIds { get; set; } = new();
            public int? CustomerAddressId { get; set; }
            public string? Notes { get; set; }
        }

        /// Response of POST /api/online/bookings
        public record OnlineBookingResultDto(
            int AppointmentId,
            string Status,             // "scheduled"
            string? PaymentUrl,         // MyFatoorah payment link
            decimal TotalPrice,
            decimal DepositAmount,      // 0 = full, > 0 = deposit
            bool IsDepositPayment    // true when DepositAmount > 0
        );

        /// Single booking item returned in list/detail
        public record OnlineCustomerBookingDto(
            int Id,
            string CategoryArabicName,
            string CategoryEnglishName,
            string ItemArabicName,
            string ItemEnglishName,
            string StaffArabicName,
            string StaffEnglishName,
            string BranchArabicName,
            string BranchEnglishName,
            string BookingDate,           // "yyyy-MM-dd"
            string StartTime,             // "HH:mm"
            string EndTime,               // "HH:mm"
            string ServiceType,           // "SALON" | "HOME"
            string Status,                // "scheduled"|"completed"|"cancelled"|"no-show"
            decimal TotalPrice,
            decimal PaidAmount,
            decimal DepositAmount,
            string PaymentStatus,         // "NONE"|"DEPOSIT"|"FULL"
            string? PaymentUrl,
            int NumberOfPersons,
            string? Notes,
            bool IsOnlineBooking,
            DateTime CreatedAt
        );

        // ═══════════════════════════════════════════════════════════
        // ── ADDRESSES
        // ═══════════════════════════════════════════════════════════

        /// Address returned from GET /api/online/addresses
        public record OnlineAddressDto(
            int AddressId,
            int? AreaId,
            string? AreaNameAR,
            string? AreaNameEN,
            int? GovernorateId,
            string? GovernorateNameAR,
            string? GovernorateNameEN,
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

        /// POST /api/online/addresses  +  PUT /api/online/addresses/{id}
        public class UpsertAddressDto
        {
            public int Id { get; set; }
            public int AreaId { get; set; }
            public string? BlockNo { get; set; }
            public string? Street { get; set; }
            public string? Avenue { get; set; }
            public string? BuildingNo { get; set; }
            public string? FlatNo { get; set; }
            public string? Floor { get; set; }
            public string? AddressLink { get; set; }
            public string? Note { get; set; }
            public bool MakeDefault { get; set; }
        }
    }
}