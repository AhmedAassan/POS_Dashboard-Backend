using Serenity.ComponentModel;

namespace PosDashboard.Membership
{
    [ScriptInclude]
    public class SignupPageModel
    {
        public string DisplayName { get; set; }
        public string Email { get; set; }
        public string ExternalProviderToken { get; set; }
    }
}