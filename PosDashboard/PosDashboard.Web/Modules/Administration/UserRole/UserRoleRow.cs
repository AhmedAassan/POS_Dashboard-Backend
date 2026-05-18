using System.ComponentModel;
using Serenity.ComponentModel;
using Serenity.Data;
using Serenity.Data.Mapping;

namespace PosDashboard.Administration
{
    [ConnectionKey("Default"), Module("Administration"), TableName("UserRoles")]
    [DisplayName("UserRoles"), InstanceName("UserRoles"), GenerateFields]
    [ReadPermission(PermissionKeys.Security)]
    [ModifyPermission(PermissionKeys.Security)]
    public sealed partial class UserRoleRow : Row<UserRoleRow.RowFields>
    {
        const string jRole = nameof(jRole);
        const string jUser = nameof(jUser);

        [DisplayName("User Role Id"), Identity, IdProperty]
        public long? UserRoleId { get => fields.UserRoleId[this]; set => fields.UserRoleId[this] = value; }

        [DisplayName("User Id"), NotNull, ForeignKey(typeof(UserRow)), LeftJoin(jUser)]
        public int? UserId { get => fields.UserId[this]; set => fields.UserId[this] = value; }

        [DisplayName("Role Id"), NotNull, ForeignKey(typeof(RoleRow)), LeftJoin(jRole)]
        public int? RoleId { get => fields.RoleId[this]; set => fields.RoleId[this] = value; }

        [DisplayName("Username"), Origin(jUser, nameof(UserRow.Username))]
        public string Username { get => fields.Username[this]; set => fields.Username[this] = value; }

        [DisplayName("User Display Name"), Origin(jUser, nameof(UserRow.DisplayName))]
        public string User { get => fields.User[this]; set => fields.User[this] = value; }

        [DisplayName("Role"), Origin(jRole, nameof(RoleRow.RoleKeyOrName))]
        public string RoleKeyOrName { get => fields.RoleKeyOrName[this]; set => fields.RoleKeyOrName[this] = value; }
    }
}