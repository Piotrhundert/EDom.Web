using EDom.Web.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.ViewComponents;

public sealed class MainNavigationViewComponent(WebAccessService access) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        // Nawigacja wykonuje serię sprawdzeń RBAC. Nie wiążemy ich bezpośrednio
        // z RequestAborted, ponieważ AuthorizationEvaluator zapisuje również audyt
        // decyzji i anulowanie żądania w trakcie SaveChangesAsync powodowało
        // TaskCanceledException podczas renderowania lewego menu.
        var modules = await access.GetAllowedModulesAsync(CancellationToken.None);
        return View(modules);
    }
}
