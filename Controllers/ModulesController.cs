using EDom.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("module")]
public sealed class ModulesController(WebAccessService webAccessService) : Controller
{
    [HttpGet("{key}")]
    public async Task<IActionResult> Index(string key, CancellationToken cancellationToken)
    {
        var module = ModuleCatalog.Find(key);
        if (module is null)
            return NotFound();

        if (!await webAccessService.CanUseModuleAsync(module, cancellationToken))
            return Forbid();

        if (string.Equals(module.Key, "household", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Household");
        if (string.Equals(module.Key, "private-finance", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "PrivateFinance");
        if (string.Equals(module.Key, "finances", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "HouseholdFinance");
        if (string.Equals(module.Key, "calendar", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Calendar");
        if (string.Equals(module.Key, "documents", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Documents");
        if (string.Equals(module.Key, "property", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Property");
        if (string.Equals(module.Key, "rental", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Rental");
        if (string.Equals(module.Key, "utilities", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "Utilities");
        if (string.Equals(module.Key, "settings", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index", "UserManagement");

        return View(module);
    }
}
