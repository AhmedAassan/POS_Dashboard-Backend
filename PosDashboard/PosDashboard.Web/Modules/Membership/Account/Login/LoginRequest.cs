using Serenity.ComponentModel;
using Serenity.Services;

namespace PosDashboard.Membership
{
    [FormScript("Membership.Login")]
    [BasedOnRow(typeof(Administration.UserRow), CheckNames = true)]
    public class LoginRequest : ServiceRequest
    {
        [Placeholder("user name")]
        public string Username { get; set; }
        [PasswordEditor, Required(true), Placeholder("password")]
        public string Password { get; set; }
        [Ignore]
        public string TwoFactorGuid { get; set; }
        [Ignore]
        public int? TwoFactorCode { get; set; }
    }
}