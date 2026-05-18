using Microsoft.AspNetCore.Mvc;
using PosDashboard.Web.Modules.System.Services;
using Serenity.Data;
using Serenity.Services;
using System.Linq;
using static PosDashboard.Web.Modules.System.Models.AccountDtos;

namespace PosDashboard.Web.Modules.System
{
    [Route("api/account/[action]")]
    [ApiController]
    public class AccountApiController : ServiceEndpoint
    {
        private readonly ISqlConnections sqlConnections;
        private readonly JwtTokenService jwt;

        public AccountApiController(ISqlConnections sqlConnections, JwtTokenService jwt)
        {
            this.sqlConnections = sqlConnections;
            this.jwt = jwt;
        }

        [HttpPost, IgnoreAntiforgeryToken]
        public ActionResult<ApiResult<AuthResponse>> Login([FromBody] LoginRequest request)
        {
            if (request == null)
                return new ApiResult<AuthResponse>(false, "Invalid username or password", null);

            var username = (request.Username ?? "").Trim();     
            var password = (request.Password ?? "").Trim();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return new ApiResult<AuthResponse>(false, "Invalid username or password", null);

            using var conn = sqlConnections.NewByKey("Default");

            var user = conn.Query<dynamic>(@"
                SELECT TOP 1
                    USER_ID,
                    USER_NAME,
                    USER_PASSWORD,
                    IS_ACTIVE,
                    USER_COMPANY_ID,
                    Role
                FROM dbo.[USER]
                WHERE USER_NAME = @Username",
                new { Username = username }).FirstOrDefault();

            if (user == null)
                return new ApiResult<AuthResponse>(false, "Invalid username or password", null);

            if ((int)user.IS_ACTIVE != 1)
                return new ApiResult<AuthResponse>(false, "User is not active", null);

            // ✅ Plain text password compare
            var storedPassword = ((string)user.USER_PASSWORD ?? "").Trim();
            if (!string.Equals(storedPassword, password))
                return new ApiResult<AuthResponse>(false, "Invalid username or password", null);

            int userId = (int)user.USER_ID;
            int role = (int)user.Role;
            int companyId = (int)user.USER_COMPANY_ID;

            var (token, exp) = jwt.CreateAccessToken(userId, username, role, companyId);

            return new ApiResult<AuthResponse>(true, null, new AuthResponse(token, exp, username));
        }
    }
}
