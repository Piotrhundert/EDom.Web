using EDom.Application.Households;
using EDom.Application.Administration;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Household")]
public sealed class HouseholdController(WebAccessService access, IHouseholdFamilyService family, IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken))
            return Forbid();
        ViewData["CanGuardian"] = await access.CanAsync("household.guardian.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        ViewData["CanResidence"] = await access.CanAsync("household.residence.assign", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        return View(await family.GetOverviewAsync(current.HouseholdId, cancellationToken));
    }

    [HttpGet("Person/{id:guid}")]
    public async Task<IActionResult> Person(Guid id, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        var canManage = await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        var isOwn = current.PersonId == id;
        if (!canManage && !isOwn) return Forbid();
        var profile = await family.GetPersonAsync(current.HouseholdId, id, cancellationToken);
        if (profile is null) return NotFound();
        ViewData["CanManage"] = canManage;
        ViewData["IsOwn"] = isOwn;
        return View(profile);
    }

    [HttpPost("AddPerson"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPerson(AddHouseholdPersonViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await family.AddPersonAsync(new CreateHouseholdPersonRequest(current.HouseholdId, model.FirstName, model.LastName, model.BirthDate, model.IsChild, model.OrganizationalRole, model.Email, model.Phone, model.City, model.PostalCode, model.Street, model.BuildingNo, model.UnitNo), current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = model.IsChild ? "Dodano profil dziecka bez konta logowania." : "Dodano osobę do gospodarstwa.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AdminEditPerson"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminEditPerson(AdminEditPersonViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await adminCrud.UpdatePersonAsync(new UpdatePersonAdminRequest(current.HouseholdId, model.PersonId, model.FirstName, model.LastName, model.BirthDate, model.Email, model.Phone, model.OrganizationalRole, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), model.Reason), cancellationToken);
            TempData["Success"] = "Dane osoby zostały zaktualizowane bez utraty audytu.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Person), new { id = model.PersonId });
    }

    [HttpPost("ArchivePerson"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchivePerson(Guid personId, string reason, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await adminCrud.ArchivePersonAsync(current.HouseholdId, personId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), string.IsNullOrWhiteSpace(reason) ? "Archiwizacja osoby" : reason, cancellationToken);
            TempData["Success"] = "Osoba została zarchiwizowana. Powiązana historia pozostała w systemie.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Person), new { id = personId }); }
    }

    [HttpPost("ProfileChange"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ProfileChange(ProfileChangeViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null || current.PersonId != model.PersonId) return Forbid();
        if (!await access.CanAsync("profile.change_request.submit", ResourceScopeTypes.Own, ownerPersonId: current.PersonId, resourceId: model.PersonId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.SubmitProfileChangeAsync(new SubmitProfileChangeRequest(model.PersonId, current.UserAccountId, model.FirstName, model.LastName, model.BirthDate, model.Email, model.Phone, model.City, model.PostalCode, model.Street, model.BuildingNo, model.UnitNo, model.Reason), CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Wniosek o zmianę profilu został zapisany i oczekuje na decyzję administratora.";
        return RedirectToAction(nameof(Person), new { id = model.PersonId });
    }

    [HttpPost("DecideProfile/{requestId:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideProfile(Guid requestId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("profile.change_request.approve", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.DecideProfileChangeAsync(requestId, approve, current.UserAccountId, reason, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = approve ? "Zmiana profilu została zatwierdzona." : "Wniosek został odrzucony.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Guardian"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Guardian(GuardianViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.guardian.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        var permissions = new List<string>();
        if (model.AllowChildFinance) permissions.Add("privatefinance.child_record.manage_guardian");
        if (model.AllowChildCalendar) permissions.Add("calendar.child.manage_guardian");
        await family.AddGuardianAsync(current.HouseholdId, new CreateGuardianRequest(model.ChildPersonId, model.GuardianPersonId, model.RelationshipType, model.IsPrimary, model.ValidFrom, permissions), current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Dodano relację opiekun–dziecko z określonym zakresem dostępu.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FamilyGroup"), ValidateAntiForgeryToken]
    public async Task<IActionResult> FamilyGroup(FamilyGroupViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.CreateFamilyGroupAsync(current.HouseholdId, model.Name, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Utworzono grupę rodzinną.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FamilyGroupMember"), ValidateAntiForgeryToken]
    public async Task<IActionResult> FamilyGroupMember(FamilyGroupMemberViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.AddFamilyGroupMemberAsync(current.HouseholdId, model.GroupId, model.PersonId, model.GroupRole, model.IsPrimary, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Dodano osobę do grupy rodzinnej.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("FamilyRelationship"), ValidateAntiForgeryToken]
    public async Task<IActionResult> FamilyRelationship(FamilyRelationshipViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.AddFamilyRelationshipAsync(current.HouseholdId, model.PersonAId, model.PersonBId, model.RelationshipType, model.Direction, model.ValidFrom, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Zapisano relację rodzinną. Nie zmienia ona uprawnień użytkowników.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Residence"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Residence(ResidenceViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await access.CanAsync("household.residence.assign", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await family.AddResidenceAsync(current.HouseholdId, model.PersonId, model.ResidenceType, model.ValidFrom, model.ValidTo, model.PayerPersonId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
        TempData["Success"] = "Dodano okres zamieszkania bez nadpisywania historii.";
        return RedirectToAction(nameof(Index));
    }
}
