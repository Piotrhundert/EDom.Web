using System.Security.Cryptography;
using System.Text;
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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("UserProfile")]
public sealed class UserProfileController(
    EDomDbContext db,
    WebAccessService access,
    IIdentityService identityService,
    IDataProtectionProvider dataProtectionProvider) : Controller
{
    private const string PeselProtectionMarker = "aspnet-dp:v1";
    private readonly IDataProtector _peselProtector = dataProtectionProvider.CreateProtector("e-dom.user-profile.pesel.v1");

    [HttpGet("")]
    [HttpGet("{accountId:guid}")]
    public async Task<IActionResult> Index(Guid? accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();

        var canManage = await CanManageUsersAsync(current.HouseholdId, cancellationToken);
        var targetAccountId = accountId ?? current.UserAccountId;
        if (targetAccountId != current.UserAccountId && !canManage) return Forbid();
        var canManageSecurity = await CanManageAccountSecurityAsync(current.HouseholdId, targetAccountId, cancellationToken);

        var model = await BuildModelAsync(current.UserAccountId, current.HouseholdId, targetAccountId, canManage, canManageSecurity, cancellationToken);
        if (model is null) return NotFound();

        ViewData["HouseholdName"] = model.HouseholdName;
        return View(model);
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserProfileEditInput input, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();

        var canManage = await CanManageUsersAsync(current.HouseholdId, cancellationToken);
        if (input.AccountId != current.UserAccountId && !canManage) return Forbid();

        var account = await db.UserAccounts.SingleOrDefaultAsync(x => x.Id == input.AccountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        if (account is null) return NotFound();
        var personId = account.PersonId is Guid pid ? pid : Guid.Empty;
        if (personId == Guid.Empty || !await BelongsToHouseholdAsync(current.HouseholdId, personId, cancellationToken)) return Forbid();

        if (string.IsNullOrWhiteSpace(input.FirstName) || string.IsNullOrWhiteSpace(input.LastName))
        {
            TempData["Error"] = "Imię i nazwisko są wymagane.";
            return RedirectToAction(nameof(Index), new { accountId = input.AccountId });
        }

        if (input.BirthDate.HasValue && input.BirthDate.Value > DateOnly.FromDateTime(DateTime.Today))
        {
            TempData["Error"] = "Data urodzenia nie może być przyszła.";
            return RedirectToAction(nameof(Index), new { accountId = input.AccountId });
        }

        var normalizedPesel = NormalizePesel(input.Pesel);
        if (!input.RemovePesel && normalizedPesel is not null && !IsValidPesel(normalizedPesel))
        {
            TempData["Error"] = "PESEL jest nieprawidłowy. Wpisz 11 cyfr z poprawną cyfrą kontrolną.";
            return RedirectToAction(nameof(Index), new { accountId = input.AccountId });
        }

        if (HasAnyAddressValue(input) && string.IsNullOrWhiteSpace(input.City))
        {
            TempData["Error"] = "Jeśli podajesz adres, miejscowość jest wymagana.";
            return RedirectToAction(nameof(Index), new { accountId = input.AccountId });
        }

        var person = await db.Persons.SingleAsync(x => x.Id == personId, cancellationToken);
        person.FirstName = input.FirstName.Trim();
        person.LastName = input.LastName.Trim();
        person.BirthDate = input.BirthDate;
        person.Version++;

        await UpdatePeselAsync(personId, normalizedPesel, input.RemovePesel, cancellationToken);
        await UpsertContactAsync(personId, "Email", input.Email, cancellationToken);
        await UpsertContactAsync(personId, "Phone", input.Phone, cancellationToken);
        await UpsertAddressAsync(personId, input, cancellationToken);

        db.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            Id = Guid.NewGuid(),
            UserAccountId = current.UserAccountId,
            HouseholdId = current.HouseholdId,
            EventType = "UserProfileUpdated",
            OccurredAtUtc = DateTime.UtcNow,
            Result = "Success",
            CorrelationId = CorrelationIdMiddleware.Get(HttpContext)
        });

        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = "Profil użytkownika został zapisany.";
        return RedirectToAction(nameof(Index), new { accountId = input.AccountId });
    }

    [HttpPost("ChangePassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            TempData["Error"] = "Nowe hasło i potwierdzenie nie są takie same.";
            return RedirectToAction(nameof(Index));
        }

        var result = await identityService.ChangePasswordAsync(
            current.UserAccountId,
            currentPassword ?? string.Empty,
            newPassword ?? string.Empty,
            BuildIdentityContext(),
            cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        await HttpContext.SignOutAsync("EDomCookie");
        TempData["Success"] = "Hasło zostało zmienione. Zaloguj się ponownie.";
        return RedirectToAction("Login", "Account");
    }

    [HttpPost("ResetPassword")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(Guid accountId, string temporaryPassword, bool mustChangePassword, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageAccountSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetAccountBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        var result = await identityService.ResetPasswordAsync(
            accountId,
            temporaryPassword ?? string.Empty,
            mustChangePassword,
            BuildIdentityContext(),
            cancellationToken);

        TempData[result.Succeeded ? "Success" : "Error"] = result.Succeeded
            ? "Hasło zostało zresetowane. Wszystkie wcześniejsze sesje użytkownika zostały unieważnione."
            : result.Message;

        if (result.Succeeded && accountId == current.UserAccountId)
        {
            await HttpContext.SignOutAsync("EDomCookie");
            return RedirectToAction("Login", "Account");
        }

        return RedirectToAction(nameof(Index), new { accountId });
    }

    [HttpPost("Unlock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(Guid accountId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanManageAccountSecurityAsync(current.HouseholdId, accountId, cancellationToken)) return Forbid();
        if (!await TargetAccountBelongsToHouseholdAsync(accountId, current.HouseholdId, cancellationToken)) return Forbid();

        await identityService.UnlockAccountAsync(accountId, BuildIdentityContext(), "Odblokowanie z profilu użytkownika", cancellationToken);
        TempData["Success"] = "Konto zostało odblokowane, a wcześniejsze sesje unieważnione.";
        return RedirectToAction(nameof(Index), new { accountId });
    }

    private async Task<UserProfilePageViewModel?> BuildModelAsync(
        Guid currentAccountId,
        Guid householdId,
        Guid targetAccountId,
        bool canManage,
        bool canManageSecurity,
        CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetAccountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        if (account is null) return null;
        var personId = account.PersonId is Guid pid ? pid : Guid.Empty;
        if (personId == Guid.Empty) return null;

        var membership = await db.HouseholdMemberships.AsNoTracking()
            .SingleOrDefaultAsync(x => x.HouseholdId == householdId && x.PersonId == personId && x.Status == MembershipStatuses.Active && x.ValidTo == null, cancellationToken);
        if (membership is null) return null;

        var person = await db.Persons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == personId, cancellationToken);
        if (person is null) return null;
        var household = await db.Households.AsNoTracking().SingleAsync(x => x.Id == householdId, cancellationToken);
        var credential = await db.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.UserAccountId == targetAccountId, cancellationToken);

        var contacts = await db.ContactPoints.AsNoTracking().Where(x => x.PersonId == personId).ToListAsync(cancellationToken);
        var email = contacts.Where(x => x.Type == "Email" && x.IsPreferred).Select(x => x.ValueCipherOrPlain).FirstOrDefault()
                    ?? contacts.Where(x => x.Type == "Email").Select(x => x.ValueCipherOrPlain).FirstOrDefault();
        var phone = contacts.Where(x => x.Type == "Phone" && x.IsPreferred).Select(x => x.ValueCipherOrPlain).FirstOrDefault()
                    ?? contacts.Where(x => x.Type == "Phone").Select(x => x.ValueCipherOrPlain).FirstOrDefault();

        var address = await db.PostalAddresses.AsNoTracking()
            .Where(x => x.PersonId == personId && x.ValidTo == null)
            .OrderByDescending(x => x.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);

        var sensitive = await db.PersonSensitiveData.AsNoTracking().SingleOrDefaultAsync(x => x.PersonId == personId, cancellationToken);
        var (pesel, peselAvailable, peselStatus) = ReadPesel(sensitive);

        var roles = await (
            from assignment in db.AccessAssignments.AsNoTracking()
            join role in db.RoleDefinitions.AsNoTracking() on assignment.RoleCode equals role.Code
            join profile in db.AccessProfileDefinitions.AsNoTracking() on assignment.ProfileCode equals profile.Code
            where assignment.UserAccountId == targetAccountId
                  && assignment.HouseholdId == householdId
                  && (assignment.ValidToUtc == null || assignment.ValidToUtc > DateTime.UtcNow)
            orderby role.Name, profile.Rank
            select new UserProfileRoleRow(
                role.Code,
                role.Name,
                profile.Code,
                profile.Name,
                assignment.ScopeType,
                assignment.ScopeId,
                assignment.ValidToUtc)).ToListAsync(cancellationToken);

        var groups = await (
            from member in db.FamilyGroupMembers.AsNoTracking()
            join familyGroup in db.FamilyGroups.AsNoTracking() on member.FamilyGroupId equals familyGroup.Id
            where member.PersonId == personId
                  && familyGroup.HouseholdId == householdId
                  && familyGroup.Status == "Active"
                  && member.ValidTo == null
            orderby familyGroup.Name
            select new UserProfileGroupRow(familyGroup.Id, familyGroup.Name, member.GroupRole, member.ValidFrom, member.ValidTo)).ToListAsync(cancellationToken);

        var residenceEntities = await db.ResidenceAssignments.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.PersonId == personId && x.ValidTo == null)
            .OrderByDescending(x => x.ValidFrom)
            .ToListAsync(cancellationToken);
        var buildingIds = residenceEntities
            .Select(x => x.BuildingId is Guid id ? id : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToArray();
        var roomIds = residenceEntities
            .Select(x => x.RoomId is Guid id ? id : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToArray();
        var buildingNames = await db.Buildings.AsNoTracking()
            .Where(x => buildingIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var roomNames = await db.Rooms.AsNoTracking()
            .Where(x => roomIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var residences = residenceEntities.Select(x =>
        {
            var buildingId = x.BuildingId is Guid bid ? bid : Guid.Empty;
            var roomId = x.RoomId is Guid rid ? rid : Guid.Empty;
            return new UserProfileResidenceRow(
                x.ResidenceType,
                buildingId != Guid.Empty && buildingNames.TryGetValue(buildingId, out var buildingName) ? buildingName : "—",
                roomId != Guid.Empty && roomNames.TryGetValue(roomId, out var roomName) ? roomName : "—",
                x.ValidFrom,
                x.ValidTo);
        }).ToList();

        var emergencyContacts = await db.EmergencyContacts.AsNoTracking()
            .Where(x => x.PersonId == personId)
            .OrderBy(x => x.Name)
            .Select(x => new UserProfileEmergencyContactRow(x.Name, x.RelationshipType))
            .ToListAsync(cancellationToken);

        var documents = await db.IdentityDocuments.AsNoTracking()
            .Where(x => x.PersonId == personId)
            .OrderByDescending(x => x.IssuedOn)
            .Select(x => new UserProfileIdentityDocumentRow(x.DocumentType, x.CountryCode ?? "—", x.IssuedOn, x.ExpiresOn, x.Status))
            .ToListAsync(cancellationToken);

        var accountOptions = canManage
            ? await LoadHouseholdAccountsAsync(householdId, cancellationToken)
            : Array.Empty<UserProfileAccountOption>();

        return new UserProfilePageViewModel
        {
            AccountId = targetAccountId,
            PersonId = personId,
            FirstName = person.FirstName,
            LastName = person.LastName,
            BirthDate = person.BirthDate,
            PersonType = TranslatePersonType(person.PersonType),
            PersonStatus = TranslateStatus(person.Status),
            CreatedAtUtc = person.CreatedAtUtc,
            Login = account.Login,
            AccountStatus = TranslateStatus(account.Status),
            LastLoginAtUtc = account.LastLoginAtUtc,
            FailedLoginCount = account.FailedLoginCount,
            LockoutReason = account.LockoutReason,
            MustChangePassword = credential?.MustChangePassword ?? false,
            PasswordChangedAtUtc = credential?.ChangedAtUtc,
            HouseholdName = household.Name,
            OrganizationalRole = TranslateOrganizationalRole(membership.OrganizationalRole),
            MembershipValidFrom = membership.ValidFrom,
            Pesel = pesel,
            PeselAvailable = peselAvailable,
            PeselStatusMessage = peselStatus,
            Email = email,
            Phone = phone,
            Country = address?.Country ?? "PL",
            Region = address?.Region,
            City = address?.City,
            PostalCode = address?.PostalCode,
            Street = address?.Street,
            BuildingNo = address?.BuildingNo,
            UnitNo = address?.UnitNo,
            IsOwnProfile = targetAccountId == currentAccountId,
            CanManageUsers = canManage,
            CanResetPassword = canManageSecurity,
            CanEditProfile = targetAccountId == currentAccountId || canManage,
            Roles = roles,
            Groups = groups,
            Residences = residences,
            EmergencyContacts = emergencyContacts,
            IdentityDocuments = documents,
            HouseholdAccounts = accountOptions
        };
    }

    private async Task<IReadOnlyList<UserProfileAccountOption>> LoadHouseholdAccountsAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var memberships = await db.HouseholdMemberships.AsNoTracking()
            .Where(x => x.HouseholdId == householdId && x.Status == MembershipStatuses.Active && x.ValidTo == null)
            .ToListAsync(cancellationToken);
        var personIds = memberships.Select(x => x.PersonId).ToHashSet();
        var people = await db.Persons.AsNoTracking().Where(x => personIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var accounts = await db.UserAccounts.AsNoTracking().Where(x => x.Status != UserAccountStatuses.Deleted).ToListAsync(cancellationToken);

        return accounts
            .Select(account => (Account: account, PersonId: account.PersonId is Guid pid ? pid : Guid.Empty))
            .Where(x => x.PersonId != Guid.Empty && personIds.Contains(x.PersonId) && people.ContainsKey(x.PersonId))
            .Select(x => new UserProfileAccountOption(
                x.Account.Id,
                $"{people[x.PersonId].FirstName} {people[x.PersonId].LastName}",
                x.Account.Login,
                TranslateStatus(x.Account.Status)))
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<bool> CanManageUsersAsync(Guid householdId, CancellationToken cancellationToken)
        => await access.CanAsync(
            "household.member.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            resourceType: "Person",
            cancellationToken: cancellationToken);

    private async Task<bool> CanManageAccountSecurityAsync(Guid householdId, Guid accountId, CancellationToken cancellationToken)
        => await access.CanAsync(
            "identity.account.security_manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            resourceType: "UserAccount",
            resourceId: accountId.ToString("D"),
            cancellationToken: cancellationToken);

    private async Task<bool> BelongsToHouseholdAsync(Guid householdId, Guid personId, CancellationToken cancellationToken)
        => await db.HouseholdMemberships.AsNoTracking().AnyAsync(
            x => x.HouseholdId == householdId && x.PersonId == personId && x.Status == MembershipStatuses.Active && x.ValidTo == null,
            cancellationToken);

    private async Task<bool> TargetAccountBelongsToHouseholdAsync(Guid accountId, Guid householdId, CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId && x.Status != UserAccountStatuses.Deleted, cancellationToken);
        var personId = account?.PersonId is Guid pid ? pid : Guid.Empty;
        return personId != Guid.Empty && await BelongsToHouseholdAsync(householdId, personId, cancellationToken);
    }

    private IdentityRequestContext BuildIdentityContext()
        => new(
            CorrelationIdMiddleware.Get(HttpContext),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].ToString(),
            null);

    private async Task UpdatePeselAsync(Guid personId, string? normalizedPesel, bool remove, CancellationToken cancellationToken)
    {
        var sensitive = await db.PersonSensitiveData.SingleOrDefaultAsync(x => x.PersonId == personId, cancellationToken);
        if (remove)
        {
            if (sensitive is not null)
            {
                sensitive.PeselCipher = null;
                sensitive.PeselNonce = null;
                sensitive.PeselSearchHash = null;
            }
            return;
        }

        if (normalizedPesel is null) return;
        sensitive ??= new PersonSensitiveData { PersonId = personId };
        if (db.Entry(sensitive).State == EntityState.Detached) db.PersonSensitiveData.Add(sensitive);
        sensitive.PeselCipher = _peselProtector.Protect(normalizedPesel);
        sensitive.PeselNonce = PeselProtectionMarker;
        sensitive.PeselSearchHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPesel)));
    }

    private (string? Value, bool Available, string? Message) ReadPesel(PersonSensitiveData? sensitive)
    {
        if (sensitive is null || string.IsNullOrWhiteSpace(sensitive.PeselCipher)) return (null, true, null);
        if (string.Equals(sensitive.PeselNonce, PeselProtectionMarker, StringComparison.Ordinal))
        {
            try { return (_peselProtector.Unprotect(sensitive.PeselCipher), true, null); }
            catch (CryptographicException) { return (null, false, "PESEL jest zapisany, ale nie można go odszyfrować przy użyciu aktualnego klucza ochrony danych."); }
        }

        if (sensitive.PeselCipher.Length == 11 && sensitive.PeselCipher.All(char.IsDigit))
            return (sensitive.PeselCipher, true, "Wykryto starszy, niechroniony zapis PESEL. Zapisanie profilu przeniesie go do chronionego formatu.");

        return (null, false, "PESEL istnieje w starszym formacie szyfrowania i nie jest ujawniany przez ten ekran.");
    }

    private async Task UpsertContactAsync(Guid personId, string type, string? value, CancellationToken cancellationToken)
    {
        var contacts = await db.ContactPoints.Where(x => x.PersonId == personId && x.Type == type).ToListAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            db.ContactPoints.RemoveRange(contacts);
            return;
        }

        foreach (var item in contacts) item.IsPreferred = false;
        var trimmed = value.Trim();
        var existing = contacts.FirstOrDefault(x => string.Equals(x.ValueCipherOrPlain, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsPreferred = true;
            existing.Visibility = "Private";
            return;
        }

        db.ContactPoints.Add(new ContactPoint
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            Type = type,
            ValueCipherOrPlain = trimmed,
            IsPreferred = true,
            Visibility = "Private"
        });
    }

    private async Task UpsertAddressAsync(Guid personId, UserProfileEditInput input, CancellationToken cancellationToken)
    {
        if (!HasAnyAddressValue(input)) return;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var current = await db.PostalAddresses
            .Where(x => x.PersonId == personId && x.ValidTo == null)
            .OrderByDescending(x => x.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is not null && current.ValidFrom >= today)
        {
            ApplyAddress(current, input);
            return;
        }

        if (current is not null) current.ValidTo = today.AddDays(-1);
        var address = new PostalAddress { Id = Guid.NewGuid(), PersonId = personId, ValidFrom = today };
        ApplyAddress(address, input);
        db.PostalAddresses.Add(address);
    }

    private static void ApplyAddress(PostalAddress address, UserProfileEditInput input)
    {
        address.Country = string.IsNullOrWhiteSpace(input.Country) ? "PL" : input.Country.Trim().ToUpperInvariant();
        address.Region = Clean(input.Region);
        address.City = input.City!.Trim();
        address.PostalCode = Clean(input.PostalCode);
        address.Street = Clean(input.Street);
        address.BuildingNo = Clean(input.BuildingNo);
        address.UnitNo = Clean(input.UnitNo);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasAnyAddressValue(UserProfileEditInput input)
        => !string.IsNullOrWhiteSpace(input.City)
           || !string.IsNullOrWhiteSpace(input.Region)
           || !string.IsNullOrWhiteSpace(input.Street)
           || !string.IsNullOrWhiteSpace(input.PostalCode)
           || !string.IsNullOrWhiteSpace(input.BuildingNo)
           || !string.IsNullOrWhiteSpace(input.UnitNo);

    private static string? NormalizePesel(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsValidPesel(string value)
    {
        if (value.Length != 11 || value.Any(ch => ch < '0' || ch > '9')) return false;
        int[] weights = [1, 3, 7, 9, 1, 3, 7, 9, 1, 3];
        var sum = 0;
        for (var i = 0; i < 10; i++) sum += (value[i] - '0') * weights[i];
        var control = (10 - (sum % 10)) % 10;
        return control == value[10] - '0';
    }

    private static string TranslatePersonType(string value) => value switch
    {
        "Adult" => "Dorosły",
        "Child" => "Dziecko",
        "Tenant" => "Lokator",
        "Guest" => "Gość",
        _ => value
    };

    private static string TranslateOrganizationalRole(string value) => value switch
    {
        "Member" => "Domownik",
        "Tenant" => "Lokator",
        "Child" => "Dziecko",
        "Guest" => "Gość",
        "Owner" => "Właściciel",
        _ => value
    };

    private static string TranslateStatus(string value) => value switch
    {
        "Active" => "Aktywne",
        "Locked" => "Zablokowane",
        "Inactive" => "Nieaktywne",
        "Archived" => "Zarchiwizowane",
        "Ended" => "Zakończone",
        "Deleted" => "Usunięte",
        _ => value
    };
}
