using System.Data.Common;
using System.Security.Cryptography;
using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Domain.Authorization;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
using EDom.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Controllers;

[Authorize]
[Route("Rental/Settlements")]
public sealed class TenantSettlementsController(
    WebAccessService access,
    ITenantSettlementService settlementService,
    ICollaborationService collaborationService,
    TenantSubmeterAutoSyncService submeterAutoSync,
    TenantWaterLegacyRepairService waterLegacyRepair,
    EDomDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var actor = await GetActorAsync(cancellationToken);
        if (actor is null)
        {
            return Forbid();
        }

        var model =
            await settlementService.GetOverviewAsync(
                actor,
                cancellationToken);

        if (!model.CanManage
            && !model.IsTenant
            && model.Settlements.Count == 0)
        {
            return Forbid();
        }

        if (model.CanManage)
        {
            var changed = false;

            foreach (var settlement in model.Settlements)
            {
                if (!IsEditableSettlementStatus(
                        settlement.Status))
                {
                    continue;
                }

                changed |=
                    await NormalizeTenantSettlementDraftAsync(
                        settlement.Id,
                        cancellationToken);
            }

            if (changed)
            {
                model =
                    await settlementService.GetOverviewAsync(
                        actor,
                        cancellationToken);
            }
        }

        return View(model);
    }

    [HttpPost("Build"), ValidateAntiForgeryToken]
    public Task<IActionResult> Build(Guid leaseContractId, string periodKey, CancellationToken cancellationToken)
        => ExecuteAsync(async actor =>
        {
            // PKG-015q-FEAT-04-FIX-01
            // W FEAT-04 techniczne placeholdery mediów były usuwane z bazy.
            // Dla istniejącego projektu mogło to wejść w konflikt z mechanizmem
            // przebudowy źródłowej i kończyć się komunikatem:
            // „Niedozwolony typ ręcznej pozycji rozliczenia.”
            //
            // Najpierw próbujemy standardowej przebudowy. Jeżeli stary projekt
            // zawiera już poprawne ręczne pozycje FEAT-02/03/04 i serwis odrzuci
            // ponowną przebudowę, zachowujemy istniejący projekt i wykonujemy
            // wyłącznie bezpieczną normalizację mediów.
            var fallbackToExistingDraft = false;

            try
            {
                await settlementService.BuildDraftAsync(
                    actor,
                    new(
                        leaseContractId,
                        periodKey),
                    cancellationToken);
            }
            catch (Exception ex) when
                (string.Equals(
                    ex.Message?.Trim(),
                    "Niedozwolony typ ręcznej pozycji rozliczenia.",
                    StringComparison.OrdinalIgnoreCase))
            {
                // PKG-015q-FEAT-04-FIX-04
                // W starszej warstwie Application ten sam komunikat może być
                // zgłoszony jako inny typ wyjątku niż InvalidOperationException.
                // Dla istniejącego projektu zachowujemy pozycje i wykonujemy
                // wyłącznie synchronizację/normalizację.
                fallbackToExistingDraft = true;
            }

            var overview =
                await settlementService.GetOverviewAsync(
                    actor,
                    cancellationToken);

            var settlement =
                overview.Settlements.FirstOrDefault(x =>
                    x.LeaseContractId == leaseContractId
                    && string.Equals(
                        x.PeriodKey,
                        periodKey,
                        StringComparison.OrdinalIgnoreCase));

            if (settlement is null && fallbackToExistingDraft)
            {
                throw new InvalidOperationException(
                    "Nie udało się utworzyć nowego rozliczenia. Błąd typu ręcznej pozycji wystąpił przed zapisaniem projektu.");
            }

            var normalizedSettlement = false;
            var submeterSync = SubmeterAutoSyncResult.Empty;
            var waterRepair = WaterLegacyRepairResult.Empty;

            if (settlement is not null
                && IsEditableSettlementStatus(
                    settlement.Status))
            {
                // PKG-015q-FEAT-04-FIX-05
                // Starsze FEAT-02 potrafiło utworzyć WaterInvoice tylko dla
                // jednego lokatora i według WaterByConsumption. Jeżeli FV jest
                // w 100% opłacona przez dom, przebuduj taki historyczny podział
                // na WaterByPersons dla wszystkich lokatorów obejmujących okres.
                waterRepair = await waterLegacyRepair.RepairAsync(
                    actor.HouseholdId,
                    periodKey,
                    cancellationToken);

                // PKG-015q-FEAT-04-FIX-03/05
                // Po kliknięciu „Przelicz projekt” aplikacja sama pobiera
                // zużycie z podlicznika przypisanego do pokoju tej umowy.
                // FIX-05 dodatkowo usuwa duplikat starej pozycji
                // SubmeterElectricity, gdy istnieje już nowa pozycja Energy.
                submeterSync = await submeterAutoSync.SyncAsync(
                    actor,
                    settlement.Id,
                    leaseContractId,
                    periodKey,
                    cancellationToken);

                normalizedSettlement =
                    await NormalizeTenantSettlementDraftAsync(
                        settlement.Id,
                        cancellationToken);
            }

            if (fallbackToExistingDraft)
            {
                if (waterRepair.ChangedLines > 0 && submeterSync.AddedLines == 0)
                {
                    return $"Istniejący projekt został zachowany. Naprawiono rozliczenie wody według liczby osób ({waterRepair.ChangedLines} poz.) i usunięto stare naliczenie WaterByConsumption.";
                }

                if (submeterSync.AddedLines > 0 && submeterSync.MissingDistributionRate)
                {
                    return $"Istniejący projekt został zachowany. Dodano energię z podlicznika ({submeterSync.AddedEnergyLines} poz.), ale brakuje stawki przesyłu / dystrybucji za kWh.";
                }

                if (submeterSync.AddedLines > 0)
                {
                    return $"Istniejący projekt został zachowany. Automatycznie dopisano z podlicznika: energia {submeterSync.AddedEnergyLines}, przesył {submeterSync.AddedDistributionLines}.";
                }

                if (submeterSync.WaitingForNextReading)
                {
                    return "Istniejący projekt został zachowany. Podlicznik ma odczyt początkowy tej umowy, ale potrzebny jest kolejny zatwierdzony odczyt z większym stanem.";
                }

                return "Istniejący projekt rozliczenia został zachowany i uporządkowany. Nie utworzono duplikatu; media lokatora pozostają rozliczane według zasad FEAT-04.";
            }

            if (waterRepair.ChangedLines > 0 && submeterSync.AddedLines == 0)
            {
                return $"Przeliczono projekt. Wodę rozdzielono ponownie według liczby osób dla wszystkich lokatorów ({waterRepair.ChangedLines} poz.).";
            }

            if (submeterSync.AddedLines > 0 && submeterSync.MissingDistributionRate)
            {
                return $"Przeliczono projekt. Dodano energię z podlicznika ({submeterSync.AddedEnergyLines} poz.), ale brakuje stawki przesyłu / dystrybucji za kWh w taryfie licznika głównego.";
            }

            if (submeterSync.AddedLines > 0 && submeterSync.MissingEnergyRate)
            {
                return $"Przeliczono projekt. Dodano przesył ({submeterSync.AddedDistributionLines} poz.), ale brakuje stawki energii za kWh w taryfie licznika głównego.";
            }

            if (submeterSync.AddedLines > 0)
            {
                return $"Przeliczono projekt. Automatycznie pobrano prąd z podlicznika pokoju: energia {submeterSync.AddedEnergyLines}, przesył {submeterSync.AddedDistributionLines}.";
            }

            if (submeterSync.ConsumptionIntervals > 0 && submeterSync.MissingEnergyRate)
            {
                return "Przeliczono projekt. Znaleziono zużycie podlicznika, ale brakuje aktywnej stawki energii za kWh w taryfie licznika głównego.";
            }

            if (submeterSync.ConsumptionIntervals > 0 && submeterSync.MissingDistributionRate)
            {
                return "Przeliczono projekt. Znaleziono zużycie podlicznika, ale brakuje stawki przesyłu / dystrybucji za kWh w taryfie licznika głównego.";
            }

            if (submeterSync.WaitingForNextReading)
            {
                return "Przeliczono projekt. Podlicznik ma już odczyt początkowy dla tej umowy, ale potrzebny jest kolejny zatwierdzony odczyt z większym stanem. Sam stan początkowy nie tworzy opłaty za prąd.";
            }

            return normalizedSettlement
                ? "Przeliczono projekt. Uporządkowano media lokatora: prąd tylko z podlicznika, bez gazu, a woda i odpady wyłącznie z właściwych rozliczeń FV."
                : "Przeliczono projekt miesięcznego rozliczenia lokatora.";
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
                        ? ToMinor(approvedAmount.GetValueOrDefault())
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
                        ? ToMinor(maxAmount.GetValueOrDefault())
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

    private static bool IsEditableSettlementStatus(
        string? status) =>
        status is
            "Draft"
            or "AwaitingData"
            or "ReadyForApproval";

    private async Task<bool> NormalizeTenantSettlementDraftAsync(
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        var connection =
            db.Database.GetDbConnection();

        var closeWhenDone =
            connection.State
            != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(
                cancellationToken);
        }

        await using var transaction =
            await connection.BeginTransactionAsync(
                cancellationToken);

        try
        {
            if (!await HasTenantSettlementSchemaAsync(
                    connection,
                    transaction,
                    cancellationToken))
            {
                await transaction.RollbackAsync(
                    cancellationToken);

                return false;
            }

            var settlementIdValue =
                settlementId.ToString("D");

            int normalizedTechnicalLines;

            await using (var normalizeLines =
                         connection.CreateCommand())
            {
                normalizeLines.Transaction =
                    transaction;

                // PKG-015q-FEAT-04-FIX-01
                // Nie usuwamy już systemowych placeholderów mediów. Serwis bazowy
                // wykorzystuje je przy ponownej przebudowie projektu. Zamiast DELETE
                // neutralizujemy ich wpływ na rozliczenie i status danych:
                // - prąd operatora nie obciąża lokatora (liczy się podlicznik),
                // - gaz nie dotyczy Domu 2,
                // - systemowe Water/Waste nie są źródłem obciążenia; właściwe pozycje
                //   trafiają jako Adjustment z SourceType WaterInvoice/WasteInvoice.
                normalizeLines.CommandText =
                    """
                    UPDATE "TenantSettlementLines"
                    SET "AmountMinor" = 0,
                        "Status" = 'Ready'
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND (
                            (
                                "LineType" = 'Electricity'
                                AND NOT (
                                       COALESCE("SourceType",'') LIKE '%Submeter%'
                                    OR COALESCE("SourceType",'') LIKE '%MeterReading%'
                                    OR COALESCE("SourceType",'') LIKE '%Calculation%'
                                    OR COALESCE("CalculationSnapshotJson",'') LIKE '%Submeter%'
                                )
                            )
                         OR "LineType" IN ('Water','Waste','Gas')
                      )
                      AND (
                            COALESCE("AmountMinor", 0) <> 0
                         OR COALESCE("Status", '') <> 'Ready'
                      );
                    """;

                AddParameter(
                    normalizeLines,
                    "$settlementId",
                    settlementIdValue);

                normalizedTechnicalLines =
                    await normalizeLines.ExecuteNonQueryAsync(
                        cancellationToken);
            }

            long currentPeriodMinor;

            await using (var sum =
                         connection.CreateCommand())
            {
                sum.Transaction =
                    transaction;

                sum.CommandText =
                    """
                    SELECT COALESCE(SUM("AmountMinor"), 0)
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId);
                    """;

                AddParameter(
                    sum,
                    "$settlementId",
                    settlementIdValue);

                currentPeriodMinor =
                    Convert.ToInt64(
                        await sum.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);
            }

            long previousBalanceMinor;
            long existingCurrentPeriodMinor;
            long existingTotalDueMinor;
            string existingStatus;

            await using (var settlement =
                         connection.CreateCommand())
            {
                settlement.Transaction =
                    transaction;

                settlement.CommandText =
                    """
                    SELECT
                        COALESCE("PreviousBalanceMinor", 0),
                        COALESCE("CurrentPeriodMinor", 0),
                        COALESCE("TotalDueMinor", 0),
                        COALESCE("Status", '')
                    FROM "TenantSettlements"
                    WHERE LOWER("Id") = LOWER($settlementId)
                    LIMIT 1;
                    """;

                AddParameter(
                    settlement,
                    "$settlementId",
                    settlementIdValue);

                await using var reader =
                    await settlement.ExecuteReaderAsync(
                        cancellationToken);

                if (!await reader.ReadAsync(
                        cancellationToken))
                {
                    await transaction.RollbackAsync(
                        cancellationToken);

                    return false;
                }

                previousBalanceMinor =
                    Convert.ToInt64(reader.GetValue(0));

                existingCurrentPeriodMinor =
                    Convert.ToInt64(reader.GetValue(1));

                existingTotalDueMinor =
                    Convert.ToInt64(reader.GetValue(2));

                existingStatus =
                    reader.GetValue(3)?.ToString()
                    ?? "";
            }

            // Prawidłowe źródło energii: historyczny SubmeterElectricity
            // albo nowy SubmeterElectricityEnergy. Sama linia przesyłu nie
            // zastępuje kosztu energii z podlicznika.
            long validElectricityCount;

            await using (var valid =
                         connection.CreateCommand())
            {
                valid.Transaction =
                    transaction;

                valid.CommandText =
                    """
                    SELECT COUNT(1)
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND (
                            COALESCE("SourceType",'') = 'SubmeterElectricity'
                         OR COALESCE("SourceType",'') LIKE 'SubmeterElectricityEnergy%'
                         OR (
                                "LineType" = 'Electricity'
                            AND (
                                   COALESCE("SourceType",'') LIKE '%Submeter%'
                                OR COALESCE("SourceType",'') LIKE '%MeterReading%'
                                OR COALESCE("CalculationSnapshotJson",'') LIKE '%Submeter%'
                            )
                         )
                      );
                    """;

                AddParameter(
                    valid,
                    "$settlementId",
                    settlementIdValue);

                validElectricityCount =
                    Convert.ToInt64(
                        await valid.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);
            }

            long validDistributionCount;

            await using (var distribution =
                         connection.CreateCommand())
            {
                distribution.Transaction =
                    transaction;

                distribution.CommandText =
                    """
                    SELECT COUNT(1)
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND LOWER(COALESCE("SourceType",'')) LIKE 'submeterelectricitydistribution%';
                    """;

                AddParameter(
                    distribution,
                    "$settlementId",
                    settlementIdValue);

                validDistributionCount =
                    Convert.ToInt64(
                        await distribution.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);
            }

            long remainingAwaitingDataCount;

            await using (var awaiting =
                         connection.CreateCommand())
            {
                awaiting.Transaction =
                    transaction;

                awaiting.CommandText =
                    """
                    SELECT COUNT(1)
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND "Status" = 'AwaitingData';
                    """;

                AddParameter(
                    awaiting,
                    "$settlementId",
                    settlementIdValue);

                remainingAwaitingDataCount =
                    Convert.ToInt64(
                        await awaiting.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);
            }

            var desiredStatus =
                existingStatus;

            if (IsEditableSettlementStatus(
                    existingStatus))
            {
                if (validElectricityCount == 0
                    || validDistributionCount == 0)
                {
                    desiredStatus =
                        "AwaitingData";
                }
                else if (string.Equals(
                             existingStatus,
                             "AwaitingData",
                             StringComparison.OrdinalIgnoreCase)
                         && remainingAwaitingDataCount == 0)
                {
                    desiredStatus =
                        "ReadyForApproval";
                }
            }

            var totalDueMinor =
                checked(
                    currentPeriodMinor
                    + previousBalanceMinor);

            var needsUpdate =
                normalizedTechnicalLines > 0
                || existingCurrentPeriodMinor != currentPeriodMinor
                || existingTotalDueMinor != totalDueMinor
                || !string.Equals(
                    existingStatus,
                    desiredStatus,
                    StringComparison.OrdinalIgnoreCase);

            if (needsUpdate)
            {
                await using var update =
                    connection.CreateCommand();

                update.Transaction =
                    transaction;

                var hasVersion =
                    await TableHasColumnAsync(
                        connection,
                        transaction,
                        "TenantSettlements",
                        "Version",
                        cancellationToken);

                var setVersion =
                    hasVersion
                        ? """, "Version" = "Version" + 1"""
                        : "";

                update.CommandText =
                    $"""
                    UPDATE "TenantSettlements"
                    SET "CurrentPeriodMinor" = $current,
                        "TotalDueMinor" = $total,
                        "Status" = $status
                        {setVersion}
                    WHERE LOWER("Id") = LOWER($settlementId);
                    """;

                AddParameter(
                    update,
                    "$current",
                    currentPeriodMinor);

                AddParameter(
                    update,
                    "$total",
                    totalDueMinor);

                AddParameter(
                    update,
                    "$status",
                    desiredStatus);

                AddParameter(
                    update,
                    "$settlementId",
                    settlementIdValue);

                await update.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await transaction.CommitAsync(
                cancellationToken);

            return needsUpdate;
        }
        catch
        {
            await transaction.RollbackAsync(
                cancellationToken);

            throw;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> HasTenantSettlementSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var lines =
            await TableHasColumnAsync(
                connection,
                transaction,
                "TenantSettlementLines",
                "CalculationSnapshotJson",
                cancellationToken);

        var lineStatus =
            await TableHasColumnAsync(
                connection,
                transaction,
                "TenantSettlementLines",
                "Status",
                cancellationToken);

        var settlements =
            await TableHasColumnAsync(
                connection,
                transaction,
                "TenantSettlements",
                "CurrentPeriodMinor",
                cancellationToken);

        return lines
               && lineStatus
               && settlements;
    }

    private static async Task<bool> TableHasColumnAsync(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command =
            connection.CreateCommand();

        command.Transaction =
            transaction;

        command.CommandText =
            $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
                   cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter =
            command.CreateParameter();

        parameter.ParameterName =
            name;

        parameter.Value =
            value;

        command.Parameters.Add(
            parameter);
    }

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
