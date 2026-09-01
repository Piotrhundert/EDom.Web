using EDom.Application.Identity;
using EDom.Domain.Authorization;
using EDom.Domain.Households;
using EDom.Domain.Identity;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Users")]
public sealed class UsersController(
    EDomDbContext db,
    WebAccessService access,
    IIdentityService identityService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageUsersAsync(current.HouseholdId, cancellationToken)) return Forbid();

        var model = await BuildModelAsync(current.UserAccountId, current.HouseholdId, cancellationToken);
        ViewData["HouseholdName"] = model.HouseholdName;
        return View(model);
    }

    [HttpPost("Activate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(Guid accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        await identityService.UnlockAccountAsync(accountId, BuildIdentityContext(), "Aktywacja / odblokowanie z panelu Użytkownicy", cancellationToken);
        TempData["Success"] = "Konto zostało zatwierdzone i aktywowane. Poprzednie sesje zostały unieważnione.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lock(Guid accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        if (accountId == current.UserAccountId)
            return RedirectWithError("Nie można zablokować konta, z którego aktualnie zarządzasz systemem.");
        if (await IsLastSuperAdministratorAsync(accountId, current.HouseholdId, cancellationToken))
            return RedirectWithError("Nie można zablokować ostatniego Superadministratora gospodarstwa.");

        var account = await db.UserAccounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        account.Status = UserAccountStatuses.Locked;
        account.LockoutReason = "ManualAdministratorLock";
        account.FailedLoginCount = 0;
        account.SecurityStamp = Guid.NewGuid().ToString("N");
        account.AccessGeneration++;
        account.Version++;

        var now = DateTime.UtcNow;
        var sessions = await db.UserSessions
            .Where(x => x.UserAccountId == accountId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAtUtc = now;

        AddAudit(current.UserAccountId, current.HouseholdId, accountId, "AccountManuallyLocked", "Success");
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Konto zostało zablokowane, a aktywne sesje użytkownika unieważnione.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ForcePasswordChange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForcePasswordChange(Guid accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        var credential = await db.PasswordCredentials.SingleOrDefaultAsync(x => x.UserAccountId == accountId, cancellationToken);
        if (credential is null) return RedirectWithError("Konto nie ma skonfigurowanego hasła.");

        credential.MustChangePassword = true;
        var account = await db.UserAccounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        account.AccessGeneration++;
        account.Version++;

        var now = DateTime.UtcNow;
        var sessions = await db.UserSessions
            .Where(x => x.UserAccountId == accountId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAtUtc = now;

        AddAudit(current.UserAccountId, current.HouseholdId, accountId, "PasswordChangeForced", "Success");
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Wymuszono zmianę hasła. Użytkownik przy następnym logowaniu będzie musiał ustawić nowe hasło.";
        if (accountId == current.UserAccountId)
        {
            await HttpContext.SignOutAsync("EDomCookie");
            return RedirectToAction("Login", "Account");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ResetPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(
        Guid accountId,
        string temporaryPassword,
        bool mustChangePassword,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        var result = await identityService.ResetPasswordAsync(
            accountId,
            temporaryPassword ?? string.Empty,
            mustChangePassword,
            BuildIdentityContext(),
            cancellationToken);

        TempData[result.Succeeded ? "Success" : "Error"] = result.Succeeded
            ? "Hasło użytkownika zostało zmienione. Wszystkie wcześniejsze sesje zostały unieważnione."
            : result.Message;

        if (result.Succeeded && accountId == current.UserAccountId)
        {
            await HttpContext.SignOutAsync("EDomCookie");
            return RedirectToAction("Login", "Account");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<UserAdministrationPageViewModel> BuildModelAsync(
        Guid currentAccountId,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, cancellationToken);
        var memberships = await db.HouseholdMemberships.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.Status == MembershipStatuses.Active && x.ValidTo == null)
            .ToListAsync(cancellationToken);
        var personIds = memberships.Select(x => x.PersonId).Distinct().ToList();
        var people = await db.Persons.AsNoTracking()
            .Where(x => personIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => x.Status != UserAccountStatuses.Deleted)
            .ToListAsync(cancellationToken);
        var credentials = await db.PasswordCredentials.AsNoTracking().ToListAsync(cancellationToken);
        var credentialMap = credentials.ToDictionary(x => x.UserAccountId);

        var now = DateTime.UtcNow;
        var assignments = await db.AccessAssignments.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && (x.ValidToUtc == null || x.ValidToUtc > now))
            .ToListAsync(cancellationToken);
        var roleNames = await db.RoleDefinitions.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Name, cancellationToken);
        var profileNames = await db.AccessProfileDefinitions.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Name, cancellationToken);
        var membershipMap = memberships.GroupBy(x => x.PersonId).ToDictionary(x => x.Key, x => x.First());

        var superAdminAccounts = assignments
            .Where(x => x.RoleCode == RoleCodes.SuperAdministrator)
            .Select(x => x.UserAccountId)
            .Distinct()
            .ToHashSet();

        var rows = new List<UserAdministrationRow>();
        foreach (var account in accounts)
        {
            var personId = account.PersonId is Guid pid ? pid : Guid.Empty;
            if (personId == Guid.Empty || !people.TryGetValue(personId, out var person) || !membershipMap.TryGetValue(personId, out var membership))
                continue;

            credentialMap.TryGetValue(account.Id, out var credential);
            var roles = assignments.Where(x => x.UserAccountId == account.Id)
                .OrderBy(x => roleNames.TryGetValue(x.RoleCode, out var roleName) ? roleName : x.RoleCode)
                .Select(x => new UserAdministrationRoleRow(
                    x.RoleCode,
                    roleNames.TryGetValue(x.RoleCode, out var roleName) ? roleName : x.RoleCode,
                    profileNames.TryGetValue(x.ProfileCode, out var profileName) ? profileName : x.ProfileCode,
                    x.ScopeType,
                    x.ScopeId,
                    x.ValidToUtc))
                .ToList();

            var isApproved = account.Status == UserAccountStatuses.Active || account.Status == UserAccountStatuses.Locked;
            rows.Add(new UserAdministrationRow
            {
                AccountId = account.Id,
                PersonId = personId,
                DisplayName = $"{person.FirstName} {person.LastName}".Trim(),
                Login = account.Login,
                OrganizationalRole = TranslateOrganizationalRole(membership.OrganizationalRole),
                AccountStatus = account.Status,
                AccountStatusLabel = TranslateAccountStatus(account.Status),
                IsApproved = isApproved,
                IsCurrentAccount = account.Id == currentAccountId,
                IsLastSuperAdministrator = superAdminAccounts.Contains(account.Id) && superAdminAccounts.Count == 1,
                MustChangePassword = credential?.MustChangePassword ?? false,
                LastLoginAtUtc = account.LastLoginAtUtc,
                PasswordChangedAtUtc = credential?.ChangedAtUtc,
                FailedLoginCount = account.FailedLoginCount,
                LockoutReason = account.LockoutReason,
                Roles = roles
            });
        }

        rows = rows.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new UserAdministrationPageViewModel
        {
            HouseholdId = householdId,
            HouseholdName = household.Name,
            CurrentAccountId = currentAccountId,
            CanManageSecurity = await CanManageSecurityForHouseholdAsync(householdId, cancellationToken),
            Users = rows
        };
    }

    private async Task<bool> CanManageUsersAsync(Guid householdId, CancellationToken cancellationToken)
        => await access.CanAsync(
            "household.member.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            resourceType: "Person",
            cancellationToken: cancellationToken);

    private async Task<bool> CanManageSecurityForHouseholdAsync(Guid householdId, CancellationToken cancellationToken)
        => await access.CanAsync(
            "identity.account.security_manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            resourceType: "UserAccount",
            cancellationToken: cancellationToken);

    private async Task<bool> CanManageSecurityAsync(Guid householdId, Guid accountId, CancellationToken cancellationToken)
        => await CanManageSecurityForHouseholdAsync(householdId, cancellationToken)
           && await TargetBelongsToHouseholdAsync(accountId, householdId, cancellationToken);

    private async Task<bool> TargetBelongsToHouseholdAsync(Guid accountId, Guid householdId, CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == accountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        var personId = account?.PersonId is Guid pid ? pid : Guid.Empty;
        if (personId == Guid.Empty) return false;
        return await db.HouseholdMemberships.AsNoTracking().AnyAsync(x =>
            x.HouseholdId == householdId && x.PersonId == personId && x.Status == MembershipStatuses.Active && x.ValidTo == null,
            cancellationToken);
    }

    private async Task<bool> IsLastSuperAdministratorAsync(Guid accountId, Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var superAdmins = await db.AccessAssignments.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.RoleCode == RoleCodes.SuperAdministrator && (x.ValidToUtc == null || x.ValidToUtc > now))
            .Select(x => x.UserAccountId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return superAdmins.Count == 1 && superAdmins[0] == accountId;
    }

    private IdentityRequestContext BuildIdentityContext()
        => new(
            CorrelationIdMiddleware.Get(HttpContext),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].ToString(),
            null);

    private void AddAudit(Guid actorAccountId, Guid householdId, Guid subjectAccountId, string eventType, string result)
    {
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            Id = Guid.NewGuid(),
            UserAccountId = subjectAccountId,
            HouseholdId = householdId,
            EventType = eventType,
            OccurredAtUtc = DateTime.UtcNow,
            Result = result,
            CorrelationId = CorrelationIdMiddleware.Get(HttpContext)
        });
    }

    private IActionResult RedirectWithError(string message)
    {
        TempData["Error"] = message;
        return RedirectToAction(nameof(Index));
    }

    private static string TranslateAccountStatus(string status) => status switch
    {
        "Active" => "Aktywne",
        "Locked" => "Zablokowane",
        "Inactive" => "Nieaktywne",
        "Deleted" => "Usunięte",
        _ => status
    };

    private static string TranslateOrganizationalRole(string value) => value switch
    {
        "Owner" => "Właściciel",
        "Member" => "Domownik",
        "Tenant" => "Lokator",
        "Guest" => "Gość",
        "Child" => "Dziecko",
        _ => value
    };
}
