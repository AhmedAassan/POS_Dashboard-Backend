// Modules/System/Models/AppointmentMultiDtos.cs
//
// DTOs for the inline "multiple services" appointment.
//
// A multi-service appointment is just N normal appointment rows that share one
// SaleGroupId. Each row keeps its OWN real price and staff, and each is placed
// independently on the calendar (next free slot, like the offer flow).
//
// Difference from the offer: there is no package — every line carries its real
// ITEM_UNIT_PRICE and the total is their sum. Difference from New Sale: these
// are FUTURE bookings (Status='scheduled', CheckoutStatus='open'), so NO invoice
// is created up-front — the invoice is produced later at checkout, exactly like
// a single-service appointment. An optional deposit may be collected now.

using System;
using System.Collections.Generic;
using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AppointmentMultiDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Request =====

        /// <summary>One service in the booking, with its chosen staff.
        /// StartTime is optional: when provided the backend places the line at
        /// exactly that time (overlaps for the same staff are allowed on purpose);
        /// when null the backend cascades it to the next free slot.
        /// UnitPriceOverride is a sale-only price for this line; null => real ITEM price.</summary>
        public record AppointmentMultiLineRequest(
            int ItemId,
            int UnitId,
            int StaffId,
            int? DurationMinutes,   // optional override; falls back to ITEM_UNIT_DURATION
            string? Notes,
            string? StartTime,          // optional "HH:mm"; null => auto next free slot
            decimal? UnitPriceOverride  // optional sale-only price; null => ITEM_UNIT_PRICE
        );

        /// <summary>Optional payment collected NOW (offline at the salon).
        /// Null = book without paying.</summary>
        public record AppointmentMultiDepositRequest(
            decimal Amount,
            int PaymentTypeId,
            string? VoucherCode,
            bool IsWalletPayment
        );

        public record AppointmentMultiRequest(
            int BranchId,
            int CustomerId,
            DateTime? AppointmentDate,  // defaults to today (server local)
            string? StartTime,          // "HH:mm"; null => backend snaps to next slot
            string ServiceType,         // "SALON" | "HOME"
            int NumberOfPersons,
            string? Notes,
            List<AppointmentMultiLineRequest> Lines,
            AppointmentMultiDepositRequest? Deposit   // null = no payment now
        );

        // ===== Response =====

        public record AppointmentMultiLineWarning(int LineIndex, string Message);

        public record AppointmentMultiResponse(
            Guid SaleGroupId,
            int LeadAppointmentId,
            List<int> AppointmentIds,
            List<AppointmentDto> Appointments,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string PaymentStatus,       // 'NONE' | 'DEPOSIT' | 'FULL'
            string Currency,
            List<AppointmentMultiLineWarning> Warnings
        );
    }
}