// Modules/System/Models/AppointmentGroupDtos.cs
//
// DTOs for consolidated (group) checkout of a multi-service appointment.
// A "group" = all AppointmentData rows that share one SaleGroupId.

using System;
using System.Collections.Generic;
using static PosDashboard.Web.Modules.System.Models.AppointmentDtos;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AppointmentGroupDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        /// <summary>Result of checking out a whole group: the single consolidated
        /// invoice + the ids of every appointment that was checked out.</summary>
        public record GroupCheckoutResponse(
            List<int> AppointmentIds,
            InvoiceDto Invoice
        );

        public record GroupServiceDto(
            int AppointmentId,
            string ItemName,
            string StaffName,
            string StartTime,
            string EndTime,
            decimal Price
        );

        // ----- Group payment (one pooled balance for the whole visit) -----

        public record GroupPaymentRequest(
            decimal Amount,
            int PaymentTypeId,
            string? VoucherCode,
            bool IsWalletPayment
        );

        public record GroupMemberPayState(
            int AppointmentId,
            decimal TotalPrice,
            decimal PaidAmount,
            decimal RemainingAmount,
            string PaymentStatus
        );

        public record GroupPaymentResponse(
            decimal GroupTotal,
            decimal GroupPaid,
            decimal GroupRemaining,
            string PaymentStatus,
            string Currency,
            List<GroupMemberPayState> Members
        );

        /// <summary>Lightweight summary the checkout drawer uses to show the whole
        /// visit (all services + combined totals) before confirming.</summary>
        public record GroupInfoDto(
            bool IsGroup,
            int Count,
            decimal Total,
            decimal Paid,
            decimal Remaining,
            string Currency,
            List<GroupServiceDto> Services
        );
    }
}