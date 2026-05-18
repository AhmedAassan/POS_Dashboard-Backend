using Microsoft.AspNetCore.Mvc;
using PosDashboard.Administration;
using Serenity.Extensions;

namespace PosDashboard.Membership.Pages
{
    [Route("Account/[action]")]
    public class AccountPasswordActionsPage : AccountPasswordActionsPageBase<UserRow>
    {
    }
}
