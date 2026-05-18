using Microsoft.AspNetCore.Mvc;
using Serenity.Web;

namespace PosDashboard.Common.Pages
{
    [Route("Dashboard/[action]")]
    public class DashboardPage : Controller
    {
        [PageAuthorize, HttpGet, Route("~/")]
        public ActionResult Index()
        {
            return View(MVC.Views.Common.Dashboard.DashboardIndex, new DashboardPageModel());
        }
    }
}
