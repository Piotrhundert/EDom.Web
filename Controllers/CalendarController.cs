using EDom.Application.Collaboration;
using EDom.Application.Administration;
using EDom.Domain.Authorization;
using EDom.Domain.Collaboration;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
public sealed class CalendarController(
    WebAccessService access,
    ICollaborationService collaboration,
    EDomDbContext db,
    IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("/Calendar")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        ViewData["CurrentPersonId"] = current.PersonId;
        var actor = Actor(current);
        var events = await collaboration.ListCalendarEventsAsync(actor, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(60), cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var groups = await (
            from membership in db.FamilyGroupMembers.AsNoTracking()
            join groupEntry in db.FamilyGroups.AsNoTracking() on membership.FamilyGroupId equals groupEntry.Id
            where groupEntry.HouseholdId == current.HouseholdId
                  && groupEntry.Status == "Active"
                  && membership.PersonId == current.PersonId
                  && membership.ValidFrom <= today
                  && (membership.ValidTo == null || membership.ValidTo >= today)
            orderby groupEntry.Name
            select groupEntry).ToListAsync(cancellationToken);

        var guardianChildIds = await db.GuardianRelationships.AsNoTracking()
            .Where(x => x.GuardianPersonId == current.PersonId
                        && x.Status == "Active"
                        && x.ValidFrom <= today
                        && (x.ValidTo == null || x.ValidTo >= today)
                        && x.PermissionScopeJson.Contains("calendar.child.manage_guardian"))
            .Select(x => x.ChildPersonId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var children = await (from member in db.HouseholdMemberships.AsNoTracking()
                              join person in db.Persons.AsNoTracking() on member.PersonId equals person.Id
                              where member.HouseholdId == current.HouseholdId
                                    && member.Status == "Active"
                                    && member.ValidFrom <= today
                                    && (member.ValidTo == null || member.ValidTo >= today)
                                    && person.PersonType == "Child"
                                    && guardianChildIds.Contains(person.Id)
                              orderby person.FirstName, person.LastName
                              select person).ToListAsync(cancellationToken);
        return View(new CalendarPageModel(events, groups, children));
    }

    [HttpPost("/Calendar/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string title, DateTime startsAtLocal, DateTime endsAtLocal, string visibility, Guid? familyGroupId, Guid? childPersonId, string? location, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Challenge();
        var scopeType = visibility switch
        {
            CalendarVisibility.Household => ResourceScopeTypes.Household,
            CalendarVisibility.FamilyGroup => ResourceScopeTypes.FamilyGroup,
            CalendarVisibility.Guardian => ResourceScopeTypes.Guardian,
            _ => ResourceScopeTypes.Own
        };
        var scopeId = scopeType switch
        {
            ResourceScopeTypes.Household => current.HouseholdId.ToString("D"),
            ResourceScopeTypes.FamilyGroup => familyGroupId?.ToString("D"),
            ResourceScopeTypes.Guardian => childPersonId?.ToString("D"),
            _ => current.PersonId.ToString("D")
        };
        try
        {
            await collaboration.CreateCalendarEventAsync(Actor(current), new CreateCalendarEventRequest(
                title, startsAtLocal.ToUniversalTime(), endsAtLocal.ToUniversalTime(), visibility, scopeType, scopeId, childPersonId, location), cancellationToken);
            return RedirectToAction(nameof(Index));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("/Calendar/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, string title, DateTime startsAtLocal, DateTime endsAtLocal, string? location, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Challenge();
        var item = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == current.HouseholdId, cancellationToken);
        if (item is null) return NotFound();
        if (item.OwnerPersonId != current.PersonId) return Forbid();
        if (!await CanManageOwnedEventAsync(item, current, cancellationToken)) return Forbid();
        try
        {
            await adminCrud.UpdateCalendarEventAsync(new UpdateCalendarEventAdminRequest(current.HouseholdId, current.PersonId, id, title, startsAtLocal.ToUniversalTime(), endsAtLocal.ToUniversalTime(), location, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
            TempData["Success"] = "Wydarzenie zostało zaktualizowane.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/Calendar/{id:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Challenge();
        var item = await db.CalendarEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == current.HouseholdId, cancellationToken);
        if (item is null) return NotFound();
        if (item.OwnerPersonId != current.PersonId || !await CanManageOwnedEventAsync(item, current, cancellationToken)) return Forbid();
        try { await adminCrud.CancelCalendarEventAsync(current.HouseholdId, current.PersonId, id, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken); TempData["Success"] = "Wydarzenie anulowano i zachowano w historii."; }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private Task<bool> CanManageOwnedEventAsync(CalendarEvent item, WebUserContext current, CancellationToken cancellationToken)
    {
        if (item.Visibility == CalendarVisibility.Guardian && item.ChildPersonId.HasValue)
            return access.CanAsync("calendar.child.manage_guardian", ResourceScopeTypes.Guardian, item.ChildPersonId.Value.ToString("D"), current.PersonId, item.ChildPersonId, "CalendarEvent", item.Id.ToString("D"), cancellationToken: cancellationToken);
        if (item.Visibility == CalendarVisibility.Own)
            return access.CanAsync("calendar.event.manage_own", ResourceScopeTypes.Own, current.PersonId.ToString("D"), current.PersonId, resourceType: "CalendarEvent", resourceId: item.Id.ToString("D"), cancellationToken: cancellationToken);
        return access.CanAsync("calendar.event.manage_shared", item.ScopeType, item.ScopeId, current.PersonId, resourceType: "CalendarEvent", resourceId: item.Id.ToString("D"), cancellationToken: cancellationToken);
    }

    private CollaborationActor Actor(WebUserContext current)
        => new(current.UserAccountId, current.PersonId, current.HouseholdId, CorrelationIdMiddleware.Get(HttpContext), DateTime.UtcNow);
}
