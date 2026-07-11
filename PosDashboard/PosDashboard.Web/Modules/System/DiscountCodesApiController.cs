// Modules/System/Controllers/DiscountCodesApiController.cs
//
// Customer Discount Codes (api/discount-codes).
//
//   Templates (Discount Templates screen)
//     GET    /api/discount-codes/templates                → list
//     POST   /api/discount-codes/templates                → create
//     PUT    /api/discount-codes/templates/{id}            → update
//     DELETE /api/discount-codes/templates/{id}            → soft delete
//
//   Assign + notify (Discount Templates screen → "Assign to customer")
//     POST   /api/discount-codes/assign                    → generate CARD-###### + WhatsApp
//
//   POS integration
//     GET    /api/discount-codes/lookup?code=&customerId=  → validate a typed code
//     GET    /api/discount-codes/customer/{customerId}/active → alert helper
//
//   Discount Codes screen
//     GET    /api/discount-codes?filter=&search=&branchId= → list + counts
//     GET    /api/discount-codes/{id}/redemptions          → invoice drill-down
//
// Design notes
// ------------
// • DB access uses Serenity.Data's SqlMapper (Dapper), exactly like PosApiController.
// • The WhatsApp message reuses dbo.WHATSAPP_CONFIG (HeaderText / FooterText /
//   InstanceId / IsEnabled) and the same Enjazatik endpoint as the POS receipt.
// • Actual redemption (used-count increment + redemption row) happens inside the
//   POS checkout transaction — see PosApiController.Checkout. This controller only
//   *validates* codes for the POS UI and *reports* on them.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dtos = PosDashboard.Web.Modules.System.Models.DiscountCodeDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/discount-codes")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class DiscountCodesApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration _configuration;
        private const string EnjazatikUrl = "https://business.enjazatik.com/api/v1/send-message";

        public DiscountCodesApiController(
            ISqlConnections sqlConnections,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            this.sqlConnections = sqlConnections;
            this.httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // =====================================================================
        // TEMPLATES
        // =====================================================================

        // GET /api/discount-codes/templates
        [HttpGet("templates")]
        public ActionResult<Dtos.ApiResult<List<Dtos.DiscountTemplateDto>>> ListTemplates(
            [FromQuery] bool includeInactive = true)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                string activeFilter = includeInactive ? "" : " AND t.IsActive = 1 ";

                var rows = SqlMapper.Query(conn, $@"
                    SELECT
                        t.Id, t.Name, t.DiscountType, t.DiscountAmount, t.MaxUses,
                        t.ExpiresAfterDays, t.BranchId, t.IsActive, t.CreatedAt,
                        ISNULL(g.GeneratedCount, 0) AS GeneratedCount,
                        ISNULL(u.UsedCount, 0)      AS UsedCount
                    FROM dbo.DiscountTemplates t
                    OUTER APPLY (
                        SELECT COUNT(*) AS GeneratedCount
                        FROM dbo.DiscountCodes dc
                        WHERE dc.TemplateId = t.Id AND dc.Deleted = 0
                    ) g
                    OUTER APPLY (
                        SELECT ISNULL(SUM(dc.UsedCount), 0) AS UsedCount
                        FROM dbo.DiscountCodes dc
                        WHERE dc.TemplateId = t.Id AND dc.Deleted = 0
                    ) u
                    WHERE t.Deleted = 0 {activeFilter}
                    ORDER BY t.Id DESC");

                var list = rows.Select(r => new Dtos.DiscountTemplateDto(
                    Id: (int)r.Id,
                    Name: (string?)r.Name ?? "",
                    DiscountType: (string?)r.DiscountType ?? "value",
                    DiscountAmount: (decimal)r.DiscountAmount,
                    MaxUses: (int)r.MaxUses,
                    ExpiresAfterDays: (int?)r.ExpiresAfterDays,
                    BranchId: (int?)r.BranchId,
                    IsActive: (bool)r.IsActive,
                    CreatedAt: (DateTime)r.CreatedAt,
                    GeneratedCount: (int)r.GeneratedCount,
                    UsedCount: (int)r.UsedCount)).ToList();

                return Ok(new Dtos.ApiResult<List<Dtos.DiscountTemplateDto>>(true, null, list));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<List<Dtos.DiscountTemplateDto>>(false, ex.Message, null));
            }
        }

        // POST /api/discount-codes/templates
        [HttpPost("templates")]
        public ActionResult<Dtos.ApiResult<Dtos.DiscountTemplateDto>> CreateTemplate(
            [FromBody] Dtos.CreateDiscountTemplateRequest request)
        {
            try
            {
                if (request == null) return Fail("Request body is required");

                string name = (request.Name ?? "").Trim();
                if (name.Length == 0) return Fail("Name is required");

                string? type = NormalizeType(request.DiscountType);
                if (type == null) return Fail("DiscountType must be 'value' or 'percentage'");

                if (request.DiscountAmount < 0) return Fail("DiscountAmount cannot be negative");
                if (type == "percentage" && request.DiscountAmount > 100)
                    return Fail("Percentage discount cannot exceed 100");

                int maxUses = request.MaxUses.HasValue && request.MaxUses.Value >= 1 ? request.MaxUses.Value : 1;
                int? expiresAfter = (request.ExpiresAfterDays.HasValue && request.ExpiresAfterDays.Value > 0)
                    ? request.ExpiresAfterDays : (int?)null;

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int userId = ResolveCurrentUserId();

                int newId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.DiscountTemplates
                        (Name, DiscountType, DiscountAmount, MaxUses, ExpiresAfterDays,
                         BranchId, IsActive, Deleted, CreatedBy, CreatedAt)
                    OUTPUT INSERTED.Id
                    VALUES
                        (@Name, @Type, @Amount, @MaxUses, @ExpiresAfter,
                         @BranchId, @IsActive, 0, @CreatedBy, SYSUTCDATETIME())",
                    new
                    {
                        Name = name,
                        Type = type,
                        Amount = request.DiscountAmount,
                        MaxUses = maxUses,
                        ExpiresAfter = expiresAfter,
                        BranchId = request.BranchId,
                        IsActive = request.IsActive ?? true,
                        CreatedBy = userId > 0 ? userId : (int?)null
                    }).First();

                return GetTemplateById(conn, newId);
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<Dtos.DiscountTemplateDto>(false, ex.Message, null));
            }
        }

        // PUT /api/discount-codes/templates/{id}
        [HttpPut("templates/{id:int}")]
        public ActionResult<Dtos.ApiResult<Dtos.DiscountTemplateDto>> UpdateTemplate(
            int id, [FromBody] Dtos.UpdateDiscountTemplateRequest request)
        {
            try
            {
                if (request == null) return Fail("Request body is required");

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var existing = SqlMapper.Query(conn,
                    "SELECT Id, DiscountType FROM dbo.DiscountTemplates WHERE Id = @Id AND Deleted = 0",
                    new { Id = id }).FirstOrDefault();
                if (existing == null) return Fail("Template not found");

                var sets = new List<string>();
                var p = new Dapper.DynamicParameters();
                p.Add("Id", id);

                if (request.Name != null)
                {
                    var nm = request.Name.Trim();
                    if (nm.Length == 0) return Fail("Name cannot be empty");
                    sets.Add("Name = @Name"); p.Add("Name", nm);
                }

                string effectiveType = (string)existing.DiscountType;
                if (request.DiscountType != null)
                {
                    string? t = NormalizeType(request.DiscountType);
                    if (t == null) return Fail("DiscountType must be 'value' or 'percentage'");
                    effectiveType = t;
                    sets.Add("DiscountType = @Type"); p.Add("Type", t);
                }

                if (request.DiscountAmount.HasValue)
                {
                    if (request.DiscountAmount.Value < 0) return Fail("DiscountAmount cannot be negative");
                    if (effectiveType == "percentage" && request.DiscountAmount.Value > 100)
                        return Fail("Percentage discount cannot exceed 100");
                    sets.Add("DiscountAmount = @Amount"); p.Add("Amount", request.DiscountAmount.Value);
                }

                if (request.MaxUses.HasValue)
                {
                    int mu = request.MaxUses.Value < 1 ? 1 : request.MaxUses.Value;
                    sets.Add("MaxUses = @MaxUses"); p.Add("MaxUses", mu);
                }

                if (request.ExpiresAfterDays.HasValue)
                {
                    int? ea = request.ExpiresAfterDays.Value > 0 ? request.ExpiresAfterDays.Value : (int?)null;
                    sets.Add("ExpiresAfterDays = @ExpiresAfter"); p.Add("ExpiresAfter", ea);
                }

                if (request.IsActive.HasValue)
                {
                    sets.Add("IsActive = @IsActive"); p.Add("IsActive", request.IsActive.Value);
                }

                if (sets.Count == 0) return GetTemplateById(conn, id);

                sets.Add("UpdatedAt = SYSUTCDATETIME()");
                SqlMapper.Execute(conn,
                    $"UPDATE dbo.DiscountTemplates SET {string.Join(", ", sets)} WHERE Id = @Id", p);

                return GetTemplateById(conn, id);
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<Dtos.DiscountTemplateDto>(false, ex.Message, null));
            }
        }

        // DELETE /api/discount-codes/templates/{id}  (soft)
        [HttpDelete("templates/{id:int}")]
        public ActionResult<Dtos.ApiResult<bool>> DeleteTemplate(int id)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                int rows = SqlMapper.Execute(conn,
                    "UPDATE dbo.DiscountTemplates SET Deleted = 1, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id AND Deleted = 0",
                    new { Id = id });

                if (rows == 0) return Ok(new Dtos.ApiResult<bool>(false, "Template not found", false));
                return Ok(new Dtos.ApiResult<bool>(true, null, true));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<bool>(false, ex.Message, false));
            }
        }

        // =====================================================================
        // ASSIGN (generate + WhatsApp)
        // =====================================================================

        // POST /api/discount-codes/assign
        [HttpPost("assign")]
        public async Task<ActionResult<Dtos.ApiResult<Dtos.AssignDiscountCodeResponse>>> Assign(
            [FromBody] Dtos.AssignDiscountCodeRequest request)
        {
            try
            {
                if (request == null) return FailAssign("Request body is required");

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var tpl = SqlMapper.Query(conn, @"
                    SELECT Id, Name, DiscountType, DiscountAmount, MaxUses, ExpiresAfterDays, IsActive
                    FROM dbo.DiscountTemplates
                    WHERE Id = @Id AND Deleted = 0",
                    new { Id = request.TemplateId }).FirstOrDefault();
                if (tpl == null) return FailAssign("Discount template not found");
                if (!(bool)tpl.IsActive) return FailAssign("Discount template is inactive");

                var customer = SqlMapper.Query(conn, @"
                    SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1,
                           ISNULL(NotificationLang, 'ar') AS Lang
                    FROM dbo.CUSTOMER
                    WHERE CUSTOMER_ID = @Id",
                    new { Id = request.CustomerId }).FirstOrDefault();
                if (customer == null) return FailAssign("Customer not found");

                int userId = ResolveCurrentUserId();
                string code = GenerateUniqueCode(conn);

                int? expiresAfter = (int?)tpl.ExpiresAfterDays;
                DateTime? expiresAt = expiresAfter.HasValue
                    ? DateTime.UtcNow.Date.AddDays(expiresAfter.Value + 1).AddSeconds(-1) // end of the last valid day
                    : (DateTime?)null;

                int codeId = SqlMapper.Query<int>(conn, @"
                    INSERT INTO dbo.DiscountCodes
                        (Code, TemplateId, DiscountType, DiscountAmount, MaxUses, UsedCount,
                         AssignedCustomerId, AssignedByUserId, AssignedAt, ExpiresAt, WhatsAppSent, Deleted)
                    OUTPUT INSERTED.Id
                    VALUES
                        (@Code, @TemplateId, @Type, @Amount, @MaxUses, 0,
                         @CustomerId, @UserId, SYSUTCDATETIME(), @ExpiresAt, 0, 0)",
                    new
                    {
                        Code = code,
                        TemplateId = (int)tpl.Id,
                        Type = (string)tpl.DiscountType,
                        Amount = (decimal)tpl.DiscountAmount,
                        MaxUses = (int)tpl.MaxUses,
                        CustomerId = request.CustomerId,
                        UserId = userId > 0 ? userId : (int?)null,
                        ExpiresAt = expiresAt
                    }).First();

                // -------- WhatsApp (best-effort) --------
                bool waSent = false; string? waErr = null;
                if (request.SendWhatsApp)
                {
                    try
                    {
                        (waSent, waErr) = await SendCodeWhatsAppAsync(
                            conn,
                            customerPhone: (string?)customer.CUSTOMER_PHONE1 ?? "",
                            customerName: (string?)customer.CUSTOMER_NAME ?? "",
                            customerLang: (string?)customer.Lang ?? "ar",
                            code: code,
                            type: (string)tpl.DiscountType,
                            amount: (decimal)tpl.DiscountAmount,
                            expiresAt: expiresAt);

                        if (waSent)
                            SqlMapper.Execute(conn,
                                "UPDATE dbo.DiscountCodes SET WhatsAppSent = 1 WHERE Id = @Id",
                                new { Id = codeId });
                    }
                    catch (Exception ex) { waSent = false; waErr = $"Send failed: {ex.Message}"; }
                }

                var resp = new Dtos.AssignDiscountCodeResponse(
                    DiscountCodeId: codeId,
                    Code: code,
                    TemplateId: (int)tpl.Id,
                    TemplateName: (string?)tpl.Name ?? "",
                    DiscountType: (string)tpl.DiscountType,
                    DiscountAmount: (decimal)tpl.DiscountAmount,
                    MaxUses: (int)tpl.MaxUses,
                    ExpiresAt: expiresAt,
                    CustomerId: request.CustomerId,
                    CustomerName: (string?)customer.CUSTOMER_NAME ?? "",
                    CustomerPhone: (string?)customer.CUSTOMER_PHONE1 ?? "",
                    WhatsAppSent: waSent,
                    WhatsAppError: waErr);

                return Ok(new Dtos.ApiResult<Dtos.AssignDiscountCodeResponse>(true, null, resp));
            }
            catch (Exception ex)
            {
                return FailAssign($"Failed to assign discount code: {ex.Message}");
            }
        }

        // =====================================================================
        // LOOKUP (POS)
        // =====================================================================

        // GET /api/discount-codes/lookup?code=CARD-######&customerId=
        [HttpGet("lookup")]
        public ActionResult<Dtos.ApiResult<Dtos.DiscountCodeLookupDto>> Lookup(
            [FromQuery] string code, [FromQuery] int? customerId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(code))
                    return Ok(new Dtos.ApiResult<Dtos.DiscountCodeLookupDto>(false, "Code is required", null));

                code = code.Trim().ToUpperInvariant();

                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var dc = SqlMapper.Query(conn, @"
                    SELECT dc.Id, dc.Code, dc.DiscountType, dc.DiscountAmount, dc.MaxUses,
                           dc.UsedCount, dc.ExpiresAt, dc.AssignedCustomerId,
                           c.CUSTOMER_NAME AS CustomerName,
                           t.IsActive AS TemplateActive
                    FROM dbo.DiscountCodes dc
                    LEFT JOIN dbo.CUSTOMER c ON c.CUSTOMER_ID = dc.AssignedCustomerId
                    LEFT JOIN dbo.DiscountTemplates t ON t.Id = dc.TemplateId
                    WHERE dc.Code = @Code AND dc.Deleted = 0",
                    new { Code = code }).FirstOrDefault();

                if (dc == null)
                    return Ok(new Dtos.ApiResult<Dtos.DiscountCodeLookupDto>(false, "Code not found", null));

                DateTime? expiresAt = (DateTime?)dc.ExpiresAt;
                bool isExpired = expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow;
                int used = (int)dc.UsedCount;
                int maxUses = (int)dc.MaxUses;
                int remaining = Math.Max(0, maxUses - used);
                bool templateActive = dc.TemplateActive == null || (bool)dc.TemplateActive;

                string? reason = null;
                bool usable = true;
                if (isExpired) { usable = false; reason = "Code has expired"; }
                else if (remaining <= 0) { usable = false; reason = "Code usage limit reached"; }
                else if (!templateActive) { usable = false; reason = "Discount template is inactive"; }

                var dto = new Dtos.DiscountCodeLookupDto(
                    DiscountCodeId: (int)dc.Id,
                    Code: (string)dc.Code,
                    DiscountType: (string)dc.DiscountType,
                    DiscountAmount: (decimal)dc.DiscountAmount,
                    MaxUses: maxUses,
                    UsedCount: used,
                    RemainingUses: remaining,
                    ExpiresAt: expiresAt,
                    IsExpired: isExpired,
                    IsUsable: usable,
                    AssignedCustomerId: (int)dc.AssignedCustomerId,
                    AssignedCustomerName: (string?)dc.CustomerName ?? "",
                    Reason: reason);

                return Ok(new Dtos.ApiResult<Dtos.DiscountCodeLookupDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<Dtos.DiscountCodeLookupDto>(false, ex.Message, null));
            }
        }

        // GET /api/discount-codes/customer/{customerId}/active
        // Drives the POS alert. Returns the newest still-usable code for the customer, or null Data.
        [HttpGet("customer/{customerId:int}/active")]
        public ActionResult<Dtos.ApiResult<Dtos.CustomerActiveCodeDto>> CustomerActiveCode(int customerId)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var dc = SqlMapper.Query(conn, @"
                    SELECT TOP 1 dc.Id, dc.Code, dc.DiscountType, dc.DiscountAmount,
                           dc.MaxUses, dc.UsedCount, dc.ExpiresAt
                    FROM dbo.DiscountCodes dc
                    LEFT JOIN dbo.DiscountTemplates t ON t.Id = dc.TemplateId
                    WHERE dc.AssignedCustomerId = @CustomerId
                      AND dc.Deleted = 0
                      AND (t.IsActive = 1 OR t.Id IS NULL)
                      AND dc.UsedCount < dc.MaxUses
                      AND (dc.ExpiresAt IS NULL OR dc.ExpiresAt >= SYSUTCDATETIME())
                    ORDER BY dc.Id DESC",
                    new { CustomerId = customerId }).FirstOrDefault();

                if (dc == null)
                    return Ok(new Dtos.ApiResult<Dtos.CustomerActiveCodeDto>(true, null, null));

                var dto = new Dtos.CustomerActiveCodeDto(
                    DiscountCodeId: (int)dc.Id,
                    Code: (string)dc.Code,
                    DiscountType: (string)dc.DiscountType,
                    DiscountAmount: (decimal)dc.DiscountAmount,
                    RemainingUses: Math.Max(0, (int)dc.MaxUses - (int)dc.UsedCount),
                    ExpiresAt: (DateTime?)dc.ExpiresAt);

                return Ok(new Dtos.ApiResult<Dtos.CustomerActiveCodeDto>(true, null, dto));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<Dtos.CustomerActiveCodeDto>(false, ex.Message, null));
            }
        }

        // =====================================================================
        // LIST (Discount Codes screen)
        // =====================================================================

        // GET /api/discount-codes?filter=total|not_used|used|partially_used|expire&search=&branchId=
        [HttpGet]
        public ActionResult<Dtos.ApiResult<Dtos.DiscountCodeListResponse>> List(
            [FromQuery] string? filter = "total",
            [FromQuery] string? search = null,
            [FromQuery] int? templateId = null)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                // Pull every code + its LATEST redemption (invoice snapshot) in one shot.
                var rows = SqlMapper.Query(conn, @"
                    SELECT
                        dc.Id, dc.Code, dc.TemplateId, dc.DiscountType, dc.DiscountAmount,
                        dc.MaxUses, dc.UsedCount, dc.ExpiresAt, dc.AssignedCustomerId,
                        dc.AssignedByUserId, dc.AssignedAt,
                        t.Name AS TemplateName,
                        ac.CUSTOMER_NAME  AS AssignedCustomerName,
                        ac.CUSTOMER_PHONE1 AS AssignedCustomerPhone,
                        au.USER_NAME      AS AssignedByUserName,
                        r.InvoiceId, r.InvoiceNumber, r.InvoiceValue,
                        r.DiscountAmount  AS RedeemedDiscountAmount,
                        r.RedeemedByCustomerId, r.RedeemedAt,
                        rc.CUSTOMER_NAME  AS RedeemedByCustomerName,
                        inv.AppointmentId AS InvoiceAppointmentId
                    FROM dbo.DiscountCodes dc
                    LEFT JOIN dbo.DiscountTemplates t ON t.Id = dc.TemplateId
                    LEFT JOIN dbo.CUSTOMER ac ON ac.CUSTOMER_ID = dc.AssignedCustomerId
                    LEFT JOIN dbo.[USER]   au ON au.USER_ID     = dc.AssignedByUserId
                    OUTER APPLY (
                        SELECT TOP 1 x.InvoiceId, x.InvoiceNumber, x.InvoiceValue,
                                     x.DiscountAmount, x.RedeemedByCustomerId, x.RedeemedAt
                        FROM dbo.DiscountCodeRedemptions x
                        WHERE x.DiscountCodeId = dc.Id
                        ORDER BY x.Id DESC
                    ) r
                    LEFT JOIN dbo.CUSTOMER rc ON rc.CUSTOMER_ID = r.RedeemedByCustomerId
                    LEFT JOIN dbo.AppointmentInvoices inv ON inv.Id = r.InvoiceId
                    WHERE dc.Deleted = 0
                    ORDER BY dc.Id DESC");

                var now = DateTime.UtcNow;
                var all = new List<Dtos.DiscountCodeListItemDto>();

                foreach (var r in rows)
                {
                    DateTime? expiresAt = (DateTime?)r.ExpiresAt;
                    bool isExpired = expiresAt.HasValue && expiresAt.Value < now;
                    int used = (int)r.UsedCount;
                    int maxUses = (int)r.MaxUses;
                    int remaining = Math.Max(0, maxUses - used);

                    string status;
                    if (used <= 0) status = isExpired ? "expired" : "not_used";
                    else if (remaining <= 0) status = "used";
                    else status = isExpired ? "expired" : "partially_used";

                    all.Add(new Dtos.DiscountCodeListItemDto(
                        DiscountCodeId: (int)r.Id,
                        Code: (string)r.Code,
                        TemplateId: (int)r.TemplateId,
                        TemplateName: (string?)r.TemplateName ?? "",
                        DiscountType: (string)r.DiscountType,
                        DiscountAmount: (decimal)r.DiscountAmount,
                        MaxUses: maxUses,
                        UsedCount: used,
                        RemainingUses: remaining,
                        Status: status,
                        ExpiresAt: expiresAt,
                        IsExpired: isExpired,
                        AssignedCustomerId: (int)r.AssignedCustomerId,
                        AssignedCustomerName: (string?)r.AssignedCustomerName ?? "",
                        AssignedCustomerPhone: (string?)r.AssignedCustomerPhone ?? "",
                        AssignedByUserId: (int?)r.AssignedByUserId,
                        AssignedByUserName: (string?)r.AssignedByUserName,
                        AssignedAt: (DateTime)r.AssignedAt,
                        RedeemedByCustomerId: (int?)r.RedeemedByCustomerId,
                        RedeemedByCustomerName: (string?)r.RedeemedByCustomerName,
                        RedeemedAt: (DateTime?)r.RedeemedAt,
                        InvoiceId: (int?)r.InvoiceId,
                        InvoiceAppointmentId: (int?)r.InvoiceAppointmentId,
                        InvoiceNumber: (string?)r.InvoiceNumber,
                        InvoiceValue: (decimal?)r.InvoiceValue,
                        RedeemedDiscountAmount: (decimal?)r.RedeemedDiscountAmount));
                }

                if (templateId.HasValue)
                    all = all.Where(x => x.TemplateId == templateId.Value).ToList();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    string s = search.Trim().ToLowerInvariant();
                    all = all.Where(x =>
                        x.Code.ToLowerInvariant().Contains(s) ||
                        x.AssignedCustomerName.ToLowerInvariant().Contains(s) ||
                        x.AssignedCustomerPhone.ToLowerInvariant().Contains(s) ||
                        (x.InvoiceNumber ?? "").ToLowerInvariant().Contains(s)).ToList();
                }

                var counts = new Dtos.DiscountCodeCountsDto(
                    Total: all.Count,
                    NotUsed: all.Count(x => x.Status == "not_used"),
                    Used: all.Count(x => x.Status == "used"),
                    PartiallyUsed: all.Count(x => x.Status == "partially_used"),
                    Expired: all.Count(x => x.Status == "expired"));

                var filtered = ApplyStatusFilter(all, filter);

                return Ok(new Dtos.ApiResult<Dtos.DiscountCodeListResponse>(
                    true, null, new Dtos.DiscountCodeListResponse(filtered, counts)));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<Dtos.DiscountCodeListResponse>(false, ex.Message, null));
            }
        }

        // GET /api/discount-codes/{id}/redemptions
        [HttpGet("{id:int}/redemptions")]
        public ActionResult<Dtos.ApiResult<List<Dtos.DiscountCodeRedemptionDto>>> Redemptions(int id)
        {
            try
            {
                using var conn = sqlConnections.NewByKey("Default");
                if (conn.State != ConnectionState.Open) conn.Open();

                var rows = SqlMapper.Query(conn, @"
                    SELECT r.Id, r.InvoiceId, r.InvoiceNumber, r.InvoiceValue, r.DiscountAmount,
                           r.RedeemedByCustomerId, r.RedeemedByUserId, r.RedeemedAt,
                           rc.CUSTOMER_NAME AS RedeemedByCustomerName,
                           ru.USER_NAME     AS RedeemedByUserName,
                           inv.AppointmentId AS InvoiceAppointmentId
                    FROM dbo.DiscountCodeRedemptions r
                    LEFT JOIN dbo.CUSTOMER rc ON rc.CUSTOMER_ID = r.RedeemedByCustomerId
                    LEFT JOIN dbo.[USER]   ru ON ru.USER_ID     = r.RedeemedByUserId
                    LEFT JOIN dbo.AppointmentInvoices inv ON inv.Id = r.InvoiceId
                    WHERE r.DiscountCodeId = @Id
                    ORDER BY r.Id DESC",
                    new { Id = id });

                var list = rows.Select(r => new Dtos.DiscountCodeRedemptionDto(
                    Id: (int)r.Id,
                    InvoiceId: (int?)r.InvoiceId,
                    InvoiceAppointmentId: (int?)r.InvoiceAppointmentId,
                    InvoiceNumber: (string?)r.InvoiceNumber,
                    InvoiceValue: (decimal?)r.InvoiceValue,
                    DiscountAmount: (decimal)r.DiscountAmount,
                    RedeemedByCustomerId: (int?)r.RedeemedByCustomerId,
                    RedeemedByCustomerName: (string?)r.RedeemedByCustomerName,
                    RedeemedByUserId: (int?)r.RedeemedByUserId,
                    RedeemedByUserName: (string?)r.RedeemedByUserName,
                    RedeemedAt: (DateTime)r.RedeemedAt)).ToList();

                return Ok(new Dtos.ApiResult<List<Dtos.DiscountCodeRedemptionDto>>(true, null, list));
            }
            catch (Exception ex)
            {
                return Ok(new Dtos.ApiResult<List<Dtos.DiscountCodeRedemptionDto>>(false, ex.Message, null));
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private ActionResult<Dtos.ApiResult<Dtos.DiscountTemplateDto>> Fail(string msg) =>
            Ok(new Dtos.ApiResult<Dtos.DiscountTemplateDto>(false, msg, null));

        private ActionResult<Dtos.ApiResult<Dtos.AssignDiscountCodeResponse>> FailAssign(string msg) =>
            Ok(new Dtos.ApiResult<Dtos.AssignDiscountCodeResponse>(false, msg, null));

        private ActionResult<Dtos.ApiResult<Dtos.DiscountTemplateDto>> GetTemplateById(IDbConnection conn, int id)
        {
            var r = SqlMapper.Query(conn, @"
                SELECT
                    t.Id, t.Name, t.DiscountType, t.DiscountAmount, t.MaxUses,
                    t.ExpiresAfterDays, t.BranchId, t.IsActive, t.CreatedAt,
                    ISNULL((SELECT COUNT(*) FROM dbo.DiscountCodes dc WHERE dc.TemplateId = t.Id AND dc.Deleted = 0), 0) AS GeneratedCount,
                    ISNULL((SELECT SUM(dc.UsedCount) FROM dbo.DiscountCodes dc WHERE dc.TemplateId = t.Id AND dc.Deleted = 0), 0) AS UsedCount
                FROM dbo.DiscountTemplates t
                WHERE t.Id = @Id",
                new { Id = id }).FirstOrDefault();

            if (r == null) return Fail("Template not found after save");

            var dto = new Dtos.DiscountTemplateDto(
                Id: (int)r.Id,
                Name: (string?)r.Name ?? "",
                DiscountType: (string?)r.DiscountType ?? "value",
                DiscountAmount: (decimal)r.DiscountAmount,
                MaxUses: (int)r.MaxUses,
                ExpiresAfterDays: (int?)r.ExpiresAfterDays,
                BranchId: (int?)r.BranchId,
                IsActive: (bool)r.IsActive,
                CreatedAt: (DateTime)r.CreatedAt,
                GeneratedCount: (int)r.GeneratedCount,
                UsedCount: (int)r.UsedCount);

            return Ok(new Dtos.ApiResult<Dtos.DiscountTemplateDto>(true, null, dto));
        }

        private static List<Dtos.DiscountCodeListItemDto> ApplyStatusFilter(
            List<Dtos.DiscountCodeListItemDto> all, string? filter)
        {
            switch ((filter ?? "total").Trim().ToLowerInvariant())
            {
                case "not_used":
                case "notused":
                    return all.Where(x => x.Status == "not_used").ToList();
                case "used":
                    return all.Where(x => x.Status == "used").ToList();
                case "partially_used":
                case "partial":
                    return all.Where(x => x.Status == "partially_used").ToList();
                case "expire":
                case "expired":
                    return all.Where(x => x.Status == "expired").ToList();
                default:
                    return all;
            }
        }

        private static string? NormalizeType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            string t = type.Trim().ToLowerInvariant();
            if (t == "value" || t == "fixed" || t == "amount") return "value";
            if (t == "percentage" || t == "percent" || t == "%") return "percentage";
            return null;
        }

        // CARD-###### with a unique 6-digit tail (checked against the table).
        private static string GenerateUniqueCode(IDbConnection conn)
        {
            var rng = new Random();
            for (int attempt = 0; attempt < 50; attempt++)
            {
                string tail = rng.Next(0, 1_000_000).ToString("D6");
                string candidate = $"CARD-{tail}";
                int exists = SqlMapper.Query<int>(conn,
                    "SELECT COUNT(*) FROM dbo.DiscountCodes WHERE Code = @Code",
                    new { Code = candidate }).FirstOrDefault();
                if (exists == 0) return candidate;
            }
            // Extremely unlikely fallback — widen the space.
            return $"CARD-{DateTime.UtcNow:ffffff}";
        }

        private int ResolveCurrentUserId()
        {
            var idClaim =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                User.FindFirst("sub")?.Value ??
                User.FindFirst("UserId")?.Value;
            return int.TryParse(idClaim, out var id) ? id : 0;
        }

        private async Task<(bool Sent, string? Error)> SendCodeWhatsAppAsync(
            IDbConnection conn, string customerPhone, string customerName, string customerLang,
            string code, string type, decimal amount, DateTime? expiresAt)
        {
            var config = SqlMapper.Query(conn, @"
                SELECT TOP 1 HeaderText, FooterText, InstanceId, IsEnabled
                FROM dbo.WHATSAPP_CONFIG ORDER BY Id").FirstOrDefault();

            if (config == null || !(bool)config.IsEnabled)
                return (false, "WhatsApp sending is disabled");

            string header = (string?)config.HeaderText ?? "";
            string footer = (string?)config.FooterText ?? "";
            string instanceId = (string?)config.InstanceId ?? "51d2e384a1ef86b";

            string message = customerLang == "en"
                ? BuildCodeMessageEn(header, footer, customerName, code, type, amount, expiresAt)
                : BuildCodeMessageAr(header, footer, customerName, code, type, amount, expiresAt);

            string phone = NormalizePhone(customerPhone);
            if (phone.Length == 0) return (false, "Customer has no phone number");

            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _configuration["WhatsApp:ApiKey"] ?? "");

            var payload = new { instance_id = instanceId, message, number = phone };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(EnjazatikUrl, content);
            if (resp.IsSuccessStatusCode) return (true, null);

            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"API error: {resp.StatusCode} — {body}");
        }

        private static string BuildCodeMessageEn(
            string header, string footer, string customerName,
            string code, string type, decimal amount, DateTime? expiresAt)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
            string value = type == "percentage" ? $"{amount:0.##}%" : $"{amount:0.##}";
            if (!string.IsNullOrWhiteSpace(customerName)) sb.AppendLine($"Hello {customerName},");
            sb.AppendLine("🎁 You've received a discount code!");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"Code: {code}");
            sb.AppendLine($"Discount: {value}");
            if (expiresAt.HasValue) sb.AppendLine($"Valid until: {expiresAt.Value:yyyy-MM-dd}");
            sb.AppendLine("Applies to services only.");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string BuildCodeMessageAr(
            string header, string footer, string customerName,
            string code, string type, decimal amount, DateTime? expiresAt)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) { sb.AppendLine(header); sb.AppendLine(); }
            string value = type == "percentage" ? $"{amount:0.##}%" : $"{amount:0.##}";
            if (!string.IsNullOrWhiteSpace(customerName)) sb.AppendLine($"مرحباً {customerName}،");
            sb.AppendLine("🎁 وصلك كود خصم!");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"الكود: {code}");
            sb.AppendLine($"قيمة الخصم: {value}");
            if (expiresAt.HasValue) sb.AppendLine($"صالح حتى: {expiresAt.Value:yyyy-MM-dd}");
            sb.AppendLine("يُطبَّق على الخدمات فقط.");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━");
            if (!string.IsNullOrWhiteSpace(footer)) { sb.AppendLine(); sb.AppendLine(footer); }
            return sb.ToString();
        }

        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("0")) digits = "965" + digits.Substring(1);
            if (digits.Length == 8) digits = "965" + digits;
            return digits;
        }
    }
}
