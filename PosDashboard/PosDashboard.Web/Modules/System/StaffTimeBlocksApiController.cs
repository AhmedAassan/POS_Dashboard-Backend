// Modules/System/Controllers/StaffTimeBlocksApiController.cs

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using static PosDashboard.Web.Modules.System.Models.StaffTimeBlockDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/staff-time-blocks")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class StaffTimeBlocksApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public StaffTimeBlocksApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        private static readonly string[] ValidBlockTypes =
            { "BREAK", "PERSONAL", "TRAINING", "DAY_OFF", "CUSTOM" };

        private static readonly string[] ValidRecurrenceRules =
            { "DAILY", "WEEKDAYS", "WEEKENDS" };
        // WEEKLY:MON,TUE,WED is also valid but checked separately

        private bool TryParseTime(string? time, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(time)) return false;
            return TimeSpan.TryParseExact(time, @"hh\:mm", null, out result);
        }

        private bool IsValidRecurrenceRule(string? rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return false;
            if (ValidRecurrenceRules.Contains(rule)) return true;
            if (rule.StartsWith("WEEKLY:"))
            {
                var days = rule.Substring(7).Split(',');
                var validDays = new[] { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
                return days.All(d => validDays.Contains(d.Trim().ToUpper()));
            }
            return false;
        }

        // Helper: check if a recurring block applies to a specific date
        private bool RecurrenceAppliesToDate(string rule, DateTime date)
        {
            var dayOfWeek = date.DayOfWeek;

            if (rule == "DAILY") return true;
            if (rule == "WEEKDAYS") return dayOfWeek >= DayOfWeek.Monday && dayOfWeek <= DayOfWeek.Friday;
            if (rule == "WEEKENDS") return dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday;

            if (rule.StartsWith("WEEKLY:"))
            {
                var days = rule.Substring(7).Split(',').Select(d => d.Trim().ToUpper());
                var dayMap = new Dictionary<string, DayOfWeek>
                {
                    {"MON", DayOfWeek.Monday}, {"TUE", DayOfWeek.Tuesday},
                    {"WED", DayOfWeek.Wednesday}, {"THU", DayOfWeek.Thursday},
                    {"FRI", DayOfWeek.Friday}, {"SAT", DayOfWeek.Saturday},
                    {"SUN", DayOfWeek.Sunday}
                };
                return days.Any(d => dayMap.ContainsKey(d) && dayMap[d] == dayOfWeek);
            }

            return false;
        }

        // =============================================
        // POST /api/staff-time-blocks — Create
        // =============================================
        [HttpPost]
        public ActionResult<ApiResult<TimeBlockDto>> Create(
            [FromBody] CreateTimeBlockRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<TimeBlockDto>(false, "Request body is required", null));

            if (!ValidBlockTypes.Contains(request.BlockType))
                return Ok(new ApiResult<TimeBlockDto>(false,
                    "BlockType must be: BREAK, PERSONAL, TRAINING, DAY_OFF, CUSTOM", null));

            if (!TryParseTime(request.StartTime, out var startTs))
                return Ok(new ApiResult<TimeBlockDto>(false,
                    "StartTime must be in HH:mm format", null));

            if (!TryParseTime(request.EndTime, out var endTs))
                return Ok(new ApiResult<TimeBlockDto>(false,
                    "EndTime must be in HH:mm format", null));

            if (endTs <= startTs)
                return Ok(new ApiResult<TimeBlockDto>(false,
                    "EndTime must be after StartTime", null));

            if (request.IsRecurring)
            {
                if (!IsValidRecurrenceRule(request.RecurrenceRule))
                    return Ok(new ApiResult<TimeBlockDto>(false,
                        "Invalid RecurrenceRule. Use: DAILY, WEEKDAYS, WEEKENDS, or WEEKLY:MON,TUE,...", null));
            }

            using var conn = sqlConnections.NewByKey("Default");

            // Validate staff
            var staff = SqlMapper.Query(conn,
                "SELECT Id, EnglishName, ArabicName FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = request.StaffId }).FirstOrDefault();

            if (staff == null)
                return Ok(new ApiResult<TimeBlockDto>(false, "Staff not found or not active", null));

            // Validate branch
            var branch = SqlMapper.Query(conn,
                @"SELECT BRANCH_ID FROM dbo.BRANCH 
                  WHERE BRANCH_ID = @Id AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { Id = request.BranchId }).FirstOrDefault();

            if (branch == null)
                return Ok(new ApiResult<TimeBlockDto>(false, "Branch not found or inactive", null));

            // Check overlap with existing blocks (same staff, same date)
            if (!request.IsRecurring)
            {
                var blockConflict = SqlMapper.Query(conn, @"
                    SELECT Id FROM dbo.StaffTimeBlocks
                    WHERE StaffId = @StaffId
                      AND BlockDate = @BlockDate
                      AND Deleted = 0
                      AND StartTime < @EndTime
                      AND EndTime > @StartTime",
                    new
                    {
                        StaffId = request.StaffId,
                        BlockDate = request.BlockDate.Date,
                        StartTime = startTs,
                        EndTime = endTs
                    }).FirstOrDefault();

                if (blockConflict != null)
                    return Ok(new ApiResult<TimeBlockDto>(false,
                        $"Overlaps with existing time block #{(int)blockConflict.Id}", null));
            }

            // Check overlap with existing appointments (same staff, same date)
            if (!request.IsRecurring)
            {
                var aptConflict = SqlMapper.Query(conn, @"
                    SELECT Id FROM dbo.AppointmentData
                    WHERE StaffId = @StaffId
                      AND AppointmentDate = @Date
                      AND Status != 'cancelled'
                      AND StartTime < @EndTime
                      AND EndTime > @StartTime",
                    new
                    {
                        StaffId = request.StaffId,
                        Date = request.BlockDate.Date,
                        StartTime = startTs,
                        EndTime = endTs
                    }).FirstOrDefault();

                if (aptConflict != null)
                    return Ok(new ApiResult<TimeBlockDto>(false,
                        $"Conflicts with appointment #{(int)aptConflict.Id}. Cancel the appointment first or choose a different time.", null));
            }

            // Insert
            var id = SqlMapper.Query<int>(conn, @"
                INSERT INTO dbo.StaffTimeBlocks (
                    StaffId, BranchId, BlockDate, StartTime, EndTime,
                    BlockType, Title, Notes,
                    IsRecurring, RecurrenceRule, RecurringStart, RecurringEnd,
                    CreatedAt, Deleted
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @StaffId, @BranchId, @BlockDate, @StartTime, @EndTime,
                    @BlockType, @Title, @Notes,
                    @IsRecurring, @RecurrenceRule, @RecurringStart, @RecurringEnd,
                    SYSUTCDATETIME(), 0
                )",
                new
                {
                    request.StaffId,
                    request.BranchId,
                    BlockDate = request.BlockDate.Date,
                    StartTime = startTs,
                    EndTime = endTs,
                    request.BlockType,
                    Title = string.IsNullOrWhiteSpace(request.Title) ? GetDefaultTitle(request.BlockType) : request.Title.Trim(),
                    Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                    request.IsRecurring,
                    RecurrenceRule = request.IsRecurring ? request.RecurrenceRule : null,
                    RecurringStart = request.IsRecurring ? request.RecurringStart : null,
                    RecurringEnd = request.IsRecurring ? request.RecurringEnd : null
                }).FirstOrDefault();

            var block = GetBlockById(conn, id);
            return Ok(new ApiResult<TimeBlockDto>(true, null, block));
        }

        // =============================================
        // GET /api/staff-time-blocks?branchId=1&date=2025-01-15
        // =============================================
        [HttpGet]
        public ActionResult<ApiResult<TimeBlockListResponse>> GetAll(
            [FromQuery] int branchId,
            [FromQuery] DateTime? date,
            [FromQuery] DateTime? dateFrom,
            [FromQuery] DateTime? dateTo,
            [FromQuery] int? staffId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            // 1) Get one-off blocks
            var where = new List<string> { "b.BranchId = @BranchId", "b.Deleted = 0" };
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("BranchId", branchId);

            // One-off blocks filter
            var oneOffWhere = new List<string>(where) { "b.IsRecurring = 0" };

            if (date.HasValue)
            {
                oneOffWhere.Add("b.BlockDate = @Date");
                parameters.Add("Date", date.Value.Date);
            }
            else
            {
                if (dateFrom.HasValue)
                {
                    oneOffWhere.Add("b.BlockDate >= @DateFrom");
                    parameters.Add("DateFrom", dateFrom.Value.Date);
                }
                if (dateTo.HasValue)
                {
                    oneOffWhere.Add("b.BlockDate <= @DateTo");
                    parameters.Add("DateTo", dateTo.Value.Date);
                }
            }

            if (staffId.HasValue)
            {
                oneOffWhere.Add("b.StaffId = @StaffId");
                parameters.Add("StaffId", staffId.Value);
            }

            var oneOffClause = string.Join(" AND ", oneOffWhere);

            var oneOffBlocks = SqlMapper.Query(conn, $@"
                SELECT 
                    b.Id, b.StaffId, s.EnglishName AS StaffEnName, s.ArabicName AS StaffArName,
                    b.BranchId,
                    CONVERT(VARCHAR(10), b.BlockDate, 120) AS BlockDate,
                    LEFT(CONVERT(VARCHAR(8), b.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), b.EndTime, 108), 5) AS EndTime,
                    b.BlockType, b.Title, b.Notes,
                    b.IsRecurring, b.RecurrenceRule,
                    CONVERT(VARCHAR(10), b.RecurringStart, 120) AS RecurringStart,
                    CONVERT(VARCHAR(10), b.RecurringEnd, 120) AS RecurringEnd,
                    b.CreatedAt
                FROM dbo.StaffTimeBlocks b
                INNER JOIN dbo.STAFF s ON s.Id = b.StaffId
                WHERE {oneOffClause}
                ORDER BY b.BlockDate, b.StartTime",
                (object)parameters).ToList();

            // 2) Get recurring blocks that might apply
            var recurringParams = new Dapper.DynamicParameters();
            recurringParams.Add("BranchId", branchId);
            if (staffId.HasValue) recurringParams.Add("StaffId", staffId.Value);

            var recurringWhere = new List<string> { "b.BranchId = @BranchId", "b.Deleted = 0", "b.IsRecurring = 1" };
            if (staffId.HasValue) recurringWhere.Add("b.StaffId = @StaffId");

            var recurringClause = string.Join(" AND ", recurringWhere);

            var recurringBlocks = SqlMapper.Query(conn, $@"
                SELECT 
                    b.Id, b.StaffId, s.EnglishName AS StaffEnName, s.ArabicName AS StaffArName,
                    b.BranchId,
                    CONVERT(VARCHAR(10), b.BlockDate, 120) AS BlockDate,
                    LEFT(CONVERT(VARCHAR(8), b.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), b.EndTime, 108), 5) AS EndTime,
                    b.BlockType, b.Title, b.Notes,
                    b.IsRecurring, b.RecurrenceRule,
                    CONVERT(VARCHAR(10), b.RecurringStart, 120) AS RecurringStart,
                    CONVERT(VARCHAR(10), b.RecurringEnd, 120) AS RecurringEnd,
                    b.CreatedAt
                FROM dbo.StaffTimeBlocks b
                INNER JOIN dbo.STAFF s ON s.Id = b.StaffId
                WHERE {recurringClause}
                ORDER BY b.StartTime",
                (object)recurringParams).ToList();

            // 3) Expand recurring blocks for the requested date range
            var expandedRecurring = new List<dynamic>();

            if (date.HasValue || (dateFrom.HasValue && dateTo.HasValue))
            {
                var rangeStart = date?.Date ?? dateFrom!.Value.Date;
                var rangeEnd = date?.Date ?? dateTo!.Value.Date;

                foreach (var rb in recurringBlocks)
                {
                    string rule = (string)rb.RecurrenceRule;
                    DateTime? recStart = rb.RecurringStart != null ? DateTime.Parse((string)rb.RecurringStart) : (DateTime?)null;
                    DateTime? recEnd = rb.RecurringEnd != null ? DateTime.Parse((string)rb.RecurringEnd) : (DateTime?)null;

                    for (var d = rangeStart; d <= rangeEnd; d = d.AddDays(1))
                    {
                        if (recStart.HasValue && d < recStart.Value) continue;
                        if (recEnd.HasValue && d > recEnd.Value) continue;

                        if (RecurrenceAppliesToDate(rule, d))
                        {
                            // Clone the block with the expanded date
                            expandedRecurring.Add(new
                            {
                                rb.Id,
                                rb.StaffId,
                                rb.StaffEnName,
                                rb.StaffArName,
                                rb.BranchId,
                                BlockDate = d.ToString("yyyy-MM-dd"),
                                rb.StartTime,
                                rb.EndTime,
                                rb.BlockType,
                                rb.Title,
                                rb.Notes,
                                rb.IsRecurring,
                                rb.RecurrenceRule,
                                rb.RecurringStart,
                                rb.RecurringEnd,
                                rb.CreatedAt
                            });
                        }
                    }
                }
            }
            else
            {
                // No date filter — just return recurring definitions as-is
                expandedRecurring.AddRange(recurringBlocks);
            }

            // 4) Combine and map
            var allRows = oneOffBlocks.Concat(expandedRecurring).ToList();

            var result = allRows.Select(row => new TimeBlockDto(
                Id: (int)row.Id,
                StaffId: (int)row.StaffId,
                StaffEnName: (string)(row.StaffEnName ?? ""),
                StaffArName: (string)(row.StaffArName ?? ""),
                BranchId: (int)row.BranchId,
                BlockDate: (string)row.BlockDate,
                StartTime: (string)row.StartTime,
                EndTime: (string)row.EndTime,
                BlockType: (string)row.BlockType,
                Title: (string?)row.Title,
                Notes: (string?)row.Notes,
                IsRecurring: (bool)row.IsRecurring,
                RecurrenceRule: (string?)row.RecurrenceRule,
                RecurringStart: (string?)row.RecurringStart,
                RecurringEnd: (string?)row.RecurringEnd,
                CreatedAt: (DateTime)row.CreatedAt
            )).ToList();

            var response = new TimeBlockListResponse(result.Count, result);
            return Ok(new ApiResult<TimeBlockListResponse>(true, null, response));
        }

        // =============================================
        // GET /api/staff-time-blocks/{id}
        // =============================================
        [HttpGet("{id:int}")]
        public ActionResult<ApiResult<TimeBlockDto>> GetById(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var block = GetBlockById(conn, id);
            if (block == null)
                return Ok(new ApiResult<TimeBlockDto>(false, "Time block not found", null));
            return Ok(new ApiResult<TimeBlockDto>(true, null, block));
        }

        // =============================================
        // PUT /api/staff-time-blocks/{id}
        // =============================================
        [HttpPost("update/{id:int}")]
        public ActionResult<ApiResult<TimeBlockDto>> Update(
            int id, [FromBody] UpdateTimeBlockRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<TimeBlockDto>(false, "Request body is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var existing = SqlMapper.Query(conn,
                "SELECT Id, Deleted FROM dbo.StaffTimeBlocks WHERE Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (existing == null || (bool)existing.Deleted)
                return Ok(new ApiResult<TimeBlockDto>(false, "Time block not found", null));

            var updates = new List<string>();
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("Id", id);

            if (request.BlockDate.HasValue)
            {
                updates.Add("BlockDate = @BlockDate");
                parameters.Add("BlockDate", request.BlockDate.Value.Date);
            }

            if (!string.IsNullOrWhiteSpace(request.StartTime))
            {
                if (!TryParseTime(request.StartTime, out var st))
                    return Ok(new ApiResult<TimeBlockDto>(false, "StartTime must be HH:mm", null));
                updates.Add("StartTime = @StartTime");
                parameters.Add("StartTime", st);
            }

            if (!string.IsNullOrWhiteSpace(request.EndTime))
            {
                if (!TryParseTime(request.EndTime, out var et))
                    return Ok(new ApiResult<TimeBlockDto>(false, "EndTime must be HH:mm", null));
                updates.Add("EndTime = @EndTime");
                parameters.Add("EndTime", et);
            }

            if (!string.IsNullOrWhiteSpace(request.BlockType))
            {
                if (!ValidBlockTypes.Contains(request.BlockType))
                    return Ok(new ApiResult<TimeBlockDto>(false, "Invalid BlockType", null));
                updates.Add("BlockType = @BlockType");
                parameters.Add("BlockType", request.BlockType);
            }

            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                updates.Add("Title = @Title");
                parameters.Add("Title", request.Title.Trim());
            }

            if (request.ClearNotes == true)
                updates.Add("Notes = NULL");
            else if (!string.IsNullOrWhiteSpace(request.Notes))
            {
                updates.Add("Notes = @Notes");
                parameters.Add("Notes", request.Notes.Trim());
            }

            if (request.IsRecurring.HasValue)
            {
                updates.Add("IsRecurring = @IsRecurring");
                parameters.Add("IsRecurring", request.IsRecurring.Value);
            }

            if (request.RecurrenceRule != null)
            {
                updates.Add("RecurrenceRule = @RecurrenceRule");
                parameters.Add("RecurrenceRule", request.RecurrenceRule);
            }

            if (request.RecurringStart.HasValue)
            {
                updates.Add("RecurringStart = @RecurringStart");
                parameters.Add("RecurringStart", request.RecurringStart.Value);
            }

            if (request.RecurringEnd.HasValue)
            {
                updates.Add("RecurringEnd = @RecurringEnd");
                parameters.Add("RecurringEnd", request.RecurringEnd.Value);
            }

            if (updates.Count == 0)
                return Ok(new ApiResult<TimeBlockDto>(false, "No fields to update", null));

            updates.Add("UpdatedAt = SYSUTCDATETIME()");

            var sql = $"UPDATE dbo.StaffTimeBlocks SET {string.Join(", ", updates)} WHERE Id = @Id";
            SqlMapper.Execute(conn, sql, parameters);

            var block = GetBlockById(conn, id);
            return Ok(new ApiResult<TimeBlockDto>(true, null, block));
        }

        // =============================================
        // DELETE /api/staff-time-blocks/{id}
        // =============================================
        [HttpPost("delete/{id:int}")]
        public ActionResult<ApiResult<object>> Delete(int id)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var existing = SqlMapper.Query(conn,
                "SELECT Id FROM dbo.StaffTimeBlocks WHERE Id = @Id AND Deleted = 0",
                new { Id = id }).FirstOrDefault();

            if (existing == null)
                return Ok(new ApiResult<object>(false, "Time block not found", null));

            SqlMapper.Execute(conn, @"
                UPDATE dbo.StaffTimeBlocks 
                SET Deleted = 1, UpdatedAt = SYSUTCDATETIME() 
                WHERE Id = @Id",
                new { Id = id });

            return Ok(new ApiResult<object>(true, null, new { DeletedId = id }));
        }

        // =============================================
        // GET /api/staff-time-blocks/availability?staffId=1&date=2025-01-15
        // Returns all blocked slots (blocks + existing appointments)
        // =============================================
        [HttpGet("availability")]
        public ActionResult<ApiResult<StaffAvailabilityResponse>> GetAvailability(
            [FromQuery] int staffId,
            [FromQuery] DateTime date)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var staff = SqlMapper.Query(conn,
                "SELECT Id, EnglishName FROM dbo.STAFF WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = staffId }).FirstOrDefault();

            if (staff == null)
                return Ok(new ApiResult<StaffAvailabilityResponse>(false,
                    "Staff not found or not active", null));

            var blockedSlots = new List<StaffBlockedSlot>();

            // 1) One-off time blocks for this date
            var oneOffBlocks = SqlMapper.Query(conn, @"
                SELECT 
                    LEFT(CONVERT(VARCHAR(8), StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), EndTime, 108), 5) AS EndTime,
                    BlockType, Title
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND BlockDate = @Date
                  AND IsRecurring = 0
                  AND Deleted = 0",
                new { StaffId = staffId, Date = date.Date }).ToList();

            foreach (var b in oneOffBlocks)
            {
                blockedSlots.Add(new StaffBlockedSlot(
                    StaffId: staffId,
                    StartTime: (string)b.StartTime,
                    EndTime: (string)b.EndTime,
                    Reason: (string?)b.Title ?? FormatBlockType((string)b.BlockType)
                ));
            }

            // 2) Recurring blocks that apply to this date
            var recurringBlocks = SqlMapper.Query(conn, @"
                SELECT 
                    LEFT(CONVERT(VARCHAR(8), StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), EndTime, 108), 5) AS EndTime,
                    BlockType, Title, RecurrenceRule,
                    RecurringStart, RecurringEnd
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND IsRecurring = 1
                  AND Deleted = 0
                  AND (RecurringStart IS NULL OR RecurringStart <= @Date)
                  AND (RecurringEnd IS NULL OR RecurringEnd >= @Date)",
                new { StaffId = staffId, Date = date.Date }).ToList();

            foreach (var rb in recurringBlocks)
            {
                string rule = (string)rb.RecurrenceRule;
                if (RecurrenceAppliesToDate(rule, date.Date))
                {
                    blockedSlots.Add(new StaffBlockedSlot(
                        StaffId: staffId,
                        StartTime: (string)rb.StartTime,
                        EndTime: (string)rb.EndTime,
                        Reason: (string?)rb.Title ?? FormatBlockType((string)rb.BlockType)
                    ));
                }
            }

            // 3) Existing appointments
            var appointments = SqlMapper.Query(conn, @"
                SELECT 
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime, 108), 5) AS EndTime,
                    c.CUSTOMER_NAME AS CustomerName
                FROM dbo.AppointmentData a
                INNER JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = a.CustomerId
                WHERE a.StaffId = @StaffId
                  AND a.AppointmentDate = @Date
                  AND a.Status != 'cancelled'",
                new { StaffId = staffId, Date = date.Date }).ToList();

            foreach (var apt in appointments)
            {
                blockedSlots.Add(new StaffBlockedSlot(
                    StaffId: staffId,
                    StartTime: (string)apt.StartTime,
                    EndTime: (string)apt.EndTime,
                    Reason: $"Appointment: {(string)apt.CustomerName}"
                ));
            }

            // Sort by start time
            blockedSlots = blockedSlots.OrderBy(s => s.StartTime).ToList();

            var response = new StaffAvailabilityResponse(
                StaffId: staffId,
                StaffName: (string)staff.EnglishName,
                Date: date.Date.ToString("yyyy-MM-dd"),
                BlockedSlots: blockedSlots
            );

            return Ok(new ApiResult<StaffAvailabilityResponse>(true, null, response));
        }

        #region Private Helpers

        private TimeBlockDto? GetBlockById(IDbConnection conn, int id)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT 
                    b.Id, b.StaffId, s.EnglishName AS StaffEnName, s.ArabicName AS StaffArName,
                    b.BranchId,
                    CONVERT(VARCHAR(10), b.BlockDate, 120) AS BlockDate,
                    LEFT(CONVERT(VARCHAR(8), b.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), b.EndTime, 108), 5) AS EndTime,
                    b.BlockType, b.Title, b.Notes,
                    b.IsRecurring, b.RecurrenceRule,
                    CONVERT(VARCHAR(10), b.RecurringStart, 120) AS RecurringStart,
                    CONVERT(VARCHAR(10), b.RecurringEnd, 120) AS RecurringEnd,
                    b.CreatedAt
                FROM dbo.StaffTimeBlocks b
                INNER JOIN dbo.STAFF s ON s.Id = b.StaffId
                WHERE b.Id = @Id AND b.Deleted = 0",
                new { Id = id }).FirstOrDefault();

            if (row == null) return null;

            return new TimeBlockDto(
                Id: (int)row.Id,
                StaffId: (int)row.StaffId,
                StaffEnName: (string)(row.StaffEnName ?? ""),
                StaffArName: (string)(row.StaffArName ?? ""),
                BranchId: (int)row.BranchId,
                BlockDate: (string)row.BlockDate,
                StartTime: (string)row.StartTime,
                EndTime: (string)row.EndTime,
                BlockType: (string)row.BlockType,
                Title: (string?)row.Title,
                Notes: (string?)row.Notes,
                IsRecurring: (bool)row.IsRecurring,
                RecurrenceRule: (string?)row.RecurrenceRule,
                RecurringStart: (string?)row.RecurringStart,
                RecurringEnd: (string?)row.RecurringEnd,
                CreatedAt: (DateTime)row.CreatedAt
            );
        }

        private static string GetDefaultTitle(string blockType) => blockType switch
        {
            "BREAK" => "Break",
            "PERSONAL" => "Personal Time",
            "TRAINING" => "Training",
            "DAY_OFF" => "Day Off",
            "CUSTOM" => "Blocked",
            _ => "Blocked"
        };

        private static string FormatBlockType(string blockType) => blockType switch
        {
            "BREAK" => "Break",
            "PERSONAL" => "Personal Time",
            "TRAINING" => "Training",
            "DAY_OFF" => "Day Off",
            "CUSTOM" => "Blocked",
            _ => blockType
        };

        #endregion
    }
}