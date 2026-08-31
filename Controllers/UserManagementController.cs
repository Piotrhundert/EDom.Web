using EDom.Application.Administration;
using EDom.Application.Identity;
using EDom.Domain.Authorization;
using EDom.Domain.Identity;
using EDom.Web.Authentication;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("UserManagement")]
public sealed class UserManagementController(
    WebAccessService access,
    IAdministrationCrudService admin,
    IIdentityService identity) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageAccountsAsync(current, cancellationToken)) return Forbid();
        var overview = await admin.GetUsersAsync(current.HouseholdId, cancellationToken);
        var canSecurity = await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: cancellationToken);
        var canAssign = await CanAssignRolesAsync(current, overview, cancellationToken);
        var canCreateAccount = await access.CanAsync("identity.account.create", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: cancellationToken);
        var canManageMembers = await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken);
        ViewData["CanSecurity"] = canSecurity;
        ViewData["CanAssign"] = canAssign;
        ViewData["CanCreate"] = canCreateAccount && canManageMembers;
        return View(new UserManagementPageViewModel
        {
            Overview = overview,
            RoleCatalog = await admin.GetRoleCatalogAsync(cancellationToken)
        });
    }

    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateManagedUserViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("identity.account.create", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: cancellationToken)) return Forbid();
        if (!await access.CanAsync("household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();

        var overview = await admin.GetUsersAsync(current.HouseholdId, cancellationToken);
        var canAssign = await CanAssignRolesAsync(current, overview, cancellationToken);
        var roleCode = canAssign ? model.RoleCode : RoleCodes.HouseholdMember;
        var profileCode = canAssign ? model.ProfileCode : AccessProfileCodes.Standard;
        if (canAssign && !overview.Roles.Any(x => x.Code == roleCode))
        {
            TempData["Error"] = "Wybranej roli nie można nadać z podstawowego panelu użytkowników.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            await admin.CreateUserAsync(new CreateManagedUserRequest(current.HouseholdId, model.ExistingPersonId, model.FirstName, model.LastName, model.BirthDate, model.Email, model.Phone, model.OrganizationalRole, model.Login, model.TemporaryPassword, model.MustChangePassword, roleCode, profileCode, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
            TempData["Success"] = "Utworzono konto użytkownika i przypisano rolę/profil.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Update"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(UpdateManagedUserViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", resourceId: model.AccountId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();

        var overview = await admin.GetUsersAsync(current.HouseholdId, cancellationToken);
        var currentUser = overview.Users.SingleOrDefault(x => x.AccountId == model.AccountId);
        if (currentUser is null) return NotFound();
        var roleOrProfileChanged = !string.Equals(currentUser.RoleCode, model.RoleCode, StringComparison.Ordinal)
                                   || !string.Equals(currentUser.ProfileCode, model.ProfileCode, StringComparison.Ordinal);
        if (roleOrProfileChanged)
        {
            var canAssign = await CanAssignRolesAsync(current, overview, cancellationToken);
            if (!canAssign) return Forbid();
        }
        try
        {
            await admin.UpdateUserAsync(new UpdateManagedUserRequest(current.HouseholdId, model.AccountId, model.Login, model.AccountStatus, model.RoleCode, model.ProfileCode, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), model.Reason), cancellationToken);
            TempData["Success"] = "Zaktualizowano konto i uprawnienia. Zmiana obowiązuje przy kolejnym żądaniu.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ChangeRole"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeRole(ChangeManagedUserRoleViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();

        var overview = await admin.GetUsersAsync(current.HouseholdId, cancellationToken);
        if (!await CanAssignRolesAsync(current, overview, cancellationToken)) return Forbid();

        var target = overview.Users.SingleOrDefault(x => x.AccountId == model.AccountId);
        if (target is null) return NotFound();
        if (!overview.Roles.Any(x => x.Code == model.RoleCode))
        {
            TempData["Error"] = "Tej roli nie można nadać z podstawowego panelu. Lokatora przypisuj przez moduł Najem, a role techniczne i Administrator nadrzędny pozostają chronione.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await admin.UpdateUserAsync(new UpdateManagedUserRequest(
                current.HouseholdId,
                target.AccountId,
                target.Login,
                target.AccountStatus,
                model.RoleCode,
                model.ProfileCode,
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext),
                string.IsNullOrWhiteSpace(model.Reason) ? "Zmiana roli użytkownika" : model.Reason), cancellationToken);
            TempData["Success"] = "Zmieniono rolę i profil użytkownika. Nowe uprawnienia obowiązują od kolejnego żądania.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Archive"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid accountId, string reason, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", resourceId: accountId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        try
        {
            await admin.ArchiveUserAsync(current.HouseholdId, accountId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), string.IsNullOrWhiteSpace(reason) ? "Archiwizacja konta" : reason, cancellationToken);
            TempData["Success"] = "Konto zostało usunięte logicznie (zarchiwizowane). Historia i audyt pozostały zachowane.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ResetPassword"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetManagedPasswordViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", resourceId: model.AccountId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        var result = await identity.ResetPasswordAsync(model.AccountId, model.TemporaryPassword, model.MustChangePassword, IdentityRequestContextFactory.Create(HttpContext), cancellationToken);
        TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Unlock"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(Guid accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", resourceId: accountId.ToString("D"), cancellationToken: cancellationToken)) return Forbid();
        await identity.UnlockAccountAsync(accountId, IdentityRequestContextFactory.Create(HttpContext), "Odblokowanie przez panel administracyjny", cancellationToken);
        TempData["Success"] = "Konto zostało odblokowane.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CanAssignRolesAsync(WebUserContext current, ManagedUserOverview? overview, CancellationToken ct)
    {
        if (await access.CanAsync("identity.access.assign", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: ct))
            return true;

        overview ??= await admin.GetUsersAsync(current.HouseholdId, ct);
        var actor = overview.Users.SingleOrDefault(x => x.AccountId == current.UserAccountId);
        return actor is not null
               && actor.AccountStatus == UserAccountStatuses.Active
               && actor.RoleCode == RoleCodes.SuperAdministrator;
    }

    private async Task<bool> CanManageAccountsAsync(WebUserContext current, CancellationToken ct)
        => await access.CanAsync("identity.account.create", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: ct)
           || await access.CanAsync("identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "UserAccount", cancellationToken: ct);
}
