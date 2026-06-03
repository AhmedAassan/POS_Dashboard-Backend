// Modules/System/Models/AppointmentOfferDtos.cs
//
// DTOs for "Package OFFER" inside Create New Appointment.
//
// This is the appointment-side twin of NewSaleDtos. The shape mirrors the
// New Sale offer flow (one package -> several services, each with its own
// staff, each placed independently on the calendar) with one difference:
// payment is NOT mandatory. The booking is created regardless, and an
// optional deposit may be collected. The total is always the package price.

using System;
using System.Collections.Generic;
using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AppointmentOfferDtos
    {
        // Generic API result — identical in shape to the rest of the project.
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Request =====

        /// <summary>
        /// One service inside the offer. Keyed by ItemUnitId so the backend can
        /// match it to a package item and reject anything outside the package.
        /// No time is sent — the backend assigns each line its own slot.
        /// </summary>
        public record AppointmentOfferLineRequest(
            int ItemUnitId,            // identifies the package item's service+unit
            int StaffId,               // the staff chosen for THIS service
            int? DurationMinutes,      // optional override; falls back to ITEM_UNIT_DURATION
            string? Notes
        );

        /// <summary>
        /// Optional up-front deposit. When null, the booking is created unpaid.
        /// Amount must be > 0 and ≤ the package price.
        /// </summary>
        public record AppointmentOfferDepositRequest(
            decimal Amount,
            int PaymentTypeId,
            string? VoucherCode,
            bool IsWalletPayment
        );

        public record AppointmentOfferRequest(
            int BranchId,
            int CustomerId,
            int PackageOfferId,            // the OFFER package being booked
            DateTime? AppointmentDate,     // optional, defaults to "today" (server local)
            string? StartTime,             // optional "HH:mm"; null => backend snaps to next slot
            string ServiceType,            // "SALON" | "HOME"
            int NumberOfPersons,
            string? Notes,
            List<AppointmentOfferLineRequest> Lines,   // one per package item, with chosen staff
            AppointmentOfferDepositRequest? Deposit    // null = no payment now
        );

        // ===== Response =====

        public record AppointmentOfferLineWarning(
            int LineIndex,
            string Message
        );

        public record AppointmentOfferResponse(
            int InvoiceId,
            string InvoiceNumber,
            int LeadAppointmentId,
            Guid SaleGroupId,
            List<int> AppointmentIds,
            List<AppointmentDto> Appointments,
            decimal TotalAmount,
            decimal PaidAmount,
            decimal RemainingAmount,
            string PaymentStatus,          // 'NONE' | 'DEPOSIT' | 'FULL'
            string Currency,
            int PackageOfferId,
            string PackageOfferName,
            decimal PackageOfferPrice,
            List<AppointmentOfferLineWarning> Warnings
        );
    }
}