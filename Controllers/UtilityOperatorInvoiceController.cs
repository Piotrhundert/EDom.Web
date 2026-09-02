using System.Text.Json;
using EDom.Application.Utilities;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Utilities/OperatorInvoice")]
public sealed class UtilityOperatorInvoiceController(
    WebAccessService access,
    IUtilitiesService utilitiesService,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Forbid();
        }

        if (!await CanManageAsync(current.HouseholdId, cancellationToken))
        {
            return Forbid();
        }

        var actor = CreateActor(current);
        var overview = await utilitiesService.GetOverviewAsync(actor, cancellationToken);
        var store = new UtilityOperatorInvoiceStore(environment.ContentRootPath);

        var meters = overview.Meters
            .Where(x =>
                string.Equals(x.Medium, "Electricity", StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.MeterType, "Main", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name)
            .Select(x => new UtilityOperatorInvoiceSelectOption(
                x.Id,
                $"{x.Name} · główny · {x.UnitCode}"))
            .ToArray();

        var contracts = overview.Contracts
            .Where(x => string.Equals(x.Medium, "Electricity", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.OperatorName)
            .Select(x => new UtilityOperatorInvoiceSelectOption(
                x.Id,
                $"{x.OperatorName} · {x.ContractNumber ?? "bez numeru"}"))
            .ToArray();

        var history = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        return View(new UtilityOperatorInvoicePageViewModel(
            current.HouseholdName,
            meters,
            contracts,
            history));
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ElectricityOperatorInvoiceInput input,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return Forbid();
        }

        if (!await CanManageAsync(current.HouseholdId, cancellationToken))
        {
            return Forbid();
        }

        try
        {
            var actor = CreateActor(current);
            var overview = await utilitiesService.GetOverviewAsync(actor, cancellationToken);

            var meter = overview.Meters.FirstOrDefault(x => x.Id == input.MeterId);
            if (meter is null)
            {
                throw new InvalidOperationException("Nie znaleziono wybranego licznika.");
            }

            if (!string.Equals(meter.Medium, "Electricity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Rozliczenie energii można przypisać wyłącznie do licznika prądu.");
            }

            if (!string.Equals(meter.MeterType, "Main", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Rozliczenie operatora musi być przypisane do licznika głównego.");
            }

            if (!string.Equals(meter.UnitCode, "kWh", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Główny licznik energii powinien mieć jednostkę kWh.");
            }

            var contract = overview.Contracts.FirstOrDefault(x => x.Id == input.ContractId);
            if (contract is null)
            {
                throw new InvalidOperationException("Nie znaleziono wybranej umowy operatora.");
            }

            if (!string.Equals(contract.Medium, "Electricity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Wybrana umowa nie dotyczy energii elektrycznej.");
            }

            Validate(input);

            var tariffCode = NormalizeTariff(input.TariffCode);
            var consumptionDay = RoundQuantity(input.CurrentDay - input.PreviousDay);
            var consumptionNight = tariffCode == "G12"
                ? RoundQuantity(input.CurrentNight - input.PreviousNight)
                : 0m;

            var lines = BuildLines(input, tariffCode, consumptionDay, consumptionNight);
            var netAmount = RoundMoney(lines.Sum(x => x.AmountNet));
            var vatAmount = RoundMoney(netAmount * input.VatRate / 100m);
            var calculatedGross = RoundMoney(netAmount + vatAmount);
            var grossAmount = input.GrossAmountOverride is > 0m
                ? RoundMoney(input.GrossAmountOverride.Value)
                : calculatedGross;

            if (grossAmount <= 0m)
            {
                throw new InvalidOperationException("Kwota brutto faktury musi być większa od 0.");
            }

            var readingSubmitted = false;
            if (input.SubmitCurrentReading)
            {
                var readingAtLocal = new DateTime(
                    input.PeriodTo.Year,
                    input.PeriodTo.Month,
                    input.PeriodTo.Day,
                    12,
                    0,
                    0,
                    DateTimeKind.Local);

                if (tariffCode == "G12")
                {
                    await utilitiesService.SubmitReadingAsync(
                        actor,
                        new(
                            input.MeterId,
                            readingAtLocal.ToUniversalTime(),
                            "OperatorInvoice",
                            [
                                new("DAY", input.CurrentDay),
                                new("NIGHT", input.CurrentNight)
                            ],
                            null),
                        cancellationToken);
                }
                else
                {
                    await utilitiesService.SubmitReadingAsync(
                        actor,
                        new(
                            input.MeterId,
                            readingAtLocal.ToUniversalTime(),
                            "OperatorInvoice",
                            [new("ALL", input.CurrentDay)],
                            null),
                        cancellationToken);
                }

                readingSubmitted = true;
            }

            var tariffSaved = false;
            if (input.SaveTariffRates)
            {
                var metadata = JsonSerializer.Serialize(new
                {
                    source = "OperatorInvoice",
                    input.InvoiceNo,
                    tariffCode,
                    input.VatRate,
                    input.Ppe,
                    input.MeterSerialNo
                });

                if (tariffCode == "G12")
                {
                    await utilitiesService.CreateTariffAsync(
                        actor,
                        new(
                            input.ContractId,
                            $"{tariffCode} · faktura {input.InvoiceNo}",
                            input.PeriodFrom,
                            input.PeriodTo,
                            input.CurrencyCode,
                            "Net",
                            metadata,
                            [
                                new("DAY", "EnergyActive", input.EnergyDayRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("NIGHT", "EnergyActive", input.EnergyNightRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("DAY", "Quality", input.QualityRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("NIGHT", "Quality", input.QualityRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("DAY", "NetworkVariable", input.VariableNetworkDayRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("NIGHT", "NetworkVariable", input.VariableNetworkNightRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("DAY", "OZE", input.OzeRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("NIGHT", "OZE", input.OzeRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("DAY", "Cogeneration", input.CogenerationRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("NIGHT", "Cogeneration", input.CogenerationRate, 6, "kWh", input.PeriodFrom, input.PeriodTo)
                            ]),
                        cancellationToken);
                }
                else
                {
                    await utilitiesService.CreateTariffAsync(
                        actor,
                        new(
                            input.ContractId,
                            $"{tariffCode} · faktura {input.InvoiceNo}",
                            input.PeriodFrom,
                            input.PeriodTo,
                            input.CurrencyCode,
                            "Net",
                            metadata,
                            [
                                new("ALL", "EnergyActive", input.EnergyDayRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("ALL", "Quality", input.QualityRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("ALL", "NetworkVariable", input.VariableNetworkDayRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("ALL", "OZE", input.OzeRate, 6, "kWh", input.PeriodFrom, input.PeriodTo),
                                new("ALL", "Cogeneration", input.CogenerationRate, 6, "kWh", input.PeriodFrom, input.PeriodTo)
                            ]),
                        cancellationToken);
                }

                tariffSaved = true;
            }

            var totalMinor = ToMinor(grossAmount);
            await utilitiesService.RegisterInvoiceAsync(
                actor,
                new(
                    input.ContractId,
                    input.InvoiceNo.Trim(),
                    input.PeriodFrom,
                    input.PeriodTo,
                    input.IssuedOn,
                    input.DueDate,
                    totalMinor,
                    NormalizeCurrency(input.CurrencyCode),
                    [new("ElectricityOperatorInvoice", totalMinor)]),
                cancellationToken);

            var store = new UtilityOperatorInvoiceStore(environment.ContentRootPath);
            await store.AddAsync(
                new UtilityOperatorInvoiceHistoryItem(
                    Guid.NewGuid(),
                    current.HouseholdId,
                    meter.Id,
                    meter.Name,
                    contract.Id,
                    $"{contract.OperatorName} · {contract.ContractNumber ?? "bez numeru"}",
                    input.InvoiceNo.Trim(),
                    tariffCode,
                    input.PeriodFrom,
                    input.PeriodTo,
                    Clean(input.MeterSerialNo),
                    Clean(input.Ppe),
                    input.PreviousDay,
                    input.CurrentDay,
                    tariffCode == "G12" ? input.PreviousNight : 0m,
                    tariffCode == "G12" ? input.CurrentNight : 0m,
                    consumptionDay,
                    consumptionNight,
                    netAmount,
                    input.VatRate,
                    vatAmount,
                    grossAmount,
                    NormalizeCurrency(input.CurrencyCode),
                    input.ExciseAmount is > 0m ? RoundMoney(input.ExciseAmount.Value) : null,
                    readingSubmitted,
                    tariffSaved,
                    DateTime.UtcNow,
                    lines),
                cancellationToken);

            TempData["Success"] =
                $"Zarejestrowano rozliczenie {tariffCode}: zużycie {consumptionDay + consumptionNight:N0} kWh, faktura {grossAmount:N2} {NormalizeCurrency(input.CurrencyCode)}.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CanManageAsync(
        Guid householdId,
        CancellationToken cancellationToken) =>
        await access.CanAsync(
            "utilities.invoice.manage",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            cancellationToken: cancellationToken);

    private UtilityActor CreateActor(WebUserContext current) =>
        new(
            current.UserAccountId,
            current.PersonId,
            current.HouseholdId,
            CorrelationIdMiddleware.Get(HttpContext),
            DateTime.UtcNow);

    private static void Validate(ElectricityOperatorInvoiceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.InvoiceNo))
        {
            throw new InvalidOperationException("Numer faktury jest wymagany.");
        }

        if (input.PeriodTo < input.PeriodFrom)
        {
            throw new InvalidOperationException("Koniec okresu nie może być wcześniejszy niż początek.");
        }

        if (input.DueDate < input.IssuedOn)
        {
            throw new InvalidOperationException("Termin płatności nie może być wcześniejszy niż data wystawienia.");
        }

        if (input.CurrentDay < input.PreviousDay)
        {
            throw new InvalidOperationException("Bieżący stan strefy dziennej/ALL nie może być niższy od poprzedniego.");
        }

        if (NormalizeTariff(input.TariffCode) == "G12"
            && input.CurrentNight < input.PreviousNight)
        {
            throw new InvalidOperationException("Bieżący stan strefy nocnej nie może być niższy od poprzedniego.");
        }

        if (input.VatRate < 0m || input.VatRate > 100m)
        {
            throw new InvalidOperationException("Nieprawidłowa stawka VAT.");
        }

        if (input.BillingMonths < 0m)
        {
            throw new InvalidOperationException("Liczba miesięcy rozliczeniowych nie może być ujemna.");
        }
    }

    private static IReadOnlyList<UtilityOperatorInvoiceLineViewModel> BuildLines(
        ElectricityOperatorInvoiceInput input,
        string tariffCode,
        decimal day,
        decimal night)
    {
        var lines = new List<UtilityOperatorInvoiceLineViewModel>();

        void Add(string code, string label, string zone, decimal quantity, string unit, decimal rate)
        {
            if (rate <= 0m || quantity <= 0m)
            {
                return;
            }

            lines.Add(new(
                code,
                label,
                zone,
                quantity,
                unit,
                rate,
                RoundMoney(quantity * rate)));
        }

        if (tariffCode == "G12")
        {
            Add("EnergyActiveDay", "Energia elektryczna czynna", "DAY", day, "kWh", input.EnergyDayRate);
            Add("EnergyActiveNight", "Energia elektryczna czynna", "NIGHT", night, "kWh", input.EnergyNightRate);

            Add("QualityDay", "Opłata jakościowa", "DAY", day, "kWh", input.QualityRate);
            Add("QualityNight", "Opłata jakościowa", "NIGHT", night, "kWh", input.QualityRate);

            Add("NetworkVariableDay", "Opłata zmienna sieciowa", "DAY", day, "kWh", input.VariableNetworkDayRate);
            Add("NetworkVariableNight", "Opłata zmienna sieciowa", "NIGHT", night, "kWh", input.VariableNetworkNightRate);

            Add("OzeDay", "Opłata OZE", "DAY", day, "kWh", input.OzeRate);
            Add("OzeNight", "Opłata OZE", "NIGHT", night, "kWh", input.OzeRate);

            Add("CogenerationDay", "Opłata kogeneracyjna", "DAY", day, "kWh", input.CogenerationRate);
            Add("CogenerationNight", "Opłata kogeneracyjna", "NIGHT", night, "kWh", input.CogenerationRate);
        }
        else
        {
            Add("EnergyActive", "Energia elektryczna czynna", "ALL", day, "kWh", input.EnergyDayRate);
            Add("Quality", "Opłata jakościowa", "ALL", day, "kWh", input.QualityRate);
            Add("NetworkVariable", "Opłata zmienna sieciowa", "ALL", day, "kWh", input.VariableNetworkDayRate);
            Add("Oze", "Opłata OZE", "ALL", day, "kWh", input.OzeRate);
            Add("Cogeneration", "Opłata kogeneracyjna", "ALL", day, "kWh", input.CogenerationRate);
        }

        Add("NetworkFixed", "Opłata stała sieciowa", "ALL", input.BillingMonths, "mies.", input.FixedNetworkMonthlyRate);
        Add("Subscription", "Opłata abonamentowa", "ALL", input.BillingMonths, "mies.", input.SubscriptionMonthlyRate);
        Add("Capacity", "Opłata mocowa", "ALL", input.BillingMonths, "mies.", input.CapacityMonthlyRate);

        return lines;
    }

    private static string NormalizeTariff(string? value) =>
        string.Equals(value?.Trim(), "G11", StringComparison.OrdinalIgnoreCase)
            ? "G11"
            : "G12";

    private static string NormalizeCurrency(string? value)
    {
        var code = (value ?? "PLN").Trim().ToUpperInvariant();
        return code.Length == 3 ? code : "PLN";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal RoundQuantity(decimal value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static long ToMinor(decimal amount) =>
        checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
}

public sealed class ElectricityOperatorInvoiceInput
{
    public Guid MeterId { get; set; }
    public Guid ContractId { get; set; }

    public string InvoiceNo { get; set; } = "";
    public string TariffCode { get; set; } = "G12";
    public string CurrencyCode { get; set; } = "PLN";

    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public DateOnly IssuedOn { get; set; }
    public DateOnly DueDate { get; set; }

    public string? MeterSerialNo { get; set; }
    public string? Ppe { get; set; }

    public decimal PreviousDay { get; set; }
    public decimal CurrentDay { get; set; }
    public decimal PreviousNight { get; set; }
    public decimal CurrentNight { get; set; }

    public decimal EnergyDayRate { get; set; }
    public decimal EnergyNightRate { get; set; }
    public decimal QualityRate { get; set; }
    public decimal VariableNetworkDayRate { get; set; }
    public decimal VariableNetworkNightRate { get; set; }
    public decimal OzeRate { get; set; }
    public decimal CogenerationRate { get; set; }

    public decimal FixedNetworkMonthlyRate { get; set; }
    public decimal SubscriptionMonthlyRate { get; set; }
    public decimal CapacityMonthlyRate { get; set; }
    public decimal BillingMonths { get; set; } = 1m;

    public decimal VatRate { get; set; } = 23m;
    public decimal? ExciseAmount { get; set; }
    public decimal? GrossAmountOverride { get; set; }

    public bool SubmitCurrentReading { get; set; } = true;
    public bool SaveTariffRates { get; set; } = true;
}
