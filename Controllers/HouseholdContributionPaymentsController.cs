using EDom.Application.HouseholdFinance;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("HouseholdContributionPayments")]
public sealed class HouseholdContributionPaymentsController(
    IHouseholdFinanceService householdFinance,
    WebAccessService access) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? obligationId, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null) return Forbid();

        var canSubmit = await CanSubmitAsync(current.HouseholdId, current.PersonId, cancellationToken);
        var canApprove = await CanApproveAsync(current.HouseholdId, cancellationToken);
        if (!canSubmit && !canApprove) return Forbid();

        // Naprawia również zatwierdzone wpłaty z wersji sprzed PKG-015h-FIX-02:
        // po wejściu Domownika do historii wpłat brakujące obciążenie prywatnego konta
        // zostanie utworzone idempotentnie.
        if (canSubmit)
            await householdFinance.ReconcilePrivateContributionDebitsAsync(current.HouseholdId, current.PersonId, cancellationToken);

        var overview = await householdFinance.GetOverviewAsync(current.HouseholdId, current.PersonId, canApprove, cancellationToken);
        var mySubmissions = overview.PaymentSubmissions.Where(x => x.PersonId == current.PersonId).ToArray();
        var pendingKeys = mySubmissions
            .Where(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.PeriodKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var model = new HouseholdContributionPaymentsViewModel
        {
            CanSubmit = canSubmit,
            CanApprove = canApprove,
            SelectedObligationId = obligationId,
            MyObligations = overview.Obligations
                .Where(x => x.PersonId == current.PersonId)
                .OrderByDescending(x => x.PeriodKey)
                .Select(x => new ContributionObligationVm(
                    x.Id, x.PeriodKey, checked(x.OriginalAmountMinor + x.AdjustmentsMinor), x.PaidMinor,
                    x.RemainingMinor, x.CurrencyCode, x.DueDate, x.Status, pendingKeys.Contains(x.PeriodKey)))
                .ToArray(),
            MySubmissions = mySubmissions.Select(ToVm).ToArray(),
            PendingForApproval = canApprove
                ? overview.PaymentSubmissions
                    .Where(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.PaidAtUtc)
                    .Select(ToVm)
                    .ToArray()
                : Array.Empty<ContributionSubmissionVm>()
        };

        return View(model);
    }

    [HttpPost("Submit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        Guid obligationId,
        decimal amount,
        DateTime paidAtLocal,
        string paymentMethod,
        string? transferReference,
        CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null || !await CanSubmitAsync(current.HouseholdId, current.PersonId, cancellationToken)) return Forbid();

        try
        {
            var amountMinor = ToMinor(amount);
            if (amountMinor <= 0) throw new InvalidOperationException("Kwota wpłaty musi być większa od 0.");

            var overview = await householdFinance.GetOverviewAsync(current.HouseholdId, current.PersonId, false, cancellationToken);
            var obligation = overview.Obligations.SingleOrDefault(x => x.Id == obligationId && x.PersonId == current.PersonId)
                ?? throw new InvalidOperationException("Nie znaleziono Twojej należności do wpłaty.");
            if (obligation.RemainingMinor <= 0) throw new InvalidOperationException("Ta należność jest już rozliczona.");

            var hasPending = overview.PaymentSubmissions.Any(x =>
                x.PersonId == current.PersonId && x.PeriodKey == obligation.PeriodKey &&
                string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase));
            if (hasPending) throw new InvalidOperationException("Dla tego okresu istnieje już zgłoszenie oczekujące na zatwierdzenie.");

            var paidAtUtc = paidAtLocal.Kind == DateTimeKind.Utc
                ? paidAtLocal
                : DateTime.SpecifyKind(paidAtLocal, DateTimeKind.Local).ToUniversalTime();

            var proofFingerprint = string.IsNullOrWhiteSpace(transferReference)
                ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(transferReference.Trim())));

            await householdFinance.SubmitContributionPaymentAsync(new SubmitContributionPaymentRequest(
                current.HouseholdId,
                current.PersonId,
                obligation.PeriodKey,
                amountMinor,
                obligation.CurrencyCode,
                paymentMethod,
                paidAtUtc,
                proofFingerprint), cancellationToken);

            TempData["Success"] = "Wpłata została oznaczona jako wysłana. Saldo domu zmieni się dopiero po zatwierdzeniu przez administratora.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { obligationId });
    }

    [HttpPost("Approve"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid submissionId, decimal approvedAmount, string? decisionReason, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null || !await CanApproveAsync(current.HouseholdId, cancellationToken)) return Forbid();

        try
        {
            var approvedMinor = ToMinor(approvedAmount);
            if (approvedMinor <= 0) throw new InvalidOperationException("Zatwierdzona kwota musi być większa od 0.");

            var overview = await householdFinance.GetOverviewAsync(current.HouseholdId, current.PersonId, true, cancellationToken);
            var submission = overview.PaymentSubmissions.SingleOrDefault(x => x.Id == submissionId)
                ?? throw new InvalidOperationException("Nie znaleziono zgłoszenia wpłaty.");
            if (!string.Equals(submission.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("To zgłoszenie nie oczekuje już na decyzję.");

            var remaining = approvedMinor;
            var allocations = new List<PaymentAllocationInput>();
            foreach (var obligation in overview.Obligations
                         .Where(x => x.PersonId == submission.PersonId && x.PeriodKey == submission.PeriodKey && x.RemainingMinor > 0)
                         .OrderBy(x => x.DueDate))
            {
                if (remaining <= 0) break;
                var allocated = Math.Min(remaining, obligation.RemainingMinor);
                allocations.Add(new PaymentAllocationInput(obligation.Id, allocated));
                remaining -= allocated;
            }

            await householdFinance.ApproveContributionPaymentAsync(new ApproveContributionPaymentRequest(
                submissionId,
                approvedMinor,
                allocations,
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext),
                decisionReason), cancellationToken);

            TempData["Success"] = "Wpłata została zatwierdzona i zaksięgowana w finansach domowych.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reject"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid submissionId, string reason, CancellationToken cancellationToken)
    {
        var current = await access.GetCurrentAsync(cancellationToken);
        if (current is null || !await CanApproveAsync(current.HouseholdId, cancellationToken)) return Forbid();

        try
        {
            await householdFinance.RejectContributionPaymentAsync(new RejectContributionPaymentRequest(
                submissionId,
                current.UserAccountId,
                CorrelationIdMiddleware.Get(HttpContext),
                reason), cancellationToken);
            TempData["Success"] = "Zgłoszenie wpłaty zostało odrzucone. Saldo domu nie zostało zmienione.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> CanSubmitAsync(Guid householdId, Guid personId, CancellationToken cancellationToken)
    {
        // Prefer the documented Own scope. Some existing household role assignments are stored
        // with Household scope, so keep a compatibility check through the same central evaluator.
        var ownAllowed = await access.CanAsync(
            "payment.submit",
            ResourceScopeTypes.Own,
            personId.ToString("D"),
            ownerPersonId: personId,
            resourceType: "ContributionPaymentSubmission",
            cancellationToken: cancellationToken);

        if (ownAllowed) return true;

        return await access.CanAsync(
            "payment.submit",
            ResourceScopeTypes.Household,
            householdId.ToString("D"),
            ownerPersonId: personId,
            resourceType: "ContributionPaymentSubmission",
            cancellationToken: cancellationToken);
    }

    private Task<bool> CanApproveAsync(Guid householdId, CancellationToken cancellationToken)
        => access.CanAsync("payment.approve", ResourceScopeTypes.Household, householdId.ToString("D"),
            resourceType: "ContributionPaymentSubmission", cancellationToken: cancellationToken);

    private static ContributionSubmissionVm ToVm(PaymentSubmissionSummary x)
        => new(x.Id, x.PersonId, x.PersonName, x.PeriodKey, x.AmountMinor, x.CurrencyCode, x.PaymentMethod,
            x.PaidAtUtc, x.Status, x.ApprovedAmountMinor, x.DecisionReason);

    private static long ToMinor(decimal amount)
        => checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
}
