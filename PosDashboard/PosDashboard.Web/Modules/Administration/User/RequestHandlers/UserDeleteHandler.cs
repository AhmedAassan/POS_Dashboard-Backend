using System.Data;
using Serenity.Data;
using Serenity.Extensions.Entities;
using Serenity.Services;
using MyRequest = Serenity.Services.DeleteRequest;
using MyResponse = Serenity.Services.DeleteResponse;
using MyRow = PosDashboard.Administration.UserRow;

namespace PosDashboard.Administration
{
    public interface IUserDeleteHandler : IDeleteHandler<MyRow, MyRequest, MyResponse> { }
    public class UserDeleteHandler : DeleteRequestHandler<MyRow, MyRequest, MyResponse>, IUserDeleteHandler
    {
        public UserDeleteHandler(IRequestContext context)
             : base(context)
        {
        }

        protected override void OnBeforeDelete()
        {
            base.OnBeforeDelete();

            new SqlDelete(UserPreferenceRow.Fields.TableName)
                .Where(UserPreferenceRow.Fields.UserId == Row.UserId.Value)
                .Execute(Connection, ExpectedRows.Ignore);

            new SqlDelete(UserRoleRow.Fields.TableName)
                .Where(UserRoleRow.Fields.UserId == Row.UserId.Value)
                .Execute(Connection, ExpectedRows.Ignore);

            new SqlDelete(UserPermissionRow.Fields.TableName)
                .Where(UserPermissionRow.Fields.UserId == Row.UserId.Value)
                .Execute(Connection, ExpectedRows.Ignore);
        }
    }
}