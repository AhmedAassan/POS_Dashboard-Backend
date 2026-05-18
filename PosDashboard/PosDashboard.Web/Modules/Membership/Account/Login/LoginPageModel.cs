using System.Collections.Generic;
using Serenity.ComponentModel;

namespace PosDashboard.Membership
{
    [ScriptInclude]
    public class LoginPageModel
    {
        public List<string> Providers { get; set; }
    }
}