using System;
using Serenity;
using Serenity.Services;
using MyRequest = PosDashboard.Administration.UserListRequest;
using MyResponse = Serenity.Services.ListResponse<PosDashboard.Administration.UserRow>;
using MyRow = PosDashboard.Administration.UserRow;


namespace PosDashboard.Administration
{
    public interface IUserListHandler : IListHandler<MyRow, MyRequest, MyResponse> { }

    public class UserListHandler : ListRequestHandler<MyRow, MyRequest, MyResponse>, IUserListHandler
    {
        public UserListHandler(IRequestContext context)
             : base(context)
        {
        }

        protected override void OnReturn()
        {
            base.OnReturn();

            if (Request.DataProtector != null &&
                Request.ClientHash != null &&
                Request.IncludeColumns != null &&
                Request.IncludeColumns.Contains("ImpersonationToken") &&
                Permissions.HasPermission("ImpersonateAs") &&
                !Response.Entities.IsEmptyOrNull())
            {
                foreach (var entity in Response.Entities)
                    if (string.Compare(entity.Username, "admin", StringComparison.OrdinalIgnoreCase) != 0)
                        entity.ImpersonationToken = UserHelper.GetImpersonationToken(Cache.Memory, Request.DataProtector,
                            Request.ClientHash, Context.User.Identity.Name, entity.Username);
            }
        }
    }
}