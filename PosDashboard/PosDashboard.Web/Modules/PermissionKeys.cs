using Serenity.ComponentModel;
using System.ComponentModel;

namespace PosDashboard.Web.Modules
{
    [NestedPermissionKeys]
    public class PermissionKeys
    {
        [DisplayName("Archiving")]
        public static class ArchivingPermissionKeys
        {
            /// <summary>Can view/list archive items (module-level)</summary>
            public const string View = "Archiving:View";

            /// <summary>Can create/update/delete archive items (module-level)</summary>
            public const string Modify = "Archiving:Modify";

            /// <summary>Admin: bypasses item-level sharing checks</summary>
            public const string Admin = "Archiving:Admin";
        }

    }
}
