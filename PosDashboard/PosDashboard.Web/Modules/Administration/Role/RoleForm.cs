using Serenity.ComponentModel;

namespace PosDashboard.Administration.Forms
{
    [FormScript("Administration.Role")]
    [BasedOnRow(typeof(RoleRow), CheckNames = true)]
    public class RoleForm
    {
        public string RoleName { get; set; }
        [Placeholder("if set, used as a special permission key like \"Role:RoleKey\"")]
        public string RoleKey { get; set; }
    }
}