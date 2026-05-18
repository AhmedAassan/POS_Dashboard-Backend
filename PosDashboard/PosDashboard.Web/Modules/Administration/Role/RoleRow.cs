using System.ComponentModel;
using Serenity.ComponentModel;
using Serenity.Data;
using Serenity.Data.Mapping;

namespace PosDashboard.Administration
{
    [ConnectionKey("Default"), Module("Administration"), TableName("Roles")]
    [DisplayName("Roles"), InstanceName("Role"), GenerateFields]
    [ReadPermission(PermissionKeys.Security)]
    [ModifyPermission(PermissionKeys.Security)]
    [LookupScript]
    public sealed partial class RoleRow : Row<RoleRow.RowFields>, IIdRow, INameRow
    {
        [DisplayName("Role Id"), Identity, ForeignKey("Roles", "RoleId"), LeftJoin("jRole"), IdProperty]
        public int? RoleId { get => fields.RoleId[this]; set => fields.RoleId[this] = value; }

        [DisplayName("Role Name"), Size(100), NotNull, QuickSearch, NameProperty]
        public string RoleName { get => fields.RoleName[this]; set => fields.RoleName[this] = value; }

        [DisplayName("Role Key"), Size(100), QuickSearch]
        public string RoleKey { get => fields.RoleKey[this]; set => fields.RoleKey[this] = value; }

        [DisplayName("Role Key or Name"), Expression("COALESCE(t0.[RoleKey], t0.[RoleName])")]
        public string RoleKeyOrName { get => fields.RoleKeyOrName[this]; set => fields.RoleKeyOrName[this] = value; }
    }
}