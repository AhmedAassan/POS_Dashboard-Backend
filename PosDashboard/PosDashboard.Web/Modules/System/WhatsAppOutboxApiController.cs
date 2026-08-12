// Modules/System/Controllers/WhatsAppOutboxApiController.cs
//
// The queue behind the "Open WhatsApp" method, and the delivery log for the
// other two.
//
// A queued message is not a notification the operator can ignore — a customer
// is waiting on it. So the tray polls a deliberately cheap count endpoint and
// only pulls full rows when someone opens the panel.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PosDashboard.Web.Modules.System.Models;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WhatsAppProviderDtos;
using static PosDashboard.Web.Modules.System.Models.WhatsAppDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/whatsapp/outbox")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WhatsAppOutboxApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IWhatsAppSender sender;

        public WhatsAppOutboxApiController(ISqlConnections sqlConnections, IWhatsAppSender sender)
        {
            this.sqlConnections = sqlConnections;
            this.sender = sender;
        }

        private int? CurrentUserId()
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userId" || c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : (int?)null;
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/whatsapp/outbox/pending-count
        // Polled on a timer by the tray. Two integers, nothing else.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("pending-count")]
        public ActionResult<ApiResult<object>> PendingCount()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var counts = SqlMapper.Query(conn, @"
                SELECT
                    SUM(CASE WHEN Status IN ('Pending','Opened') THEN 1 ELSE 0 END) AS PendingCount,
                    SUM(CASE WHEN Status = 'Failed'
                              AND CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS RecentFailedCount,
                    SUM(CASE WHEN Status = 'Sent'
                              AND CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS RecentSentCount
                FROM dbo.WHATSAPP_OUTBOX").FirstOrDefault();

            var provider = WhatsAppProviders.Normalize(
                BusinessSettingsService.GetValue(conn, "whatsapp.provider"));

            return Ok(new ApiResult<object>(true, null, new
            {
                PendingCount = (int?)counts?.PendingCount ?? 0,
                RecentFailedCount = (int?)counts?.RecentFailedCount ?? 0,
                RecentSentCount = (int?)counts?.RecentSentCount ?? 0,
                ActiveProvider = provider
            }));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/whatsapp/outbox?status=pending&take=50&skip=0
        //   status: pending (default) | handled | failed | all
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ActionResult<ApiResult<OutboxPageDto>> List(
            [FromQuery] string status = "pending",
            [FromQuery] int take = 50,
            [FromQuery] int skip = 0,
            [FromQuery] string? search = null)
        {
            using var conn = sqlConnections.NewByKey("Default");

            take = Math.Clamp(take, 1, 200);
            skip = Math.Max(0, skip);

            var filter = (status ?? "pending").Trim().ToLowerInvariant() switch
            {
                "handled" => "Status IN ('Sent','Cancelled')",
                "failed" => "Status = 'Failed'",
                "all" => "1 = 1",
                _ => "Status IN ('Pending','Opened')"
            };

            // Newest first everywhere. Treating the queue as first-in-first-out was
            // the wrong model: the operator has just done the thing that created
            // the top row, and that is the one they came here to send.
            const string order = "CreatedAt DESC";

            var where = $"WHERE {filter} AND (@Search IS NULL OR Phone LIKE @Like OR CustomerName LIKE @Like)";
            var args = new
            {
                Search = string.IsNullOrWhiteSpace(search) ? null : search,
                Like = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%",
                Take = take,
                Skip = skip
            };

            var items = SqlMapper.Query<OutboxItemDto>(conn, $@"
                SELECT Id, Provider, Status, Phone, MessageText, WaLink, MessageType,
                       ReferenceId, CustomerId, CustomerName, Lang, ErrorText,
                       CreatedAt, OpenedAt, HandledAt
                FROM dbo.WHATSAPP_OUTBOX
                {where}
                ORDER BY {order}
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", args)
                .Select(AsUtc)
                .ToList();

            var totalCount = SqlMapper.Query<int>(conn,
                $"SELECT COUNT(*) FROM dbo.WHATSAPP_OUTBOX {where}", args).FirstOrDefault();

            var counts = SqlMapper.Query(conn, @"
                SELECT
                    SUM(CASE WHEN Status IN ('Pending','Opened') THEN 1 ELSE 0 END) AS PendingCount,
                    SUM(CASE WHEN Status = 'Failed'
                              AND CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS RecentFailedCount,
                    SUM(CASE WHEN Status = 'Sent'
                              AND CreatedAt > DATEADD(DAY, -1, SYSUTCDATETIME())
                             THEN 1 ELSE 0 END) AS RecentSentCount
                FROM dbo.WHATSAPP_OUTBOX").FirstOrDefault();

            var provider = WhatsAppProviders.Normalize(
                BusinessSettingsService.GetValue(conn, "whatsapp.provider"));

            // Housekeeping rides along with the panel opening rather than needing
            // a scheduler this project does not have.
            var retention = BusinessSettingsService.GetInt(conn, "whatsapp.outbox.retentionDays", 30);
            sender.PurgeHandledOlderThan(conn, retention);

            return Ok(new ApiResult<OutboxPageDto>(true, null,
                new OutboxPageDto(items, (int?)counts?.PendingCount ?? 0, totalCount, provider,
                                  (int?)counts?.RecentFailedCount ?? 0,
                                  (int?)counts?.RecentSentCount ?? 0)));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/opened   — the link was opened
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("opened")]
        public ActionResult<ApiResult<bool>> MarkOpened([FromBody] OutboxUpdateRequest request)
            => SetStatus(request?.Id ?? 0, WhatsAppOutboxStatus.Opened);

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/sent     — the operator confirms delivery
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("sent")]
        public ActionResult<ApiResult<bool>> MarkSent([FromBody] OutboxUpdateRequest request)
            => SetStatus(request?.Id ?? 0, WhatsAppOutboxStatus.Sent);

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/cancel   — not needed after all
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("cancel")]
        public ActionResult<ApiResult<bool>> Cancel([FromBody] OutboxUpdateRequest request)
            => SetStatus(request?.Id ?? 0, WhatsAppOutboxStatus.Cancelled);

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/bulk-sent | bulk-cancel
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("bulk-sent")]
        public ActionResult<ApiResult<int>> BulkSent([FromBody] OutboxBulkRequest request)
            => SetStatusMany(request?.Ids, WhatsAppOutboxStatus.Sent);

        [HttpPost("bulk-cancel")]
        public ActionResult<ApiResult<int>> BulkCancel([FromBody] OutboxBulkRequest request)
            => SetStatusMany(request?.Ids, WhatsAppOutboxStatus.Cancelled);

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/clear-pending
        // Empties the queue after a switch back to an automatic provider, so
        // stale messages never surprise someone weeks later.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("clear-pending")]
        public ActionResult<ApiResult<int>> ClearPending()
        {
            using var conn = sqlConnections.NewByKey("Default");
            var affected = SqlMapper.Execute(conn, @"
                UPDATE dbo.WHATSAPP_OUTBOX
                SET Status = 'Cancelled', HandledAt = SYSUTCDATETIME(), HandledBy = @UserId
                WHERE Status IN ('Pending','Opened')",
                new { UserId = CurrentUserId() });

            return Ok(new ApiResult<int>(true, null, affected));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/outbox/retry
        // Pushes a failed row through whichever provider is active now.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("retry")]
        public async Task<ActionResult<ApiResult<WhatsAppSendResult>>> Retry(
            [FromBody] OutboxUpdateRequest request)
        {
            if (request == null || request.Id <= 0)
                return Ok(new ApiResult<WhatsAppSendResult>(false, "Which message should be retried?", null));

            using var conn = sqlConnections.NewByKey("Default");

            var row = SqlMapper.Query<OutboxItemDto>(conn, @"
                SELECT TOP 1 Id, Provider, Status, Phone, MessageText, WaLink, MessageType,
                       ReferenceId, CustomerId, CustomerName, Lang, ErrorText,
                       CreatedAt, OpenedAt, HandledAt
                FROM dbo.WHATSAPP_OUTBOX WHERE Id = @Id",
                new { Id = request.Id }).FirstOrDefault();

            if (row == null)
                return Ok(new ApiResult<WhatsAppSendResult>(false, "That message is no longer in the log", null));

            var result = await sender.SendAsync(conn, row.Phone, row.MessageText,
                new WhatsAppContext(
                    MessageType: row.MessageType,
                    ReferenceId: row.ReferenceId,
                    CustomerId: row.CustomerId,
                    CustomerName: row.CustomerName,
                    Lang: row.Lang,
                    UserId: CurrentUserId()));

            if (result.Sent)
                SqlMapper.Execute(conn, @"
                    UPDATE dbo.WHATSAPP_OUTBOX
                    SET Status = 'Cancelled', HandledAt = SYSUTCDATETIME(), HandledBy = @UserId,
                        ErrorText = 'Retried — replaced by a newer attempt'
                    WHERE Id = @Id AND Status = 'Failed'",
                    new { Id = request.Id, UserId = CurrentUserId() });

            return Ok(new ApiResult<WhatsAppSendResult>(true, null, result));
        }

        #region Helpers

        /// <summary>
        /// Every row is written with SYSUTCDATETIME(), but SQL hands datetime2 back
        /// as Kind=Unspecified — and Newtonsoft then serialises it with no timezone
        /// marker at all. The browser reads a bare timestamp as local time, so in
        /// Cairo a message sent three minutes ago reads as three hours ago.
        /// Stamping the Kind is what makes the clock tell the truth.
        /// </summary>
        private static OutboxItemDto AsUtc(OutboxItemDto d) => d with
        {
            CreatedAt = DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc),
            OpenedAt = d.OpenedAt.HasValue
                ? DateTime.SpecifyKind(d.OpenedAt.Value, DateTimeKind.Utc) : null,
            HandledAt = d.HandledAt.HasValue
                ? DateTime.SpecifyKind(d.HandledAt.Value, DateTimeKind.Utc) : null
        };

        private ActionResult<ApiResult<bool>> SetStatus(long id, string status)
        {
            if (id <= 0)
                return Ok(new ApiResult<bool>(false, "Which message?", false));

            using var conn = sqlConnections.NewByKey("Default");

            var sql = status == WhatsAppOutboxStatus.Opened
                ? @"UPDATE dbo.WHATSAPP_OUTBOX
                    SET Status = 'Opened', OpenedAt = SYSUTCDATETIME()
                    WHERE Id = @Id AND Status = 'Pending'"
                : @"UPDATE dbo.WHATSAPP_OUTBOX
                    SET Status = @Status, HandledAt = SYSUTCDATETIME(), HandledBy = @UserId
                    WHERE Id = @Id AND Status IN ('Pending','Opened')";

            var affected = SqlMapper.Execute(conn, sql,
                new { Id = id, Status = status, UserId = CurrentUserId() });

            return Ok(new ApiResult<bool>(true, null, affected > 0));
        }

        private ActionResult<ApiResult<int>> SetStatusMany(List<long>? ids, string status)
        {
            if (ids == null || ids.Count == 0)
                return Ok(new ApiResult<int>(false, "Nothing selected", 0));

            using var conn = sqlConnections.NewByKey("Default");

            var affected = SqlMapper.Execute(conn, @"
                UPDATE dbo.WHATSAPP_OUTBOX
                SET Status = @Status, HandledAt = SYSUTCDATETIME(), HandledBy = @UserId
                WHERE Id IN @Ids AND Status IN ('Pending','Opened')",
                new { Ids = ids, Status = status, UserId = CurrentUserId() });

            return Ok(new ApiResult<int>(true, null, affected));
        }

        #endregion
    }
}
