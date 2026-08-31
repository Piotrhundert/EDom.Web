namespace EDom.Web.Authorization;

public sealed record ModuleDefinition(
    string Key,
    string Label,
    string Description,
    string PermissionCode);

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> All =
    [
        new("household", "Gospodarstwo", "Osoby, rodzina i konfiguracja gospodarstwa.", "household.member.manage"),
        new("private-finance", "Moje finanse", "Prywatne konta, dochody, świadczenia, wydatki i subskrypcje.", "privatefinance.account.manage_own"),
        new("finances", "Finanse domowe", "Wspólny budżet, faktury, wpłaty i rozliczenia.", "householdfinance.payment.submit"),
        new("property", "Nieruchomości", "Działki, budynki, pomieszczenia i wyposażenie.", "property.structure.manage"),
        new("rental", "Najem", "Umowy, lokatorzy i rozliczenia najmu.", "rental.payment.submit"),
        new("utilities", "Media", "Liczniki, odczyty, taryfy i faktury operatorów.", "utilities.invoice.manage"),
        new("calendar", "Kalendarz", "Wspólne wydarzenia, terminy i planowanie.", "calendar.event.manage_shared"),
        new("documents", "Dokumenty", "Dokumenty i załączniki z kontrolą dostępu przy pobraniu.", "documents.document.manage_own"),
        new("maintenance", "Utrzymanie domu", "Usterki, serwis i zadania techniczne.", "maintenance.ticket.manage"),
        new("settings", "Użytkownicy i ustawienia", "Konta, role i podstawowa administracja.", "identity.account.create")
    ];

    public static ModuleDefinition? Find(string key)
        => All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
}
