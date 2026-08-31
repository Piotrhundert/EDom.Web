using System.Security.Claims;
using EDom.Application.Authorization;
using EDom.Domain.Authorization;
using EDom.Domain.Households;
using EDom.Infrastructure.Persistence;
using EDom.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Authorization;

public sealed record WebUserContext(
    Guid UserAccountId,
    Guid PersonId,
    string Login,
    string DisplayName,
    Guid HouseholdId,
    string HouseholdName);

public sealed class WebAccessService(
    IHttpContextAccessor httpContextAccessor,
    EDomDbContext db,
    IAuthorizationEvaluator authorizationEvaluator)
{
    public async Task<WebUserContext?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var http = httpContextAccessor.HttpContext;
        if (http?.User.Identity?.IsAuthenticated != true)
            return null;

        var idText = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idText, out var accountId))
            return null;

        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account?.PersonId is not { } personId)
            return null;

        var person = await db.Persons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == personId, cancellationToken);
        if (person is null)
            return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var household = await (
            from membership in db.HouseholdMemberships.AsNoTracking()
            join h in db.Households.AsNoTracking() on membership.HouseholdId equals h.Id
            where membership.PersonId == personId
            where membership.Status == MembershipStatuses.Active
            where membership.ValidFrom <= today
            where membership.ValidTo == null || membership.ValidTo >= today
            where h.Status == HouseholdStatuses.Active
            orderby h.CreatedAtUtc
            select h).FirstOrDefaultAsync(cancellationToken);

        if (household is null)
            return null;

        var displayName = string.Join(" ", new[] { person.FirstName, person.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new WebUserContext(account.Id, person.Id, account.Login, displayName, household.Id, household.Name);
    }

    public async Task<IReadOnlyList<ModuleDefinition>> GetAllowedModulesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ModuleDefinition>();
        foreach (var module in ModuleCatalog.All)
        {
            if (await CanUseModuleAsync(module, cancellationToken))
                result.Add(module);
        }
        return result;
    }

    public async Task<bool> CanAsync(
        string permissionCode,
        string requestedScope,
        string? requestedScopeId = null,
        Guid? ownerPersonId = null,
        Guid? childPersonId = null,
        string resourceType = "HouseholdFamily",
        string? resourceId = null,
        string riskLevel = "R0",
        CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null)
            return false;

        var http = httpContextAccessor.HttpContext;
        var correlationId = http is null ? Guid.NewGuid().ToString("N") : CorrelationIdMiddleware.Get(http);
        var decision = await authorizationEvaluator.EvaluateAsync(
            new AuthorizationRequest(
                current.UserAccountId, current.HouseholdId, permissionCode, resourceType, resourceId,
                requestedScope, requestedScopeId ?? (requestedScope == ResourceScopeTypes.Household ? current.HouseholdId.ToString("D") : null),
                ownerPersonId, childPersonId, riskLevel, "Web", correlationId, DateTime.UtcNow,
                FeatureEnabled: true, HasMfa: false, ReauthAtUtc: null),
            cancellationToken);
        return decision.IsAllowed;
    }

    public async Task<bool> CanUseModuleAsync(ModuleDefinition module, CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null)
            return false;

        if (string.Equals(module.Key, "documents", StringComparison.OrdinalIgnoreCase))
        {
            if (await CanAsync("documents.document.manage_own", ResourceScopeTypes.Own, current.PersonId.ToString("D"), current.PersonId, resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                return true;
            return await CanAsync("documents.document.manage_shared", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken);
        }

        if (string.Equals(module.Key, "calendar", StringComparison.OrdinalIgnoreCase))
        {
            if (await CanAsync("calendar.event.manage_own", ResourceScopeTypes.Own, current.PersonId.ToString("D"), current.PersonId, resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                return true;
            return await CanAsync("calendar.event.manage_shared", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken);
        }

        if (string.Equals(module.Key, "rental", StringComparison.OrdinalIgnoreCase))
        {
            if (await CanAsync("rental.contract.create", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                return true;
            var rentalParcelIds = await db.Parcels.AsNoTracking().Where(x => x.HouseholdId == current.HouseholdId).Select(x => x.Id).ToListAsync(cancellationToken);
            foreach (var parcelId in rentalParcelIds)
                if (await CanAsync("rental.contract.create", ResourceScopeTypes.Property, parcelId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
            var now = DateTime.UtcNow;
            var roomScopes = await db.AccessAssignments.AsNoTracking()
                .Where(x => x.UserAccountId == current.UserAccountId)
                .Where(x => x.HouseholdId == current.HouseholdId)
                .Where(x => x.RoleCode == RoleCodes.Tenant)
                .Where(x => x.ScopeType == ResourceScopeTypes.AssignedRoom)
                .Where(x => x.ValidFromUtc <= now)
                .Where(x => x.ValidToUtc == null || x.ValidToUtc > now)
                .Select(x => x.ScopeId).ToListAsync(cancellationToken);
            foreach (var roomScope in roomScopes.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (await CanAsync("rental.payment.submit", ResourceScopeTypes.AssignedRoom, roomScope, current.PersonId, resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
            return false;
        }

        if (string.Equals(module.Key, "utilities", StringComparison.OrdinalIgnoreCase))
        {
            if (await CanAsync("utilities.invoice.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                return true;

            var utilityParcelIds = await db.Parcels.AsNoTracking().Where(x => x.HouseholdId == current.HouseholdId).Select(x => x.Id).ToListAsync(cancellationToken);
            foreach (var parcelId in utilityParcelIds)
            {
                if (await CanAsync("utilities.invoice.manage", ResourceScopeTypes.Property, parcelId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
                if (await CanAsync("utilities.reading.submit", ResourceScopeTypes.Property, parcelId.ToString("D"), current.PersonId, resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
            }

            var now = DateTime.UtcNow;
            var roomScopes = await db.AccessAssignments.AsNoTracking()
                .Where(x => x.UserAccountId == current.UserAccountId)
                .Where(x => x.HouseholdId == current.HouseholdId)
                .Where(x => x.RoleCode == RoleCodes.Tenant)
                .Where(x => x.ScopeType == ResourceScopeTypes.AssignedRoom)
                .Where(x => x.ValidFromUtc <= now)
                .Where(x => x.ValidToUtc == null || x.ValidToUtc > now)
                .Select(x => x.ScopeId).ToListAsync(cancellationToken);
            foreach (var roomScope in roomScopes.Where(x => !string.IsNullOrWhiteSpace(x)))
                if (await CanAsync("utilities.reading.submit", ResourceScopeTypes.AssignedRoom, roomScope, current.PersonId, resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
            return false;
        }

        if (string.Equals(module.Key, "property", StringComparison.OrdinalIgnoreCase))
        {
            if (await CanAsync("property.structure.manage", ResourceScopeTypes.Household, current.HouseholdId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                return true;

            var parcelIds = await db.Parcels.AsNoTracking().Where(x => x.HouseholdId == current.HouseholdId).Select(x => x.Id).ToListAsync(cancellationToken);
            foreach (var parcelId in parcelIds)
                if (await CanAsync("property.structure.manage", ResourceScopeTypes.Property, parcelId.ToString("D"), resourceType: "WebModule", resourceId: module.Key, cancellationToken: cancellationToken))
                    return true;
            return false;
        }

        var http = httpContextAccessor.HttpContext;
        var correlationId = http is null ? Guid.NewGuid().ToString("N") : CorrelationIdMiddleware.Get(http);

        var isOwnScoped = string.Equals(module.Key, "private-finance", StringComparison.OrdinalIgnoreCase);
        var requestedScope = isOwnScoped ? ResourceScopeTypes.Own : ResourceScopeTypes.Household;
        var requestedScopeId = isOwnScoped ? current.PersonId.ToString("D") : current.HouseholdId.ToString("D");

        var decision = await authorizationEvaluator.EvaluateAsync(
            new AuthorizationRequest(
                current.UserAccountId,
                current.HouseholdId,
                module.PermissionCode,
                "WebModule",
                module.Key,
                requestedScope,
                requestedScopeId,
                current.PersonId,
                null,
                "R0",
                "Web",
                correlationId,
                DateTime.UtcNow,
                FeatureEnabled: true,
                HasMfa: false,
                ReauthAtUtc: null),
            cancellationToken);

        return decision.IsAllowed;
    }
}
