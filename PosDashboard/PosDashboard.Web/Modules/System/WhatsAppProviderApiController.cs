// Modules/System/Controllers/WhatsAppProviderApiController.cs
//
// Reads and writes the transport configuration behind Settings → WhatsApp.
//
// The switch itself (whatsapp.provider) lives in dbo.BusinessSetting so it
// appears in the generic System Settings list alongside every other flag. The
// credentials live in dbo.WHATSAPP_CONFIG, because BusinessSetting is readable
// by any signed-in user and an API token is not something to hand out.
//
// Tokens are write-only over this API: the read model reports whether one is
// saved and the last four characters, which is enough to tell two keys apart
// without ever putting a secret on the wire.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PosDashboard.Web.Modules.System.Models;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WhatsAppProviderDtos;
using static PosDashboard.Web.Modules.System.Models.WhatsAppDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/whatsapp/provider")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class WhatsAppProviderApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IWhatsAppSender sender;

        public WhatsAppProviderApiController(ISqlConnections sqlConnections, IWhatsAppSender sender)
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
        // GET /api/whatsapp/provider
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ActionResult<ApiResult<ProviderConfigDto>> Get()
        {
            using var conn = sqlConnections.NewByKey("Default");
            return Ok(new ApiResult<ProviderConfigDto>(true, null, ReadConfig(conn)));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/provider
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("")]
        public ActionResult<ApiResult<ProviderConfigDto>> Update(
            [FromBody] UpdateProviderConfigRequest request)
        {
            if (request == null)
                return Ok(new ApiResult<ProviderConfigDto>(false, "Nothing to save", null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            // Reject a choice that cannot work before it silently swallows a
            // customer's confirmation.
            if (!string.IsNullOrWhiteSpace(request.Provider))
            {
                var target = WhatsAppProviders.Normalize(request.Provider);
                if (target == WhatsAppProviders.Cartley)
                {
                    var hasToken = !string.IsNullOrWhiteSpace(request.CartleyToken)
                                   || !string.IsNullOrWhiteSpace(ReadSecret(conn, "CartleyToken"));
                    if (!hasToken)
                        return Ok(new ApiResult<ProviderConfigDto>(false,
                            "Add the Cartley Connect API token before switching to it.", null));
                }
            }

            using (var uow = new UnitOfWork(conn))
            {
                EnsureConfigRow(uow.Connection);
                SaveCredentials(uow.Connection, request);
                SaveFlags(uow.Connection, request);
                uow.Commit();
            }

            return Ok(new ApiResult<ProviderConfigDto>(true, null, ReadConfig(conn)));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/whatsapp/provider/test
        // Sends a short message so the operator can prove the wiring works
        // without booking a fake appointment.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("test")]
        public async Task<ActionResult<ApiResult<WhatsAppSendResult>>> Test(
            [FromBody] TestSendRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Phone))
                return Ok(new ApiResult<WhatsAppSendResult>(false,
                    "Enter a phone number to test with", null));

            using var conn = sqlConnections.NewByKey("Default");

            var cfg = sender.LoadConfig(conn);

            // Testing a provider you have not switched to yet is the whole point.
            if (!string.IsNullOrWhiteSpace(request.Provider))
                cfg.Provider = WhatsAppProviders.Normalize(request.Provider);

            // A disabled system should still be testable — otherwise there is no
            // way to verify a new token before turning notifications back on.
            cfg.Enabled = true;

            var body = string.IsNullOrWhiteSpace(request.Message)
                ? "✅ WhatsApp test message — your booking system is connected."
                : request.Message!;

            var result = await sender.SendAsync(conn, request.Phone, body,
                new WhatsAppContext(
                    MessageType: WhatsAppMessageTypes.Test,
                    UserId: CurrentUserId()),
                requiresRealtime: false,
                config: cfg);

            return Ok(new ApiResult<WhatsAppSendResult>(true, null, result));
        }

        #region Read / write

        private ProviderConfigDto ReadConfig(IDbConnection conn)
        {
            var cfg = sender.LoadConfig(conn);
            var cartleyToken = cfg.CartleyToken ?? "";

            return new ProviderConfigDto(
                Provider: cfg.Provider,
                Enabled: cfg.Enabled,
                DefaultCountryCode: cfg.DefaultCountryCode,
                LinkTarget: cfg.LinkTarget,
                RealtimeProvider: cfg.RealtimeProvider,
                InstanceId: cfg.InstanceId,
                EnjazatikTokenIsSet: !string.IsNullOrWhiteSpace(cfg.EnjazatikToken),
                EnjazatikTokenHint: Hint(cfg.EnjazatikToken),
                CartleyBaseUrl: cfg.CartleyBaseUrl,
                CartleySendPath: cfg.CartleySendPath,
                CartleySenderId: cfg.CartleySenderId,
                CartleyTokenIsSet: !string.IsNullOrWhiteSpace(cartleyToken),
                CartleyTokenHint: Hint(cartleyToken),
                CartleyFieldMap: cfg.CartleyFieldMap,
                OutboxRetentionDays: BusinessSettingsService.GetInt(conn, "whatsapp.outbox.retentionDays", 30));
        }

        private static string? Hint(string? token) =>
            string.IsNullOrWhiteSpace(token) || token.Length < 4
                ? null
                : "····" + token.Substring(token.Length - 4);

        private static string? ReadSecret(IDbConnection conn, string column)
        {
            return SqlMapper.Query<string>(conn,
                $"SELECT TOP 1 {column} FROM dbo.WHATSAPP_CONFIG ORDER BY Id").FirstOrDefault();
        }

        private static void EnsureConfigRow(IDbConnection conn)
        {
            var exists = SqlMapper.Query<int>(conn,
                "SELECT COUNT(*) FROM dbo.WHATSAPP_CONFIG").FirstOrDefault();
            if (exists > 0) return;

            SqlMapper.Execute(conn, @"
                INSERT INTO dbo.WHATSAPP_CONFIG (HeaderText, FooterText, InstanceId, IsEnabled, UpdatedAt)
                VALUES ('', '', '51d2e384a1ef86b', 1, SYSUTCDATETIME())");
        }

        private static void SaveCredentials(IDbConnection conn, UpdateProviderConfigRequest req)
        {
            var sets = new List<string>();
            var p = new Dapper.DynamicParameters();

            void Set(string column, string? value)
            {
                if (value == null) return;
                sets.Add($"{column} = @{column}");
                p.Add(column, value.Trim());
            }

            Set("InstanceId", req.InstanceId);
            Set("CartleyBaseUrl", req.CartleyBaseUrl);
            Set("CartleySendPath", req.CartleySendPath);
            Set("CartleySenderId", req.CartleySenderId);
            Set("CartleyFieldMap", req.CartleyFieldMap);

            // A blank token box means "leave what is stored alone" — the field is
            // rendered empty on every load, so treating blank as "erase" would
            // wipe a working key the first time someone edits the sender name.
            if (req.ClearCartleyToken)
            {
                sets.Add("CartleyToken = NULL");
            }
            else if (!string.IsNullOrWhiteSpace(req.CartleyToken))
            {
                sets.Add("CartleyToken = @CartleyToken");
                p.Add("CartleyToken", req.CartleyToken!.Trim());
            }

            if (req.Enabled.HasValue)
            {
                sets.Add("IsEnabled = @IsEnabled");
                p.Add("IsEnabled", req.Enabled.Value);
            }

            if (sets.Count == 0) return;

            sets.Add("UpdatedAt = SYSUTCDATETIME()");

            SqlMapper.Execute(conn,
                $@"UPDATE dbo.WHATSAPP_CONFIG SET {string.Join(", ", sets)}
                   WHERE Id = (SELECT TOP 1 Id FROM dbo.WHATSAPP_CONFIG ORDER BY Id)", p);
        }

        private void SaveFlags(IDbConnection conn, UpdateProviderConfigRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.Provider))
                UpsertFlag(conn, "whatsapp.provider", WhatsAppProviders.Normalize(req.Provider));

            if (!string.IsNullOrWhiteSpace(req.DefaultCountryCode))
            {
                var digits = new string(req.DefaultCountryCode!.Where(char.IsDigit).ToArray());
                if (digits.Length > 0) UpsertFlag(conn, "whatsapp.defaultCountryCode", digits);
            }

            if (!string.IsNullOrWhiteSpace(req.LinkTarget))
            {
                var t = req.LinkTarget!.Trim().ToLowerInvariant();
                UpsertFlag(conn, "whatsapp.link.target", t is "web" or "app" ? t : "auto");
            }

            if (req.OutboxRetentionDays.HasValue)
            {
                // One day floor, three years ceiling. Zero would mean "delete on
                // sight", which destroys the only record of what was sent.
                var days = Math.Clamp(req.OutboxRetentionDays.Value, 1, 1095);
                UpsertFlag(conn, "whatsapp.outbox.retentionDays", days.ToString());
            }

            if (!string.IsNullOrWhiteSpace(req.RealtimeProvider))
            {
                var r = WhatsAppProviders.Normalize(req.RealtimeProvider);
                if (r == WhatsAppProviders.Link) r = WhatsAppProviders.Enjazatik;
                UpsertFlag(conn, "whatsapp.link.realtimeProvider", r);
            }
        }

        /// <summary>Global scope only — the transport is an account-level decision.</summary>
        private void UpsertFlag(IDbConnection conn, string key, string value)
        {
            var affected = SqlMapper.Execute(conn, @"
                UPDATE dbo.BusinessSetting
                SET SettingValue = @Value, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @UserId
                WHERE SettingKey = @Key AND BranchId IS NULL",
                new { Key = key, Value = value, UserId = CurrentUserId() });

            if (affected > 0) return;

            // The migration seeds these rows; this is the safety net for an
            // install where it has not been run yet.
            SqlMapper.Execute(conn, @"
                INSERT INTO dbo.BusinessSetting
                    (SettingKey, SettingValue, ValueType, Category,
                     DisplayNameEn, DisplayNameAr, BranchId, IsEditable, Ordering,
                     CreatedAt, UpdatedAt, UpdatedBy)
                VALUES
                    (@Key, @Value, 'string', 'WhatsApp',
                     @Key, @Key, NULL, 1, 400,
                     SYSUTCDATETIME(), SYSUTCDATETIME(), @UserId)",
                new { Key = key, Value = value, UserId = CurrentUserId() });
        }

        #endregion
    }
}
