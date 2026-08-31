using EDom.Application.HouseholdFinance;
using EDom.Application.Administration;
using EDom.Application.Households;
using EDom.Domain.Authorization;
using EDom.SharedKernel.Values;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("HouseholdFinance")]
public sealed class HouseholdFinanceController(
    WebAccessService access,
    IHouseholdFinanceService finance,
    IHouseholdFamilyService family,
    IAdministrationCrudService adminCrud) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();
        var canSubmit = await CanOwnAsync("householdfinance.payment.submit", current, cancellationToken);
        var canManage = await CanHouseholdAsync("householdfinance.invoice.manage", current, cancellationToken);
        if (!canSubmit && !canManage) return Forbid();

        var household = await family.GetOverviewAsync(current.HouseholdId, cancellationToken);
        return View(new HouseholdFinancePageViewModel
        {
            Overview = await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, canManage, cancellationToken),
            People = household.Persons.Where(x => !x.IsChild).Select(x => (x.PersonId, x.DisplayName)).ToArray(),
            CanManage = canManage
        });
    }

    [HttpPost("Rule"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Rule(AddContributionRuleViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.contribution.calculate", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.CreateContributionRuleAsync(new CreateContributionRuleRequest(
            current.HouseholdId, model.PersonId, model.Method,
            model.FixedAmount.HasValue ? ToMinor(model.FixedAmount.Value, currency) : null,
            model.Percent.HasValue ? (long)Math.Round(model.Percent.Value * 100m, MidpointRounding.AwayFromZero) : null,
            10_000, model.DuePolicyType, model.DueDayOrOffset, model.ValidFrom, model.ValidTo), cancellationToken);
        TempData["Success"] = "Dodano regułę obowiązkowej wpłaty.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Obligation"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Obligation(GenerateContributionViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.contribution.calculate", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.GenerateContributionObligationAsync(new GenerateContributionObligationRequest(
            model.ContributionRuleId, model.PersonId, model.PeriodKey,
            model.IncomeAmount.HasValue ? ToMinor(model.IncomeAmount.Value, currency) : null,
            model.IncomeDate, null, model.IsDraft), cancellationToken);
        TempData["Success"] = "Utworzono należność dla wskazanego okresu.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Payment/Submit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPayment(SubmitContributionPaymentViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanOwnAsync("householdfinance.payment.submit", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.SubmitContributionPaymentAsync(new SubmitContributionPaymentRequest(
            current.HouseholdId, current.PersonId, model.PeriodKey, ToMinor(model.Amount, model.CurrencyCode),
            model.CurrencyCode, model.PaymentMethod, model.PaidAtUtc, model.ProofFingerprint), cancellationToken);
        TempData["Success"] = "Wpłata została zgłoszona. Saldo rachunku domowego nie zmieni się do czasu akceptacji.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Payment/Approve"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ApprovePayment(ApproveContributionPaymentViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.payment.approve", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        var allocations = model.ObligationId.HasValue && model.AllocateAmount is > 0
            ? new[] { new PaymentAllocationInput(model.ObligationId.Value, ToMinor(model.AllocateAmount.Value, currency)) }
            : Array.Empty<PaymentAllocationInput>();
        await finance.ApproveContributionPaymentAsync(new ApproveContributionPaymentRequest(
            model.SubmissionId, ToMinor(model.ApprovedAmount, currency), allocations,
            current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), model.DecisionReason), cancellationToken);
        TempData["Success"] = "Wpłata została zaakceptowana i zaksięgowana dokładnie raz.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Payment/Reject"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectPayment(RejectContributionPaymentViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.payment.approve", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.RejectContributionPaymentAsync(new RejectContributionPaymentRequest(
            model.SubmissionId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), model.Reason), cancellationToken);
        TempData["Success"] = "Zgłoszenie wpłaty odrzucono z zachowaniem historii.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Invoice"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Invoice(AddHouseholdInvoiceViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.invoice.manage", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.CreateInvoiceAsync(new CreateHouseholdInvoiceRequest(
            current.HouseholdId, model.InvoiceNo, model.Supplier, model.CategoryCode, model.PeriodFrom, model.PeriodTo,
            model.IssuedOn, model.DueDate, model.Net.HasValue ? ToMinor(model.Net.Value, model.CurrencyCode) : null,
            model.Vat.HasValue ? ToMinor(model.Vat.Value, model.CurrencyCode) : null, ToMinor(model.Gross, model.CurrencyCode),
            model.CurrencyCode, "Household", current.HouseholdId.ToString("D")), cancellationToken);
        TempData["Success"] = "Dodano fakturę gospodarstwa.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Invoice/Pay"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PayInvoice(PayHouseholdInvoiceViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.invoice.pay", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.PayInvoiceAsync(new PayHouseholdInvoiceRequest(
            model.InvoiceId, ToMinor(model.Amount, currency), model.SourceType, null, model.PaidAtUtc,
            current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
        TempData["Success"] = "Zapisano płatność faktury i zaktualizowano ledger.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Claim"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(SubmitPrivatePaidClaimViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanOwnAsync("householdfinance.claim.submit", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        await finance.SubmitPrivatePaidClaimAsync(new SubmitPrivatePaidClaimRequest(
            current.HouseholdId, current.PersonId, model.HouseholdInvoiceId, ToMinor(model.ClaimedAmount, model.CurrencyCode),
            model.CurrencyCode, model.PaidAtUtc, model.ProposedSettlementType, model.Description), cancellationToken);
        TempData["Success"] = "Zgłoszono rachunek opłacony prywatnie. Samo zgłoszenie nie zmienia wspólnego salda.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Claim/Decide"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideClaim(DecidePrivatePaidClaimViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.claim.approve", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.DecidePrivatePaidClaimAsync(new DecidePrivatePaidClaimRequest(
            model.ClaimId, model.ApprovedAmount.HasValue ? ToMinor(model.ApprovedAmount.Value, currency) : null,
            current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), model.Reason, model.Approve), cancellationToken);
        TempData["Success"] = "Zapisano decyzję dla prywatnie opłaconego rachunku.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Claim/Settle"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SettleClaim(SettlePrivatePaidClaimViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.claim.settle", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.SettlePrivatePaidClaimAsync(new SettlePrivatePaidClaimRequest(
            model.ClaimId, model.SettlementType, ToMinor(model.Amount, currency), model.TargetObligationId,
            model.Pocket, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
        TempData["Success"] = "Rozliczono część roszczenia. System kontroluje wspólny limit zwrotu i kompensaty.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Obligation/Adjust"), ValidateAntiForgeryToken]
    public async Task<IActionResult> AdjustObligation(AdjustContributionViewModel model, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        if (!await CanHouseholdAsync("householdfinance.contribution.calculate", current, cancellationToken)) return Forbid();
        if (!ModelState.IsValid || model.AdjustmentAmount == 0) return RedirectToAction(nameof(Index));
        var currency = (await finance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken)).Ledger.CurrencyCode;
        await finance.AdjustObligationAsync(new AdjustObligationRequest(
            model.ObligationId, "Manual", ToMinorSigned(model.AdjustmentAmount, currency), model.Reason, "Web", null,
            current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext)), cancellationToken);
        TempData["Success"] = "Dodano jawną korektę należności. Pierwotna kwota nie została nadpisana.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ArchiveRecord"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveRecord(string recordType, Guid recordId, string reason, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(cancellationToken); if (current is null) return Forbid();
        var permission = recordType == "Invoice" ? "householdfinance.invoice.manage" : "householdfinance.contribution.calculate";
        if (!await CanHouseholdAsync(permission, current, cancellationToken)) return Forbid();
        try
        {
            await adminCrud.ArchiveHouseholdFinanceRecordAsync(current.HouseholdId, recordType, recordId, current.UserAccountId, CorrelationIdMiddleware.Get(HttpContext), string.IsNullOrWhiteSpace(reason) ? "Archiwizacja/anulowanie" : reason, cancellationToken);
            TempData["Success"] = recordType == "Invoice" ? "Fakturę anulowano bez kasowania historii." : "Regułę wpłaty zarchiwizowano.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private Task<WebUserContext?> RequireCurrentAsync(CancellationToken cancellationToken) => access.GetCurrentAsync(cancellationToken);

    private Task<bool> CanHouseholdAsync(string permissionCode, WebUserContext current, CancellationToken cancellationToken)
        => access.CanAsync(permissionCode, ResourceScopeTypes.Household, current.HouseholdId.ToString("D"),
            ownerPersonId: current.PersonId, resourceType: "HouseholdFinance", resourceId: current.HouseholdId.ToString("D"), cancellationToken: cancellationToken);

    private Task<bool> CanOwnAsync(string permissionCode, WebUserContext current, CancellationToken cancellationToken)
        => access.CanAsync(permissionCode, ResourceScopeTypes.Own, current.PersonId.ToString("D"),
            ownerPersonId: current.PersonId, resourceType: "HouseholdFinance", resourceId: current.PersonId.ToString("D"), cancellationToken: cancellationToken);

    private static long ToMinor(decimal amount, string currencyCode) => Money.FromMajorRounded(amount, currencyCode).AmountMinor;
    private static long ToMinorSigned(decimal amount, string currencyCode)
        => amount >= 0 ? ToMinor(amount, currencyCode) : checked(-ToMinor(decimal.Abs(amount), currencyCode));
}
