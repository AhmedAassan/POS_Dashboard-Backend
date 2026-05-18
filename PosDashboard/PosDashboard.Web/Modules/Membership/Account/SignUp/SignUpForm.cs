using System.ComponentModel;
using Serenity.ComponentModel;

namespace PosDashboard.Membership
{
    [FormScript("Membership.SignUp")]
    public class SignUpForm
    {
        [Required(true), Placeholder("full name")]
        public string DisplayName { get; set; }
        [EmailAddressEditor, Required(true), Placeholder("e-mail")]
        public string Email { get; set; }
        [EmailAddressEditor, Required(true), Placeholder("confirm email")]
        public string ConfirmEmail { get; set; }
        [PasswordEditor, Required(true), Placeholder("password")]
        public string Password { get; set; }
        [PasswordEditor, Required(true), Placeholder("confirm password")]
        public string ConfirmPassword { get; set; }
        // make sure you have configured Recaptcha:SiteKey and Recaptcha:SecretKey in appsettings.json
        [DisplayName(""), Recaptcha]
        public string Recaptcha { get; set; }
    }
}
