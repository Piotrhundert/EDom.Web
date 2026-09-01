using System.Text;
using EDom.Domain.Authorization;
using EDom.Domain.Households;
using EDom.Domain.Identity;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("RoleStudio")]
public sealed class RoleStudioController(
    EDomDbContext db,
    WebAccessService access) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? roleCode, CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        var model = await BuildModelAsync(current.Value.UserAccountId, current.Value.HouseholdId, roleCode, cancellationToken);
        ViewData["HouseholdName"] = model.HouseholdName;
        return View(model);
    }

    [HttpPost("CreateRole")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRole(string name, string? codeSuffix, string? copyFromRoleCode, CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        name = (name ?? string.Empty).Trim();
        if (name.Length is < 2 or > 200)
            return RedirectWithError("Nazwa roli musi mieć od 2 do 200 znaków.");

        var suffix = NormalizeRoleSuffix(codeSuffix ?? name);
        if (suffix.Length < 2)
            return RedirectWithError("Podaj krótką nazwę techniczną roli, np. OpiekunDomu.");

        var roleCode = CustomRolePrefix(current.Value.HouseholdId) + suffix;
        if (roleCode.Length > 100)
            return RedirectWithError("Kod roli jest zbyt długi. Skróć nazwę techniczną.");

        if (await db.RoleDefinitions.AnyAsync(x => x.Code == roleCode, cancellationToken))
            return RedirectWithError("Rola o takim kodzie już istnieje.");

        db.RoleDefinitions.Add(new RoleDefinition { Code = roleCode, Name = name });

        if (!string.IsNullOrWhiteSpace(copyFromRoleCode))
        {
            var sourceExists = await IsAllowedRoleForHouseholdAsync(copyFromRoleCode, current.Value.HouseholdId, cancellationToken);
            if (!sourceExists) return RedirectWithError("Nie znaleziono roli źródłowej dostępnej w tym gospodarstwie.");

            var sourcePermissions = await db.RolePermissions.AsNoTracking()
                .Where(x => x.RoleCode == copyFromRoleCode)
                .ToListAsync(cancellationToken);

            foreach (var source in sourcePermissions)
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleCode = roleCode,
                    PermissionCode = source.PermissionCode,
                    Effect = source.Effect
                });
            }
        }

        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.RoleCreated", roleCode,
            $"Utworzono własną rolę '{name}'.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Rola została utworzona. Możesz teraz szczegółowo ustawić jej uprawnienia.";
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    [HttpPost("RenameRole")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameRole(string roleCode, string name, CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();
        if (IsProtectedRole(roleCode))
            return RedirectWithError("Nie można edytować głównej roli administratora.", roleCode);

        var role = await db.RoleDefinitions.SingleOrDefaultAsync(x => x.Code == roleCode, cancellationToken);
        if (role is null) return NotFound();

        name = (name ?? string.Empty).Trim();
        if (name.Length is < 2 or > 200)
            return RedirectWithError("Nazwa roli musi mieć od 2 do 200 znaków.", roleCode);

        role.Name = name;
        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.RoleRenamed", roleCode,
            $"Zmieniono nazwę roli na '{name}'.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Nazwa roli została zmieniona.";
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    [HttpPost("SavePermissions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePermissions(
        string roleCode,
        string[] permissionCodes,
        string[] permissionEffects,
        CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();
        if (IsProtectedRole(roleCode))
            return RedirectWithError("Nie można edytować uprawnień głównej roli administratora.", roleCode);

        var roleExists = await db.RoleDefinitions.AnyAsync(x => x.Code == roleCode, cancellationToken);
        if (!roleExists) return NotFound();

        if (permissionCodes.Length != permissionEffects.Length)
            return RedirectWithError("Nie udało się odczytać macierzy uprawnień. Odśwież stronę i spróbuj ponownie.", roleCode);

        var validCodeList = await db.PermissionDefinitions.AsNoTracking().Select(x => x.Code).ToListAsync(cancellationToken);
        var validCodes = validCodeList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < permissionCodes.Length; i++)
        {
            var code = permissionCodes[i];
            if (!validCodes.Contains(code)) continue;
            var effect = permissionEffects[i] switch
            {
                "Allow" => "Allow",
                "Deny" => "Deny",
                _ => "Unset"
            };
            desired[code] = effect;
        }

        var existing = await db.RolePermissions.Where(x => x.RoleCode == roleCode).ToListAsync(cancellationToken);
        db.RolePermissions.RemoveRange(existing);
        foreach (var item in desired.Where(x => x.Value is "Allow" or "Deny"))
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleCode = roleCode,
                PermissionCode = item.Key,
                Effect = item.Value
            });
        }

        var allowCount = desired.Count(x => x.Value == "Allow");
        var denyCount = desired.Count(x => x.Value == "Deny");
        var unsetCount = desired.Count(x => x.Value == "Unset");
        await TouchAuthorizationPolicyAsync(current.Value.HouseholdId, roleCode, cancellationToken);
        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.PermissionsChanged", roleCode,
            $"Zapisano macierz roli: Allow={allowCount}, Deny={denyCount}, Unset={unsetCount}.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Uprawnienia roli zostały zapisane. Użytkownicy z tą rolą otrzymają nową politykę dostępu.";
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    [HttpPost("DeleteRole")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRole(string roleCode, CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!IsCustomRoleForHousehold(roleCode, current.Value.HouseholdId))
            return RedirectWithError("Role systemowe nie mogą zostać usunięte.", roleCode);

        var inUse = await db.AccessAssignments.AnyAsync(x => x.RoleCode == roleCode, cancellationToken);
        if (inUse) return RedirectWithError("Nie można usunąć roli, ponieważ ma historię przypisań. Dla zachowania audytu pozostaw ją bez aktywnych użytkowników.", roleCode);

        var role = await db.RoleDefinitions.SingleOrDefaultAsync(x => x.Code == roleCode, cancellationToken);
        if (role is null) return NotFound();

        var mappings = await db.RolePermissions.Where(x => x.RoleCode == roleCode).ToListAsync(cancellationToken);
        db.RolePermissions.RemoveRange(mappings);
        db.RoleDefinitions.Remove(role);
        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.RoleDeleted", roleCode,
            "Usunięto własną rolę bez aktywnych przypisań.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Własna rola została usunięta.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("AddAssignment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAssignment(
        Guid userAccountId,
        string roleCode,
        string profileCode,
        string scopeType,
        string? scopeId,
        DateTime? validToLocal,
        string reason,
        CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        if (!await AccountBelongsToHouseholdAsync(userAccountId, current.Value.HouseholdId, cancellationToken))
            return RedirectWithError("Wybrany użytkownik nie należy do tego gospodarstwa.", roleCode);
        if (!await IsAllowedRoleForHouseholdAsync(roleCode, current.Value.HouseholdId, cancellationToken))
            return RedirectWithError("Wybrana rola nie może zostać użyta w tym gospodarstwie.", roleCode);
        if (!await db.AccessProfileDefinitions.AnyAsync(x => x.Code == profileCode, cancellationToken))
            return RedirectWithError("Nieprawidłowy profil dostępu.", roleCode);

        scopeType = (scopeType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(scopeType) || scopeType.Length > 50)
            return RedirectWithError("Wybierz prawidłowy typ zakresu.", roleCode);

        var now = DateTime.UtcNow;
        var validToUtc = ToUtc(validToLocal);
        if (validToUtc.HasValue && validToUtc.Value <= now)
            return RedirectWithError("Termin ważności musi być w przyszłości.", roleCode);

        var normalizedScopeId = string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();
        var duplicate = await db.AccessAssignments.AnyAsync(x =>
            x.UserAccountId == userAccountId && x.HouseholdId == current.Value.HouseholdId &&
            x.RoleCode == roleCode && x.ScopeType == scopeType && x.ScopeId == normalizedScopeId &&
            (x.ValidToUtc == null || x.ValidToUtc > now), cancellationToken);
        if (duplicate) return RedirectWithError("Takie aktywne przypisanie już istnieje.", roleCode);

        db.AccessAssignments.Add(new AccessAssignment
        {
            Id = Guid.NewGuid(),
            UserAccountId = userAccountId,
            HouseholdId = current.Value.HouseholdId,
            RoleCode = roleCode,
            ProfileCode = profileCode,
            ScopeType = scopeType,
            ScopeId = normalizedScopeId,
            ValidFromUtc = now,
            ValidToUtc = validToUtc,
            CreatedByAccountId = current.Value.UserAccountId,
            Reason = NormalizeReason(reason, "Ręczne przypisanie w Role & Permissions Studio"),
            CreatedAtUtc = now,
            Version = 1
        });

        await TouchAuthorizationPolicyAsync(current.Value.HouseholdId, null, cancellationToken, userAccountId);
        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.AssignmentAdded", roleCode,
            $"Dodano rolę użytkownikowi {userAccountId:D}; profil={profileCode}; scope={scopeType}:{normalizedScopeId ?? "*"}.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Rola została przypisana użytkownikowi.";
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    [HttpPost("UpdateAssignment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignment(
        Guid assignmentId,
        string roleCode,
        string profileCode,
        string scopeType,
        string? scopeId,
        DateTime? validToLocal,
        string reason,
        CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        var assignment = await db.AccessAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId && x.HouseholdId == current.Value.HouseholdId, cancellationToken);
        if (assignment is null) return NotFound();

        if (!await IsAllowedRoleForHouseholdAsync(roleCode, current.Value.HouseholdId, cancellationToken))
            return RedirectWithError("Wybrana rola nie może zostać użyta w tym gospodarstwie.", assignment.RoleCode);
        if (!await db.AccessProfileDefinitions.AnyAsync(x => x.Code == profileCode, cancellationToken))
            return RedirectWithError("Nieprawidłowy profil dostępu.", assignment.RoleCode);

        if (assignment.RoleCode == RoleCodes.SuperAdministrator && roleCode != RoleCodes.SuperAdministrator &&
            await IsLastSuperAdministratorAsync(assignment.UserAccountId, current.Value.HouseholdId, cancellationToken))
            return RedirectWithError("Nie można odebrać ostatniego przypisania Superadministratora w gospodarstwie.", assignment.RoleCode);

        scopeType = (scopeType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(scopeType) || scopeType.Length > 50)
            return RedirectWithError("Wybierz prawidłowy typ zakresu.", assignment.RoleCode);

        var validToUtc = ToUtc(validToLocal);
        if (validToUtc.HasValue && validToUtc.Value <= DateTime.UtcNow)
            return RedirectWithError("Termin ważności w edycji musi być w przyszłości. Aby zakończyć rolę teraz, użyj przycisku „Zakończ przypisanie”.", assignment.RoleCode);

        assignment.RoleCode = roleCode;
        assignment.ProfileCode = profileCode;
        assignment.ScopeType = scopeType;
        assignment.ScopeId = string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();
        assignment.ValidToUtc = validToUtc;
        assignment.Reason = NormalizeReason(reason, assignment.Reason);
        assignment.Version++;

        await TouchAuthorizationPolicyAsync(current.Value.HouseholdId, null, cancellationToken, assignment.UserAccountId);
        await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.AssignmentUpdated", roleCode,
            $"Zmieniono przypisanie {assignmentId:D}; profil={profileCode}; scope={assignment.ScopeType}:{assignment.ScopeId ?? "*"}.", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Success"] = "Przypisanie roli zostało zaktualizowane.";
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    [HttpPost("EndAssignment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndAssignment(Guid assignmentId, string? selectedRoleCode, CancellationToken cancellationToken)
    {
        var current = await RequireRoleStudioAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        var assignment = await db.AccessAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId && x.HouseholdId == current.Value.HouseholdId, cancellationToken);
        if (assignment is null) return NotFound();

        if (assignment.RoleCode == RoleCodes.SuperAdministrator &&
            await IsLastSuperAdministratorAsync(assignment.UserAccountId, current.Value.HouseholdId, cancellationToken))
            return RedirectWithError("Nie można zakończyć ostatniego przypisania Superadministratora w gospodarstwie.", selectedRoleCode ?? assignment.RoleCode);

        if (assignment.ValidToUtc is null || assignment.ValidToUtc > DateTime.UtcNow)
        {
            assignment.ValidToUtc = DateTime.UtcNow;
            assignment.Version++;
            await TouchAuthorizationPolicyAsync(current.Value.HouseholdId, null, cancellationToken, assignment.UserAccountId);
            await AddAuditAsync(current.Value.UserAccountId, current.Value.HouseholdId, "RoleStudio.AssignmentEnded", assignment.RoleCode,
                $"Zakończono przypisanie {assignment.Id:D}.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["Success"] = "Przypisanie zostało zakończone.";
        return RedirectToAction(nameof(Index), new { roleCode = selectedRoleCode ?? assignment.RoleCode });
    }

    private async Task<RoleStudioPageViewModel> BuildModelAsync(Guid actorAccountId, Guid householdId, string? selectedRoleCode, CancellationToken cancellationToken)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, cancellationToken);
        var now = DateTime.UtcNow;
        var customPrefix = CustomRolePrefix(householdId);

        var rolesAll = await db.RoleDefinitions.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var rolesRaw = rolesAll
            .Where(x => !x.Code.StartsWith("custom.", StringComparison.OrdinalIgnoreCase) || x.Code.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var rolePermissions = await db.RolePermissions.AsNoTracking().ToListAsync(cancellationToken);
        var activeAssignments = await db.AccessAssignments.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && (x.ValidToUtc == null || x.ValidToUtc > now))
            .ToListAsync(cancellationToken);

        var roleRows = rolesRaw.Select(role =>
        {
            var mappings = rolePermissions.Where(x => x.RoleCode == role.Code).ToList();
            return new RoleStudioRoleRow(
                role.Code,
                role.Name,
                role.Code.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase),
                role.Code == RoleCodes.SuperAdministrator,
                mappings.Count(x => x.Effect == "Allow"),
                mappings.Count(x => x.Effect == "Deny"),
                activeAssignments.Count(x => x.RoleCode == role.Code));
        }).ToList();

        var selected = roleRows.FirstOrDefault(x => x.Code == selectedRoleCode)
                       ?? roleRows.FirstOrDefault(x => x.Code.StartsWith(customPrefix, StringComparison.OrdinalIgnoreCase))
                       ?? roleRows.FirstOrDefault(x => x.Code == RoleCodes.SuperAdministrator)
                       ?? roleRows.First();

        var effectByPermission = rolePermissions
            .Where(x => x.RoleCode == selected.Code)
            .ToDictionary(x => x.PermissionCode, x => x.Effect, StringComparer.OrdinalIgnoreCase);

        var permissionDefs = await db.PermissionDefinitions.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken);
        var permissionRows = permissionDefs.Select(p =>
        {
            var groupKey = PermissionGroupKey(p.Code);
            return new RoleStudioPermissionRow(
                p.Code,
                p.Description,
                groupKey,
                PermissionGroupLabel(groupKey),
                PermissionGroupDescription(groupKey),
                UiKind(p.Code),
                PermissionActionLabel(p.Code),
                PermissionImpactDescription(p.Code),
                p.DefaultScopeType,
                p.RiskLevel,
                p.IntroducedPackage,
                effectByPermission.TryGetValue(p.Code, out var effect) ? effect : "Unset");
        }).OrderBy(x => x.GroupLabel).ThenBy(x => x.Code).ToList();

        var profiles = await db.AccessProfileDefinitions.AsNoTracking().OrderBy(x => x.Rank)
            .Select(x => new RoleStudioProfileRow(x.Code, x.Name, x.Rank)).ToListAsync(cancellationToken);

        var membershipPersonIds = await db.HouseholdMemberships.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.Status == MembershipStatuses.Active && x.ValidTo == null)
            .Select(x => x.PersonId).ToListAsync(cancellationToken);
        var people = await db.Persons.AsNoTracking().Where(x => membershipPersonIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var accountsRaw = await db.UserAccounts.AsNoTracking().Where(x => x.Status != UserAccountStatuses.Deleted).ToListAsync(cancellationToken);
        var users = accountsRaw
            .Select(x => (Account: x, PersonId: x.PersonId is Guid pid ? pid : Guid.Empty))
            .Where(x => x.PersonId != Guid.Empty && people.ContainsKey(x.PersonId))
            .Select(x => new RoleStudioUserRow(x.Account.Id, $"{people[x.PersonId].FirstName} {people[x.PersonId].LastName}", x.Account.Login, x.Account.Status))
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var userMap = users.ToDictionary(x => x.AccountId);
        var roleMap = rolesRaw.ToDictionary(x => x.Code, x => x.Name, StringComparer.OrdinalIgnoreCase);
        var profileMap = profiles.ToDictionary(x => x.Code, x => x.Name, StringComparer.OrdinalIgnoreCase);

        var assignments = activeAssignments
            .Where(x => userMap.ContainsKey(x.UserAccountId))
            .OrderBy(x => userMap[x.UserAccountId].DisplayName)
            .ThenBy(x => roleMap.TryGetValue(x.RoleCode, out var roleName) ? roleName : x.RoleCode)
            .Select(x => new RoleStudioAssignmentRow(
                x.Id,
                x.UserAccountId,
                userMap[x.UserAccountId].DisplayName,
                userMap[x.UserAccountId].Login,
                x.RoleCode,
                roleMap.TryGetValue(x.RoleCode, out var roleName) ? roleName : x.RoleCode,
                x.ProfileCode,
                profileMap.TryGetValue(x.ProfileCode, out var profileName) ? profileName : x.ProfileCode,
                x.ScopeType,
                x.ScopeId,
                x.ValidFromUtc,
                x.ValidToUtc,
                x.Reason,
                x.UserAccountId == actorAccountId)).ToList();

        var scopes = permissionDefs.Select(x => x.DefaultScopeType)
            .Concat(activeAssignments.Select(x => x.ScopeType))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        return new RoleStudioPageViewModel
        {
            HouseholdId = householdId,
            HouseholdName = household.Name,
            SelectedRoleCode = selected.Code,
            SelectedRoleIsCustom = selected.IsCustom,
            SelectedRoleIsProtected = selected.IsProtected,
            Roles = roleRows,
            Permissions = permissionRows,
            Assignments = assignments,
            Profiles = profiles,
            Users = users,
            ScopeTypes = scopes,
            AllowCount = permissionRows.Count(x => x.Effect == "Allow"),
            DenyCount = permissionRows.Count(x => x.Effect == "Deny"),
            UnsetCount = permissionRows.Count(x => x.Effect == "Unset")
        };
    }

    private async Task<(Guid UserAccountId, Guid HouseholdId)?> RequireRoleStudioAccessAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return null;

        var isSuperAdmin = await db.AccessAssignments.AsNoTracking().AnyAsync(x =>
            x.UserAccountId == current.UserAccountId && x.HouseholdId == current.HouseholdId &&
            x.RoleCode == RoleCodes.SuperAdministrator &&
            (x.ValidToUtc == null || x.ValidToUtc > DateTime.UtcNow), cancellationToken);
        if (!isSuperAdmin) return null;

        var canManageMembers = await access.CanAsync(
            "household.member.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"),
            resourceType: "Person", cancellationToken: cancellationToken);
        var canManageSecurity = await access.CanAsync(
            "identity.account.security_manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"),
            resourceType: "UserAccount", cancellationToken: cancellationToken);

        return canManageMembers && canManageSecurity
            ? (current.UserAccountId, current.HouseholdId)
            : null;
    }

    private async Task<bool> AccountBelongsToHouseholdAsync(Guid accountId, Guid householdId, CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        if (account?.PersonId is not Guid personId) return false;
        return await db.HouseholdMemberships.AsNoTracking().AnyAsync(x =>
            x.HouseholdId == householdId && x.PersonId == personId && x.Status == MembershipStatuses.Active && x.ValidTo == null,
            cancellationToken);
    }

    private async Task<bool> IsAllowedRoleForHouseholdAsync(string roleCode, Guid householdId, CancellationToken cancellationToken)
    {
        if (!await db.RoleDefinitions.AnyAsync(x => x.Code == roleCode, cancellationToken)) return false;
        return !roleCode.StartsWith("custom.", StringComparison.OrdinalIgnoreCase) || IsCustomRoleForHousehold(roleCode, householdId);
    }

    private async Task<bool> IsLastSuperAdministratorAsync(Guid accountId, Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var superAdmins = await db.AccessAssignments.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.RoleCode == RoleCodes.SuperAdministrator && (x.ValidToUtc == null || x.ValidToUtc > now))
            .Select(x => x.UserAccountId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return superAdmins.Count <= 1 && superAdmins.Contains(accountId);
    }

    private async Task TouchAuthorizationPolicyAsync(Guid householdId, string? roleCode, CancellationToken cancellationToken, Guid? specificAccountId = null)
    {
        var household = await db.Households.SingleAsync(x => x.Id == householdId, cancellationToken);
        household.AccessPolicyVersion++;
        household.Version++;

        List<Guid> accountIds;
        if (specificAccountId.HasValue)
        {
            accountIds = [specificAccountId.Value];
        }
        else if (!string.IsNullOrWhiteSpace(roleCode))
        {
            var now = DateTime.UtcNow;
            accountIds = await db.AccessAssignments
                .Where(x => x.HouseholdId == householdId && x.RoleCode == roleCode && (x.ValidToUtc == null || x.ValidToUtc > now))
                .Select(x => x.UserAccountId).Distinct().ToListAsync(cancellationToken);
        }
        else
        {
            accountIds = [];
        }

        if (accountIds.Count == 0) return;
        var accounts = await db.UserAccounts.Where(x => accountIds.Contains(x.Id)).ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            account.AccessGeneration++;
            account.Version++;
        }
    }

    private async Task AddAuditAsync(Guid actorAccountId, Guid householdId, string eventType, string resourceId, string detailsSafe, CancellationToken cancellationToken)
    {
        db.AccessAuditEvents.Add(new AccessAuditEvent
        {
            Id = Guid.NewGuid(),
            ActorAccountId = actorAccountId,
            SubjectAccountId = actorAccountId,
            HouseholdId = householdId,
            EventType = eventType,
            PermissionCode = null,
            ResourceType = "RoleDefinition",
            ResourceId = Truncate(resourceId, 200),
            ScopeType = ResourceScopeTypes.Household,
            DecisionCode = "Applied",
            CorrelationId = CorrelationIdMiddleware.Get(HttpContext),
            AuditClassification = "Extended",
            DetailsSafe = Truncate(detailsSafe, 500),
            OccurredAtUtc = DateTime.UtcNow
        });
        await Task.CompletedTask;
    }

    private IActionResult RedirectWithError(string message, string? roleCode = null)
    {
        TempData["Error"] = message;
        return RedirectToAction(nameof(Index), new { roleCode });
    }

    private static DateTime? ToUtc(DateTime? local)
    {
        if (!local.HasValue) return null;
        var value = local.Value;
        if (value.Kind == DateTimeKind.Utc) return value;
        if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        return value.ToUniversalTime();
    }

    private static string NormalizeReason(string? value, string fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return Truncate(result, 500);
    }

    private static string NormalizeRoleSuffix(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-').ToLowerInvariant();
    }

    private static bool IsProtectedRole(string roleCode)
        => string.Equals(roleCode, RoleCodes.SuperAdministrator, StringComparison.OrdinalIgnoreCase);

    private static string CustomRolePrefix(Guid householdId) => $"custom.{householdId:N}.";
    private static bool IsCustomRoleForHousehold(string roleCode, Guid householdId)
        => roleCode.StartsWith(CustomRolePrefix(householdId), StringComparison.OrdinalIgnoreCase);

    private static string PermissionGroupKey(string code)
        => code.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "other";

    private static string PermissionGroupLabel(string key) => key switch
    {
        "household" => "Gospodarstwo i osoby",
        "identity" => "Konta i bezpieczeństwo",
        "private" or "privatefinance" => "Finanse prywatne",
        "finance" or "finances" or "householdfinance" or "payment" => "Finanse domowe",
        "documents" or "document" => "Dokumenty",
        "calendar" => "Kalendarz",
        "property" => "Nieruchomości",
        "assets" or "equipment" => "Sprzęt i wyposażenie",
        "rental" => "Najem i lokatorzy",
        "utilities" => "Media i liczniki",
        "system" => "System i administracja",
        "notifications" or "notification" => "Powiadomienia",
        _ => char.ToUpperInvariant(key[0]) + key[1..]
    };


    private static string PermissionGroupDescription(string key) => key switch
    {
        "household" => "Osoby, członkostwo w gospodarstwie, relacje rodzinne i podstawowe zarządzanie domownikami.",
        "identity" => "Konta użytkowników, hasła, blokady, sesje i operacje bezpieczeństwa konta.",
        "private" or "privatefinance" => "Prywatne konta, dochody i wydatki widoczne wyłącznie w dozwolonym zakresie właściciela.",
        "finance" or "finances" or "householdfinance" => "Wspólny budżet domu, wpłaty, rachunki, zwroty, kompensaty i saldo gospodarstwa.",
        "documents" or "document" => "Dokumenty, załączniki oraz dostęp do przechowywanych plików i ich metadanych.",
        "calendar" => "Kalendarz rodzinny, wydarzenia, terminy i widoczność zdarzeń.",
        "property" => "Działki, budynki, pokoje, statusy pomieszczeń oraz ich przypisania.",
        "assets" or "equipment" => "Sprzęt i wyposażenie przypisane do domu, pokoju, osoby lub lokatora.",
        "rental" => "Umowy najmu, lokatorzy, rozliczenia, wpłaty i proces zakończenia najmu.",
        "utilities" => "Liczniki, odczyty, taryfy, prognozy i rozliczenia mediów.",
        "system" => "Ustawienia techniczne, administracja systemem i funkcje o podwyższonym ryzyku.",
        "notifications" or "notification" => "Powiadomienia, kanały dostarczania oraz statusy komunikatów.",
        _ => "Uprawnienia związane z tym obszarem funkcjonalnym aplikacji."
    };

    private static string PermissionActionLabel(string code)
    {
        var parts = code.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault()?.ToLowerInvariant() ?? string.Empty;
        return last switch
        {
            "view" or "read" or "list" or "open" => "Widoczność i podgląd",
            "create" or "add" => "Dodawanie nowych danych",
            "edit" or "update" => "Edycja istniejących danych",
            "delete" => "Usuwanie danych",
            "archive" => "Archiwizowanie",
            "manage" => "Pełne zarządzanie",
            "submit" => "Zgłaszanie / wysyłanie",
            "approve" => "Zatwierdzanie",
            "reject" => "Odrzucanie",
            "activate" => "Aktywowanie",
            "publish" => "Publikowanie",
            "correct" => "Korygowanie",
            "end" or "close" => "Kończenie / zamykanie",
            "execute" => "Wykonywanie operacji",
            "test" => "Uruchamianie testu",
            "disable" => "Wyłączanie",
            "rotate" => "Zmiana danych uwierzytelniających",
            "export" => "Eksport danych",
            "import" => "Import danych",
            _ => "Operacja specjalna"
        };
    }

    private static string PermissionImpactDescription(string code)
    {
        var last = code.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant() ?? string.Empty;
        return last switch
        {
            "view" or "read" or "list" or "open" => "Zmiana tej opcji wpływa przede wszystkim na to, czy użytkownik może wejść do danego widoku i odczytać jego dane.",
            "create" or "add" => "Zmiana tej opcji decyduje, czy użytkownik zobaczy i będzie mógł użyć funkcji dodawania nowych rekordów.",
            "edit" or "update" => "Zmiana tej opcji decyduje, czy użytkownik może modyfikować już istniejące dane.",
            "delete" => "Zmiana tej opcji decyduje, czy użytkownik może usuwać dane. To uprawnienie warto nadawać ostrożnie.",
            "manage" => "To szerokie uprawnienie zarządcze. Może obejmować kilka akcji w danym module, dlatego powinno być nadawane tylko zaufanym rolom.",
            "approve" => "Zmiana tej opcji decyduje, czy użytkownik może zatwierdzić operację przygotowaną lub zgłoszoną przez inną osobę.",
            "reject" => "Zmiana tej opcji decyduje, czy użytkownik może odrzucić oczekującą operację lub zgłoszenie.",
            "publish" => "Zmiana tej opcji decyduje, czy użytkownik może opublikować dane i uczynić je obowiązującymi dla innych osób.",
            "correct" => "Zmiana tej opcji decyduje, czy użytkownik może wykonywać korekty po zatwierdzeniu lub publikacji. Zwykle jest to uprawnienie podwyższonego ryzyka.",
            "end" or "close" => "Zmiana tej opcji decyduje, czy użytkownik może zakończyć dany proces, np. najem lub aktywne przypisanie.",
            "test" => "Zmiana tej opcji decyduje, czy użytkownik może uruchomić techniczny test konfiguracji lub połączenia.",
            _ => "Zmiana tej opcji wpływa na konkretną operację opisaną poniżej. Szczegółowy efekt wynika z opisu PermissionCode."
        };
    }

    private static string UiKind(string code)
    {
        var last = code.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant() ?? string.Empty;
        if (last is "view" or "read" or "list" or "open") return "Moduł / ekran";
        if (last is "create" or "edit" or "update" or "delete" or "archive" or "manage" or "submit" or "approve" or "reject" or "activate" or "publish" or "correct" or "end" or "execute" or "test" or "disable" or "rotate") return "Przycisk / akcja";
        return "Operacja / dane";
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
