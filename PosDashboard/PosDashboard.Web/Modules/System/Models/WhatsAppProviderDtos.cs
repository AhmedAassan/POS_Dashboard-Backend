// Modules/System/Models/WhatsAppProviderDtos.cs
//
// Types shared by the sender service, the outbox and the two new controllers.
//
// Naming note: "provider" is the transport (Enjazatik / Cartley / Link).
// Everything above it — the header, the footer, the message bodies — stays
// exactly where it was, so switching transport never changes what a customer
// reads.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public static class WhatsAppProviders
    {
        public const string Enjazatik = "enjazatik";
        public const string Cartley = "cartley";
        public const string Link = "link";
        public const string None = "none";

        public static bool IsAutomatic(string? p) =>
            string.Equals(p, Enjazatik, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, Cartley, StringComparison.OrdinalIgnoreCase);

        /// <summary>Unknown / empty input falls back to Enjazatik — the pre-existing behaviour.</summary>
        public static string Normalize(string? raw)
        {
            var v = (raw ?? "").Trim().ToLowerInvariant();
            return v switch
            {
                Cartley => Cartley,
                Link => Link,
                None => None,
                _ => Enjazatik
            };
        }
    }

    public static class WhatsAppOutboxStatus
    {
        public const string Pending = "Pending";
        public const string Opened = "Opened";
        public const string Sent = "Sent";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";
    }

    /// <summary>Where a message came from — drives the label in the outbox tray.</summary>
    public static class WhatsAppMessageTypes
    {
        public const string AppointmentConfirmation = "appointment.confirmation";
        public const string PaymentLink = "payment.link";
        public const string PackageAssignment = "package.assignment";
        public const string SaleConfirmation = "sale.confirmation";
        public const string SessionServed = "session.served";
        public const string PosReceipt = "pos.receipt";
        public const string DiscountCode = "discount.code";
        public const string WalletCreated = "wallet.created";
        public const string BookingOtp = "booking.otp";
        public const string BookingConfirmation = "booking.confirmation";
        public const string Test = "test";
    }

    public class WhatsAppProviderDtos
    {
        // ── Runtime config, resolved once per send ────────────────────────

        /// <summary>Everything the sender needs, read from WHATSAPP_CONFIG + BusinessSetting.</summary>
        public sealed class WhatsAppRuntimeConfig
        {
            public bool Enabled { get; set; }
            public string Provider { get; set; } = WhatsAppProviders.Enjazatik;
            public string Header { get; set; } = "";
            public string Footer { get; set; } = "";
            public string DefaultCountryCode { get; set; } = "965";

            // Enjazatik
            public string InstanceId { get; set; } = "";
            public string EnjazatikToken { get; set; } = "";

            // Cartley
            public string CartleyBaseUrl { get; set; } = "https://connectapi.cartley.com/api/v1";
            public string CartleySendPath { get; set; } = "/whatsapp/send-message";
            /// <summary>Looks a contact up by number. {phone} is replaced at call time.</summary>
            public string CartleyContactLookupPath { get; set; } = "/contacts/phone/{phone}/view";
            /// <summary>Creates a contact when the lookup finds nothing.</summary>
            public string CartleyContactCreatePath { get; set; } = "/contacts/create";
            /// <summary>Off by default: adding a customer to someone's address book is not a side effect to assume.</summary>
            public bool CartleyAutoCreateContacts { get; set; }
            public string CartleyTokenUrl { get; set; } = "https://connect.cartley.com/oauth/token";
            public string CartleyAccessKey { get; set; } = "";
            public string CartleySecretKey { get; set; } = "";
            /// <summary>Override — a bearer token issued by hand, sent as-is with no exchange.</summary>
            public string CartleyToken { get; set; } = "";
            public string CartleySenderId { get; set; } = "";
            public string? CartleyFieldMap { get; set; }

            // Link
            public string LinkTarget { get; set; } = "auto";      // auto | web | app
            public string RealtimeProvider { get; set; } = WhatsAppProviders.Enjazatik;
        }

        /// <summary>Identifies the message for the outbox tray. All fields optional.</summary>
        public sealed record WhatsAppContext(
            string? MessageType = null,
            string? ReferenceId = null,
            int? CustomerId = null,
            string? CustomerName = null,
            int? BranchId = null,
            string? Lang = null,
            int? UserId = null);

        /// <summary>
        /// The single result type every call site gets back.
        ///
        /// Sent == true means "this message is on its way" — delivered to an API,
        /// or queued for a person when the link method is on. Call sites that only
        /// check Sent keep working unchanged.
        /// </summary>
        public sealed record WhatsAppSendResult(
            bool Sent,
            string Phone,
            string? Error,
            string Provider,
            bool AwaitingManualSend = false,
            string? WaLink = null,
            long? OutboxId = null)
        {
            public static WhatsAppSendResult Disabled() =>
                new(false, "", "WhatsApp sending is disabled", WhatsAppProviders.None);

            public static WhatsAppSendResult Fail(string phone, string provider, string error) =>
                new(false, phone, error, provider);

            public static WhatsAppSendResult Ok(string phone, string provider) =>
                new(true, phone, null, provider);

            public static WhatsAppSendResult Queued(string phone, string link, long outboxId) =>
                new(true, phone, null, WhatsAppProviders.Link, true, link, outboxId);
        }

        // ── Outbox API ────────────────────────────────────────────────────

        public sealed record OutboxItemDto(
            long Id,
            string Provider,
            string Status,
            string Phone,
            string MessageText,
            string? WaLink,
            string? MessageType,
            string? ReferenceId,
            int? CustomerId,
            string? CustomerName,
            string? Lang,
            string? ErrorText,
            DateTime CreatedAt,
            DateTime? OpenedAt,
            DateTime? HandledAt);

        public sealed record OutboxPageDto(
            List<OutboxItemDto> Items,
            int PendingCount,
            int TotalCount,
            string ActiveProvider,
            // Failures in the last day. A message the API rejected needs someone
            // to know about it just as much as one waiting to be sent by hand —
            // more so, because nothing in the UI would otherwise ever mention it.
            int RecentFailedCount,
            // Sends in the last day. Automatic providers never queue anything, so
            // without this the Sent tab looks empty from the outside and the
            // operator concludes the message was never recorded at all.
            int RecentSentCount);

        public sealed record OutboxUpdateRequest(long Id, string? Note = null);

        public sealed record OutboxBulkRequest(List<long> Ids);

        // ── Provider config API ───────────────────────────────────────────

        /// <summary>
        /// Read model for the settings screen. Secrets are reported as
        /// "is one saved, and what does it end with" — never the value.
        /// </summary>
        public sealed record ProviderConfigDto(
            string Provider,
            bool Enabled,
            string DefaultCountryCode,
            string LinkTarget,
            string RealtimeProvider,
            string InstanceId,
            bool EnjazatikTokenIsSet,
            string? EnjazatikTokenHint,
            string CartleyBaseUrl,
            string CartleySendPath,
            string CartleyContactLookupPath,
            string CartleyContactCreatePath,
            bool CartleyAutoCreateContacts,
            string CartleyTokenUrl,
            string CartleySenderId,
            string CartleyAccessKey,
            bool CartleySecretKeyIsSet,
            string? CartleySecretKeyHint,
            bool CartleyTokenIsSet,
            string? CartleyTokenHint,
            string? CartleyFieldMap,
            int OutboxRetentionDays);

        public sealed record UpdateProviderConfigRequest(
            string? Provider,
            bool? Enabled,
            string? DefaultCountryCode,
            string? LinkTarget,
            string? RealtimeProvider,
            string? InstanceId,
            string? CartleyBaseUrl,
            string? CartleySendPath,
            string? CartleyContactLookupPath,
            string? CartleyContactCreatePath,
            bool? CartleyAutoCreateContacts,
            string? CartleyTokenUrl,
            string? CartleySenderId,
            // Not a secret in the OAuth sense — it is the client_id — but it is
            // half of a credential pair, so it is written the same way.
            string? CartleyAccessKey,
            // Only written when non-empty, same rule as the token below.
            string? CartleySecretKey,
            string? CartleyFieldMap,
            // How long a handled message stays in the delivery log. Pending ones
            // are never purged — a message waiting for a person waits as long as
            // it takes.
            int? OutboxRetentionDays,
            // Only written when non-empty — an empty box leaves the stored token alone.
            string? CartleyToken,
            // Explicit opt-in to wipe a stored token.
            bool ClearCartleyToken = false,
            bool ClearCartleySecretKey = false);

        public sealed record TestSendRequest(string Phone, string? Message, string? Provider);
    }
}
