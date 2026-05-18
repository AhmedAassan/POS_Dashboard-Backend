using System;

namespace PosDashboard.Web.Modules.System.Models
{
    public class AccountDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record LoginRequest(string Username, string Password);

        public record AuthResponse(
            string AccessToken,
            DateTime AccessTokenExpiresAtUtc,
            string Username
        );
    }
}
