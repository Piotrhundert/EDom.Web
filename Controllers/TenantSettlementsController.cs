using System.Security.Cryptography;
using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Domain.Authorization;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Settlements")]
public sealed class TenantSettlementsController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    ICollaborationService collaborationService) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken); if (actor is null) return Forbid();
        var model = await settlementService.GetOverviewAsync(actor, cancellationToken);
        if (!model.CanManage && !model.IsTenant && model.Settlements.Count == 0) return Forbid();
        return View(model);
    }

    [HttpPost("Build"), ValidateAntiForgeryToken]
    public Task<IActionResult> Build(Guid leaseContractId, string periodKey, CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.BuildDraftAsync(
                actor,
                new(leaseContractId, periodKey),
                cancellationToken);

            return "Przeliczono projekt miesięcznego rozliczenia lokatora.";
        }, cancellationToken);

    [HttpPost("Line"), ValidateAntiForgeryToken]
    public Task<IActionResult> AddLine(
        Guid settlementId,
        string lineType,
        decimal amount,
        string currencyCode,
        string sourceType,
        string? calculationNote,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            var snapshot = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    note = calculationNote?.Trim(),
                    enteredManually = true
                });

            await settlementService.AddManualLineAsync(
                actor,
                new(
                    settlementId,
                    lineType,
                    ToMinor(amount),
                    currencyCode,
                    sourceType,
                    null,
                    snapshot),
                cancellationToken);

            return "Dodano ręczną pozycję rozliczenia.";
        }, cancellationToken);

    [HttpPost("Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(
        Guid settlementId,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.ApproveAsync(
                actor,
                settlementId,
                cancellationToken);

            return "Rozliczenie zostało zatwierdzone.";
        }, cancellationToken);

    [HttpPost("Publish"), ValidateAntiForgeryToken]
    public Task<IActionResult> Publish(
        Guid settlementId,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.PublishAsync(
                actor,
                settlementId,
                cancellationToken);

            return "Rozliczenie opublikowano lokatorowi.";
        }, cancellationToken);

    [HttpPost("Payment/Submit"), ValidateAntiForgeryToken]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> SubmitPayment(
        Guid settlementId,
        string amount,
        string currencyCode,
        DateTime declaredPaidAtLocal,
        string paymentMethod,
        IFormFile? proof,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null)
        {
            return Forbid();
        }

        try
        {
            if (!TryParseFlexibleDecimal(
                    amount,
                    out var parsedAmount)
                || parsedAmount <= 0m)
            {
                throw new InvalidOperationException(
                    $"Nie udało się odczytać kwoty wpłaty „{amount}”. " +
                    "Podaj kwotę większą od 0, np. 57,50 lub 57.50.");
            }

            Guid? proofId = null;
            string? fingerprint = null;

            if (proof is { Length: > 0 })
            {
                if (proof.Length > 25 * 1024 * 1024)
                {
                    throw new InvalidOperationException(
                        "Potwierdzenie wpłaty przekracza limit 25 MB.");
                }

                await using var memory = new MemoryStream();
                await proof.CopyToAsync(memory, cancellationToken);

                var bytes = memory.ToArray();
                fingerprint = Convert.ToHexString(
                    SHA256.HashData(bytes));

                var document = await collaborationService.CreateDocumentAsync(
                    new CollaborationActor(
                        actor.AccountId,
                        actor.PersonId,
                        actor.HouseholdId,
                        actor.CorrelationId,
                        actor.NowUtc),
                    new CreateDocumentRequest(
                        $"Potwierdzenie wpłaty {declaredPaidAtLocal:yyyy-MM-dd}",
                        "TenantPaymentProof",
                        "Private",
                        ResourceScopeTypes.Own,
                        actor.PersonId.ToString("D"),
                        proof.FileName,
                        string.IsNullOrWhiteSpace(proof.ContentType)
                            ? "application/octet-stream"
                            : proof.ContentType,
                        bytes,
                        SourceModule: "Rental",
                        SourceObjectType: "TenantSettlement",
                        SourceObjectId: settlementId.ToString("D")),
                    cancellationToken);

                proofId = document.Id;
            }

            var declaredUtc =
                DateTime.SpecifyKind(
                        declaredPaidAtLocal,
                        DateTimeKind.Local)
                    .ToUniversalTime();

            await settlementService.SubmitPaymentAsync(
                actor,
                new(
                    settlementId,
                    ToMinor(parsedAmount),
                    currencyCode,
                    declaredUtc,
                    paymentMethod,
                    proofId,
                    fingerprint),
                cancellationToken);

            TempData["Success"] =
                $"Wpłata {parsedAmount:N2} {currencyCode} została zgłoszona. " +
                "Saldo zmieni się dopiero po zatwierdzeniu przez administratora.";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Payment/Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> ApprovePayment(
        Guid submissionId,
        decimal? approvedAmount,
        string? reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.ApprovePaymentAsync(
                actor,
                new(
                    submissionId,
                    approvedAmount.HasValue
                        ? ToMinor(approvedAmount.Value)
                        : null,
                    reason),
                cancellationToken);

            return "Wpłata została zatwierdzona i zaksięgowana w Finansach domowych.";
        }, cancellationToken);

    [HttpPost("Payment/Reject"), ValidateAntiForgeryToken]
    public Task<IActionResult> RejectPayment(
        Guid submissionId,
        string reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.RejectPaymentAsync(
                actor,
                new(
                    submissionId,
                    null,
                    reason),
                cancellationToken);

            return "Wpłata została odrzucona bez zmiany salda.";
        }, cancellationToken);

    [HttpPost("Arrangement"), ValidateAntiForgeryToken]
    public Task<IActionResult> Arrangement(
        Guid settlementId,
        decimal declaredAmount,
        DateOnly agreedDate,
        string? description,
        string? sourceInformation,
        bool visibleToTenant,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.CreatePaymentArrangementAsync(
                actor,
                new(
                    settlementId,
                    ToMinor(declaredAmount),
                    agreedDate,
                    description,
                    sourceInformation,
                    visibleToTenant),
                cancellationToken);

            return "Zapisano uzgodniony termin dopłaty. Zaległość nie została pomniejszona.";
        }, cancellationToken);

    [HttpPost("Correct"), ValidateAntiForgeryToken]
    public Task<IActionResult> Correct(
        Guid settlementId,
        decimal amount,
        string reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.CorrectSettlementAsync(
                actor,
                new(
                    settlementId,
                    ToMinor(amount),
                    reason),
                cancellationToken);

            return "Dodano jawną korektę rozliczenia. Poprzednie pozycje pozostały w historii.";
        }, cancellationToken);

    [HttpPost("LateFeeRule"), ValidateAntiForgeryToken]
    public Task<IActionResult> LateFeeRule(
        Guid? leaseContractId,
        string startTrigger,
        int triggerAfterDays,
        string method,
        decimal value,
        decimal? maxAmount,
        DateOnly validFrom,
        DateOnly? validTo,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            const int scale = 10000;

            var scaled = checked(
                (long)Math.Round(
                    value * scale,
                    0,
                    MidpointRounding.AwayFromZero));

            await settlementService.CreateLateFeeRuleAsync(
                actor,
                new(
                    leaseContractId,
                    startTrigger,
                    triggerAfterDays,
                    method,
                    scaled,
                    scale,
                    maxAmount.HasValue
                        ? ToMinor(maxAmount.Value)
                        : null,
                    validFrom,
                    validTo),
                cancellationToken);

            return "Dodano regułę opłaty za opóźnienie.";
        }, cancellationToken);

    [HttpPost("RefreshDelinquency"), ValidateAntiForgeryToken]
    public Task<IActionResult> RefreshDelinquency(
        DateOnly asOf,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            var changed =
                await settlementService.RefreshDelinquencyAsync(
                    actor,
                    asOf,
                    cancellationToken);

            return $"Przeliczono zaległości i opłaty. Zmienione/utworzone rekordy: {changed}.";
        }, cancellationToken);

    [HttpPost("LateFee/Correct"), ValidateAntiForgeryToken]
    public Task<IActionResult> CorrectLateFee(
        Guid chargeId,
        decimal correctedAmount,
        string reason,
        CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            await settlementService.CorrectLateFeeAsync(
                actor,
                new(
                    chargeId,
                    ToMinor(correctedAmount),
                    reason),
                cancellationToken);

            return "Utworzono jawną korektę opłaty za opóźnienie; pierwotny zapis pozostał w historii.";
        }, cancellationToken);

    private async Task<IActionResult> ExecuteAsync(
        Func<RentalActor, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null)
        {
            return Forbid();
        }

        try
        {
            TempData["Success"] =
                await operation(actor);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            TempData["Error"] =
                ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<RentalActor?> GetActorAsync(
        CancellationToken cancellationToken)
    {
        var current =
            await access.GetCurrentAsync(cancellationToken);

        return current is null
            ? null
            : new RentalActor(
                current.UserAccountId,
                current.PersonId,
                current.HouseholdId,
                CorrelationIdMiddleware.Get(HttpContext),
                DateTime.UtcNow);
    }

    private static bool TryParseFlexibleDecimal(
        string? value,
        out decimal result)
    {
        result = 0m;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized =
            value.Trim()
                .Replace("\u00A0", "")
                .Replace(" ", "");

        const System.Globalization.NumberStyles styles =
            System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowDecimalPoint;

        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.GetCultureInfo("pl-PL"),
                out result))
        {
            return true;
        }

        if (decimal.TryParse(
                normalized,
                styles,
                System.Globalization.CultureInfo.InvariantCulture,
                out result))
        {
            return true;
        }

        return false;
    }

    private static long ToMinor(decimal amount) =>
        checked(
            (long)Math.Round(
                amount * 100m,
                0,
                MidpointRounding.AwayFromZero));
}
