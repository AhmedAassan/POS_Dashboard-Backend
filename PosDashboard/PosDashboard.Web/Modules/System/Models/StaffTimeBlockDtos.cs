// Modules/System/Models/StaffTimeBlockDtos.cs

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class StaffTimeBlockDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        // ===== Create =====
        public record CreateTimeBlockRequest(
            int StaffId,
            int BranchId,
            DateTime BlockDate,
            string StartTime,       // "HH:mm"
            string EndTime,         // "HH:mm"
            string BlockType,       // BREAK, PERSONAL, TRAINING, DAY_OFF, CUSTOM
            string? Title,
            string? Notes,
            bool IsRecurring,
            string? RecurrenceRule,  // DAILY, WEEKLY:MON,TUE, WEEKDAYS, WEEKENDS
            DateTime? RecurringStart,
            DateTime? RecurringEnd
        );

        // ===== Update =====
        public record UpdateTimeBlockRequest(
            DateTime? BlockDate,
            string? StartTime,
            string? EndTime,
            string? BlockType,
            string? Title,
            string? Notes,
            bool? ClearNotes,
            bool? IsRecurring,
            string? RecurrenceRule,
            DateTime? RecurringStart,
            DateTime? RecurringEnd
        );

        // ===== Response =====
        public record TimeBlockDto(
            int Id,
            int StaffId,
            string StaffEnName,
            string StaffArName,
            int BranchId,
            string BlockDate,       // "yyyy-MM-dd"
            string StartTime,       // "HH:mm"
            string EndTime,         // "HH:mm"
            string BlockType,
            string? Title,
            string? Notes,
            bool IsRecurring,
            string? RecurrenceRule,
            string? RecurringStart,
            string? RecurringEnd,
            DateTime CreatedAt
        );

        public record TimeBlockListResponse(
            int TotalCount,
            List<TimeBlockDto> Blocks
        );

        // ===== Blocked Slots (for frontend time picker) =====
        public record StaffBlockedSlot(
            int StaffId,
            string StartTime,       // "HH:mm"
            string EndTime,         // "HH:mm"
            string Reason           // "Break", "Appointment: Client Name", etc.
        );

        public record StaffAvailabilityResponse(
            int StaffId,
            string StaffName,
            string Date,
            List<StaffBlockedSlot> BlockedSlots
        );
    }
}