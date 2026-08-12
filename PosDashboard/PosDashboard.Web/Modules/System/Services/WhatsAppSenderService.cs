// Modules/System/Services/WhatsAppSenderService.cs
//
// One place that knows how a WhatsApp message leaves the system.
//
// Before this file, twelve call sites each built their own HttpClient, read
// WHATSAPP_CONFIG themselves and POSTed to Enjazatik. Adding a second provider
// that way would have meant twelve copies of the same branch — and the day one
// of them was missed, a customer would silently get nothing. So the transport
// moved here and the call sites now say what they want sent, not how.
//
// Three transports:
//   enjazatik  – the original API. Unchanged behaviour, unchanged payload.
//   cartley    – Cartley Connect. Endpoint, path and JSON field names are
//                configuration, not code, so the contract can be corrected
//                from the settings screen without a redeploy.
//   link       – nothing is sent automatically. The message is queued with a
//                ready-made wa.me link and a person presses send.
//
// Every send is written to dbo.WHATSAPP_OUTBOX regardless of transport, which
// gives support one table to look at when a customer says "I never got it".

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WhatsAppProviderDtos;
using PosDashboard.Web.Modules.System.Models;

namespace PosDashboard.Web.Modules.System.Services
{
    public interface IWhatsAppSender
    {
        WhatsAppRuntimeConfig LoadConfig(IDbConnection conn, int? branchId = null);

        Task<WhatsAppSendResult> SendAsync(
            IDbConnection conn,
            string rawPhone,
            string message,
            WhatsAppContext? context = null,
            bool requiresRealtime = false,
            WhatsAppRuntimeConfig? config = null);

        string NormalizePhone(string? phone, string? defaultCountryCode = null);

        string BuildWaLink(string phone, string message, string target);

        int PurgeHandledOlderThan(IDbConnection conn, int days);
    }

    public sealed class WhatsAppSenderService : IWhatsAppSender
    {
        public const string EnjazatikUrl = "https://business.enjazatik.com/api/v1/send-message";
        private const string LegacyInstanceId = "51d2e384a1ef86b";

        private readonly IHttpClientFactory httpClientFactory;
        private readonly IConfiguration configuration;
        private readonly ILogger<WhatsAppSenderService>? logger;

        public WhatsAppSenderService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<WhatsAppSenderService>? logger = null)
        {
            this.httpClientFactory = httpClientFactory;
            this.configuration = configuration;
            this.logger = logger;
        }

        // ═════════════════════════════════════════════════════════════════
        // Configuration
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// WHATSAPP_CONFIG supplies the credentials, BusinessSetting supplies the
        /// switch. Columns are read defensively so an install that has not run the
        /// migration yet still sends through Enjazatik instead of throwing.
        /// </summary>
        public WhatsAppRuntimeConfig LoadConfig(IDbConnection conn, int? branchId = null)
        {
            var cfg = new WhatsAppRuntimeConfig();

            var row = SqlMapper.Query(conn,
                "SELECT TOP 1 * FROM dbo.WHATSAPP_CONFIG ORDER BY Id").FirstOrDefault()
                as IDictionary<string, object>;

            if (row != null)
            {
                cfg.Enabled = Bool(row, "IsEnabled", true);
                cfg.Header = Str(row, "HeaderText") ?? "";
                cfg.Footer = Str(row, "FooterText") ?? "";
                cfg.InstanceId = Str(row, "InstanceId") ?? LegacyInstanceId;
                cfg.CartleyBaseUrl = Str(row, "CartleyBaseUrl") ?? cfg.CartleyBaseUrl;
                cfg.CartleySendPath = Str(row, "CartleySendPath") ?? cfg.CartleySendPath;
                cfg.CartleyToken = Str(row, "CartleyToken") ?? "";
                cfg.CartleySenderId = Str(row, "CartleySenderId") ?? "";
                cfg.CartleyFieldMap = Str(row, "CartleyFieldMap");
            }
            else
            {
                // No config row at all — keep the historical default rather than
                // silently disabling every notification in the system.
                cfg.Enabled = true;
                cfg.InstanceId = LegacyInstanceId;
            }

            // Secrets prefer appsettings; the DB column is the fallback so an
            // operator can fix a token without shell access to the server.
            var enjazatikFromConfig = configuration["WhatsApp:ApiKey"];
            cfg.EnjazatikToken = !string.IsNullOrWhiteSpace(enjazatikFromConfig)
                ? enjazatikFromConfig!
                : "";

            var cartleyFromConfig = configuration["WhatsApp:Cartley:Token"];
            if (!string.IsNullOrWhiteSpace(cartleyFromConfig))
                cfg.CartleyToken = cartleyFromConfig!;

            cfg.Provider = WhatsAppProviders.Normalize(
                BusinessSettingsService.GetValue(conn, "whatsapp.provider", branchId));

            cfg.DefaultCountryCode = Digits(
                BusinessSettingsService.GetValue(conn, "whatsapp.defaultCountryCode", branchId));
            if (string.IsNullOrEmpty(cfg.DefaultCountryCode)) cfg.DefaultCountryCode = "965";

            var target = (BusinessSettingsService.GetValue(conn, "whatsapp.link.target", branchId) ?? "auto")
                .Trim().ToLowerInvariant();
            cfg.LinkTarget = target is "web" or "app" ? target : "auto";

            cfg.RealtimeProvider = WhatsAppProviders.Normalize(
                BusinessSettingsService.GetValue(conn, "whatsapp.link.realtimeProvider", branchId));
            if (cfg.RealtimeProvider == WhatsAppProviders.Link)
                cfg.RealtimeProvider = WhatsAppProviders.Enjazatik;

            return cfg;
        }

        // ═════════════════════════════════════════════════════════════════
        // Sending
        // ═════════════════════════════════════════════════════════════════

        /// <param name="requiresRealtime">
        /// True for messages a customer is actively waiting on — a login or
        /// booking code. Those can never sit in a queue waiting for staff, so
        /// while the link method is selected they are routed to the API named by
        /// whatsapp.link.realtimeProvider instead.
        /// </param>
        public async Task<WhatsAppSendResult> SendAsync(
            IDbConnection conn,
            string rawPhone,
            string message,
            WhatsAppContext? context = null,
            bool requiresRealtime = false,
            WhatsAppRuntimeConfig? config = null)
        {
            var cfg = config ?? LoadConfig(conn, context?.BranchId);

            if (!cfg.Enabled)
                return WhatsAppSendResult.Disabled();

            var phone = NormalizePhone(rawPhone, cfg.DefaultCountryCode);
            if (string.IsNullOrWhiteSpace(phone))
                return WhatsAppSendResult.Fail("", cfg.Provider, "No usable phone number");

            if (string.IsNullOrWhiteSpace(message))
                return WhatsAppSendResult.Fail(phone, cfg.Provider, "Nothing to send — the message is empty");

            var provider = cfg.Provider;

            // A code cannot wait for a human. Reroute it.
            if (provider == WhatsAppProviders.Link && requiresRealtime)
            {
                provider = cfg.RealtimeProvider;
                if (provider == WhatsAppProviders.None)
                {
                    var res = WhatsAppSendResult.Fail(phone, WhatsAppProviders.None,
                        "Verification codes are turned off while WhatsApp is set to open manually");
                    Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.None, phone, message, null, res.Error, context);
                    return res;
                }
            }

            try
            {
                return provider switch
                {
                    WhatsAppProviders.Link => Queue(conn, cfg, phone, message, context),
                    WhatsAppProviders.Cartley => await SendViaCartleyAsync(conn, cfg, phone, message, context),
                    _ => await SendViaEnjazatikAsync(conn, cfg, phone, message, context)
                };
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "WhatsApp send failed for {Phone} via {Provider}", phone, provider);
                var error = $"Send failed: {ex.Message}";
                Log(conn, WhatsAppOutboxStatus.Failed, provider, phone, message, null, error, context);
                return WhatsAppSendResult.Fail(phone, provider, error);
            }
        }

        // ── enjazatik ────────────────────────────────────────────────────

        private async Task<WhatsAppSendResult> SendViaEnjazatikAsync(
            IDbConnection conn, WhatsAppRuntimeConfig cfg,
            string phone, string message, WhatsAppContext? context)
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.EnjazatikToken ?? "");

            var payload = new
            {
                instance_id = string.IsNullOrWhiteSpace(cfg.InstanceId) ? LegacyInstanceId : cfg.InstanceId,
                message,
                number = phone
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(EnjazatikUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Log(conn, WhatsAppOutboxStatus.Sent, WhatsAppProviders.Enjazatik, phone, message, null, null, context);
                return WhatsAppSendResult.Ok(phone, WhatsAppProviders.Enjazatik);
            }

            var error = $"API error: {(int)response.StatusCode} {response.StatusCode} — {Trim(body, 400)}";
            Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.Enjazatik, phone, message, null, error, context);
            return WhatsAppSendResult.Fail(phone, WhatsAppProviders.Enjazatik, error);
        }

        // ── cartley ──────────────────────────────────────────────────────

        /// <summary>
        /// Cartley Connect. The JSON keys come from CartleyFieldMap so the shape
        /// can be matched to the published contract from the admin screen:
        ///   {"to":"to","message":"message","sender":"sender","type":"type"}
        /// A key mapped to an empty string is left out of the body entirely.
        /// </summary>
        private async Task<WhatsAppSendResult> SendViaCartleyAsync(
            IDbConnection conn, WhatsAppRuntimeConfig cfg,
            string phone, string message, WhatsAppContext? context)
        {
            if (string.IsNullOrWhiteSpace(cfg.CartleyToken))
            {
                const string missing = "Cartley Connect has no API token saved. Add one in Settings → WhatsApp.";
                Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.Cartley, phone, message, null, missing, context);
                return WhatsAppSendResult.Fail(phone, WhatsAppProviders.Cartley, missing);
            }

            var map = ParseFieldMap(cfg.CartleyFieldMap);

            var body = new Dictionary<string, object?>();
            void Put(string logicalKey, object? value)
            {
                if (value == null) return;
                if (!map.TryGetValue(logicalKey, out var name)) name = logicalKey;
                if (string.IsNullOrWhiteSpace(name)) return;   // explicitly suppressed
                body[name] = value;
            }

            Put("to", phone);
            Put("message", message);
            Put("type", "text");
            if (!string.IsNullOrWhiteSpace(cfg.CartleySenderId))
                Put("sender", cfg.CartleySenderId);

            var url = CombineUrl(cfg.CartleyBaseUrl, cfg.CartleySendPath);

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", cfg.CartleyToken);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var raw = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode && !LooksLikeFailureBody(raw))
            {
                Log(conn, WhatsAppOutboxStatus.Sent, WhatsAppProviders.Cartley, phone, message, null, null, context);
                return WhatsAppSendResult.Ok(phone, WhatsAppProviders.Cartley);
            }

            var error = response.IsSuccessStatusCode
                ? $"Cartley rejected the message — {Trim(raw, 400)}"
                : $"API error: {(int)response.StatusCode} {response.StatusCode} — {Trim(raw, 400)}";

            Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.Cartley, phone, message, null, error, context);
            return WhatsAppSendResult.Fail(phone, WhatsAppProviders.Cartley, error);
        }

        /// <summary>Some gateways answer 200 with {"success":false}. Treat that as a failure.</summary>
        private static bool LooksLikeFailureBody(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

                foreach (var name in new[] { "success", "status", "ok" })
                {
                    if (!doc.RootElement.TryGetProperty(name, out var el)) continue;
                    if (el.ValueKind == JsonValueKind.False) return true;
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString()?.Trim().ToLowerInvariant();
                        if (s is "false" or "error" or "failed" or "failure") return true;
                    }
                }
            }
            catch (JsonException) { /* not JSON — trust the status code */ }
            return false;
        }

        private static Dictionary<string, string> ParseFieldMap(string? json)
        {
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["to"] = "to",
                ["message"] = "message",
                ["sender"] = "sender",
                ["type"] = "type"
            };

            if (string.IsNullOrWhiteSpace(json)) return defaults;

            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsed == null) return defaults;
                foreach (var kv in parsed) defaults[kv.Key] = kv.Value ?? "";
            }
            catch (JsonException) { /* malformed map — the defaults still work */ }

            return defaults;
        }

        // ── link ─────────────────────────────────────────────────────────

        /// <summary>
        /// Nothing leaves the server. The message is parked with a link that opens
        /// WhatsApp on the right conversation with the text already typed, and the
        /// tray in the dashboard picks it up.
        /// </summary>
        private WhatsAppSendResult Queue(
            IDbConnection conn, WhatsAppRuntimeConfig cfg,
            string phone, string message, WhatsAppContext? context)
        {
            var link = BuildWaLink(phone, message, cfg.LinkTarget);
            var id = Log(conn, WhatsAppOutboxStatus.Pending, WhatsAppProviders.Link,
                         phone, message, link, null, context);
            return WhatsAppSendResult.Queued(phone, link, id);
        }

        public string BuildWaLink(string phone, string message, string target)
        {
            var text = Uri.EscapeDataString(message ?? "");
            return (target ?? "auto").ToLowerInvariant() switch
            {
                "web" => $"https://web.whatsapp.com/send?phone={phone}&text={text}",
                "app" => $"whatsapp://send?phone={phone}&text={text}",
                _ => $"https://wa.me/{phone}?text={text}"
            };
        }

        // ═════════════════════════════════════════════════════════════════
        // Outbox write
        // ═════════════════════════════════════════════════════════════════

        private long Log(
            IDbConnection conn, string status, string provider,
            string phone, string message, string? link, string? error,
            WhatsAppContext? ctx)
        {
            try
            {
                return SqlMapper.Query<long>(conn, @"
                    INSERT INTO dbo.WHATSAPP_OUTBOX
                        (Provider, Status, Phone, MessageText, WaLink, MessageType,
                         ReferenceId, CustomerId, CustomerName, BranchId, Lang,
                         ErrorText, CreatedAt, CreatedBy, HandledAt)
                    OUTPUT INSERTED.Id
                    VALUES
                        (@Provider, @Status, @Phone, @MessageText, @WaLink, @MessageType,
                         @ReferenceId, @CustomerId, @CustomerName, @BranchId, @Lang,
                         @ErrorText, SYSUTCDATETIME(), @CreatedBy,
                         CASE WHEN @Status IN ('Sent','Failed') THEN SYSUTCDATETIME() ELSE NULL END)",
                    new
                    {
                        Provider = provider,
                        Status = status,
                        Phone = phone,
                        MessageText = message,
                        WaLink = link,
                        MessageType = ctx?.MessageType,
                        ReferenceId = ctx?.ReferenceId,
                        CustomerId = ctx?.CustomerId,
                        CustomerName = ctx?.CustomerName,
                        BranchId = ctx?.BranchId,
                        Lang = ctx?.Lang,
                        ErrorText = Trim(error, 1000),
                        CreatedBy = ctx?.UserId
                    }).FirstOrDefault();
            }
            catch (Exception ex)
            {
                // The log is diagnostics. It must never be the reason a customer
                // fails to get a confirmation.
                logger?.LogWarning(ex, "Could not write to WHATSAPP_OUTBOX");
                return 0;
            }
        }

        public int PurgeHandledOlderThan(IDbConnection conn, int days)
        {
            if (days <= 0) return 0;
            try
            {
                return SqlMapper.Execute(conn, @"
                    DELETE FROM dbo.WHATSAPP_OUTBOX
                    WHERE Status IN ('Sent','Failed','Cancelled')
                      AND CreatedAt < DATEADD(DAY, -@Days, SYSUTCDATETIME())",
                    new { Days = days });
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "WHATSAPP_OUTBOX purge skipped");
                return 0;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Digits only, in international form, with no plus sign — the shape every
        /// one of the three transports expects.
        ///
        /// Handles 00-prefixed international numbers, a local leading zero, and a
        /// bare subscriber number, in that order. The country code is configurable
        /// rather than the hard-coded 965 it replaces.
        /// </summary>
        public string NormalizePhone(string? phone, string? defaultCountryCode = null)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";

            var cc = Digits(defaultCountryCode);
            if (string.IsNullOrEmpty(cc)) cc = "965";

            var cleaned = new string(phone.Where(char.IsDigit).ToArray());
            if (cleaned.Length == 0) return "";

            if (cleaned.StartsWith("00"))
                return cleaned.Substring(2);

            if (cleaned.StartsWith(cc))
                return cleaned;

            if (cleaned.StartsWith("0"))
                return cc + cleaned.TrimStart('0');

            // A subscriber number on its own is anything shorter than a full
            // international number for this country.
            if (cleaned.Length <= 10)
                return cc + cleaned;

            return cleaned;
        }

        private static string Digits(string? s) =>
            string.IsNullOrWhiteSpace(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

        private static string CombineUrl(string baseUrl, string path)
        {
            baseUrl = (baseUrl ?? "").TrimEnd('/');
            path = (path ?? "").Trim();
            if (path.Length == 0) return baseUrl;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return path;
            return baseUrl + (path.StartsWith("/") ? path : "/" + path);
        }

        private static string? Trim(string? s, int max) =>
            s == null ? null : (s.Length <= max ? s : s.Substring(0, max));

        private static string? Str(IDictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) && v != null && v != DBNull.Value
                ? Convert.ToString(v, CultureInfo.InvariantCulture)
                : null;

        private static bool Bool(IDictionary<string, object> row, string key, bool fallback)
        {
            if (!row.TryGetValue(key, out var v) || v == null || v == DBNull.Value) return fallback;
            if (v is bool b) return b;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
            if (bool.TryParse(s, out var parsed)) return parsed;
            return s == "1";
        }
    }
}
