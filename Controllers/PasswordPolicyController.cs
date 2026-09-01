using EDom.Domain.Authorization;
using EDom.Domain.Identity;
using EDom.Infrastructure.Identity;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("PasswordPolicy")]
public sealed class PasswordPolicyController(
    EDomDbContext db,
    WebAccessService access) : Controller
{
    private const int FixedLockoutThreshold = 3;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await RequireAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        var household = await db.Households.AsNoTracking()
            .SingleAsync(x => x.Id == current.Value.HouseholdId, cancellationToken);
        var householdPolicy = await db.SecurityPolicies.AsNoTracking()
            .Where(x => x.IsActive && x.HouseholdId == current.Value.HouseholdId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
        var policy = householdPolicy ?? await db.SecurityPolicies.AsNoTracking()
            .Where(x => x.IsActive && x.HouseholdId == null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? SecurityPolicyDefaultsAccessor.CreateGlobal();

        return View(new PasswordPolicyViewModel
        {
            HouseholdId = current.Value.HouseholdId,
            HouseholdName = household.Name,
            PolicySource = householdPolicy is null ? "Globalna / domyślna" : "Własna polityka gospodarstwa",
            MinLength = policy.MinLength,
            MinUpper = policy.MinUpper,
            MinLower = policy.MinLower,
            MinDigits = policy.MinDigits,
            MinSpecial = policy.MinSpecial,
            HistoryCount = policy.HistoryCount,
            PasswordMaxAgeDays = policy.PasswordMaxAgeDays,
            LockoutThreshold = FixedLockoutThreshold,
            Version = policy.Version
        });
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PasswordPolicyViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireAccessAsync(cancellationToken);
        if (current is null) return Forbid();

        var error = Validate(model);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        var policy = await db.SecurityPolicies
            .Where(x => x.IsActive && x.HouseholdId == current.Value.HouseholdId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            var source = await db.SecurityPolicies.AsNoTracking()
                .Where(x => x.IsActive && x.HouseholdId == null)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken)
                ?? SecurityPolicyDefaultsAccessor.CreateGlobal();

            policy = new SecurityPolicy
            {
                Id = Guid.NewGuid(),
                HouseholdId = current.Value.HouseholdId,
                MinLength = model.MinLength,
                MinUpper = model.MinUpper,
                MinLower = model.MinLower,
                MinDigits = model.MinDigits,
                MinSpecial = model.MinSpecial,
                HistoryCount = model.HistoryCount,
                PasswordMaxAgeDays = NormalizeMaxAge(model.PasswordMaxAgeDays),
                RequireMfaForRisk = source.RequireMfaForRisk,
                IsActive = true,
                Version = 1
            };
            db.SecurityPolicies.Add(policy);
        }
        else
        {
            policy.MinLength = model.MinLength;
            policy.MinUpper = model.MinUpper;
            policy.MinLower = model.MinLower;
            policy.MinDigits = model.MinDigits;
            policy.MinSpecial = model.MinSpecial;
            policy.HistoryCount = model.HistoryCount;
            policy.PasswordMaxAgeDays = NormalizeMaxAge(model.PasswordMaxAgeDays);
            policy.Version++;
        }

        await InvalidateHouseholdSessionsAsync(current.Value.HouseholdId, cancellationToken);
        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            Id = Guid.NewGuid(),
            UserAccountId = current.Value.UserAccountId,
            HouseholdId = current.Value.HouseholdId,
            EventType = "PasswordPolicyChanged",
            IpHash = null,
            DeviceInfo = null,
            OccurredAtUtc = DateTime.UtcNow,
            Result = $"MinLength={model.MinLength}; Upper={model.MinUpper}; Lower={model.MinLower}; Digits={model.MinDigits}; Special={model.MinSpecial}; History={model.HistoryCount}; MaxAgeDays={NormalizeMaxAge(model.PasswordMaxAgeDays)?.ToString() ?? "off"}",
            CorrelationId = CorrelationIdMiddleware.Get(HttpContext)
        });

        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "Polityka haseł została zapisana. Aktywne sesje użytkowników gospodarstwa wymagają ponownego uwierzytelnienia.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<(Guid UserAccountId, Guid HouseholdId)?> RequireAccessAsync(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return null;

        var allowed = await access.CanAsync(
            "identity.account.security_manage",
            ResourceScopeTypes.Household,
            current.HouseholdId.ToString("D"),
            resourceType: "SecurityPolicy",
            cancellationToken: cancellationToken);

        return allowed ? (current.UserAccountId, current.HouseholdId) : null;
    }

    private async Task InvalidateHouseholdSessionsAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var personIds = await db.HouseholdMemberships.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.Status == "Active" && x.ValidTo == null)
            .Select(x => x.PersonId)
            .ToListAsync(cancellationToken);

        var accounts = await db.UserAccounts
            .Where(x => x.PersonId != null && personIds.Contains(x.PersonId.Value))
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            account.AccessGeneration++;
            account.Version++;
        }
    }

    private static string? Validate(PasswordPolicyViewModel model)
    {
        if (model.MinLength is < 6 or > 128)
            return "Minimalna długość hasła musi mieścić się w zakresie 6–128 znaków.";
        if (model.MinUpper is < 0 or > 20 || model.MinLower is < 0 or > 20 || model.MinDigits is < 0 or > 20 || model.MinSpecial is < 0 or > 20)
            return "Minimalna liczba znaków każdej klasy musi mieścić się w zakresie 0–20.";
        if (model.MinUpper + model.MinLower + model.MinDigits + model.MinSpecial > model.MinLength)
            return "Suma wymaganych wielkich liter, małych liter, cyfr i znaków specjalnych nie może przekraczać minimalnej długości hasła.";
        if (model.HistoryCount is < 0 or > 50)
            return "Historia haseł musi mieścić się w zakresie 0–50.";
        if (model.PasswordMaxAgeDays is < 0 or > 3650)
            return "Okres ważności hasła musi mieścić się w zakresie 0–3650 dni. Wartość 0 wyłącza wygasanie.";
        return null;
    }

    private static int? NormalizeMaxAge(int? value)
        => value is null or <= 0 ? null : value;
}
