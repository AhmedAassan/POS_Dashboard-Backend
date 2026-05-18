// Modules/System/Models/WhatsAppDtos.cs

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
            string? Error
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