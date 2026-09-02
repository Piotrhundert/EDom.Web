using EDom.Application.HouseholdFinance;
using EDom.Application.Property;
using EDom.Application.Rental;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("HouseholdFinance/PelletInvoice")]
public sealed class HouseholdPelletInvoiceController(
    WebAccessService access,
    IHouseholdFinanceService finance,
    IPropertyAssetService propertyService,
    ITenantSettlementService settlementService,
    IRentalService rentalService,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
            return Unauthorized();

        if (!await CanManageInvoicesAsync(
                current.HouseholdId,
                cancellationToken))
            return Forbid();

        var propertyActor = new PropertyActor(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

        var overview = await propertyService.GetOverviewAsync(
            propertyActor,
            cancellationToken);

        var buildings = overview.Buildings
            .Where(b => overview.Rooms.Any(r =>
                r.BuildingId == b.Id
                && r.IsRentable))
            .OrderBy(b => b.Name)
            .Select(b => new
            {
                id = b.Id,
                name = b.Name,
                rentableRooms = overview.Rooms.Count(r =>
                    r.BuildingId == b.Id
                    && r.IsRentable)
            })
            .ToArray();

        return Json(new
        {
            buildings
        });
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        HouseholdPelletInvoiceInput model,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
            return Unauthorized();

        if (!await CanManageInvoicesAsync(
                current.HouseholdId,
                cancellationToken))
            return Forbid();

        try
        {
            Validate(model);

            var currency = NormalizeCurrency(model.CurrencyCode);
            var amountMinor = ToMinor(model.Gross);

            var propertyActor = new PropertyActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);

            var propertyOverview = await propertyService.GetOverviewAsync(
                propertyActor,
                cancellationToken);

            var building = propertyOverview.Buildings.FirstOrDefault(x =>
                x.Id == model.BuildingId);

            if (building is null)
                throw new InvalidOperationException(
                    "Nie znaleziono wybranego domu dla puli pelletu.");

            if (!propertyOverview.Rooms.Any(x =>
                    x.BuildingId == building.Id
                    && x.IsRentable))
                throw new InvalidOperationException(
                    "Wybrany budynek nie ma pokoi/lokali przeznaczonych do wynajmu.");

            var financeOverview = await finance.GetOverviewAsync(
                current.HouseholdId,
                current.PersonId,
                true,
                cancellationToken);

            var existingInvoice = financeOverview.Invoices.FirstOrDefault(x =>
                string.Equals(
                    x.InvoiceNo,
                    model.InvoiceNo.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    x.Supplier,
                    model.Supplier.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            var invoiceCreated = false;

            if (existingInvoice is null)
            {
                await finance.CreateInvoiceAsync(
                    new CreateHouseholdInvoiceRequest(
                        current.HouseholdId,
                        model.InvoiceNo.Trim(),
                        model.Supplier.Trim(),
                        "Pellet",
                        model.PeriodFrom,
                        model.PeriodTo,
                        model.IssuedOn,
                        model.DueDate,
                        model.Net.HasValue
                            ? ToMinor(model.Net.Value)
                            : null,
                        model.Vat.HasValue
                            ? ToMinor(model.Vat.Value)
                            : null,
                        amountMinor,
                        currency,
                        "Household",
                        current.HouseholdId.ToString("D")),
                    cancellationToken);

                invoiceCreated = true;
            }
            else
            {
                if (existingInvoice.GrossMinor != amountMinor
                    || !string.Equals(
                        existingInvoice.CurrencyCode,
                        currency,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Faktura o tym numerze i dostawcy już istnieje, ale ma inną kwotę lub walutę.");
                }

                if (!IsPelletCategory(existingInvoice.CategoryCode))
                {
                    throw new InvalidOperationException(
                        "Faktura o tym numerze już istnieje, ale nie ma kategorii Pellet.");
                }
            }

            var store = new TenantPelletPoolStore(
                environment.ContentRootPath);

            var seasonName = string.IsNullOrWhiteSpace(model.PelletSeasonName)
                ? $"{model.PeriodFrom:yyyy-MM} – {model.PeriodTo:yyyy-MM}"
                : model.PelletSeasonName.Trim();

            var poolResult = await store.UpsertInvoicePurchaseAsync(
                current.HouseholdId,
                building.Id,
                building.Name,
                seasonName,
                model.PeriodFrom,
                model.PeriodTo,
                model.IssuedOn,
                amountMinor,
                currency,
                model.PelletPalletCount,
                model.PelletWeightKg,
                model.Supplier.Trim(),
                model.InvoiceNo.Trim(),
                Clean(model.PelletNotes),
                current.UserAccountId,
                cancellationToken);

            var rentalActor = new RentalActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);

            var engine = new TenantPelletPoolEngine(
                settlementService,
                rentalService,
                propertyService,
                environment.ContentRootPath);

            var reconcile = await engine.ReconcileInvoicePurchaseAsync(
                rentalActor,
                current.HouseholdId,
                poolResult,
                cancellationToken);

            var parts = new List<string>();

            parts.Add(invoiceCreated
                ? "Dodano fakturę pelletu do Finansów domowych."
                : "Faktura była już zapisana — nie utworzono duplikatu.");

            parts.Add(poolResult.PoolCreated
                ? $"Automatycznie utworzono pulę pelletu dla {building.Name}."
                : poolResult.AddedToPoolMinor > 0
                    ? $"Zasilono istniejącą pulę pelletu dla {building.Name} kwotą {poolResult.AddedToPoolMinor / 100m:N2} {currency}."
                    : $"Fakturę powiązano z istniejącą pulą pelletu dla {building.Name} bez podwójnego naliczenia.");

            if (reconcile.CorrectionCount > 0)
            {
                parts.Add(
                    $"Wygenerowano {reconcile.CorrectionCount} korekt do już zatwierdzonych/opublikowanych rozliczeń lokatorów na łączną kwotę {reconcile.CorrectionAmountMinor / 100m:N2} {currency}.");
            }

            if (reconcile.OpenSettlementLineCount > 0)
            {
                parts.Add(
                    $"Do {reconcile.OpenSettlementLineCount} otwartych rozliczeń dopisano pellet bez tworzenia korekty.");
            }

            return Json(new
            {
                ok = true,
                invoiceCreated,
                poolCreated = poolResult.PoolCreated,
                purchaseWasDuplicate = poolResult.PurchaseWasDuplicate,
                poolId = poolResult.Pool.Id,
                correctionCount = reconcile.CorrectionCount,
                correctionAmountMinor = reconcile.CorrectionAmountMinor,
                openSettlementLineCount = reconcile.OpenSettlementLineCount,
                message = string.Join(" ", parts)
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    private async Task<bool> CanManageInvoicesAsync(
        Guid householdId,
        CancellationToken cancellationToken) =>
        await access.CanAsync(
            "householdfinance.invoice.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            resourceType: "HouseholdFinance",
            resourceId: householdId.ToString("D"),
            cancellationToken: cancellationToken);

    private static void Validate(
        HouseholdPelletInvoiceInput model)
    {
        if (string.IsNullOrWhiteSpace(model.InvoiceNo))
            throw new InvalidOperationException(
                "Numer faktury jest wymagany.");

        if (string.IsNullOrWhiteSpace(model.Supplier))
            throw new InvalidOperationException(
                "Dostawca jest wymagany.");

        if (!IsPelletCategory(model.CategoryCode))
            throw new InvalidOperationException(
                "Automatyczna pula jest tworzona tylko dla kategorii Pellet.");

        if (model.BuildingId == Guid.Empty)
            throw new InvalidOperationException(
                "Wybierz dom, którego dotyczy zakup pelletu.");

        if (model.PeriodTo < model.PeriodFrom)
            throw new InvalidOperationException(
                "Koniec okresu rozliczania pelletu nie może być wcześniejszy niż początek.");

        if (model.DueDate < model.IssuedOn)
            throw new InvalidOperationException(
                "Termin płatności nie może być wcześniejszy niż data wystawienia.");

        if (model.Gross <= 0m)
            throw new InvalidOperationException(
                "Kwota brutto musi być większa od 0.");

        if (model.PelletPalletCount is < 0m
            || model.PelletWeightKg is < 0m)
            throw new InvalidOperationException(
                "Liczba palet i masa pelletu nie mogą być ujemne.");
    }

    private static bool IsPelletCategory(
        string? category)
    {
        var value = (category ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return value is "pellet" or "pelet"
            || value.Contains("pellet")
            || value.Contains("pelet");
    }

    private static string NormalizeCurrency(
        string? value)
    {
        var currency = (value ?? "PLN")
            .Trim()
            .ToUpperInvariant();

        return currency.Length == 3
            ? currency
            : "PLN";
    }

    private static string? Clean(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static long ToMinor(
        decimal amount) =>
        checked((long)Math.Round(
            amount * 100m,
            0,
            MidpointRounding.AwayFromZero));
}

public sealed class HouseholdPelletInvoiceInput
{
    public string InvoiceNo { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string CategoryCode { get; set; } = "Pellet";
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public DateOnly IssuedOn { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal? Net { get; set; }
    public decimal? Vat { get; set; }
    public decimal Gross { get; set; }
    public string CurrencyCode { get; set; } = "PLN";

    public Guid BuildingId { get; set; }
    public string? PelletSeasonName { get; set; }
    public decimal? PelletPalletCount { get; set; }
    public decimal? PelletWeightKg { get; set; }
    public string? PelletNotes { get; set; }
}
