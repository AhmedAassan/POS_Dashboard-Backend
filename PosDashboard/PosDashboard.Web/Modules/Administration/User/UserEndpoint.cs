using System.Data;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using Serenity.Services;
using MyRow = PosDashboard.Administration.UserRow;

namespace PosDashboard.Administration.Endpoints
{
    [Route("Services/Administration/User/[action]")]
    [ConnectionKey(typeof(MyRow)), ServiceAuthorize(typeof(MyRow))]
    public class UserEndpoint : ServiceEndpoint
    {
        [HttpPost, AuthorizeCreate(typeof(MyRow))]
        public SaveResponse Create(IUnitOfWork uow, SaveRequest<MyRow> request, [FromServices] IUserSaveHandler handler)
        {
            return handler.Create(uow, request);
        }

        [HttpPost, AuthorizeUpdate(typeof(MyRow))]
        public SaveResponse Update(IUnitOfWork uow, SaveRequest<MyRow> request, [FromServices] IUserSaveHandler handler)
        {
            return handler.Update(uow, request);
        }

        [HttpPost, AuthorizeDelete(typeof(MyRow))]
        public DeleteResponse Delete(IUnitOfWork uow, DeleteRequest request, [FromServices] IUserDeleteHandler handler)
        {
            return handler.Delete(uow, request);
        }

        public RetrieveResponse<MyRow> Retrieve(IDbConnection connection, RetrieveRequest request, [FromServices] IUserRetrieveHandler handler)
        {
            return handler.Retrieve(connection, request);
        }

        public ListResponse<MyRow> List(IDbConnection connection, UserListRequest request, [FromServices] IUserListHandler handler)
        {
            if (request != null && Permissions.HasPermission("ImpersonateAs"))
            {
                request.DataProtector = HttpContext.RequestServices.GetDataProtector("ImpersonateAs");

                var remoteIp = HttpContext.Connection.RemoteIpAddress.ToString();
                remoteIp = remoteIp == "::1" ? "127.0.0.1" : remoteIp;
                var clientId = Request.Headers["User-Agent"] + "|" + remoteIp;
                request.ClientHash = Encoding.UTF8.GetBytes(clientId);
            }

            return handler.List(connection, request);
        }
    }
}