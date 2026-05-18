using System.ComponentModel;
using Serenity.ComponentModel;
using Serenity.Data;
using Serenity.Data.Mapping;

namespace PosDashboard.Administration
{
    [ConnectionKey("Default"), Module("Administration"), TableName("RolePermissions")]
    [DisplayName("Role Permissions"), InstanceName("Role Permission"), GenerateFields]
    [ReadPermission(PermissionKeys.Security)]
    [ModifyPermission(PermissionKeys.Security)]
    public sealed partial class RolePermissionRow : Row<RolePermissionRow.RowFields>
    {
        [DisplayName("Role Permission Id"), Identity, IdProperty]
        public long? RolePermissionId { get => fields.RolePermissionId[this]; set => fields.RolePermissionId[this] = value; }

        [DisplayName("Role Id"), NotNull, ForeignKey("Roles", "RoleId"), LeftJoin("jRole")]
        public int? RoleId { get => fields.RoleId[this]; set => fields.RoleId[this] = value; }

        [DisplayName("Permission Key"), Size(100), NotNull, QuickSearch, NameProperty]
        public string PermissionKey { get => fields.PermissionKey[this]; set => fields.PermissionKey[this] = value; }

        [DisplayName("Role"), Expression("COALESCE(jRole.[RoleKey], jRole.[RoleName])")]
        public string RoleKeyOrName { get => fields.RoleKeyOrName[this]; set => fields.RoleKeyOrName[this] = value; }
    }
}