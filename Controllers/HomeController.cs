using EDom.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
public sealed class HomeController(WebAccessService webAccessService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await webAccessService.GetCurrentAsync(cancellationToken);
        if (current is null)
            return Forbid();

        ViewData["DisplayName"] = current.DisplayName;
        ViewData["HouseholdName"] = current.HouseholdName;
        ViewData["Modules"] = await webAccessService.GetAllowedModulesAsync(cancellationToken);
        return View();
    }
}
