using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Serenity.Abstractions;

namespace PosDashboard.Membership.Pages
{
    public partial class AccountPage : Controller
    {
        public ActionResult ImpersonateAs(string token,
            [FromServices] IUserRetrieveService userRetriever,
            [FromServices] IUserClaimCreator userClaimCreator)
        {
            if (userRetriever is null)
                throw new ArgumentNullException(nameof(userRetriever));
            if (userClaimCreator is null)
                throw new ArgumentNullException(nameof(userClaimCreator));

            var bytes = HttpContext.RequestServices.GetDataProtector("ImpersonateAs")
                .Unprotect(Convert.FromBase64String(token));

            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            var dt = DateTime.FromBinary(br.ReadInt64());
            if (dt < DateTime.UtcNow)
                return new ContentResult { Content = "Your impersonation token is expired. Please refresh the page you were using and try again." };

            var loginAsUser = br.ReadString();

            if (string.Compare(loginAsUser, "admin", StringComparison.OrdinalIgnoreCase) != 0)
                return new ContentResult { Content = "Only admin can use impersonation functionality!" };

            var loginAs = br.ReadString();

            if (string.Compare(loginAs, "admin", StringComparison.OrdinalIgnoreCase) == 0)
                return new ContentResult { Content = "Can't impersonate as admin!" };

            var remoteIp = HttpContext.Connection.RemoteIpAddress.ToString();
            remoteIp = remoteIp == "::1" ? "127.0.0.1" : remoteIp;
            var currentClientId = Request.Headers["User-Agent"] + "|" + remoteIp;
            var currentHash = Encoding.UTF8.GetBytes(currentClientId);
            if (!currentHash.SequenceEqual(br.ReadBytes(currentHash.Length)))
                return new ContentResult { Content = "Invalid token! User-agent or IP mismatch!" };

            if (userRetriever.ByUsername(loginAs) is not UserDefinition user)
                return new ContentResult { Content = loginAs + " is not a valid username!" };

            if (User?.Identity.Name?.ToLowerInvariant() == loginAsUser.ToLowerInvariant())
                return new ContentResult
                {
                    Content = "Please use Incognito mode by right clicking the impersonation link! " +
                        "If you are already in Incognito mode, signout or close all Incognito browser windows and try again."
                };

            var principal = userClaimCreator.CreatePrincipal(user.Username, authType: "Impersonation");
            HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal).Wait();

            return new RedirectResult("~/");
        }
    }
}