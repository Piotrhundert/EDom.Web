using EDom.Application.Households;
using EDom.Application.Administration;
using EDom.Application.HouseholdFinance;
using EDom.Application.PrivateFinance;
using EDom.Domain.Authorization;
using EDom.SharedKernel.Values;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;
[Authorize]
[Route("PrivateFinance")]
public sealed class PrivateFinanceController(
    WebAccessService access,
    IPrivateFinanceService finance,
    IHouseholdFinanceService householdFinance,
    IHouseholdFamilyService family,
    IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.account.manage_own", current, cancellationToken)) return Forbid();

        // Zatwierdzone wpłaty do domu są rzeczywistym wydatkiem prywatnym domownika.
        // Rekonsyliacja jest idempotentna i uzupełnia również starsze zatwierdzone wpłaty,
        // zanim pobierzemy saldo i listę prywatnych wydatków.
        await householdFinance.ReconcilePrivateContributionDebitsAsync(
            current.HouseholdId,
            current.PersonId,
            cancellationToken);

        var household = await family.GetOverviewAsync(current.HouseholdId, cancellationToken);
        var children = household.Persons.Where(x => x.IsChild).Select(x => (x.PersonId, x.DisplayName)).ToArray();
        var contributionOverview = await householdFinance.GetOverviewAsync(
            current.HouseholdId,
            current.PersonId,
            false,
            cancellationToken);

        var householdContributions = contributionOverview.PaymentSubmissions
            .Where(x => x.PersonId == current.PersonId)
            .OrderByDescending(x => x.PaidAtUtc)
            .Select(x => new ContributionSubmissionVm(
                x.Id,
                x.PersonId,
                x.PersonName,
                x.PeriodKey,
                x.AmountMinor,
                x.CurrencyCode,
                x.PaymentMethod,
                x.PaidAtUtc,
                x.Status,
                x.ApprovedAmountMinor,
                x.DecisionReason))
            .ToArray();

        return View(new PrivateFinancePageViewModel
        {
            Overview = await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, cancellationToken),
            Children = children,
            HouseholdContributions = householdContributions
        });
    }
    [HttpPost("Account"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Account(AddPrivateAccountViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.account.manage_own", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.CreateAccountAsync(new CreateFinancialAccountRequest(
            current.HouseholdId, current.PersonId, model.Name, model.BankName, model.AccountType,
            model.CurrencyCode, ToMinor(model.OpeningBalance, model.CurrencyCode)), cancellationToken);
        TempData["Success"] = "Dodano prywatne konto finansowe. Jest widoczne wyłącznie dla właściciela, chyba że później zostanie jawnie udostępnione.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("Income"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Income(AddIncomeSourceViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.income.manage_own", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var sourceId = await finance.CreateIncomeSourceAsync(new CreateIncomeSourceRequest(
            current.HouseholdId, current.PersonId, model.FinancialAccountId, model.Name, model.IncomeType,
            model.PlannedAmount.HasValue ? ToMinor(model.PlannedAmount.Value, model.CurrencyCode) : null,
            model.CurrencyCode, model.Frequency, model.PlannedDayOfMonth, model.ValidFrom, model.ValidTo, model.IsVariable), cancellationToken);
        await finance.GeneratePlannedIncomeAsync(sourceId, model.ValidFrom, cancellationToken);
        TempData["Success"] = "Dodano źródło dochodu i pierwszy planowany wpływ.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("ConfirmIncome"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmIncome(ConfirmIncomeViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.income.manage_own", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var overview = await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, cancellationToken);
        var planned = overview.PlannedIncomes.FirstOrDefault(x => x.Id == model.PlannedIncomeId);
        if (planned is null) return NotFound();
        await finance.ConfirmIncomeAsync(new ConfirmIncomeRequest(
            model.PlannedIncomeId, current.PersonId, model.ActualReceivedOn, ToMinor(model.ActualAmount, planned.CurrencyCode),
            current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
        TempData["Success"] = "Potwierdzono rzeczywisty wpływ. Plan pozostał zachowany w historii, a aktywne reguły wpłaty domowej zostały przeliczone.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("Benefit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Benefit(AddBenefitViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanGuardianAsync(model.BeneficiaryPersonId, current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.CreateBenefitAsync(new CreateBenefitRequest(
            current.HouseholdId, current.PersonId, model.BeneficiaryPersonId, model.FinancialAccountId,
            model.Name, model.BenefitType, ToMinor(model.PlannedAmount, model.CurrencyCode), model.CurrencyCode,
            model.Frequency, model.ValidFrom, model.ValidTo, model.Institution, model.DecisionReference), cancellationToken);
        TempData["Success"] = "Dodano prywatne świadczenie dotyczące dziecka. Beneficjent i właściciel rachunku pozostają rozdzieleni.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("Expense"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Expense(AddPrivateExpenseViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.account.manage_own", current, cancellationToken)) return Forbid();
        if (model.ChildPersonId.HasValue && !await CanGuardianAsync(model.ChildPersonId.Value, current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var allocations = model.ChildPersonId.HasValue
            ? new[] { new ChildExpenseAllocationInput(model.ChildPersonId.Value, ToMinor(model.ChildAmount ?? model.PlannedAmount, model.CurrencyCode), null) }
            : Array.Empty<ChildExpenseAllocationInput>();
        await finance.CreateExpenseAsync(new CreateExpenseRequest(
            current.HouseholdId, current.PersonId, model.FinancialAccountId, model.Name, model.Category,
            ToMinor(model.PlannedAmount, model.CurrencyCode), model.CurrencyCode, model.DueOn, allocations), cancellationToken);
        TempData["Success"] = "Dodano prywatny wydatek.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("Subscription"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscription(AddSubscriptionViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        if (!await CanOwnAsync("privatefinance.subscription.manage_own", current, cancellationToken)) return Forbid();
        if (model.ChildPersonId.HasValue && !await CanGuardianAsync(model.ChildPersonId.Value, current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var subscriptionId = await finance.CreateSubscriptionAsync(new CreateSubscriptionRequest(
            current.HouseholdId, current.PersonId, model.FinancialAccountId, model.ChildPersonId, model.Name, model.Provider,
            model.Category, ToMinor(model.PlannedAmount, model.CurrencyCode), model.CurrencyCode, model.Cycle,
            model.NextChargeOn, model.AutoRenew, model.PaymentMethodLabel, model.PaymentMethodLast4, model.ReminderDays), cancellationToken);
        await finance.GenerateSubscriptionExpensesAsync(subscriptionId, model.NextChargeOn.AddMonths(2), cancellationToken);
        TempData["Success"] = "Dodano subskrypcję i wygenerowano najbliższe planowane koszty bez nadpisywania historii.";
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("UpdateRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRecord(UpdatePrivateRecordViewModel model, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanPrivateRecordAsync(model.RecordType, current, cancellationToken)) return Forbid();
        try
        {
            await adminCrud.UpdatePrivateRecordAsync(new UpdatePrivateRecordRequest(current.HouseholdId, current.PersonId, model.RecordType, model.RecordId, model.Name, model.SecondaryText, model.PlannedAmount.HasValue ? ToMinor(model.PlannedAmount.Value, model.CurrencyCode) : null, model.DateValue, model.Status, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
            TempData["Success"] = "Zapisano zmiany. Dane historyczne nie zostały nadpisane.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost("ArchiveRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveRecord(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanPrivateRecordAsync(recordType, current, cancellationToken)) return Forbid();
        try
        {
            await adminCrud.ArchivePrivateRecordAsync(current.HouseholdId, current.PersonId, recordType, recordId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), cancellationToken);
            TempData["Success"] = recordType == "Expense" ? "Wydatek anulowano bez kasowania historii." : "Rekord został zarchiwizowany.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
    private Task<bool> CanPrivateRecordAsync(string recordType, WebUserContext current, CancellationToken cancellationToken)
    {
        var permission = recordType switch
        {
            "Income" => "privatefinance.income.manage_own",
            "Subscription" => "privatefinance.subscription.manage_own",
            "Benefit" => "privatefinance.account.manage_own",
            _ => "privatefinance.account.manage_own"
        };
        return CanOwnAsync(permission, current, cancellationToken);
    }
    private Task<bool> CanOwnAsync(string permissionCode, WebUserContext current, CancellationToken cancellationToken)
        => access.CanAsync(permissionCode, ResourceScopeTypes.Own, current.PersonId.ToString("D"),
            ownerPersonId: current.PersonId, resourceType: "PrivateFinance", resourceId: current.PersonId.ToString("D"), cancellationToken: cancellationToken);
    private Task<bool> CanGuardianAsync(Guid childPersonId, WebUserContext current, CancellationToken cancellationToken)
        => access.CanAsync("privatefinance.child_record.manage_guardian", ResourceScopeTypes.Guardian, childPersonId.ToString("D"),
            ownerPersonId: current.PersonId, childPersonId: childPersonId, resourceType: "PrivateFinanceChild", resourceId: childPersonId.ToString("D"), cancellationToken: cancellationToken);
    private static long ToMinor(decimal amount, string currencyCode)
        => Money.FromMajorRounded(amount, currencyCode).AmountMinor;
}
