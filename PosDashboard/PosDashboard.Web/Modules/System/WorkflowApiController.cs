// Modules/System/Controllers/WorkflowApiController.cs
//
// Order (Invoice) Workflow — api/workflow
//
//   GET    /api/workflow/config                       → bootstrap (flags, statuses, drivers, …)
//   GET    /api/workflow/orders                       → the table: filtered, sorted, paged
//   GET    /api/workflow/board                        → the board: one capped slice per stage
//   GET    /api/workflow/order/{invoiceId}            → header + merged timeline + legal moves
//   POST   /api/workflow/order/{invoiceId}/transition → move one order along
//   POST   /api/workflow/orders/transition            → move a selection along
//   POST   /api/workflow/order/{invoiceId}/driver     → assign / change the driver
//   POST   /api/workflow/order/{invoiceId}/comment    → comment (+ attachments) at any stage
//   DELETE /api/workflow/comment/{commentId}          → soft-delete a comment
//   POST   /api/workflow/upload                       → stage a file, returns an attachment id
//   DELETE /api/workflow/attachment/{attachmentId}    → soft-delete an attachment
//
// Design notes
// ------------
// • WORKFLOW STATE AND PAYMENT STATE ARE INDEPENDENT. Nothing here writes to
//   PaymentStatus / PaidAmount / RemainingAmount. Completing an unpaid order is
//   legal and leaves the debt exactly where /orders already tracks it; the only
//   guard is that the caller has to say AllowUnpaid so it can never happen by
//   accident. A wallet-paid order has RemainingAmount = 0 already, so it sails
//   through with no payment prompt at all — which is the behaviour asked for.
//
// • The driver is NOT stored twice. Assigning one writes the same two places the
//   POS writes (AppointmentInvoices.DeliveryDriverId + dbo.InvoiceDelivery), so
//   the /orders driver filter and the driver hand-in sheet keep working with no
//   changes at all.
//
// • Legality lives on the server. The client renders whatever /order/{id}
//   returns in `Allowed`; it never computes a transition itself. That way the
//   board, the table, the dialog and any future mobile client cannot disagree
//   about what is possible.
//
// • Every transition is appended to dbo.InvoiceWorkflowEvents with the money
//   state frozen in, which is what makes "it was completed unpaid on Tuesday by
//   Ahmed" answerable months later.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PosDashboard.Web.Modules.System.Models;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/workflow")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WorkflowApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public WorkflowApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        // ---- Settings keys -------------------------------------------------
        private const string KeyEnabled = "workflow.enabled";
        private const string KeyRequireDriver = "workflow.requireDriver";
        private const string KeyAllowSkip = "workflow.allowSkipStages";
        private const string KeyPromptPayment = "workflow.promptPaymentOnComplete";
        private const string KeyStaleMinutes = "workflow.stalePendingMinutes";

        // ---- Upload limits -------------------------------------------------
        private const long MaxAttachmentBytes = 10 * 1024 * 1024;   // 10 MB
        private const int MaxBoardColumnItems = 50;

        private static readonly string[] AllowedContentTypes =
        {
            "image/jpeg", "image/png", "image/webp", "image/gif", "image/heic",
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "text/plain", "text/csv"
        };

        // =====================================================================
        // GET /api/workflow/config
        // =====================================================================
        [HttpGet("config")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowConfigDto>> Config(
            [FromQuery] int? branchId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                branchId ??= ResolveUserBranchId(conn);

                var settings = LoadSettings(conn, branchId);
                var branch = LoadBranch(conn, ref branchId);
                int tz = BusinessSettingsService.GetTimeZoneOffset(conn);

                var drivers = SqlMapper.Query<DeliveryDtos.DeliveryDriverDto>(conn, @"
                    SELECT
                        d.DRIVER_ID      AS DriverId,
                        d.DRIVER_NAME    AS DriverName,
                        d.DRIVER_NAME_AR AS DriverNameAr,
                        d.DRIVER_PHONE   AS DriverPhone,
                        d.DRIVER_ADRESS  AS DriverAddress,
                        d.BRANCH_ID      AS BranchId,
                        d.GOVERNORATE_ID AS GovernorateId,
                        ISNULL(g.GOVERNORATE_NAME1, '') AS GovernorateNameEn,
                        ISNULL(g.GOVERNORATE_NAME2, '') AS GovernorateNameAr,
                        CAST(CASE WHEN ISNULL(d.IS_ACTIVE, 1) = 1 THEN 1 ELSE 0 END AS BIT) AS IsActive
                    FROM dbo.DRIVER d
                    LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                    WHERE ISNULL(d.IS_ACTIVE, 1) = 1
                      AND (d.BRANCH_ID IS NULL OR @BranchId IS NULL OR d.BRANCH_ID = @BranchId)
                    ORDER BY d.DRIVER_NAME", new { BranchId = branchId }).ToList();

                // INVOICE_PAYMENT_TYPE has no IsWallet flag — the POS decides by
                // name, so this does the same rather than inventing a second
                // source of truth for "is this the wallet".
                var paymentTypes = new List<PosDtos.PosPaymentTypeDto>();
                var paymentTypeRows = SqlMapper.Query(conn, @"
                    SELECT
                        INVOICE_PAYMENT_TYPE_ID    AS PaymentTypeId,
                        INVOICE_PAYMENT_TYPE_NAME1 AS NameEn,
                        INVOICE_PAYMENT_TYPE_NAME2 AS NameAr,
                        OnlinePayment
                    FROM dbo.INVOICE_PAYMENT_TYPE
                    ORDER BY INVOICE_PAYMENT_TYPE_ID");

                foreach (var p in paymentTypeRows)
                {
                    string en = (string?)p.NameEn ?? "";
                    string ar = (string?)p.NameAr ?? en;
                    bool isWallet = en.ToLowerInvariant().Contains("wallet") || ar.Contains("محفظة");

                    paymentTypes.Add(new PosDtos.PosPaymentTypeDto(
                        PaymentTypeId: (int)p.PaymentTypeId,
                        NameEn: en,
                        NameAr: ar,
                        IsWallet: isWallet,
                        OnlinePayment: (bool?)p.OnlinePayment ?? false));
                }

                var governorates = SqlMapper.Query<DeliveryDtos.GovernorateOptionDto>(conn, @"
                    SELECT
                        GOVERNORATE_ID    AS GovernorateId,
                        GOVERNORATE_NAME1 AS NameEn,
                        GOVERNORATE_NAME2 AS NameAr,
                        COLOR_CODE        AS ColorCode
                    FROM dbo.GOVERNORATE
                    ORDER BY GOVERNORATE_NAME1").ToList();

                var areas = SqlMapper.Query<DeliveryDtos.AreaOptionDto>(conn, @"
                    SELECT
                        a.AREA_ID           AS AreaId,
                        a.AREA_NAME1        AS NameEn,
                        a.AREA_NAME2        AS NameAr,
                        a.GOVERNORATE_ID    AS GovernorateId,
                        g.GOVERNORATE_NAME1 AS GovernorateNameEn,
                        g.GOVERNORATE_NAME2 AS GovernorateNameAr,
                        ISNULL(adc.Charge, 0) AS Charge,
                        CAST(CASE WHEN adc.Id IS NULL THEN 0 ELSE 1 END AS BIT) AS HasCharge
                    FROM dbo.GOVERNORATE_AREA a
                    INNER JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = a.GOVERNORATE_ID
                    LEFT  JOIN dbo.AreaDeliveryCharge adc
                           ON adc.AreaId = a.AREA_ID AND adc.BranchId = @BranchId
                    ORDER BY g.GOVERNORATE_NAME1, a.AREA_NAME1",
                    new { BranchId = branchId }).ToList();

                var debtSettings = new DebtDtos.DebtSettingsDto(
                    Enabled: BusinessSettingsService.GetBool(conn, BusinessSettingsService.KeyDebtEnabled, false, branchId),
                    AllowSettlementDiscount: BusinessSettingsService.GetBool(conn, BusinessSettingsService.KeyDebtAllowSettlementDiscount, false, branchId),
                    CustomerLimit: BusinessSettingsService.GetDecimal(conn, BusinessSettingsService.KeyDebtCustomerLimit, 0m, branchId));

                // Board/tab badges want the counts for the branch as a whole, with
                // no user filter applied — the filtered counts come back with the
                // list itself.
                var counts = LoadStatusCounts(conn, "1 = 1 AND (@BranchId IS NULL OR inv.BranchId = @BranchId)",
                    Params(("BranchId", branchId)));

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowConfigDto>(true, null,
                    new WorkflowDtos.WorkflowConfigDto(
                        Settings: settings,
                        Branch: branch,
                        Statuses: BuildStatusCatalog(settings.DeliveryEnabled),
                        Drivers: drivers,
                        PaymentTypes: paymentTypes,
                        Areas: areas,
                        Governorates: governorates,
                        DebtSettings: debtSettings,
                        TzOffset: tz,
                        Counts: counts)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowConfigDto>(
                    false, $"Failed to load workflow config: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/workflow/orders  — the table view
        // =====================================================================
        [HttpGet("orders")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowListDto>> Orders(
            [FromQuery] string? status = null,          // one stage, or 'active' / 'all'
            [FromQuery] int? branchId = null,
            [FromQuery] string? search = null,
            [FromQuery] int? customerId = null,
            [FromQuery] int? driverId = null,
            [FromQuery] int? areaId = null,
            [FromQuery] int? governorateId = null,
            [FromQuery] string? orderType = null,       // delivery | pickup
            [FromQuery] string? paymentState = null,    // paid | unpaid | wallet | completedUnpaid
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] string? dateField = "created",  // created | completed | paid | delivery
            [FromQuery] bool onlyStale = false,
            [FromQuery] string? sortBy = "stage",
            [FromQuery] string? sortDir = "desc",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                branchId ??= ResolveUserBranchId(conn);
                int tz = BusinessSettingsService.GetTimeZoneOffset(conn);
                var settings = LoadSettings(conn, branchId);

                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 200) pageSize = 25;

                var (where, prm) = BuildWhere(
                    status, branchId, search, customerId, driverId, areaId, governorateId,
                    orderType, paymentState, dateFrom, dateTo, dateField, onlyStale,
                    settings.StalePendingMinutes, tz);

                var summary = LoadSummary(conn, where, prm, branchId, settings.StalePendingMinutes);

                int total = summary.OrderCount;
                int totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
                if (page > totalPages) page = totalPages;

                var items = QueryPage(conn, where, prm, sortBy, sortDir, page, pageSize,
                    settings.StalePendingMinutes);

                items = EnrichPage(conn, items);

                var paged = new WorkflowDtos.PagedResult<WorkflowDtos.WorkflowOrderDto>(
                    items, total, page, pageSize, totalPages);

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowListDto>(true, null,
                    new WorkflowDtos.WorkflowListDto(paged, summary, tz, status)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowListDto>(
                    false, $"Failed to load orders: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/workflow/board  — the stage board
        // ---------------------------------------------------------------------
        // Same filter as the table, but sliced per stage and capped, because a
        // column is something you work through, not something you scroll for
        // ten minutes. TotalCount is the truth; Items is the first page of it.
        // =====================================================================
        [HttpGet("board")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowBoardDto>> Board(
            [FromQuery] int? branchId = null,
            [FromQuery] string? search = null,
            [FromQuery] int? customerId = null,
            [FromQuery] int? driverId = null,
            [FromQuery] int? areaId = null,
            [FromQuery] int? governorateId = null,
            [FromQuery] string? orderType = null,
            [FromQuery] string? paymentState = null,
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] string? dateField = "created",
            [FromQuery] bool onlyStale = false,
            [FromQuery] int columnSize = 20)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                branchId ??= ResolveUserBranchId(conn);
                int tz = BusinessSettingsService.GetTimeZoneOffset(conn);
                var settings = LoadSettings(conn, branchId);

                if (columnSize < 5 || columnSize > MaxBoardColumnItems) columnSize = 20;

                var (where, prm) = BuildWhere(
                    null, branchId, search, customerId, driverId, areaId, governorateId,
                    orderType, paymentState, dateFrom, dateTo, dateField, onlyStale,
                    settings.StalePendingMinutes, tz);

                var summary = LoadSummary(conn, where, prm, branchId, settings.StalePendingMinutes);

                // Cancelled is history, not a lane you push work through, so the
                // board stops at Completed. The table view still reaches it.
                var lanes = new List<string>
                {
                    WorkflowDtos.Status.Pending,
                    WorkflowDtos.Status.Processing,
                    WorkflowDtos.Status.Ready
                };
                if (settings.DeliveryEnabled)
                {
                    lanes.Add(WorkflowDtos.Status.OutForDelivery);
                    lanes.Add(WorkflowDtos.Status.Delivered);
                }
                lanes.Add(WorkflowDtos.Status.Completed);

                var columns = new List<WorkflowDtos.WorkflowBoardColumnDto>();
                foreach (var lane in lanes)
                {
                    var laneWhere = $"({where}) AND inv.WorkflowStatus = @LaneStatus";
                    var laneParams = Clone(prm);
                    laneParams.Add("LaneStatus", lane);

                    summary.CountByStatus.TryGetValue(lane, out int laneCount);
                    summary.ValueByStatus.TryGetValue(lane, out decimal laneValue);

                    var rows = laneCount == 0
                        ? new List<WorkflowDtos.WorkflowOrderDto>()
                        : EnrichPage(conn, QueryPage(conn, laneWhere, laneParams,
                            "stage", "asc", 1, columnSize, settings.StalePendingMinutes));

                    columns.Add(new WorkflowDtos.WorkflowBoardColumnDto(
                        Status: lane,
                        TotalCount: laneCount,
                        TotalValue: laneValue,
                        Items: rows,
                        HasMore: laneCount > rows.Count));
                }

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowBoardDto>(true, null,
                    new WorkflowDtos.WorkflowBoardDto(columns, summary, tz)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowBoardDto>(
                    false, $"Failed to load board: {ex.Message}", null));
            }
        }

        // =====================================================================
        // GET /api/workflow/order/{invoiceId}
        // =====================================================================
        [HttpGet("order/{invoiceId:int}")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowDetailDto>> Detail(int invoiceId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int tz = BusinessSettingsService.GetTimeZoneOffset(conn);
                var order = LoadOrder(conn, invoiceId);
                if (order == null)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowDetailDto>(
                        false, "Order not found", null));

                var settings = LoadSettings(conn, order.BranchId);

                var timeline = LoadTimeline(conn, invoiceId);
                var allowed = BuildAllowed(order, settings);
                var lines = LoadLines(conn, invoiceId);

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowDetailDto>(true, null,
                    new WorkflowDtos.WorkflowDetailDto(order, timeline, allowed, lines, tz)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowDetailDto>(
                    false, $"Failed to load order: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/workflow/order/{invoiceId}/transition
        // =====================================================================
        [HttpPost("order/{invoiceId:int}/transition")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.TransitionResultDto>> Transition(
            int invoiceId, [FromBody] WorkflowDtos.TransitionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ToStatus))
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.TransitionResultDto>(
                    false, "A target status is required", null));

            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var (result, error) = ApplyTransition(conn, invoiceId, request);
                if (error != null)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.TransitionResultDto>(false, error, null));

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.TransitionResultDto>(true, null, result));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.TransitionResultDto>(
                    false, $"Failed to update the order: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/workflow/orders/transition  — a whole selection at once
        // ---------------------------------------------------------------------
        // Per-order, not all-or-nothing. Dispatching 30 orders to one driver and
        // rolling all of them back because #17 has no address is worse than
        // moving 29 and naming the one that did not go.
        // =====================================================================
        [HttpPost("orders/transition")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.BulkTransitionResultDto>> BulkTransition(
            [FromBody] WorkflowDtos.BulkTransitionRequest request)
        {
            if (request?.InvoiceIds == null || request.InvoiceIds.Count == 0)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.BulkTransitionResultDto>(
                    false, "Select at least one order", null));

            if (request.InvoiceIds.Count > 200)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.BulkTransitionResultDto>(
                    false, "Up to 200 orders can be moved at once", null));

            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int ok = 0;
                var failures = new List<WorkflowDtos.BulkTransitionFailureDto>();

                foreach (var id in request.InvoiceIds.Distinct())
                {
                    var single = new WorkflowDtos.TransitionRequest(
                        ToStatus: request.ToStatus,
                        DriverId: request.DriverId,
                        Note: request.Note,
                        AttachmentIds: null,
                        ConfirmSkip: request.ConfirmSkip,
                        AllowUnpaid: request.AllowUnpaid);

                    var (res, err) = ApplyTransition(conn, id, single);
                    if (err == null && res != null) ok++;
                    else failures.Add(new WorkflowDtos.BulkTransitionFailureDto(
                        id, res?.InvoiceNumber, err ?? "Unknown error"));
                }

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.BulkTransitionResultDto>(true, null,
                    new WorkflowDtos.BulkTransitionResultDto(ok, failures.Count, failures)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.BulkTransitionResultDto>(
                    false, $"Failed to move the selected orders: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/workflow/order/{invoiceId}/driver
        // =====================================================================
        [HttpPost("order/{invoiceId:int}/driver")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>> AssignDriver(
            int invoiceId, [FromBody] WorkflowDtos.AssignDriverRequest request)
        {
            if (request == null || request.DriverId <= 0)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(
                    false, "Pick a driver", null));

            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var order = LoadOrder(conn, invoiceId);
                if (order == null)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(
                        false, "Order not found", null));

                if (!order.IsDelivery)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(
                        false, "This is a pickup order — it has no driver", null));

                var driver = LoadDriver(conn, request.DriverId);
                if (driver == null)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(
                        false, "Driver not found or inactive", null));

                WriteDriver(conn, invoiceId, driver);

                // Reassignment is a real operational event, so it lands on the
                // timeline as a comment rather than changing silently.
                string who = ResolveUserName(conn);
                string text = $"Driver set to {driver.DriverName}";
                if (!string.IsNullOrWhiteSpace(request.Note))
                    text += $" — {request.Note!.Trim()}";

                InsertComment(conn, invoiceId, order.BranchId, null, order.WorkflowStatus,
                    text, ResolveCurrentUserId(), who, true);

                var fresh = LoadOrder(conn, invoiceId)!;
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(true, null, fresh));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowOrderDto>(
                    false, $"Failed to assign the driver: {ex.Message}", null));
            }
        }

        // =====================================================================
        // POST /api/workflow/order/{invoiceId}/comment
        // =====================================================================
        [HttpPost("order/{invoiceId:int}/comment")]
        public ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowCommentDto>> AddComment(
            int invoiceId, [FromBody] WorkflowDtos.AddCommentRequest request)
        {
            bool hasText = !string.IsNullOrWhiteSpace(request?.CommentText);
            bool hasFiles = request?.AttachmentIds is { Count: > 0 };

            // A blank comment with nothing attached is not a comment.
            if (!hasText && !hasFiles)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowCommentDto>(
                    false, "Write something or attach a file", null));

            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var order = LoadOrder(conn, invoiceId);
                if (order == null)
                    return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowCommentDto>(
                        false, "Order not found", null));

                int userId = ResolveCurrentUserId();
                string who = ResolveUserName(conn);

                int commentId = InsertComment(conn, invoiceId, order.BranchId, null,
                    order.WorkflowStatus,
                    hasText ? request!.CommentText!.Trim() : null,
                    userId, who, request?.IsInternal ?? true);

                LinkAttachments(conn, invoiceId, commentId, request?.AttachmentIds);

                var dto = LoadComment(conn, commentId);
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowCommentDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowCommentDto>(
                    false, $"Failed to add the comment: {ex.Message}", null));
            }
        }

        // =====================================================================
        // DELETE /api/workflow/comment/{commentId}
        // ---------------------------------------------------------------------
        // Soft delete. A transition note is part of the audit trail and stays;
        // only free comments can be withdrawn.
        // =====================================================================
        [HttpDelete("comment/{commentId:int}")]
        public ActionResult<WorkflowDtos.ApiResult<bool>> DeleteComment(int commentId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var row = SqlMapper.Query(conn, @"
                    SELECT Id, EventId, IsDeleted
                    FROM dbo.InvoiceWorkflowComments WHERE Id = @Id",
                    new { Id = commentId }).FirstOrDefault();

                if (row == null)
                    return Ok(new WorkflowDtos.ApiResult<bool>(false, "Comment not found", false));

                if (row.EventId != null)
                    return Ok(new WorkflowDtos.ApiResult<bool>(
                        false, "A note recorded with a status change cannot be removed", false));

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.InvoiceWorkflowComments
                       SET IsDeleted = 1, DeletedAt = SYSUTCDATETIME(), DeletedByUserId = @UserId
                     WHERE Id = @Id AND IsDeleted = 0",
                    new { Id = commentId, UserId = NullIfZero(ResolveCurrentUserId()) });

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.InvoiceWorkflowAttachments
                       SET IsDeleted = 1 WHERE CommentId = @Id",
                    new { Id = commentId });

                return Ok(new WorkflowDtos.ApiResult<bool>(true, null, true));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<bool>(
                    false, $"Failed to remove the comment: {ex.Message}", false));
            }
        }

        // =====================================================================
        // POST /api/workflow/upload  (multipart)
        // ---------------------------------------------------------------------
        // Files are uploaded FIRST and attached afterwards, so the composer can
        // show a thumbnail and an upload error before the user commits to the
        // comment. An id that is never linked is simply an orphan row — cheap,
        // and safe to sweep on a schedule.
        // =====================================================================
        [HttpPost("upload")]
        [RequestSizeLimit(MaxAttachmentBytes + (1024 * 512))]
        public async Task<ActionResult<WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>>> Upload(
            [FromForm] IFormFile file, [FromForm] int invoiceId)
        {
            if (file == null || file.Length == 0)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>(
                    false, "No file received", null));

            if (file.Length > MaxAttachmentBytes)
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>(
                    false, "The file is larger than 10 MB", null));

            string contentType = (file.ContentType ?? "").ToLowerInvariant();
            if (!AllowedContentTypes.Contains(contentType))
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>(
                    false, "That file type is not accepted. Use an image, a PDF, an Office file or a text file.", null));

            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "workflow");
                Directory.CreateDirectory(folder);

                // The original name is kept for display only; what lands on disk
                // is a GUID, so a hostile filename can never escape the folder.
                var ext = Path.GetExtension(file.FileName ?? "");
                if (ext.Length > 10) ext = "";
                var stored = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";

                await using (var stream = new FileStream(Path.Combine(folder, stored), FileMode.Create))
                    await file.CopyToAsync(stream);

                string url = $"/uploads/workflow/{stored}";
                bool isImage = contentType.StartsWith("image/", StringComparison.Ordinal);
                string who = ResolveUserName(conn);

                int id = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.InvoiceWorkflowAttachments
                        (InvoiceId, CommentId, FileName, FileUrl, ContentType, FileSize,
                         IsImage, UserId, UserName, CreatedAt, IsDeleted)
                    OUTPUT INSERTED.Id
                    VALUES (@InvoiceId, NULL, @FileName, @FileUrl, @ContentType, @FileSize,
                            @IsImage, @UserId, @UserName, SYSUTCDATETIME(), 0)",
                    new
                    {
                        InvoiceId = invoiceId,
                        FileName = Truncate(Path.GetFileName(file.FileName ?? stored), 300),
                        FileUrl = url,
                        ContentType = contentType,
                        FileSize = file.Length,
                        IsImage = isImage,
                        UserId = NullIfZero(ResolveCurrentUserId()),
                        UserName = who
                    }).First();

                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>(true, null,
                    new WorkflowDtos.WorkflowAttachmentDto(
                        id, invoiceId, null,
                        Truncate(Path.GetFileName(file.FileName ?? stored), 300),
                        url, contentType, file.Length, isImage, who, DateTime.UtcNow)));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<WorkflowDtos.WorkflowAttachmentDto>(
                    false, $"Upload failed: {ex.Message}", null));
            }
        }

        // =====================================================================
        // DELETE /api/workflow/attachment/{attachmentId}
        // =====================================================================
        [HttpDelete("attachment/{attachmentId:int}")]
        public ActionResult<WorkflowDtos.ApiResult<bool>> DeleteAttachment(int attachmentId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                SqlMapper.Execute(conn, @"
                    UPDATE dbo.InvoiceWorkflowAttachments
                       SET IsDeleted = 1 WHERE Id = @Id",
                    new { Id = attachmentId });

                return Ok(new WorkflowDtos.ApiResult<bool>(true, null, true));
            }
            catch (Exception ex)
            {
                return Ok(new WorkflowDtos.ApiResult<bool>(false, $"Failed to remove the file: {ex.Message}", false));
            }
        }

        // #####################################################################
        // ##  The state machine
        // #####################################################################

        /// The one place that decides what may follow what.
        private static List<string> NextStatuses(string current, bool isDelivery)
        {
            return current switch
            {
                WorkflowDtos.Status.Pending => new List<string>
                    { WorkflowDtos.Status.Processing, WorkflowDtos.Status.Cancelled },

                WorkflowDtos.Status.Processing => new List<string>
                    { WorkflowDtos.Status.Ready, WorkflowDtos.Status.Cancelled },

                // The fork. A delivery order goes out with a driver; a pickup
                // order is finished the moment the customer takes it.
                WorkflowDtos.Status.Ready => isDelivery
                    ? new List<string> { WorkflowDtos.Status.OutForDelivery, WorkflowDtos.Status.Cancelled }
                    : new List<string> { WorkflowDtos.Status.Completed, WorkflowDtos.Status.Cancelled },

                WorkflowDtos.Status.OutForDelivery => new List<string>
                    { WorkflowDtos.Status.Delivered, WorkflowDtos.Status.Ready, WorkflowDtos.Status.Cancelled },

                WorkflowDtos.Status.Delivered => new List<string>
                    { WorkflowDtos.Status.Completed },

                _ => new List<string>()   // Completed and Cancelled are the end
            };
        }

        /// The single obvious next step, or null where the user has to choose.
        private static string? PrimaryNext(string current, bool isDelivery)
        {
            var next = NextStatuses(current, isDelivery)
                .Where(s => s != WorkflowDtos.Status.Cancelled &&
                            WorkflowDtos.Status.Rank(s) > WorkflowDtos.Status.Rank(current))
                .ToList();
            return next.Count == 1 ? next[0] : next.FirstOrDefault();
        }

        private static List<WorkflowDtos.WorkflowTransitionOptionDto> BuildAllowed(
            WorkflowDtos.WorkflowOrderDto o, WorkflowDtos.WorkflowSettingsDto s)
        {
            var list = new List<WorkflowDtos.WorkflowTransitionOptionDto>();
            var direct = NextStatuses(o.WorkflowStatus, o.IsDelivery);

            foreach (var to in direct)
                list.Add(Option(to, o, s, isSkip: false));

            // Skipping is a supervisor's escape hatch for the day the kitchen
            // forgot to press the buttons. Off by default; still logged, still
            // one event per stage crossed.
            if (s.AllowSkipStages && !WorkflowDtos.Status.Terminal.Contains(o.WorkflowStatus))
            {
                foreach (var to in ForwardStages(o.IsDelivery, s.DeliveryEnabled))
                {
                    if (WorkflowDtos.Status.Rank(to) <= WorkflowDtos.Status.Rank(o.WorkflowStatus)) continue;
                    if (direct.Contains(to)) continue;
                    list.Add(Option(to, o, s, isSkip: true));
                }
            }

            return list;
        }

        private static IEnumerable<string> ForwardStages(bool isDelivery, bool deliveryEnabled)
        {
            yield return WorkflowDtos.Status.Processing;
            yield return WorkflowDtos.Status.Ready;
            if (isDelivery && deliveryEnabled)
            {
                yield return WorkflowDtos.Status.OutForDelivery;
                yield return WorkflowDtos.Status.Delivered;
            }
            yield return WorkflowDtos.Status.Completed;
        }

        private static WorkflowDtos.WorkflowTransitionOptionDto Option(
            string to, WorkflowDtos.WorkflowOrderDto o, WorkflowDtos.WorkflowSettingsDto s, bool isSkip)
        {
            var meta = StatusMeta(to);

            bool requiresDriver =
                s.RequireDriver && o.IsDelivery && o.DriverId == null &&
                (to == WorkflowDtos.Status.OutForDelivery ||
                 (isSkip && WorkflowDtos.Status.Rank(to) >= WorkflowDtos.Status.Rank(WorkflowDtos.Status.OutForDelivery)));

            // Only completion is a money moment. Everything before it is the
            // shop's own progress and has nothing to do with the customer's wallet.
            bool suggestsPayment =
                to == WorkflowDtos.Status.Completed && !o.IsPaid && s.PromptPaymentOnComplete;

            return new WorkflowDtos.WorkflowTransitionOptionDto(
                ToStatus: to,
                NameEn: ActionLabelEn(to),
                NameAr: ActionLabelAr(to),
                Icon: meta.Icon,
                RequiresDriver: requiresDriver,
                SuggestsPayment: suggestsPayment,
                IsSkip: isSkip,
                IsDestructive: to == WorkflowDtos.Status.Cancelled);
        }

        // ---------------------------------------------------------------------
        // The write path
        // ---------------------------------------------------------------------
        private (WorkflowDtos.TransitionResultDto? Result, string? Error) ApplyTransition(
            IDbConnection conn, int invoiceId, WorkflowDtos.TransitionRequest request)
        {
            var order = LoadOrder(conn, invoiceId);
            if (order == null) return (null, "Order not found");

            var settings = LoadSettings(conn, order.BranchId);
            if (!settings.Enabled) return (null, "The order workflow is switched off");

            string to = request.ToStatus.Trim();
            if (!WorkflowDtos.Status.All.Contains(to))
                return (null, $"'{to}' is not a valid status");

            string from = order.WorkflowStatus;
            if (from == to)
                return (null, $"The order is already {StatusMeta(to).NameEn}");

            if (order.IsVoid)
                return (null, "This invoice was voided and can no longer be moved");

            if (WorkflowDtos.Status.Terminal.Contains(from))
                return (null, $"{order.InvoiceNumber} is already {StatusMeta(from).NameEn.ToLowerInvariant()}");

            // A pickup order can never be out for delivery or delivered.
            if (!order.IsDelivery && WorkflowDtos.Status.DeliveryOnly.Contains(to))
                return (null, "This is a pickup order — it has no delivery stage");

            var allowed = BuildAllowed(order, settings);
            var option = allowed.FirstOrDefault(a => a.ToStatus == to);
            if (option == null)
                return (null, $"An order cannot move from {StatusMeta(from).NameEn} to {StatusMeta(to).NameEn}");

            if (option.IsSkip && !request.ConfirmSkip)
                return (null, "SKIP_CONFIRM_REQUIRED");

            // ---- Driver ------------------------------------------------------
            DeliveryDtos.DeliveryDriverDto? driver = null;
            if (request.DriverId is > 0)
            {
                driver = LoadDriver(conn, request.DriverId.Value);
                if (driver == null) return (null, "Driver not found or inactive");
                if (!order.IsDelivery) return (null, "A pickup order cannot be given a driver");
            }

            bool willHaveDriver = driver != null || order.DriverId != null;
            if (option.RequiresDriver && !willHaveDriver)
                return (null, "DRIVER_REQUIRED");

            // ---- Money -------------------------------------------------------
            // The only guard on the whole flow: finishing an order that still
            // owes money has to be a decision, not a slip. A wallet-paid order
            // has RemainingAmount = 0, so it never reaches this line.
            bool completedUnpaid = false;
            if (to == WorkflowDtos.Status.Completed && !order.IsPaid)
            {
                if (!request.AllowUnpaid) return (null, "UNPAID_CONFIRM_REQUIRED");
                completedUnpaid = true;
            }

            var now = DateTime.UtcNow;
            int? secondsInPrevious = order.WorkflowStatusAt.HasValue
                ? (int)Math.Max(0, (now - order.WorkflowStatusAt.Value).TotalSeconds)
                : null;

            using (var uow = new UnitOfWork(conn))
            {
                if (driver != null) WriteDriver(uow.Connection, invoiceId, driver);

                // Cancelling wipes the forward stamps it never reached; the event
                // log keeps the history, and a half-filled stamp set would make
                // "average time to ready" quietly wrong.
                string stampColumn = StampColumn(to);
                var sql = new StringBuilder(@"
                    UPDATE dbo.AppointmentInvoices
                       SET WorkflowStatus   = @To,
                           WorkflowStatusAt = @Now");

                if (stampColumn != null)
                    sql.Append($", {stampColumn} = ISNULL({stampColumn}, @Now)");

                if (to == WorkflowDtos.Status.Cancelled)
                    sql.Append(", WorkflowCancelReason = @Reason");

                sql.Append(" WHERE Id = @Id");

                SqlMapper.Execute(uow.Connection, sql.ToString(), new
                {
                    To = to,
                    Now = now,
                    Id = invoiceId,
                    Reason = Truncate(request.Note?.Trim(), 400)
                });

                int userId = ResolveCurrentUserId();
                string who = ResolveUserName(uow.Connection);

                int eventId = SqlMapper.Query<int>(uow.Connection, @"
                    INSERT INTO dbo.InvoiceWorkflowEvents
                        (InvoiceId, InvoiceNumber, BranchId, FromStatus, ToStatus,
                         DriverId, DriverName, DriverNameAr,
                         RemainingAmount, WasPaid, Note, UserId, UserName,
                         CreatedAt, SecondsInPrevious)
                    OUTPUT INSERTED.Id
                    VALUES (@InvoiceId, @InvoiceNumber, @BranchId, @From, @To,
                            @DriverId, @DriverName, @DriverNameAr,
                            @Remaining, @WasPaid, @Note, @UserId, @UserName,
                            @Now, @Seconds)",
                    new
                    {
                        InvoiceId = invoiceId,
                        InvoiceNumber = order.InvoiceNumber,
                        BranchId = order.BranchId,
                        From = from,
                        To = to,
                        DriverId = driver?.DriverId ?? order.DriverId,
                        DriverName = driver?.DriverName ?? order.DriverName,
                        DriverNameAr = driver?.DriverNameAr ?? order.DriverNameAr,
                        Remaining = order.RemainingAmount,
                        WasPaid = order.IsPaid,
                        Note = Truncate(request.Note?.Trim(), 1000),
                        UserId = NullIfZero(userId),
                        UserName = who,
                        Now = now,
                        Seconds = secondsInPrevious
                    }).First();

                // A note or a photo taken with the move belongs to the move, so
                // the timeline shows one entry rather than two unrelated ones.
                bool hasNote = !string.IsNullOrWhiteSpace(request.Note);
                bool hasFiles = request.AttachmentIds is { Count: > 0 };
                if (hasNote || hasFiles)
                {
                    int commentId = InsertComment(uow.Connection, invoiceId, order.BranchId,
                        eventId, to, hasNote ? request.Note!.Trim() : null, userId, who, true);
                    LinkAttachments(uow.Connection, invoiceId, commentId, request.AttachmentIds);
                }

                uow.Commit();
            }

            var fresh = LoadOrder(conn, invoiceId)!;

            return (new WorkflowDtos.TransitionResultDto(
                InvoiceId: invoiceId,
                InvoiceNumber: order.InvoiceNumber,
                FromStatus: from,
                ToStatus: to,
                At: now,
                RemainingAmount: fresh.RemainingAmount,
                IsPaid: fresh.IsPaid,
                CompletedUnpaid: completedUnpaid,
                Order: fresh), null);
        }

        private static string? StampColumn(string status) => status switch
        {
            WorkflowDtos.Status.Processing => "ProcessingAt",
            WorkflowDtos.Status.Ready => "ReadyAt",
            WorkflowDtos.Status.OutForDelivery => "OutForDeliveryAt",
            WorkflowDtos.Status.Delivered => "DeliveredAt",
            WorkflowDtos.Status.Completed => "CompletedAt",
            WorkflowDtos.Status.Cancelled => "CancelledAt",
            _ => null
        };

        /// Writes the driver where the rest of the system already looks for it.
        private static void WriteDriver(IDbConnection conn, int invoiceId,
            DeliveryDtos.DeliveryDriverDto driver)
        {
            SqlMapper.Execute(conn, @"
                UPDATE dbo.AppointmentInvoices
                   SET DeliveryDriverId = @DriverId
                 WHERE Id = @InvoiceId",
                new { DriverId = driver.DriverId, InvoiceId = invoiceId });

            // InvoiceDelivery holds the frozen snapshot the invoice was printed
            // from, so the name is copied in rather than joined at read time.
            SqlMapper.Execute(conn, @"
                UPDATE dbo.InvoiceDelivery
                   SET DriverId     = @DriverId,
                       DriverName   = @Name,
                       DriverNameAr = @NameAr,
                       DriverPhone  = @Phone
                 WHERE InvoiceId = @InvoiceId",
                new
                {
                    DriverId = driver.DriverId,
                    Name = driver.DriverName,
                    NameAr = driver.DriverNameAr,
                    Phone = driver.DriverPhone,
                    InvoiceId = invoiceId
                });
        }

        // #####################################################################
        // ##  Reads
        // #####################################################################

        private const string FromJoins = @"
            FROM dbo.AppointmentInvoices inv
            INNER JOIN dbo.CUSTOMER c          ON c.CUSTOMER_ID = inv.CustomerId
            LEFT  JOIN dbo.BRANCH   b          ON b.BRANCH_ID   = inv.BranchId
            LEFT  JOIN dbo.InvoiceDelivery idl ON idl.InvoiceId = inv.Id
            LEFT  JOIN dbo.DeliveryType dt     ON dt.Id         = inv.DeliveryTypeId
            LEFT  JOIN dbo.AppointmentData a   ON a.Id          = inv.AppointmentId";

        /// An order counts as a delivery when the frozen snapshot says so, and
        /// falls back to the delivery type only when there is no snapshot.
        private const string IsDeliveryExpr =
            "ISNULL(idl.IsDelivery, ISNULL(dt.IsDelivery, 0))";

        private const string DriverIdExpr = "ISNULL(idl.DriverId, inv.DeliveryDriverId)";

        /// Everything owing is settled — cash, card, or wallet alike.
        private const string IsPaidExpr = "CASE WHEN ISNULL(inv.RemainingAmount, 0) <= 0 THEN 1 ELSE 0 END";

        private static string SelectColumns => $@"
            inv.Id            AS InvoiceId,
            inv.InvoiceNumber AS InvoiceNumber,
            inv.AppointmentId AS LeadAppointmentId,
            inv.BranchId      AS BranchId,
            inv.CreatedAt     AS CreatedAt,

            inv.CustomerId    AS CustomerId,
            ISNULL(c.CUSTOMER_NAME, '')   AS CustomerName,
            ISNULL(c.CUSTOMER_PHONE1, '') AS CustomerPhone,
            c.CUSTOMER_PHONE2             AS CustomerPhone2,

            ISNULL(inv.SubTotal, inv.TotalAmount) AS SubTotal,
            ISNULL(inv.DiscountAmount, 0)         AS DiscountAmount,
            ISNULL(inv.DeliveryCharge, 0)         AS DeliveryCharge,
            ISNULL(inv.TotalAmount, 0)            AS TotalAmount,
            ISNULL(inv.PaidAmount, 0)             AS PaidAmount,
            ISNULL(inv.RemainingAmount, 0)        AS RemainingAmount,
            ISNULL(inv.Currency, b.EnglishCurrencyName) AS Currency,
            inv.PaymentStatus  AS PaymentStatus,
            ISNULL(inv.IsDeferred, 0) AS IsDeferred,
            inv.PaidAt         AS PaidAt,
            inv.SettledAt      AS SettledAt,
            {IsPaidExpr}       AS IsPaid,

            {IsDeliveryExpr}   AS IsDelivery,
            inv.DeliveryTypeId AS DeliveryTypeId,
            ISNULL(idl.DeliveryTypeNameEn, dt.NameEn) AS DeliveryTypeNameEn,
            ISNULL(idl.DeliveryTypeNameAr, dt.NameAr) AS DeliveryTypeNameAr,
            {DriverIdExpr}     AS DriverId,
            idl.DriverName, idl.DriverNameAr, idl.DriverPhone,
            idl.AreaId, idl.AreaNameEn, idl.AreaNameAr,
            idl.GovernorateId, idl.GovernorateNameEn, idl.GovernorateNameAr,
            LTRIM(RTRIM(
                ISNULL('Block ' + idl.AddressBlock + ', ', '') +
                ISNULL('St ' + idl.AddressStreet + ', ', '') +
                ISNULL('Bldg ' + idl.AddressBuilding + ', ', '') +
                ISNULL('Flat ' + idl.AddressFlat, '')
            )) AS AddressSummary,
            ISNULL(idl.DeliveryDate, inv.DeliveryDate) AS DeliveryDate,

            ISNULL(inv.WorkflowStatus, 'Pending') AS WorkflowStatus,
            inv.WorkflowStatusAt, inv.ProcessingAt, inv.ReadyAt,
            inv.OutForDeliveryAt, inv.DeliveredAt, inv.CompletedAt, inv.CancelledAt,
            inv.WorkflowCancelReason,
            DATEDIFF(minute, ISNULL(inv.WorkflowStatusAt, inv.CreatedAt), SYSUTCDATETIME()) AS MinutesInStage,

            (SELECT COUNT(*) FROM dbo.AppointmentInvoiceLines l
              WHERE l.InvoiceId = inv.Id AND ISNULL(l.IsRefunded, 0) = 0) AS ItemCount,
            a.Notes AS Notes,
            ISNULL(inv.IsVoid, 0) AS IsVoid,
            DATEDIFF(day, inv.CreatedAt, SYSUTCDATETIME()) AS AgeDays";

        private static (string Where, Dapper.DynamicParameters Params) BuildWhere(
            string? status, int? branchId, string? search, int? customerId, int? driverId,
            int? areaId, int? governorateId, string? orderType, string? paymentState,
            DateTime? dateFrom, DateTime? dateTo, string? dateField, bool onlyStale,
            int staleMinutes, int tzOffset)
        {
            var sb = new StringBuilder("ISNULL(inv.IsVoid, 0) = 0");
            sb.Append(" AND (@BranchId IS NULL OR inv.BranchId = @BranchId)");

            // ---- Stage --------------------------------------------------------
            var normalized = (status ?? "active").Trim();
            if (string.Equals(normalized, "active", StringComparison.OrdinalIgnoreCase))
            {
                // The default view: work that still needs someone to do something.
                sb.Append(" AND ISNULL(inv.WorkflowStatus, 'Pending') NOT IN ('Completed', 'Cancelled')");
            }
            else if (!string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" AND ISNULL(inv.WorkflowStatus, 'Pending') = @Status");
            }

            if (customerId.HasValue) sb.Append(" AND inv.CustomerId = @CustomerId");
            if (driverId.HasValue) sb.Append($" AND {DriverIdExpr} = @DriverId");
            if (areaId.HasValue) sb.Append(" AND idl.AreaId = @AreaId");
            if (governorateId.HasValue) sb.Append(" AND idl.GovernorateId = @GovernorateId");

            if (orderType == "delivery") sb.Append($" AND {IsDeliveryExpr} = 1");
            else if (orderType == "pickup") sb.Append($" AND {IsDeliveryExpr} = 0");

            // ---- Payment ------------------------------------------------------
            switch ((paymentState ?? "").Trim().ToLowerInvariant())
            {
                case "paid":
                    sb.Append(" AND ISNULL(inv.RemainingAmount, 0) <= 0");
                    break;
                case "unpaid":
                    sb.Append(" AND ISNULL(inv.RemainingAmount, 0) > 0");
                    break;
                case "wallet":
                    sb.Append(@" AND EXISTS (
                        SELECT 1 FROM dbo.AppointmentPayments wap
                        WHERE ISNULL(wap.IsWalletPayment, 0) = 1 AND wap.Amount > 0
                          AND (wap.AppointmentId = inv.AppointmentId
                               OR wap.AppointmentId IN (
                                    SELECT wl.AppointmentId FROM dbo.AppointmentInvoiceLines wl
                                     WHERE wl.InvoiceId = inv.Id)))");
                    break;
                case "completedunpaid":
                    // The reason this page exists: handed over, never collected.
                    sb.Append(" AND ISNULL(inv.WorkflowStatus,'Pending') IN ('Delivered','Completed')");
                    sb.Append(" AND ISNULL(inv.RemainingAmount, 0) > 0");
                    break;
            }

            // ---- Dates --------------------------------------------------------
            string dateCol = (dateField ?? "created").Trim().ToLowerInvariant() switch
            {
                "completed" => "inv.CompletedAt",
                "paid" => "inv.PaidAt",
                "delivery" => "ISNULL(idl.DeliveryDate, inv.DeliveryDate)",
                _ => "inv.CreatedAt"
            };
            if (dateFrom.HasValue) sb.Append($" AND {dateCol} >= @DateFromUtc");
            if (dateTo.HasValue) sb.Append($" AND {dateCol} < @DateToUtc");

            if (onlyStale)
                sb.Append($@" AND ISNULL(inv.WorkflowStatus,'Pending') NOT IN ('Completed','Cancelled')
                              AND DATEDIFF(minute, ISNULL(inv.WorkflowStatusAt, inv.CreatedAt),
                                           SYSUTCDATETIME()) >= {staleMinutes}");

            if (!string.IsNullOrWhiteSpace(search))
                sb.Append(@" AND (
                    inv.InvoiceNumber LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_NAME   LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_PHONE1 LIKE '%' + @Search + '%' OR
                    c.CUSTOMER_PHONE2 LIKE '%' + @Search + '%' OR
                    idl.DriverName    LIKE '%' + @Search + '%' OR
                    idl.DriverNameAr  LIKE '%' + @Search + '%' OR
                    idl.AreaNameEn    LIKE '%' + @Search + '%' OR
                    idl.AreaNameAr    LIKE '%' + @Search + '%')");

            var prm = new Dapper.DynamicParameters();
            prm.Add("BranchId", branchId);
            prm.Add("Status", normalized);
            prm.Add("CustomerId", customerId);
            prm.Add("DriverId", driverId);
            prm.Add("AreaId", areaId);
            prm.Add("GovernorateId", governorateId);
            prm.Add("Search", string.IsNullOrWhiteSpace(search) ? null : search.Trim());
            // The UI speaks branch-local dates; the columns are UTC.
            prm.Add("DateFromUtc", dateFrom?.Date.AddHours(-tzOffset));
            prm.Add("DateToUtc", dateTo?.Date.AddDays(1).AddHours(-tzOffset));

            return (sb.ToString(), prm);
        }

        private static string BuildOrderBy(string? sortBy, string? sortDir)
        {
            bool asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
            string dir = asc ? "ASC" : "DESC";

            return (sortBy ?? "stage").ToLowerInvariant() switch
            {
                // Oldest-in-stage first is what a queue means, so 'stage' sorts on
                // the age of the stage stamp rather than on the stage name.
                "stage" => $"ISNULL(inv.WorkflowStatusAt, inv.CreatedAt) {(asc ? "ASC" : "DESC")}, inv.Id DESC",
                "date" => $"inv.CreatedAt {dir}, inv.Id DESC",
                "amount" => $"ISNULL(inv.TotalAmount,0) {dir}, inv.Id DESC",
                "outstanding" => $"ISNULL(inv.RemainingAmount,0) {dir}, inv.Id DESC",
                "customer" => $"c.CUSTOMER_NAME {dir}, inv.Id DESC",
                "delivery" => $"ISNULL(idl.DeliveryDate, inv.DeliveryDate) {dir}, inv.Id DESC",
                _ => $"ISNULL(inv.WorkflowStatusAt, inv.CreatedAt) {dir}, inv.Id DESC"
            };
        }

        private static List<WorkflowDtos.WorkflowOrderDto> QueryPage(
            IDbConnection conn, string where, Dapper.DynamicParameters prm,
            string? sortBy, string? sortDir, int page, int pageSize, int staleMinutes)
        {
            var p = Clone(prm);
            p.Add("Skip", (page - 1) * pageSize);
            p.Add("Take", pageSize);

            var rows = SqlMapper.Query(conn, $@"
                SELECT {SelectColumns}
                {FromJoins}
                WHERE ({where})
                ORDER BY {BuildOrderBy(sortBy, sortDir)}
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", p).ToList();

            var mapped = new List<WorkflowDtos.WorkflowOrderDto>(rows.Count);
            foreach (var r in rows) mapped.Add(MapOrder(r, staleMinutes));
            return mapped;
        }

        private static WorkflowDtos.WorkflowOrderDto? LoadOrderCore(IDbConnection conn, int invoiceId, int staleMinutes)
        {
            var row = SqlMapper.Query(conn, $@"
                SELECT {SelectColumns}
                {FromJoins}
                WHERE inv.Id = @Id", new { Id = invoiceId }).FirstOrDefault();

            return row == null ? null : MapOrder(row, staleMinutes);
        }

        private WorkflowDtos.WorkflowOrderDto? LoadOrder(IDbConnection conn, int invoiceId)
        {
            var basic = LoadOrderCore(conn, invoiceId, 30);
            if (basic == null) return null;

            int stale = BusinessSettingsService.GetInt(conn, KeyStaleMinutes, 30, basic.BranchId);
            var order = LoadOrderCore(conn, invoiceId, stale)!;
            return EnrichPage(conn, new List<WorkflowDtos.WorkflowOrderDto> { order }).First();
        }

        private static WorkflowDtos.WorkflowOrderDto MapOrder(dynamic r, int staleMinutes)
        {
            string status = (string?)r.WorkflowStatus ?? WorkflowDtos.Status.Pending;
            bool isDelivery = ToBool(r.IsDelivery);
            int minutes = r.MinutesInStage == null ? 0 : (int)r.MinutesInStage;
            bool terminal = WorkflowDtos.Status.Terminal.Contains(status);

            return new WorkflowDtos.WorkflowOrderDto(
                InvoiceId: (int)r.InvoiceId,
                InvoiceNumber: (string?)r.InvoiceNumber ?? "",
                LeadAppointmentId: r.LeadAppointmentId == null ? 0 : (int)r.LeadAppointmentId,
                BranchId: r.BranchId == null ? 0 : (int)r.BranchId,
                CreatedAt: (DateTime)r.CreatedAt,

                CustomerId: r.CustomerId == null ? 0 : (int)r.CustomerId,
                CustomerName: (string?)r.CustomerName ?? "",
                CustomerPhone: (string?)r.CustomerPhone ?? "",
                CustomerPhone2: (string?)r.CustomerPhone2,

                SubTotal: Dec(r.SubTotal),
                DiscountAmount: Dec(r.DiscountAmount),
                DeliveryCharge: Dec(r.DeliveryCharge),
                TotalAmount: Dec(r.TotalAmount),
                PaidAmount: Dec(r.PaidAmount),
                RemainingAmount: Dec(r.RemainingAmount),
                Currency: (string?)r.Currency ?? "KWD",
                PaymentStatus: (string?)r.PaymentStatus,
                IsDeferred: ToBool(r.IsDeferred),
                PaidAt: (DateTime?)r.PaidAt,
                SettledAt: (DateTime?)r.SettledAt,
                IsPaid: ToBool(r.IsPaid),
                IsWalletPaid: false,          // filled in by EnrichPage
                WalletPaidAmount: 0m,         // filled in by EnrichPage

                IsDelivery: isDelivery,
                DeliveryTypeId: (int?)r.DeliveryTypeId,
                DeliveryTypeNameEn: (string?)r.DeliveryTypeNameEn,
                DeliveryTypeNameAr: (string?)r.DeliveryTypeNameAr,
                DriverId: (int?)r.DriverId,
                DriverName: (string?)r.DriverName,
                DriverNameAr: (string?)r.DriverNameAr,
                DriverPhone: (string?)r.DriverPhone,
                AreaId: (int?)r.AreaId,
                AreaNameEn: (string?)r.AreaNameEn,
                AreaNameAr: (string?)r.AreaNameAr,
                GovernorateId: (int?)r.GovernorateId,
                GovernorateNameEn: (string?)r.GovernorateNameEn,
                GovernorateNameAr: (string?)r.GovernorateNameAr,
                AddressSummary: NullIfBlank((string?)r.AddressSummary),
                DeliveryDate: (DateTime?)r.DeliveryDate,

                WorkflowStatus: status,
                WorkflowStatusAt: (DateTime?)r.WorkflowStatusAt,
                ProcessingAt: (DateTime?)r.ProcessingAt,
                ReadyAt: (DateTime?)r.ReadyAt,
                OutForDeliveryAt: (DateTime?)r.OutForDeliveryAt,
                DeliveredAt: (DateTime?)r.DeliveredAt,
                CompletedAt: (DateTime?)r.CompletedAt,
                CancelledAt: (DateTime?)r.CancelledAt,
                WorkflowCancelReason: (string?)r.WorkflowCancelReason,
                MinutesInStage: minutes,
                IsStale: !terminal && minutes >= staleMinutes,
                NextStatus: terminal ? null : PrimaryNext(status, isDelivery),

                ItemCount: r.ItemCount == null ? 0 : (int)r.ItemCount,
                ServicesSummary: null,        // filled in by EnrichPage
                Notes: (string?)r.Notes,
                CommentCount: 0,              // filled in by EnrichPage
                AttachmentCount: 0,
                LastCommentText: null,
                LastCommentBy: null,
                LastCommentAt: null,

                IsVoid: ToBool(r.IsVoid),
                AgeDays: r.AgeDays == null ? 0 : (int)r.AgeDays);
        }

        /// Page-scoped enrichment: services, wallet split and the comment digest.
        /// Three round trips for the page, never one per row.
        private static List<WorkflowDtos.WorkflowOrderDto> EnrichPage(
            IDbConnection conn, List<WorkflowDtos.WorkflowOrderDto> items)
        {
            if (items.Count == 0) return items;
            var ids = items.Select(i => i.InvoiceId).ToList();

            // NOTE: every SqlMapper.Query without a <T> yields dynamic rows, and any
            // LINQ whose lambda body touches a dynamic value is bound at runtime —
            // so Select/ToDictionary come back as `dynamic`, not List<T>/Dictionary<..>.
            // Plain foreach loops keep these projections statically typed.
            var serviceRows = SqlMapper.Query(conn, @"
                SELECT l.InvoiceId,
                       STUFF((
                           SELECT TOP 4 ', ' + ISNULL(i2.ITEM_NAME1, ISNULL(i2.ITEM_NAME2, ''))
                           FROM dbo.AppointmentInvoiceLines l2
                           LEFT JOIN dbo.ITEM i2 ON i2.ITEM_ID = l2.ItemId
                           WHERE l2.InvoiceId = l.InvoiceId AND ISNULL(l2.IsRefunded, 0) = 0
                           FOR XML PATH(''), TYPE
                       ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS Summary
                FROM dbo.AppointmentInvoiceLines l
                WHERE l.InvoiceId IN @Ids
                GROUP BY l.InvoiceId", new { Ids = ids });

            var services = new Dictionary<int, string?>();
            foreach (var r in serviceRows) services[(int)r.InvoiceId] = (string?)r.Summary;

            var walletRows = SqlMapper.Query(conn, @"
                WITH Map AS (
                    SELECT inv.Id AS InvoiceId, inv.AppointmentId AS AppointmentId
                      FROM dbo.AppointmentInvoices inv WHERE inv.Id IN @Ids
                    UNION
                    SELECT l.InvoiceId, l.AppointmentId
                      FROM dbo.AppointmentInvoiceLines l WHERE l.InvoiceId IN @Ids
                )
                SELECT m.InvoiceId,
                       ISNULL(SUM(CASE WHEN ISNULL(ap.IsWalletPayment,0) = 1 THEN ap.Amount ELSE 0 END), 0) AS WalletAmt,
                       ISNULL(SUM(CASE WHEN ISNULL(ap.IsWalletPayment,0) = 0 THEN ap.Amount ELSE 0 END), 0) AS OtherAmt
                FROM Map m
                INNER JOIN dbo.AppointmentPayments ap ON ap.AppointmentId = m.AppointmentId
                GROUP BY m.InvoiceId", new { Ids = ids });

            var wallet = new Dictionary<int, (decimal Wallet, decimal Other)>();
            foreach (var r in walletRows)
                wallet[(int)r.InvoiceId] = ((decimal)r.WalletAmt, (decimal)r.OtherAmt);

            var digestRows = SqlMapper.Query(conn, @"
                SELECT x.InvoiceId,
                       x.CommentCount,
                       x.AttachmentCount,
                       lc.CommentText AS LastCommentText,
                       lc.UserName    AS LastCommentBy,
                       lc.CreatedAt   AS LastCommentAt
                FROM (
                    SELECT i.Id AS InvoiceId,
                           (SELECT COUNT(*) FROM dbo.InvoiceWorkflowComments cc
                             WHERE cc.InvoiceId = i.Id AND cc.IsDeleted = 0) AS CommentCount,
                           (SELECT COUNT(*) FROM dbo.InvoiceWorkflowAttachments aa
                             WHERE aa.InvoiceId = i.Id AND aa.IsDeleted = 0) AS AttachmentCount
                    FROM dbo.AppointmentInvoices i
                    WHERE i.Id IN @Ids
                ) x
                OUTER APPLY (
                    SELECT TOP 1 c2.CommentText, c2.UserName, c2.CreatedAt
                    FROM dbo.InvoiceWorkflowComments c2
                    WHERE c2.InvoiceId = x.InvoiceId AND c2.IsDeleted = 0
                      AND c2.CommentText IS NOT NULL
                    ORDER BY c2.CreatedAt DESC
                ) lc", new { Ids = ids });

            var digest = new Dictionary<int, CommentDigest>();
            foreach (var r in digestRows)
            {
                digest[(int)r.InvoiceId] = new CommentDigest(
                    Comments: (int)r.CommentCount,
                    Attachments: (int)r.AttachmentCount,
                    LastText: (string?)r.LastCommentText,
                    LastBy: (string?)r.LastCommentBy,
                    LastAt: (DateTime?)r.LastCommentAt);
            }

            var enriched = new List<WorkflowDtos.WorkflowOrderDto>(items.Count);
            foreach (var o in items)
            {
                services.TryGetValue(o.InvoiceId, out var svc);
                wallet.TryGetValue(o.InvoiceId, out var w);
                digest.TryGetValue(o.InvoiceId, out var d);

                // "Wallet-paid" means the wallet covered the whole thing with no
                // cash or card alongside it. That is the only case where a
                // completion must never ask for money.
                bool fullyWallet = w.Wallet > 0m && w.Other <= 0m && o.RemainingAmount <= 0m;

                enriched.Add(o with
                {
                    ServicesSummary = svc,
                    WalletPaidAmount = Math.Round(w.Wallet, 3),
                    IsWalletPaid = fullyWallet,
                    CommentCount = d?.Comments ?? 0,
                    AttachmentCount = d?.Attachments ?? 0,
                    LastCommentText = d?.LastText,
                    LastCommentBy = d?.LastBy,
                    LastCommentAt = d?.LastAt
                });
            }

            return enriched;
        }

        /// The per-invoice comment/attachment counts plus the newest comment,
        /// so the list and the board can show "3 notes — Ahmed, 10:42" without
        /// a query per row.
        private sealed record CommentDigest(
            int Comments,
            int Attachments,
            string? LastText,
            string? LastBy,
            DateTime? LastAt);

        private static Dictionary<string, int> LoadStatusCounts(
            IDbConnection conn, string where, Dapper.DynamicParameters prm)
        {
            var rows = SqlMapper.Query(conn, $@"
                SELECT ISNULL(inv.WorkflowStatus, 'Pending') AS Status, COUNT(*) AS Cnt
                {FromJoins}
                WHERE ({where}) AND ISNULL(inv.IsVoid, 0) = 0
                GROUP BY ISNULL(inv.WorkflowStatus, 'Pending')", prm);

            var map = WorkflowDtos.Status.All.ToDictionary(s => s, _ => 0);
            foreach (var r in rows)
            {
                string k = (string)r.Status;
                map[k] = (int)r.Cnt;
            }
            return map;
        }

        private static WorkflowDtos.WorkflowSummaryDto LoadSummary(
            IDbConnection conn, string where, Dapper.DynamicParameters prm,
            int? branchId, int staleMinutes)
        {
            var agg = SqlMapper.Query(conn, $@"
                SELECT
                    COUNT(*) AS OrderCount,
                    ISNULL(SUM(ISNULL(inv.TotalAmount,0)), 0)     AS TotalValue,
                    ISNULL(SUM(ISNULL(inv.RemainingAmount,0)), 0) AS OutstandingValue,
                    SUM(CASE WHEN ISNULL(inv.RemainingAmount,0) > 0 THEN 1 ELSE 0 END) AS UnpaidCount,
                    SUM(CASE WHEN ISNULL(inv.WorkflowStatus,'Pending') IN ('Delivered','Completed')
                              AND ISNULL(inv.RemainingAmount,0) > 0 THEN 1 ELSE 0 END) AS CompletedUnpaidCount,
                    ISNULL(SUM(CASE WHEN ISNULL(inv.WorkflowStatus,'Pending') IN ('Delivered','Completed')
                              AND ISNULL(inv.RemainingAmount,0) > 0
                              THEN inv.RemainingAmount ELSE 0 END), 0) AS CompletedUnpaidValue,
                    SUM(CASE WHEN {IsDeliveryExpr} = 1 THEN 1 ELSE 0 END) AS DeliveryCount,
                    SUM(CASE WHEN {IsDeliveryExpr} = 0 THEN 1 ELSE 0 END) AS PickupCount,
                    SUM(CASE WHEN ISNULL(inv.WorkflowStatus,'Pending') NOT IN ('Completed','Cancelled')
                              AND DATEDIFF(minute, ISNULL(inv.WorkflowStatusAt, inv.CreatedAt),
                                           SYSUTCDATETIME()) >= {staleMinutes}
                             THEN 1 ELSE 0 END) AS StaleCount,
                    MAX(ISNULL(inv.Currency, b.EnglishCurrencyName)) AS Currency
                {FromJoins}
                WHERE ({where})", prm).FirstOrDefault();

            var byStatus = SqlMapper.Query(conn, $@"
                SELECT ISNULL(inv.WorkflowStatus,'Pending') AS Status,
                       COUNT(*) AS Cnt,
                       ISNULL(SUM(ISNULL(inv.TotalAmount,0)), 0) AS Val
                {FromJoins}
                WHERE ({where})
                GROUP BY ISNULL(inv.WorkflowStatus,'Pending')", prm).ToList();

            var counts = WorkflowDtos.Status.All.ToDictionary(s => s, _ => 0);
            var values = WorkflowDtos.Status.All.ToDictionary(s => s, _ => 0m);
            foreach (var r in byStatus)
            {
                string k = (string)r.Status;
                if (!counts.ContainsKey(k)) { counts[k] = 0; values[k] = 0m; }
                counts[k] = (int)r.Cnt;
                values[k] = Math.Round((decimal)r.Val, 3);
            }

            string currency = (agg == null ? null : (string?)agg.Currency)
                ?? SqlMapper.Query<string>(conn,
                    "SELECT TOP 1 EnglishCurrencyName FROM dbo.BRANCH WHERE BRANCH_ID = @Id",
                    new { Id = branchId }).FirstOrDefault() ?? "KWD";

            return new WorkflowDtos.WorkflowSummaryDto(
                OrderCount: agg == null ? 0 : (int)agg.OrderCount,
                TotalValue: agg == null ? 0m : Math.Round((decimal)agg.TotalValue, 3),
                OutstandingValue: agg == null ? 0m : Math.Round((decimal)agg.OutstandingValue, 3),
                UnpaidCount: agg == null ? 0 : (int)(agg.UnpaidCount ?? 0),
                CompletedUnpaidCount: agg == null ? 0 : (int)(agg.CompletedUnpaidCount ?? 0),
                CompletedUnpaidValue: agg == null ? 0m : Math.Round((decimal)agg.CompletedUnpaidValue, 3),
                DeliveryCount: agg == null ? 0 : (int)(agg.DeliveryCount ?? 0),
                PickupCount: agg == null ? 0 : (int)(agg.PickupCount ?? 0),
                StaleCount: agg == null ? 0 : (int)(agg.StaleCount ?? 0),
                Currency: currency,
                CountByStatus: counts,
                ValueByStatus: values);
        }

        private static List<WorkflowDtos.WorkflowOrderLineDto> LoadLines(IDbConnection conn, int invoiceId)
        {
            var rows = SqlMapper.Query(conn, @"
                SELECT
                    l.AppointmentId AS AppointmentId,
                    ISNULL(i.ITEM_NAME1, ISNULL(i.ITEM_NAME2, '')) AS ServiceName,
                    ISNULL(l.UnitPrice, 0)   AS UnitPrice,
                    ISNULL(l.TotalPrice, ISNULL(l.UnitPrice, 0)) AS LineTotal,
                    CAST(ISNULL(l.IsRefunded, 0) AS BIT) AS IsRefunded
                FROM dbo.AppointmentInvoiceLines l
                LEFT JOIN dbo.ITEM i ON i.ITEM_ID = l.ItemId
                WHERE l.InvoiceId = @Id
                ORDER BY l.Id", new { Id = invoiceId });

            var lines = new List<WorkflowDtos.WorkflowOrderLineDto>();
            foreach (var r in rows)
            {
                lines.Add(new WorkflowDtos.WorkflowOrderLineDto(
                    AppointmentId: r.AppointmentId == null ? 0 : (int)r.AppointmentId,
                    ServiceName: (string?)r.ServiceName ?? "",
                    UnitPrice: Dec(r.UnitPrice),
                    // A line is one booked service, so there is no quantity
                    // column to read — the DTO keeps the field for shape only.
                    Quantity: 1,
                    LineTotal: Dec(r.LineTotal),
                    IsRefunded: ToBool(r.IsRefunded)));
            }
            return lines;
        }

        // ---------------------------------------------------------------------
        // Timeline
        // ---------------------------------------------------------------------
        private static List<WorkflowDtos.WorkflowTimelineEntryDto> LoadTimeline(
            IDbConnection conn, int invoiceId)
        {
            var eventRows = SqlMapper.Query(conn, @"
                SELECT Id, InvoiceId, FromStatus, ToStatus, DriverId, DriverName, DriverNameAr,
                       RemainingAmount, WasPaid, Note, UserId, UserName, CreatedAt, SecondsInPrevious
                FROM dbo.InvoiceWorkflowEvents
                WHERE InvoiceId = @Id
                ORDER BY CreatedAt, Id", new { Id = invoiceId });

            var events = new List<WorkflowDtos.WorkflowEventDto>();
            foreach (var r in eventRows)
            {
                events.Add(new WorkflowDtos.WorkflowEventDto(
                    Id: (int)r.Id,
                    InvoiceId: (int)r.InvoiceId,
                    FromStatus: (string?)r.FromStatus,
                    ToStatus: (string)r.ToStatus,
                    DriverId: (int?)r.DriverId,
                    DriverName: (string?)r.DriverName,
                    DriverNameAr: (string?)r.DriverNameAr,
                    RemainingAmount: (decimal?)r.RemainingAmount,
                    WasPaid: ToBool(r.WasPaid),
                    Note: (string?)r.Note,
                    UserId: (int?)r.UserId,
                    UserName: (string?)r.UserName,
                    CreatedAt: (DateTime)r.CreatedAt,
                    SecondsInPrevious: (int?)r.SecondsInPrevious));
            }

            var comments = LoadComments(conn, invoiceId);

            // A note written with a transition is rendered inside that event, so
            // it must not also appear as a standalone entry.
            var noteByEvent = comments.Where(c => c.EventId != null)
                                      .ToDictionary(c => c.EventId!.Value, c => c);

            var entries = new List<WorkflowDtos.WorkflowTimelineEntryDto>();

            foreach (var e in events)
            {
                entries.Add(new WorkflowDtos.WorkflowTimelineEntryDto(
                    "event", e.CreatedAt, e, null));

                if (noteByEvent.TryGetValue(e.Id, out var attached))
                    entries.Add(new WorkflowDtos.WorkflowTimelineEntryDto(
                        "comment", attached.CreatedAt, null, attached));
            }

            foreach (var c in comments.Where(c => c.EventId == null))
                entries.Add(new WorkflowDtos.WorkflowTimelineEntryDto(
                    "comment", c.CreatedAt, null, c));

            return entries.OrderBy(e => e.CreatedAt).ToList();
        }

        private static List<WorkflowDtos.WorkflowCommentDto> LoadComments(
            IDbConnection conn, int invoiceId)
        {
            var comments = SqlMapper.Query(conn, @"
                SELECT Id, InvoiceId, EventId, Stage, CommentText, IsInternal,
                       UserId, UserName, CreatedAt, EditedAt
                FROM dbo.InvoiceWorkflowComments
                WHERE InvoiceId = @Id AND IsDeleted = 0
                ORDER BY CreatedAt, Id", new { Id = invoiceId }).ToList();

            if (comments.Count == 0) return new List<WorkflowDtos.WorkflowCommentDto>();

            var fileRows = SqlMapper.Query(conn, @"
                SELECT Id, InvoiceId, CommentId, FileName, FileUrl, ContentType,
                       FileSize, IsImage, UserName, CreatedAt
                FROM dbo.InvoiceWorkflowAttachments
                WHERE InvoiceId = @Id AND IsDeleted = 0 AND CommentId IS NOT NULL
                ORDER BY Id", new { Id = invoiceId });

            // Grouped by comment up front, so the loop below never queries per row.
            var files = new Dictionary<int, List<WorkflowDtos.WorkflowAttachmentDto>>();
            foreach (var r in fileRows)
            {
                var dto = new WorkflowDtos.WorkflowAttachmentDto(
                    Id: (int)r.Id,
                    InvoiceId: (int)r.InvoiceId,
                    CommentId: (int?)r.CommentId,
                    FileName: (string?)r.FileName ?? "",
                    FileUrl: (string?)r.FileUrl ?? "",
                    ContentType: (string?)r.ContentType,
                    FileSize: (long?)r.FileSize,
                    IsImage: ToBool(r.IsImage),
                    UserName: (string?)r.UserName,
                    CreatedAt: (DateTime)r.CreatedAt);

                int key = dto.CommentId ?? 0;
                if (!files.TryGetValue(key, out var bucket))
                    files[key] = bucket = new List<WorkflowDtos.WorkflowAttachmentDto>();
                bucket.Add(dto);
            }

            var result = new List<WorkflowDtos.WorkflowCommentDto>(comments.Count);
            foreach (var r in comments)
            {
                int id = (int)r.Id;
                files.TryGetValue(id, out var att);

                result.Add(new WorkflowDtos.WorkflowCommentDto(
                    Id: id,
                    InvoiceId: (int)r.InvoiceId,
                    EventId: (int?)r.EventId,
                    Stage: (string?)r.Stage,
                    CommentText: (string?)r.CommentText,
                    IsInternal: ToBool(r.IsInternal),
                    UserId: (int?)r.UserId,
                    UserName: (string?)r.UserName,
                    CreatedAt: (DateTime)r.CreatedAt,
                    EditedAt: (DateTime?)r.EditedAt,
                    Attachments: att ?? new List<WorkflowDtos.WorkflowAttachmentDto>()));
            }

            return result;
        }

        private static WorkflowDtos.WorkflowCommentDto LoadComment(IDbConnection conn, int commentId)
        {
            int invoiceId = SqlMapper.Query<int>(conn,
                "SELECT InvoiceId FROM dbo.InvoiceWorkflowComments WHERE Id = @Id",
                new { Id = commentId }).First();

            return LoadComments(conn, invoiceId).First(c => c.Id == commentId);
        }

        private static int InsertComment(
            IDbConnection conn, int invoiceId, int branchId, int? eventId, string? stage,
            string? text, int userId, string userName, bool isInternal)
        {
            return SqlMapper.Query<int>(conn, @"
                INSERT INTO dbo.InvoiceWorkflowComments
                    (InvoiceId, BranchId, EventId, Stage, CommentText, IsInternal,
                     UserId, UserName, CreatedAt, IsDeleted)
                OUTPUT INSERTED.Id
                VALUES (@InvoiceId, @BranchId, @EventId, @Stage, @Text, @IsInternal,
                        @UserId, @UserName, SYSUTCDATETIME(), 0)",
                new
                {
                    InvoiceId = invoiceId,
                    BranchId = NullIfZero(branchId),
                    EventId = eventId,
                    Stage = stage,
                    Text = text,
                    IsInternal = isInternal,
                    UserId = NullIfZero(userId),
                    UserName = userName
                }).First();
        }

        /// Only claims files that were uploaded against THIS invoice and are not
        /// already attached, so a stale id from another tab cannot be stolen.
        private static void LinkAttachments(
            IDbConnection conn, int invoiceId, int commentId, List<int>? attachmentIds)
        {
            if (attachmentIds == null || attachmentIds.Count == 0) return;

            SqlMapper.Execute(conn, @"
                UPDATE dbo.InvoiceWorkflowAttachments
                   SET CommentId = @CommentId
                 WHERE Id IN @Ids
                   AND InvoiceId = @InvoiceId
                   AND CommentId IS NULL
                   AND IsDeleted = 0",
                new { CommentId = commentId, Ids = attachmentIds.Distinct().ToList(), InvoiceId = invoiceId });
        }

        // #####################################################################
        // ##  Small helpers
        // #####################################################################

        private WorkflowDtos.WorkflowSettingsDto LoadSettings(IDbConnection conn, int? branchId)
        {
            return new WorkflowDtos.WorkflowSettingsDto(
                Enabled: BusinessSettingsService.GetBool(conn, KeyEnabled, true, branchId),
                RequireDriver: BusinessSettingsService.GetBool(conn, KeyRequireDriver, true, branchId),
                AllowSkipStages: BusinessSettingsService.GetBool(conn, KeyAllowSkip, false, branchId),
                PromptPaymentOnComplete: BusinessSettingsService.GetBool(conn, KeyPromptPayment, true, branchId),
                StalePendingMinutes: Math.Max(1, BusinessSettingsService.GetInt(conn, KeyStaleMinutes, 30, branchId)),
                DebtEnabled: BusinessSettingsService.GetBool(conn, BusinessSettingsService.KeyDebtEnabled, false, branchId),
                DeliveryEnabled: BusinessSettingsService.GetBool(conn, BusinessSettingsService.KeyDeliveryEnabled, false, branchId));
        }

        private static PosDtos.PosBranchDto LoadBranch(IDbConnection conn, ref int? branchId)
        {
            var row = SqlMapper.Query(conn, @"
                SELECT TOP 1
                    BRANCH_ID    AS BranchId,
                    COMPANY_ID   AS CompanyId,
                    BRANCH_NAME1 AS BranchName1,
                    BRANCH_NAME2 AS BranchName2,
                    BRANCH_PHONE AS BranchPhone,
                    EnglishCurrencyName AS CurrencyEn,
                    ArabicCurrencyName  AS CurrencyAr,
                    ISNULL(RoundOfDigits, 3) AS RoundOfDigits,
                    TaxValue     AS TaxValue
                FROM dbo.BRANCH
                WHERE (@BranchId IS NULL OR BRANCH_ID = @BranchId)
                ORDER BY BRANCH_ID", new { BranchId = branchId }).FirstOrDefault();

            if (row == null)
                return new PosDtos.PosBranchDto(0, 0, "", "", null, "KWD", "د.ك", 3, null);

            branchId = (int)row.BranchId;
            return new PosDtos.PosBranchDto(
                BranchId: (int)row.BranchId,
                CompanyId: row.CompanyId == null ? 0 : (int)row.CompanyId,
                BranchName1: (string?)row.BranchName1 ?? "",
                BranchName2: (string?)row.BranchName2 ?? "",
                BranchPhone: (string?)row.BranchPhone,
                CurrencyEn: (string?)row.CurrencyEn ?? "KWD",
                CurrencyAr: (string?)row.CurrencyAr ?? "د.ك",
                RoundOfDigits: row.RoundOfDigits == null ? 3 : (int)row.RoundOfDigits,
                TaxValue: (decimal?)row.TaxValue);
        }

        private static DeliveryDtos.DeliveryDriverDto? LoadDriver(IDbConnection conn, int driverId)
        {
            return SqlMapper.Query<DeliveryDtos.DeliveryDriverDto>(conn, @"
                SELECT
                    d.DRIVER_ID      AS DriverId,
                    d.DRIVER_NAME    AS DriverName,
                    d.DRIVER_NAME_AR AS DriverNameAr,
                    d.DRIVER_PHONE   AS DriverPhone,
                    d.DRIVER_ADRESS  AS DriverAddress,
                    d.BRANCH_ID      AS BranchId,
                    d.GOVERNORATE_ID AS GovernorateId,
                    ISNULL(g.GOVERNORATE_NAME1, '') AS GovernorateNameEn,
                    ISNULL(g.GOVERNORATE_NAME2, '') AS GovernorateNameAr,
                    CAST(1 AS BIT) AS IsActive
                FROM dbo.DRIVER d
                LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = d.GOVERNORATE_ID
                WHERE d.DRIVER_ID = @Id AND ISNULL(d.IS_ACTIVE, 1) = 1",
                new { Id = driverId }).FirstOrDefault();
        }

        private static List<WorkflowDtos.WorkflowStatusDto> BuildStatusCatalog(bool deliveryEnabled)
        {
            var list = new List<WorkflowDtos.WorkflowStatusDto>();
            foreach (var code in WorkflowDtos.Status.All)
            {
                if (!deliveryEnabled && WorkflowDtos.Status.DeliveryOnly.Contains(code)) continue;
                var m = StatusMeta(code);
                list.Add(m);
            }
            return list;
        }

        /// Labels, icon and colour for one status. Kept server-side so the board,
        /// the table, the timeline and the receipt cannot drift apart.
        private static WorkflowDtos.WorkflowStatusDto StatusMeta(string code) => code switch
        {
            WorkflowDtos.Status.Pending => new(code, "Pending", "قيد الانتظار",
                "schedule", "#f59e0b", 0, false, false),
            WorkflowDtos.Status.Processing => new(code, "Processing", "جارٍ التجهيز",
                "autorenew", "#0284c7", 1, false, false),
            WorkflowDtos.Status.Ready => new(code, "Ready", "جاهز",
                "inventory_2", "#7c3aed", 2, false, false),
            WorkflowDtos.Status.OutForDelivery => new(code, "Out for delivery", "خرج للتوصيل",
                "local_shipping", "#0891b2", 3, false, true),
            WorkflowDtos.Status.Delivered => new(code, "Delivered", "تم التسليم",
                "where_to_vote", "#059669", 4, false, true),
            WorkflowDtos.Status.Completed => new(code, "Completed", "مكتمل",
                "task_alt", "#10b981", 5, true, false),
            WorkflowDtos.Status.Cancelled => new(code, "Cancelled", "ملغي",
                "cancel", "#ef4444", 6, true, false),
            _ => new(code, code, code, "help", "#94a3b8", 99, false, false)
        };

        /// The button text. An action, not a state — "Start processing", never
        /// "Processing", so the control says what pressing it does.
        private static string ActionLabelEn(string to) => to switch
        {
            WorkflowDtos.Status.Processing => "Start processing",
            WorkflowDtos.Status.Ready => "Mark ready",
            WorkflowDtos.Status.OutForDelivery => "Send with driver",
            WorkflowDtos.Status.Delivered => "Mark delivered",
            WorkflowDtos.Status.Completed => "Complete order",
            WorkflowDtos.Status.Cancelled => "Cancel order",
            WorkflowDtos.Status.Pending => "Move back to pending",
            _ => to
        };

        private static string ActionLabelAr(string to) => to switch
        {
            WorkflowDtos.Status.Processing => "بدء التجهيز",
            WorkflowDtos.Status.Ready => "تحديد كجاهز",
            WorkflowDtos.Status.OutForDelivery => "إرسال مع السائق",
            WorkflowDtos.Status.Delivered => "تأكيد التسليم",
            WorkflowDtos.Status.Completed => "إكمال الطلب",
            WorkflowDtos.Status.Cancelled => "إلغاء الطلب",
            WorkflowDtos.Status.Pending => "إرجاع لقيد الانتظار",
            _ => to
        };

        private int ResolveCurrentUserId()
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userId" || c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : 0;
        }

        /// The display name for "who did this". The JWT already carries the
        /// username, so the DB is only touched when the token is thin.
        private string ResolveUserName(IDbConnection conn)
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userName" || c.Type == "unique_name" || c.Type == ClaimTypes.Name);
            if (!string.IsNullOrWhiteSpace(claim?.Value)) return claim!.Value;

            int id = ResolveCurrentUserId();
            if (id <= 0) return "System";

            return SqlMapper.Query<string>(conn,
                "SELECT USER_NAME FROM dbo.[USER] WHERE USER_ID = @Id",
                new { Id = id }).FirstOrDefault() ?? $"User #{id}";
        }

        private int? ResolveUserBranchId(IDbConnection conn)
        {
            int userId = ResolveCurrentUserId();
            if (userId <= 0) return null;
            return SqlMapper.Query<int?>(conn,
                "SELECT BRANCH_ID FROM dbo.[USER] WHERE USER_ID = @UserId",
                new { UserId = userId }).FirstOrDefault();
        }

        private static Dapper.DynamicParameters Params(params (string Name, object? Value)[] pairs)
        {
            var p = new Dapper.DynamicParameters();
            foreach (var (n, v) in pairs) p.Add(n, v);
            return p;
        }

        /// DynamicParameters is mutable, and the board reuses one filter across
        /// six lane queries — each needs its own copy or @Skip leaks between them.
        private static Dapper.DynamicParameters Clone(Dapper.DynamicParameters source)
        {
            var copy = new Dapper.DynamicParameters();
            foreach (var name in source.ParameterNames)
                copy.Add(name, source.Get<object>(name));
            return copy;
        }

        private static decimal Dec(object? v) =>
            v == null ? 0m : Math.Round(Convert.ToDecimal(v), 3);

        private static bool ToBool(object? v) =>
            v != null && (v is bool b ? b : Convert.ToInt32(v) != 0);

        private static int? NullIfZero(int v) => v > 0 ? v : (int?)null;

        private static string? NullIfBlank(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd(',');

        private static string? Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
    }
}
