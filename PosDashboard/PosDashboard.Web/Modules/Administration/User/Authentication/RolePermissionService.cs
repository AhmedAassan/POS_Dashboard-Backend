using System;
using System.Collections.Generic;
using System.Linq;
using PosDashboard.Administration.Repositories;
using Serenity;
using Serenity.Abstractions;
using Serenity.Data;
using Serenity.Net.Core.Authorization;

namespace PosDashboard.Administration
{
    public class RolePermissionService : IRolePermissionService
    {
        protected ITwoLevelCache Cache { get; }
        protected ISqlConnections SqlConnections { get; }
        public ITypeSource TypeSource { get; }

        public RolePermissionService(ITwoLevelCache cache, ISqlConnections sqlConnections,
            ITypeSource typeSource)
        {
            Cache = cache ?? throw new ArgumentNullException(nameof(cache));
            SqlConnections = sqlConnections ?? throw new ArgumentNullException(nameof(sqlConnections));
            TypeSource = typeSource ?? throw new ArgumentNullException(nameof(typeSource));
        }

        public bool HasPermission(string role, string permission)
        {
            return GetRolePermissions(role).Contains(permission);
        }

        private ISet<string> GetRolePermissions(string role)
        {
            if (role == null) throw new ArgumentNullException(nameof(role));

            var fld = RolePermissionRow.Fields;

            return Cache.GetLocalStoreOnly("RolePermissions:" + role, TimeSpan.Zero, fld.GenerationKey, () =>
            {
                using var connection = SqlConnections.NewByKey("Default");
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                connection.List<RolePermissionRow>(q => q
                        .Select(fld.PermissionKey)
                        .Where(fld.RoleKeyOrName == role))
                    .ForEach(x => result.Add(x.PermissionKey));

                result.Add("Role:" + role);

                var implicitPermissions = UserPermissionRepository.GetImplicitPermissions(Cache.Memory, TypeSource);
                foreach (var key in result.ToArray())
                {
                    if (implicitPermissions.TryGetValue(key, out HashSet<string> list))
                        foreach (var x in list)
                            result.Add(x);
                }

                return result;
            });
        }
    }
}