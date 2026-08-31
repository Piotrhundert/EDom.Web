using EDom.Application.Collaboration;
using EDom.Web.Authorization;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
public sealed class NotificationsController(WebAccessService access, ICollaborationService collaboration) : Controller
{
    [HttpGet("/Notifications")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        await collaboration.SyncFinancialNotificationsAsync(current.HouseholdId, cancellationToken);
        var items = await collaboration.ListOwnNotificationsAsync(current.UserAccountId, current.HouseholdId, cancellationToken);
        return View(new NotificationsPageModel(items));
    }

    [HttpPost("/Notifications/{id:guid}/Read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        await collaboration.MarkNotificationReadAsync(current.UserAccountId, id, cancellationToken);
        return RedirectToAction(nameof(Index));
    }
}
