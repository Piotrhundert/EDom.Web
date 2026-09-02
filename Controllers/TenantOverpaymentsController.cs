using System.Collections;
using System.Reflection;
using System.Text.Json;
using EDom.Application.Rental;
using EDom.Application.Property;
using EDom.Application.HouseholdFinance;
using EDom.Domain.Authorization;
using EDom.Domain.Rental;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Overpayments")]
public sealed class TenantOverpaymentsController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    IHouseholdFinanceService householdFinance,
    IRentalService rentalService,
    IPropertyAssetService propertyService,
    IWebHostEnvironment environment) : Controller
{
    [HttpGet("Data")]
    public async Task<IActionResult> Data(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);
        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        var store = new TenantOverpaymentStore(environment.ContentRootPath);
        var records = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var result = new List<object>();

        foreach (var settlement in AsObjects(GetValue(overview, "Settlements")))
        {
            var settlementId = GetGuid(settlement, "Id");
            if (settlementId == Guid.Empty)
            {
                continue;
            }

            var approvedTotal = GetApprovedTotal(settlement);
            var totalDue = GetLong(settlement, "TotalDueMinor");
            var overpayment = Math.Max(0, approvedTotal - totalDue);

            var decisions = records
                .Where(x => x.SourceSettlementId == settlementId)
                .ToArray();

            var decided = decisions.Sum(x => x.AmountMinor);
            var available = Math.Max(0, overpayment - decided);

            result.Add(new
            {
                settlementId,
                tenantName = GetString(settlement, "TenantName"),
                roomName = GetString(settlement, "RoomName"),
                periodKey = GetString(settlement, "PeriodKey"),
                currencyCode = GetString(settlement, "CurrencyCode", "PLN"),
                approvedTotalMinor = approvedTotal,
                totalDueMinor = totalDue,
                overpaymentMinor = overpayment,
                availableMinor = available,
                carryForwardMinor = decisions
                    .Where(x => x.Decision == TenantOverpaymentDecisions.CarryForward)
                    .Sum(x => x.AmountMinor),
                carryForwardAppliedMinor = decisions
                    .Where(x => x.Decision == TenantOverpaymentDecisions.CarryForward)
                    .Sum(x => x.Applications.Sum(a => a.AmountMinor)),
                refundedMinor = decisions
                    .Where(x => x.Decision == TenantOverpaymentDecisions.Refunded)
                    .Sum(x => x.AmountMinor),
                decisions = decisions.Select(x => new
                {
                    x.Id,
                    x.AmountMinor,
                    x.CurrencyCode,
                    x.Decision,
                    x.CreatedAtUtc,
                    x.RefundedOn,
                    x.RefundMethod,
                    x.Note,
                    appliedMinor = x.Applications.Sum(a => a.AmountMinor),
                    applications = x.Applications.Select(a => new
                    {
                        a.TargetSettlementId,
                        a.TargetPeriodKey,
                        a.AmountMinor,
                        a.AppliedAtUtc
                    })
                })
            });
        }

        return Json(new
        {
            canManage = GetBool(overview, "CanManage"),
            settlements = result
        });
    }

    [HttpPost("CarryForward")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CarryForward(
        Guid settlementId,
        string? note,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);
        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(overview, "CanManage"))
        {
            return Forbid();
        }

        var source = FindSettlement(overview, settlementId);
        if (source is null)
        {
            return BadRequest(new { message = "Nie znaleziono rozliczenia lokatora." });
        }

        var store = new TenantOverpaymentStore(environment.ContentRootPath);
        var records = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var available = CalculateAvailableOverpayment(source, records);
        if (available <= 0)
        {
            return BadRequest(new { message = "Brak nierozliczonej nadpłaty do przeniesienia." });
        }

        var contractId = ResolveLeaseContractId(overview, source);
        if (contractId == Guid.Empty)
        {
            return BadRequest(new { message = "Nie udało się powiązać nadpłaty z umową najmu." });
        }

        await store.AddDecisionAsync(
            CreateRecord(
                current,
                source,
                contractId,
                available,
                TenantOverpaymentDecisions.CarryForward,
                null,
                null,
                note),
            cancellationToken);

        await ApplyCarryForwardAsync(
            actor,
            current.HouseholdId,
            contractId,
            cancellationToken);

        return Json(new
        {
            ok = true,
            amountMinor = available,
            message = "Nadpłata została przeznaczona na kolejne rozliczenia lokatora."
        });
    }

    [HttpPost("Refund")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refund(
        Guid settlementId,
        DateOnly refundedOn,
        string refundMethod,
        string? note,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);
        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(overview, "CanManage"))
        {
            return Forbid();
        }

        var source = FindSettlement(overview, settlementId);
        if (source is null)
        {
            return BadRequest(new { message = "Nie znaleziono rozliczenia lokatora." });
        }

        var store = new TenantOverpaymentStore(environment.ContentRootPath);
        var records = await store.GetForHouseholdAsync(
            current.HouseholdId,
            cancellationToken);

        var available = CalculateAvailableOverpayment(source, records);
        if (available <= 0)
        {
            return BadRequest(new { message = "Brak nierozliczonej nadpłaty do zwrotu." });
        }

        var normalizedMethod = string.Equals(
            refundMethod,
            "Cash",
            StringComparison.OrdinalIgnoreCase)
            ? "Cash"
            : "Bank";

        var canManageFinance = await access.CanAsync(
            "householdfinance.invoice.manage",
            ResourceScopeTypes.Household,
            current.HouseholdId.ToString("D"),
            ownerPersonId: current.PersonId,
            resourceType: "HouseholdFinance",
            resourceId: current.HouseholdId.ToString("D"),
            cancellationToken: cancellationToken);

        var canPayFinance = await access.CanAsync(
            "householdfinance.invoice.pay",
            ResourceScopeTypes.Household,
            current.HouseholdId.ToString("D"),
            ownerPersonId: current.PersonId,
            resourceType: "HouseholdFinance",
            resourceId: current.HouseholdId.ToString("D"),
            cancellationToken: cancellationToken);

        if (!canManageFinance || !canPayFinance)
        {
            return Forbid();
        }

        await BookRefundInHouseholdFinanceAsync(
            current,
            source,
            available,
            normalizedMethod,
            refundedOn,
            cancellationToken);

        var contractId = ResolveLeaseContractId(overview, source);

        await store.AddDecisionAsync(
            CreateRecord(
                current,
                source,
                contractId,
                available,
                TenantOverpaymentDecisions.Refunded,
                refundedOn,
                normalizedMethod,
                note),
            cancellationToken);

        return Json(new
        {
            ok = true,
            amountMinor = available,
            message = normalizedMethod == "Cash"
                ? "Zarejestrowano zwrot nadpłaty lokatorowi w gotówce."
                : "Zarejestrowano zwrot nadpłaty lokatorowi przelewem."
        });
    }

    [HttpPost("Sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);
        if (actor is null || current is null)
        {
            return Unauthorized();
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        if (!GetBool(overview, "CanManage"))
        {
            return Json(new { ok = true, applied = 0 });
        }

        var applied = await ApplyAllAvailableCarryForwardAsync(
            actor,
            current.HouseholdId,
            overview,
            cancellationToken);

        return Json(new { ok = true, applied });
    }

    [HttpPost("Build")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Build(
        Guid leaseContractId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        var current = await access.GetCurrentAsync(cancellationToken);

        if (actor is null || current is null)
        {
            return Forbid();
        }

        try
        {
            await settlementService.BuildDraftAsync(
                actor,
                new(leaseContractId, periodKey),
                cancellationToken);

            var pelletEngine = new TenantPelletPoolEngine(
                settlementService,
                rentalService,
                propertyService,
                environment.ContentRootPath);

            var pelletApplied = await pelletEngine.ApplyAsync(
                actor,
                current.HouseholdId,
                leaseContractId,
                periodKey,
                cancellationToken);

            var applied = await ApplyCarryForwardAsync(
                actor,
                current.HouseholdId,
                leaseContractId,
                cancellationToken,
                periodKey);

            var messages = new List<string>
            {
                "Przeliczono projekt miesięcznego rozliczenia lokatora."
            };

            if (pelletApplied > 0)
            {
                messages.Add(
                    $"Pellet: {pelletApplied / 100m:N2} PLN według aktualnej liczby lokatorów i pozostałej puli.");
            }

            if (applied > 0)
            {
                messages.Add(
                    $"Nadpłata z wcześniejszych miesięcy: -{applied / 100m:N2} PLN.");
            }

            TempData["Success"] = string.Join(" ", messages);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction("Index", "TenantSettlements");
    }

    private async Task<int> ApplyAllAvailableCarryForwardAsync(
        RentalActor actor,
        Guid householdId,
        object overview,
        CancellationToken cancellationToken)
    {
        var count = 0;

        foreach (var contract in AsObjects(GetValue(overview, "Contracts")))
        {
            var contractId = GetGuid(contract, "ContractId", "Id");
            if (contractId == Guid.Empty)
            {
                continue;
            }

            var applied = await ApplyCarryForwardAsync(
                actor,
                householdId,
                contractId,
                cancellationToken);

            if (applied > 0)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<long> ApplyCarryForwardAsync(
        RentalActor actor,
        Guid householdId,
        Guid leaseContractId,
        CancellationToken cancellationToken,
        string? exactPeriodKey = null)
    {
        if (leaseContractId == Guid.Empty)
        {
            return 0;
        }

        var overview = await settlementService.GetOverviewAsync(
            actor,
            cancellationToken);

        var targets = AsObjects(GetValue(overview, "Settlements"))
            .Where(x => ResolveLeaseContractId(overview, x) == leaseContractId)
            .Where(x =>
            {
                var status = GetString(x, "Status");
                return status is TenantSettlementStatuses.Draft
                    or TenantSettlementStatuses.AwaitingData
                    or TenantSettlementStatuses.ReadyForApproval;
            })
            .Where(x =>
                string.IsNullOrWhiteSpace(exactPeriodKey)
                || string.Equals(
                    GetString(x, "PeriodKey"),
                    exactPeriodKey,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => GetString(x, "PeriodKey"))
            .ToArray();

        if (targets.Length == 0)
        {
            return 0;
        }

        var store = new TenantOverpaymentStore(environment.ContentRootPath);
        var records = (await store.GetForHouseholdAsync(
                householdId,
                cancellationToken))
            .Where(x =>
                x.LeaseContractId == leaseContractId
                && x.Decision == TenantOverpaymentDecisions.CarryForward)
            .OrderBy(x => x.SourcePeriodKey)
            .ThenBy(x => x.CreatedAtUtc)
            .ToArray();

        long totalAppliedNow = 0;

        foreach (var target in targets)
        {
            var targetId = GetGuid(target, "Id");
            var targetPeriod = GetString(target, "PeriodKey");
            if (targetId == Guid.Empty || string.IsNullOrWhiteSpace(targetPeriod))
            {
                continue;
            }

            var targetDue = Math.Max(0, GetLong(target, "TotalDueMinor"));
            if (targetDue <= 0)
            {
                continue;
            }

            foreach (var credit in records)
            {
                if (string.Compare(
                        targetPeriod,
                        credit.SourcePeriodKey,
                        StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    continue;
                }

                var existingApplication = credit.Applications.FirstOrDefault(x =>
                    x.TargetSettlementId == targetId
                    && string.Equals(
                        x.TargetPeriodKey,
                        targetPeriod,
                        StringComparison.OrdinalIgnoreCase));

                if (existingApplication is not null)
                {
                    if (!SettlementContainsCredit(target, credit.Id))
                    {
                        await AddCreditLineAsync(
                            actor,
                            target,
                            credit,
                            existingApplication.AmountMinor,
                            cancellationToken);
                    }

                    targetDue = Math.Max(
                        0,
                        targetDue - existingApplication.AmountMinor);
                    continue;
                }

                var used = credit.Applications.Sum(x => x.AmountMinor);
                var available = Math.Max(0, credit.AmountMinor - used);

                if (available <= 0 || targetDue <= 0)
                {
                    continue;
                }

                var amount = Math.Min(available, targetDue);

                await AddCreditLineAsync(
                    actor,
                    target,
                    credit,
                    amount,
                    cancellationToken);

                await store.AddApplicationAsync(
                    householdId,
                    credit.Id,
                    targetId,
                    targetPeriod,
                    amount,
                    cancellationToken);

                totalAppliedNow += amount;
                targetDue -= amount;
            }
        }

        return totalAppliedNow;
    }

    private async Task AddCreditLineAsync(
        RentalActor actor,
        object target,
        TenantOverpaymentRecord credit,
        long amountMinor,
        CancellationToken cancellationToken)
    {
        if (amountMinor <= 0)
        {
            return;
        }

        var targetId = GetGuid(target, "Id");
        var currency = GetString(
            target,
            "CurrencyCode",
            credit.CurrencyCode);

        var snapshot = JsonSerializer.Serialize(new
        {
            kind = "TenantOverpaymentCarryForward",
            overpaymentId = credit.Id,
            sourceSettlementId = credit.SourceSettlementId,
            sourcePeriodKey = credit.SourcePeriodKey,
            amountMinor
        });

        await settlementService.AddManualLineAsync(
            actor,
            new(
                targetId,
                TenantSettlementLineTypes.Correction,
                checked(-amountMinor),
                currency,
                "TenantOverpayment",
                null,
                snapshot),
            cancellationToken);
    }

    private static bool SettlementContainsCredit(
        object settlement,
        Guid creditId)
    {
        var token = creditId.ToString("D");

        foreach (var line in AsObjects(GetValue(settlement, "Lines")))
        {
            if (!string.Equals(
                    GetString(line, "SourceType"),
                    "TenantOverpayment",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var propertyName in new[]
                     {
                         "CalculationSnapshotJson",
                         "CalculationSnapshot",
                         "SnapshotJson",
                         "Snapshot"
                     })
            {
                var raw = GetValue(line, propertyName)?.ToString();
                if (!string.IsNullOrWhiteSpace(raw)
                    && raw.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task BookRefundInHouseholdFinanceAsync(
        WebUserContext current,
        object source,
        long amountMinor,
        string refundMethod,
        DateOnly refundedOn,
        CancellationToken cancellationToken)
    {
        if (amountMinor <= 0)
        {
            throw new InvalidOperationException(
                "Kwota zwrotu musi być większa od 0.");
        }

        var settlementId = GetGuid(source, "Id");
        var periodKey = GetString(source, "PeriodKey");
        var tenantName = GetString(source, "TenantName");
        var currency = GetString(source, "CurrencyCode", "PLN");

        var shortId = settlementId == Guid.Empty
            ? Guid.NewGuid().ToString("N")[..8]
            : settlementId.ToString("N")[..8];

        var invoiceNo = $"ZWROT-NADPLATY-{periodKey}-{shortId}";

        var financeOverview = await householdFinance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var ledgerCurrency = financeOverview.Ledger.CurrencyCode;
        if (!string.Equals(
                ledgerCurrency,
                currency,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Nie można wykonać zwrotu {currency} z rachunku gospodarstwa prowadzonego w {ledgerCurrency}.");
        }

        var availableMinor = refundMethod == "Cash"
            ? financeOverview.Ledger.CashBalanceMinor
            : financeOverview.Ledger.BankBalanceMinor;

        if (availableMinor < amountMinor)
        {
            var missingMinor = amountMinor - Math.Max(0, availableMinor);
            var availableMajor = Math.Max(0, availableMinor) / 100m;
            var requiredMajor = amountMinor / 100m;
            var missingMajor = missingMinor / 100m;
            var sourceLabel = refundMethod == "Cash"
                ? "kasie domowej"
                : "rachunku bankowym domu";

            throw new InvalidOperationException(
                $"Brak wystarczających środków w {sourceLabel}. " +
                $"Dostępne: {availableMajor:N2} {currency}, " +
                $"wymagane: {requiredMajor:N2} {currency}, " +
                $"brakuje: {missingMajor:N2} {currency}. Zwrot nie został zaksięgowany.");
        }

        var invoice = financeOverview.Invoices.FirstOrDefault(x =>
            string.Equals(
                x.InvoiceNo,
                invoiceNo,
                StringComparison.OrdinalIgnoreCase));

        if (invoice is null)
        {
            await householdFinance.CreateInvoiceAsync(
                new CreateHouseholdInvoiceRequest(
                    current.HouseholdId,
                    invoiceNo,
                    $"Zwrot nadpłaty — {tenantName}",
                    "TenantOverpaymentRefund",
                    refundedOn,
                    refundedOn,
                    refundedOn,
                    refundedOn,
                    null,
                    null,
                    amountMinor,
                    currency,
                    "TenantSettlement",
                    settlementId.ToString("D")),
                cancellationToken);

            financeOverview = await householdFinance.GetOverviewAsync(
                current.HouseholdId,
                current.PersonId,
                true,
                cancellationToken);

            invoice = financeOverview.Invoices.FirstOrDefault(x =>
                string.Equals(
                    x.InvoiceNo,
                    invoiceNo,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (invoice is null)
        {
            throw new InvalidOperationException(
                "Utworzono dyspozycję zwrotu, ale nie udało się odnaleźć pozycji w Finansach domowych.");
        }

        if (invoice.RemainingMinor <= 0)
        {
            return;
        }

        var amountToPay = Math.Min(
            amountMinor,
            invoice.RemainingMinor);

        var refreshedFinance = await householdFinance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            true,
            cancellationToken);

        var refreshedAvailableMinor = refundMethod == "Cash"
            ? refreshedFinance.Ledger.CashBalanceMinor
            : refreshedFinance.Ledger.BankBalanceMinor;

        if (refreshedAvailableMinor < amountToPay)
        {
            var missingMinor = amountToPay - Math.Max(0, refreshedAvailableMinor);
            var sourceLabel = refundMethod == "Cash"
                ? "kasie domowej"
                : "rachunku bankowym domu";

            throw new InvalidOperationException(
                $"Saldo w {sourceLabel} zmieniło się przed zaksięgowaniem. " +
                $"Dostępne: {Math.Max(0, refreshedAvailableMinor) / 100m:N2} {currency}, " +
                $"wymagane: {amountToPay / 100m:N2} {currency}, " +
                $"brakuje: {missingMinor / 100m:N2} {currency}. Zwrot nie został wykonany.");
        }

        var localNoon = DateTime.SpecifyKind(
            refundedOn.ToDateTime(new TimeOnly(12, 0)),
            DateTimeKind.Local);

        await householdFinance.PayInvoiceAsync(
            new PayHouseholdInvoiceRequest(
                invoice.Id,
                amountToPay,
                refundMethod == "Cash"
                    ? "HouseholdCash"
                    : "HouseholdBank",
                null,
                localNoon.ToUniversalTime(),
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext)),
            cancellationToken);
    }

    private static TenantOverpaymentRecord CreateRecord(
        WebUserContext current,
        object source,
        Guid contractId,
        long amountMinor,
        string decision,
        DateOnly? refundedOn,
        string? refundMethod,
        string? note) =>
        new()
        {
            Id = Guid.NewGuid(),
            HouseholdId = current.HouseholdId,
            LeaseContractId = contractId,
            SourceSettlementId = GetGuid(source, "Id"),
            SourcePeriodKey = GetString(source, "PeriodKey"),
            PayerPersonId = GetGuid(source, "PayerPersonId"),
            TenantName = GetString(source, "TenantName"),
            RoomName = GetString(source, "RoomName"),
            AmountMinor = amountMinor,
            CurrencyCode = GetString(source, "CurrencyCode", "PLN"),
            Decision = decision,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserAccountId = current.UserAccountId,
            RefundedOn = refundedOn,
            RefundMethod = refundMethod,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };

    private static long CalculateAvailableOverpayment(
        object settlement,
        IReadOnlyList<TenantOverpaymentRecord> records)
    {
        var approved = GetApprovedTotal(settlement);
        var due = GetLong(settlement, "TotalDueMinor");
        var overpayment = Math.Max(0, approved - due);
        var decided = records
            .Where(x => x.SourceSettlementId == GetGuid(settlement, "Id"))
            .Sum(x => x.AmountMinor);

        return Math.Max(0, overpayment - decided);
    }

    private static long GetApprovedTotal(object settlement)
    {
        long total = 0;

        foreach (var submission in AsObjects(GetValue(settlement, "Submissions")))
        {
            if (!string.Equals(
                    GetString(submission, "Status"),
                    "Approved",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var approved = GetNullableLong(
                submission,
                "ApprovedAmountMinor",
                "DecisionAmountMinor",
                "AcceptedAmountMinor");

            total = checked(total + (approved ?? GetLong(submission, "AmountMinor")));
        }

        return total;
    }

    private static object? FindSettlement(
        object overview,
        Guid settlementId) =>
        AsObjects(GetValue(overview, "Settlements"))
            .FirstOrDefault(x => GetGuid(x, "Id") == settlementId);

    private static Guid ResolveLeaseContractId(
        object overview,
        object settlement)
    {
        var direct = GetGuid(
            settlement,
            "LeaseContractId",
            "ContractId");

        if (direct != Guid.Empty)
        {
            return direct;
        }

        var tenantName = GetString(settlement, "TenantName");
        var roomName = GetString(settlement, "RoomName");

        foreach (var contract in AsObjects(GetValue(overview, "Contracts")))
        {
            if (string.Equals(
                    GetString(contract, "TenantName"),
                    tenantName,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    GetString(contract, "RoomName"),
                    roomName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetGuid(contract, "ContractId", "Id");
            }
        }

        return Guid.Empty;
    }

    private async Task<RentalActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);

        return current is null
            ? null
            : new RentalActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    private static IEnumerable<object> AsObjects(object? value)
    {
        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static object? GetValue(
        object? source,
        params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        var type = source.GetType();

        foreach (var name in names)
        {
            var property = type.GetProperty(
                name,
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.IgnoreCase);

            if (property is not null)
            {
                return property.GetValue(source);
            }
        }

        return null;
    }

    private static string GetString(
        object source,
        string name,
        string fallback = "") =>
        GetValue(source, name)?.ToString() ?? fallback;

    private static bool GetBool(
        object source,
        string name)
    {
        var value = GetValue(source, name);
        return value is bool b && b;
    }

    private static Guid GetGuid(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is Guid guid)
        {
            return guid;
        }

        return Guid.TryParse(
            value?.ToString(),
            out var parsed)
            ? parsed
            : Guid.Empty;
    }

    private static long GetLong(
        object source,
        params string[] names) =>
        GetNullableLong(source, names) ?? 0L;

    private static long? GetNullableLong(
        object source,
        params string[] names)
    {
        var value = GetValue(source, names);

        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }
}
