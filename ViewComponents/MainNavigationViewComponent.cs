using EDom.Web.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.ViewComponents;

public sealed class MainNavigationViewComponent(WebAccessService webAccessService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (UserClaimsPrincipal.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        var modules = await webAccessService.GetAllowedModulesAsync(HttpContext.RequestAborted);
        return View(modules);
    }
}
