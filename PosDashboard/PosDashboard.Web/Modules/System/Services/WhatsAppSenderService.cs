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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.WhatsAppProviderDtos;
using PosDashboard.Web.Modules.System.Models;
using System.Net;

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
                cfg.CartleyTokenUrl = Str(row, "CartleyTokenUrl") ?? cfg.CartleyTokenUrl;
                cfg.CartleyContactLookupPath = Str(row, "CartleyContactLookupPath") ?? cfg.CartleyContactLookupPath;
                cfg.CartleyContactCreatePath = Str(row, "CartleyContactCreatePath") ?? cfg.CartleyContactCreatePath;
                cfg.CartleyAutoCreateContacts = Bool(row, "CartleyAutoCreateContacts", false);
                cfg.CartleyAccessKey = Str(row, "CartleyAccessKey") ?? "";
                cfg.CartleySecretKey = Str(row, "CartleySecretKey") ?? "";
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

            var accessFromConfig = configuration["WhatsApp:Cartley:AccessKey"];
            if (!string.IsNullOrWhiteSpace(accessFromConfig))
                cfg.CartleyAccessKey = accessFromConfig!;

            var secretFromConfig = configuration["WhatsApp:Cartley:SecretKey"];
            if (!string.IsNullOrWhiteSpace(secretFromConfig))
                cfg.CartleySecretKey = secretFromConfig!;

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

        // ── Cartley OAuth ────────────────────────────────────────────────
        //
        // Cartley does not accept a static key. The Access Key / Secret Key pair
        // buys a short-lived bearer token, and that is what a send carries.
        //
        // Cached because the exchange is a second network round trip, and a busy
        // salon would otherwise pay it on every receipt. Keyed by the credentials
        // that produced it, so changing them in settings invalidates the cache
        // without a restart.

        private readonly SemaphoreSlim cartleyTokenGate = new(1, 1);
        private string? cachedCartleyToken;
        private string? cachedCartleyCredentials;
        private DateTime cachedCartleyExpiresUtc = DateTime.MinValue;

        private async Task<(string? Token, string? Error)> GetCartleyTokenAsync(WhatsAppRuntimeConfig cfg)
        {
            // A token pasted by hand wins — it exists so an operator can test
            // without credentials to hand.
            if (!string.IsNullOrWhiteSpace(cfg.CartleyToken))
                return (cfg.CartleyToken, null);

            if (string.IsNullOrWhiteSpace(cfg.CartleyAccessKey) ||
                string.IsNullOrWhiteSpace(cfg.CartleySecretKey))
            {
                return (null, "Cartley Connect needs both an Access Key and a Secret Key. Add them in Settings → WhatsApp.");
            }

            var credentials = cfg.CartleyAccessKey + ":" + cfg.CartleySecretKey;

            // A minute of headroom: a token that expires mid-flight fails a send
            // that had no other reason to fail.
            if (cachedCartleyToken != null &&
                cachedCartleyCredentials == credentials &&
                cachedCartleyExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return (cachedCartleyToken, null);
            }

            await cartleyTokenGate.WaitAsync();
            try
            {
                if (cachedCartleyToken != null &&
                    cachedCartleyCredentials == credentials &&
                    cachedCartleyExpiresUtc > DateTime.UtcNow.AddMinutes(1))
                {
                    return (cachedCartleyToken, null);
                }

                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                // Without this Cartley answers a 200 carrying its marketing home
                // page instead of a JSON error, and every failure looks like
                // success. It is the single most important header here.
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                using var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", cfg.CartleyAccessKey),
                    new KeyValuePair<string, string>("client_secret", cfg.CartleySecretKey)
                });

                var response = await client.PostAsync(cfg.CartleyTokenUrl, form);
                var raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (null, $"Cartley refused the credentials: {(int)response.StatusCode} " +
                                  $"at {cfg.CartleyTokenUrl} — {Trim(raw, 300)}");
                }

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) ||
                    tokenEl.ValueKind != JsonValueKind.String)
                {
                    return (null, $"Cartley returned no access_token — {Trim(raw, 300)}");
                }

                var token = tokenEl.GetString()!;

                var lifetime = TimeSpan.FromHours(1);
                if (doc.RootElement.TryGetProperty("expires_in", out var expEl) &&
                    expEl.TryGetInt32(out var seconds) && seconds > 0)
                {
                    lifetime = TimeSpan.FromSeconds(seconds);
                }

                cachedCartleyToken = token;
                cachedCartleyCredentials = credentials;
                cachedCartleyExpiresUtc = DateTime.UtcNow.Add(lifetime);

                return (token, null);
            }
            catch (Exception ex)
            {
                return (null, $"Could not reach {cfg.CartleyTokenUrl}: {ex.Message}");
            }
            finally
            {
                cartleyTokenGate.Release();
            }
        }


        // ── Cartley contacts ─────────────────────────────────────────────
        //
        // Cartley addresses a contact_uid, never a phone number, and everything
        // upstream in this system knows only phone numbers.
        //
        // GET /contacts/phone/{phone}/view does the translation in one call and
        // normalises the number on Cartley's side, which is strictly better than
        // downloading the address book and matching locally — no pagination, no
        // guessing at which stored format a number is in, and no risk of two
        // customers with similar numbers being confused for one another.
        //
        // The uid is cached per number because it never changes, so a salon that
        // messages the same customer twice in a morning pays for one lookup.

        private static readonly TimeSpan ContactUidCacheTtl = TimeSpan.FromHours(6);

        private readonly ConcurrentDictionary<string, (string Uid, DateTime CachedUtc)> cartleyUidCache = new();

        private async Task<(string? Uid, string? Error)> ResolveCartleyContactAsync(
            WhatsAppRuntimeConfig cfg, string phone, string? customerName)
        {
            if (cartleyUidCache.TryGetValue(phone, out var hit) &&
                DateTime.UtcNow - hit.CachedUtc < ContactUidCacheTtl)
            {
                return (hit.Uid, null);
            }

            var (token, tokenError) = await GetCartleyTokenAsync(cfg);
            if (token == null) return (null, tokenError);

            var client = BuildCartleyClient(token);

            var lookupPath = (cfg.CartleyContactLookupPath ?? "/contacts/phone/{phone}/view")
                .Replace("{phone}", Uri.EscapeDataString(phone));
            var lookupUrl = CombineUrl(cfg.CartleyBaseUrl, lookupPath);

            var response = await client.GetAsync(lookupUrl);
            var raw = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var uid = ExtractContactUid(raw);
                if (uid != null)
                {
                    cartleyUidCache[phone] = (uid, DateTime.UtcNow);
                    return (uid, null);
                }
            }

            // 404 is the ordinary answer for a customer Cartley has never seen.
            // Anything else is a real fault and should not be mistaken for one.
            if (response.StatusCode != HttpStatusCode.NotFound &&
                !response.IsSuccessStatusCode)
            {
                return (null, $"Cartley contact lookup failed: {(int)response.StatusCode} " +
                              $"at {lookupUrl} — {Trim(raw, 300)}");
            }

            if (!cfg.CartleyAutoCreateContacts)
            {
                return (null,
                    $"+{phone} is not in the Cartley contact list. Turn on \"Add unknown customers\" " +
                    "in Settings → WhatsApp, or add the contact in the Cartley dashboard.");
            }

            return await CreateCartleyContactAsync(cfg, client, phone, customerName);
        }

        /// <summary>
        /// Creates the contact, then reads back its uid.
        ///
        /// Cartley requires a first and last name between 3 and 20 characters,
        /// which a POS customer record often cannot satisfy — many have no name
        /// at all. Rather than fail, the number itself becomes the placeholder,
        /// so the contact exists and the message goes out; a human can tidy the
        /// name later in the Cartley dashboard.
        /// </summary>
        private async Task<(string? Uid, string? Error)> CreateCartleyContactAsync(
            WhatsAppRuntimeConfig cfg, HttpClient client, string phone, string? customerName)
        {
            var (first, last) = SplitContactName(customerName, phone);

            var url = CombineUrl(cfg.CartleyBaseUrl, cfg.CartleyContactCreatePath);

            var payload = new
            {
                country_code = GuessCountryCode(phone, cfg.DefaultCountryCode),
                first_name = first,
                last_name = last,
                phone_number = phone
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var raw = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Could not add +{phone} to Cartley as {payload.country_code}: " +
                              $"{(int)response.StatusCode} — {Trim(raw, 300)}");
            }

            var uid = ExtractContactUid(raw);
            if (uid == null)
            {
                // Created, but the uid was not in the reply — ask for it directly
                // rather than treating a successful create as a failure.
                var lookupPath = (cfg.CartleyContactLookupPath ?? "/contacts/phone/{phone}/view")
                    .Replace("{phone}", Uri.EscapeDataString(phone));
                var again = await client.GetAsync(CombineUrl(cfg.CartleyBaseUrl, lookupPath));
                if (again.IsSuccessStatusCode)
                    uid = ExtractContactUid(await again.Content.ReadAsStringAsync());
            }

            if (uid == null)
                return (null, $"Added +{phone} to Cartley but could not read back its contact id.");

            cartleyUidCache[phone] = (uid, DateTime.UtcNow);
            return (uid, null);
        }

        /// <summary>
        /// Finds the uuid wherever the reply happens to put it — at the root, or
        /// under "data", or wrapped in "contact". The three shapes all appear
        /// across Cartley's contact endpoints.
        /// </summary>
        private static string? ExtractContactUid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                return FindUuid(doc.RootElement, 0);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? FindUuid(JsonElement element, int depth)
        {
            if (depth > 4) return null;

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("uuid", out var direct) &&
                        direct.ValueKind == JsonValueKind.String)
                    {
                        return direct.GetString();
                    }

                    foreach (var name in new[] { "data", "contact", "model", "result" })
                    {
                        if (element.TryGetProperty(name, out var nested))
                        {
                            var found = FindUuid(nested, depth + 1);
                            if (found != null) return found;
                        }
                    }
                    return null;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var found = FindUuid(item, depth + 1);
                        if (found != null) return found;
                    }
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>Cartley enforces 3–20 characters on both name parts.</summary>
        private static (string First, string Last) SplitContactName(string? name, string phone)
        {
            var cleaned = (name ?? "").Trim();

            if (cleaned.Length == 0)
                return ("Customer", Pad(phone.Length >= 4 ? phone.Substring(phone.Length - 4) : phone));

            var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = Pad(parts[0]);
            var last = parts.Length > 1 ? Pad(string.Join(" ", parts.Skip(1))) : Pad(parts[0]);

            return (first, last);

            static string Pad(string value)
            {
                value = value.Trim();
                if (value.Length > 20) value = value.Substring(0, 20);
                while (value.Length < 3) value += ".";
                return value;
            }
        }

        /// <summary>
        /// Cartley wants an ISO two-letter country, not a dialling prefix — its
        /// own replies say country.code = "KW" with phone_code = 965 as a
        /// separate field. Sending "965" there makes it read the number and the
        /// country as contradicting each other.
        ///
        /// Ordered longest-prefix-first so 966 is matched before 96, and 20
        /// before 2.
        /// </summary>
        private static readonly (string Dial, string Iso)[] DiallingPrefixes =
        {
            ("966", "SA"), ("965", "KW"), ("971", "AE"), ("974", "QA"),
            ("973", "BH"), ("968", "OM"), ("962", "JO"), ("970", "PS"),
            ("961", "LB"), ("964", "IQ"), ("963", "SY"), ("967", "YE"),
            ("249", "SD"), ("218", "LY"), ("216", "TN"), ("213", "DZ"),
            ("212", "MA"), ("90", "TR"), ("44", "GB"), ("20", "EG"), ("1", "US")
        };

        private static string GuessCountryCode(string phone, string fallback)
        {
            var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());

            foreach (var (dial, iso) in DiallingPrefixes)
            {
                if (digits.StartsWith(dial, StringComparison.Ordinal)) return iso;
            }

            // The configured default is a dialling code, so translate it too
            // rather than passing a number through to a field expecting letters.
            var fallbackDigits = new string((fallback ?? "").Where(char.IsDigit).ToArray());
            foreach (var (dial, iso) in DiallingPrefixes)
            {
                if (fallbackDigits == dial) return iso;
            }

            return "KW";
        }

        private HttpClient BuildCartleyClient(string token)
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            // Without this Cartley answers 200 with its marketing home page
            // instead of a JSON error, and every failure reads as success.
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

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
            var (token, tokenError) = await GetCartleyTokenAsync(cfg);
            if (token == null)
            {
                Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.Cartley, phone, message, null, tokenError, context);
                return WhatsAppSendResult.Fail(phone, WhatsAppProviders.Cartley, tokenError!);
            }

            // Cartley addresses a saved contact, not a number. Everything above
            // this line deals in phone numbers, so the translation happens here.
            var (contactUid, contactError) = await ResolveCartleyContactAsync(cfg, phone, context?.CustomerName);
            if (contactUid == null)
            {
                Log(conn, WhatsAppOutboxStatus.Failed, WhatsAppProviders.Cartley, phone, message, null, contactError, context);
                return WhatsAppSendResult.Fail(phone, WhatsAppProviders.Cartley, contactError!);
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

            Put("to", contactUid);
            Put("message", message);
            Put("type", "text");
            if (!string.IsNullOrWhiteSpace(cfg.CartleySenderId))
                Put("sender", cfg.CartleySenderId);

            var url = CombineUrl(cfg.CartleyBaseUrl, cfg.CartleySendPath);

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content);
            var raw = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode && !LooksLikeFailureBody(raw))
            {
                // Worth being precise about what this means: Cartley answers
                // "accepted", which is a receipt for the request, not proof of
                // delivery. Meta can still reject it seconds later — most often
                // because the customer's 24-hour conversation window has closed.
                // The outbox therefore records "sent to the provider".
                Log(conn, WhatsAppOutboxStatus.Sent, WhatsAppProviders.Cartley, phone, message, null, null, context);
                return WhatsAppSendResult.Ok(phone, WhatsAppProviders.Cartley);
            }

            // The URL belongs in the message. A bare 404 sends someone hunting
            // through credentials when the endpoint was simply wrong.
            var error = response.IsSuccessStatusCode
                ? $"Cartley rejected the message — {Trim(raw, 400)}"
                : $"API error: {(int)response.StatusCode} {response.StatusCode} at {url} — {Trim(raw, 400)}";

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
                // Cartley's published shape. A key mapped to an empty string is
                // dropped from the body, which is how "sender" and "type" — which
                // Cartley does not accept — stay out of it.
                ["to"] = "contact_uid",
                ["message"] = "message_body",
                ["sender"] = "",
                ["type"] = ""
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
