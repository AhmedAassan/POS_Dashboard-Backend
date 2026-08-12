// Modules/System/Models/WhatsAppDtos.cs
//
// REPLACES the existing file. The only change is SendWhatsAppResponse, which
// gained three optional fields.
//
// They are optional so that every existing caller — backend and Angular alike —
// keeps compiling and behaving exactly as before. A caller that wants to offer
// "Open WhatsApp now" reads AwaitingManualSend; everyone else keeps reading Sent.

namespace PosDashboard.Web.Modules.System.Models
{
    public class WhatsAppDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record WhatsAppTemplateDto(
            string Header,
            string Footer,
            bool Enabled
        );

        public record UpdateWhatsAppTemplateRequest(
            string? Header,
            string? Footer,
            bool? Enabled
        );

        public record SendWhatsAppResponse(
            bool Sent,
            string Phone,
            string? Error,

            // True when the message is queued for a person to send by hand — the
            // "Open WhatsApp" method. Sent is still true: the message is on its
            // way, it is just travelling through a human.
            bool AwaitingManualSend = false,

            // Ready-made wa.me link. Populated only alongside AwaitingManualSend.
            string? WaLink = null,

            // Row in dbo.WHATSAPP_OUTBOX, so the UI can mark it handled.
            long? OutboxId = null
        );

        public record SendPaymentLinkRequest(
            int AppointmentId,
            string PaymentLink
        );

        public record SendPackageAssignmentRequest(
            int CustomerPackageId
        );
    }
}
