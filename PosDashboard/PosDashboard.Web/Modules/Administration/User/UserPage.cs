using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serenity.Extensions;
using Serenity.Web;

namespace PosDashboard.Administration.Pages
{
    [PageAuthorize(typeof(UserRow))]
    public class UserPage : Controller
    {
        [Route("Administration/User")]
        public ActionResult Index()
        {
            return this.GridPage(ESM.Modules.Administration.User.UserPage,
                UserRow.Fields.PageTitle());
        }

        [Route("Administration/ExceptionLog/{*pathInfo}"), IgnoreAntiforgeryToken]
        public async Task ExceptionLog([FromServices] IOptions<EnvironmentSettings> environmentSettings)
        {
            await StackExchange.Exceptional.ExceptionalMiddleware
                .HandleRequestAsync(HttpContext).ConfigureAwait(false);
        }
    }
}