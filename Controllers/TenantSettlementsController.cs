using System.Data.Common;
using System.Security.Cryptography;
using EDom.Application.Collaboration;
using EDom.Application.Rental;
using EDom.Domain.Authorization;
using EDom.Infrastructure.Persistence;
using EDom.Web.Authorization;
using EDom.Web.Infrastructure;
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
                    await RemoveInvoiceElectricityFromTenantSettlementAsync(
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
            await settlementService.BuildDraftAsync(
                actor,
                new(
                    leaseContractId,
                    periodKey),
                cancellationToken);

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

            var removedInvoiceElectricity = false;

            if (settlement is not null
                && IsEditableSettlementStatus(
                    settlement.Status))
            {
                removedInvoiceElectricity =
                    await RemoveInvoiceElectricityFromTenantSettlementAsync(
                        settlement.Id,
                        cancellationToken);
            }

            return removedInvoiceElectricity
                ? "Przeliczono projekt. Usunięto koszt prądu pochodzący z pełnej FV — prąd lokatora jest liczony wyłącznie z podlicznika."
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

    private async Task<bool> RemoveInvoiceElectricityFromTenantSettlementAsync(
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

            int removed;

            await using (var delete =
                         connection.CreateCommand())
            {
                delete.Transaction =
                    transaction;

                // Zasada e-dom:
                // LineType=Electricity może zostać w rozliczeniu lokatora
                // tylko wtedy, gdy jego źródłem jest podlicznik / wyliczenie
                // z odczytów licznika.
                //
                // Pełna FV operatora i alokacja całej FV są usuwane z draftu.
                delete.CommandText =
                    """
                    DELETE FROM "TenantSettlementLines"
                    WHERE "TenantSettlementId" = $settlementId
                      AND "LineType" = 'Electricity'
                      AND NOT (
                            COALESCE("SourceType",'') LIKE '%Submeter%'
                         OR COALESCE("SourceType",'') LIKE '%MeterReading%'
                         OR COALESCE("SourceType",'') LIKE '%Calculation%'
                         OR COALESCE("CalculationSnapshotJson",'') LIKE '%Submeter%'
                      );
                    """;

                AddParameter(
                    delete,
                    "$settlementId",
                    settlementIdValue);

                removed =
                    await delete.ExecuteNonQueryAsync(
                        cancellationToken);
            }

            if (removed <= 0)
            {
                await transaction.CommitAsync(
                    cancellationToken);

                return false;
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
                    WHERE "TenantSettlementId" = $settlementId;
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

            await using (var previous =
                         connection.CreateCommand())
            {
                previous.Transaction =
                    transaction;

                previous.CommandText =
                    """
                    SELECT COALESCE("PreviousBalanceMinor", 0)
                    FROM "TenantSettlements"
                    WHERE "Id" = $settlementId
                    LIMIT 1;
                    """;

                AddParameter(
                    previous,
                    "$settlementId",
                    settlementIdValue);

                previousBalanceMinor =
                    Convert.ToInt64(
                        await previous.ExecuteScalarAsync(
                            cancellationToken)
                        ?? 0L);
            }

            // Sprawdzamy, czy po usunięciu błędnej FV pozostał prawidłowy
            // koszt prądu z podlicznika.
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
                    WHERE "TenantSettlementId" = $settlementId
                      AND (
                            COALESCE("SourceType",'') LIKE '%SubmeterElectricity%'
                         OR (
                                "LineType" = 'Electricity'
                            AND (
                                   COALESCE("SourceType",'') LIKE '%Submeter%'
                                OR COALESCE("SourceType",'') LIKE '%MeterReading%'
                                OR COALESCE("SourceType",'') LIKE '%Calculation%'
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

            await using (var update =
                         connection.CreateCommand())
            {
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

                // Jeżeli pełna FV była jedynym "źródłem prądu", projekt
                // wraca do AwaitingData. Po wygenerowaniu podlicznika można
                // go ponownie przeliczyć / zatwierdzić.
                update.CommandText =
                    $"""
                    UPDATE "TenantSettlements"
                    SET "CurrentPeriodMinor" = $current,
                        "TotalDueMinor" = $total,
                        "Status" = CASE
                            WHEN $validElectricityCount = 0
                                 AND "Status" IN ('Draft','ReadyForApproval')
                            THEN 'AwaitingData'
                            ELSE "Status"
                        END
                        {setVersion}
                    WHERE "Id" = $settlementId;
                    """;

                AddParameter(
                    update,
                    "$current",
                    currentPeriodMinor);

                AddParameter(
                    update,
                    "$total",
                    checked(
                        currentPeriodMinor
                        + previousBalanceMinor));

                AddParameter(
                    update,
                    "$validElectricityCount",
                    validElectricityCount);

                AddParameter(
                    update,
                    "$settlementId",
                    settlementIdValue);

                await update.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await transaction.CommitAsync(
                cancellationToken);

            return true;
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

        var settlements =
            await TableHasColumnAsync(
                connection,
                transaction,
                "TenantSettlements",
                "CurrentPeriodMinor",
                cancellationToken);

        return lines
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
