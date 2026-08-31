using EDom.Application.Setup;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[AllowAnonymous]
[Route("Setup")]
public sealed class SetupController(IFirstRunSetupService firstRunSetupService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var state = await firstRunSetupService.GetStateAsync(cancellationToken);
        if (!state.IsConsistent)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!state.SetupRequired)
            return RedirectToAction("Login", "Account");

        return View(new FirstRunSetupViewModel());
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(FirstRunSetupViewModel model, CancellationToken cancellationToken)
    {
        var state = await firstRunSetupService.GetStateAsync(cancellationToken);
        if (!state.IsConsistent)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!state.SetupRequired)
            return RedirectToAction("Login", "Account");

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await firstRunSetupService.CreateAsync(
                new CreateFirstRunRequest(
                    model.HouseholdName,
                    model.FirstName,
                    model.LastName,
                    model.BirthDate,
                    model.Login,
                    model.Password),
                CorrelationIdMiddleware.Get(HttpContext),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers["User-Agent"].ToString(),
                cancellationToken);

            TempData["SetupComplete"] = "Pierwsze gospodarstwo i konto administratora zostały utworzone. Zaloguj się swoim loginem i hasłem.";
            return RedirectToAction("Login", "Account");
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return View(model);
        }
    }
}
