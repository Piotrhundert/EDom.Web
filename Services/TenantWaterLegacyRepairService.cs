using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EDom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Services;

/// <summary>
/// PKG-015q-FEAT-04-FIX-05
/// Naprawia stare rozliczenia FV za wodę utworzone przed zasadą WaterByPersons.
/// Działa wyłącznie dla w pełni opłaconej FV i tylko na edytowalnych projektach.
/// </summary>
public sealed class TenantWaterLegacyRepairService(
    EDomDbContext db)
{
    public async Task<WaterLegacyRepairResult> RepairAsync(
        Guid householdId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var settlements = await LoadSettlementsAsync(
                connection,
                householdId,
                periodKey,
                cancellationToken);

            if (settlements.Count == 0)
            {
                return WaterLegacyRepairResult.Empty;
            }

            var waterLines = await LoadWaterLinesAsync(
                connection,
                householdId,
                periodKey,
                cancellationToken);

            if (waterLines.Count == 0)
            {
                return WaterLegacyRepairResult.Empty;
            }

            var changedLines = 0;
            var repairedInvoices = 0;

            foreach (var invoiceGroup in waterLines
                         .Where(x => x.Snapshot.UtilityInvoiceId != Guid.Empty)
                         .GroupBy(x => x.Snapshot.UtilityInvoiceId))
            {
                var sample = invoiceGroup
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .First();

                if (!await IsHouseholdInvoiceFullyPaidAsync(
                        connection,
                        householdId,
                        invoiceGroup.Key,
                        cancellationToken))
                {
                    // Zasada FEAT-04: woda trafia do lokatorów dopiero po
                    // opłaceniu 100% FV przez gospodarstwo.
                    continue;
                }

                var periodFrom = sample.Snapshot.PeriodFrom
                                 ?? ParsePeriodStart(periodKey);
                var periodTo = sample.Snapshot.PeriodTo
                               ?? ParsePeriodEnd(periodKey);

                var eligible = settlements
                    .Where(x =>
                        x.LeaseFrom <= periodTo
                        && (!x.LeaseTo.HasValue || x.LeaseTo.Value >= periodFrom))
                    .ToArray();

                if (eligible.Length == 0)
                {
                    continue;
                }

                var householdPersons = Math.Max(0, sample.Snapshot.HouseholdPersonCount);
                var tenantPersonsTotal = Math.Max(0, sample.Snapshot.TenantPersonCount);

                if (householdPersons <= 0 || tenantPersonsTotal <= 0)
                {
                    // Bez zapisanej migawki liczby osób nie zgadujemy podziału.
                    continue;
                }

                var personsByLease = new Dictionary<Guid, int>();

                foreach (var line in invoiceGroup)
                {
                    if (line.LeaseContractId != Guid.Empty
                        && line.Snapshot.TenantPersons > 0)
                    {
                        personsByLease[line.LeaseContractId] = line.Snapshot.TenantPersons;
                    }
                }

                var unknown = eligible
                    .Where(x => !personsByLease.ContainsKey(x.LeaseContractId))
                    .ToArray();

                var knownPersons = personsByLease
                    .Where(x => eligible.Any(e => e.LeaseContractId == x.Key))
                    .Sum(x => x.Value);

                var remainingPersons = tenantPersonsTotal - knownPersons;

                // FIX-05 naprawia bez zgadywania. Typowy stary przypadek:
                // była pozycja tylko dla jednego lokatora, ale migawka mówiła,
                // że łącznie lokatorów/osób było dwóch. Brakującemu kontraktowi
                // można wtedy jednoznacznie przypisać 1 osobę.
                if (unknown.Length > 0)
                {
                    if (remainingPersons != unknown.Length)
                    {
                        continue;
                    }

                    foreach (var item in unknown)
                    {
                        personsByLease[item.LeaseContractId] = 1;
                    }
                }
                else if (knownPersons != tenantPersonsTotal)
                {
                    continue;
                }

                var grossAmountMinor = sample.Snapshot.GrossAmountMinor;
                if (grossAmountMinor <= 0)
                {
                    continue;
                }

                var totalPersons = householdPersons + tenantPersonsTotal;
                if (totalPersons <= 0)
                {
                    continue;
                }

                var tenantShareMinor = checked(
                    grossAmountMinor * tenantPersonsTotal / totalPersons);

                var allocations = AllocateByPersons(
                    tenantShareMinor,
                    eligible,
                    personsByLease);

                var invoiceChanged = false;

                foreach (var settlement in eligible)
                {
                    if (!IsEditable(settlement.Status)
                        || !allocations.TryGetValue(
                            settlement.LeaseContractId,
                            out var amountMinor)
                        || amountMinor <= 0)
                    {
                        continue;
                    }

                    var existing = invoiceGroup
                        .Where(x => x.SettlementId == settlement.SettlementId)
                        .ToArray();

                    var alreadyCorrect = existing.Length == 1
                        && existing[0].AmountMinor == amountMinor
                        && string.Equals(
                            existing[0].Snapshot.AllocationMode,
                            "WaterByPersons",
                            StringComparison.OrdinalIgnoreCase)
                        && existing[0].SourceId == invoiceGroup.Key;

                    if (alreadyCorrect)
                    {
                        continue;
                    }

                    await DeleteWaterLinesAsync(
                        connection,
                        settlement.SettlementId,
                        invoiceGroup.Key,
                        cancellationToken);

                    var snapshot = JsonSerializer.Serialize(new
                    {
                        source = "WaterInvoiceAfterHouseholdPayment",
                        package = "PKG-015q-FEAT-04-FIX-05",
                        utilityInvoiceId = invoiceGroup.Key,
                        invoiceNo = sample.Snapshot.InvoiceNo,
                        medium = "Water",
                        periodFrom,
                        periodTo,
                        periodKey,
                        grossAmountMinor,
                        householdPaidFullInvoice = true,
                        householdPersonCount = householdPersons,
                        tenantPersonCount = tenantPersonsTotal,
                        tenantPersons = personsByLease[settlement.LeaseContractId],
                        tenantShareMinor,
                        tenantAmountMinor = amountMinor,
                        householdShareMinor = grossAmountMinor - tenantShareMinor,
                        allocationMode = "WaterByPersons",
                        migratedFromLegacyAllocation = true
                    });

                    await InsertWaterLineAsync(
                        connection,
                        settlement.SettlementId,
                        invoiceGroup.Key,
                        amountMinor,
                        settlement.CurrencyCode,
                        snapshot,
                        cancellationToken);

                    await RefreshSettlementTotalsAsync(
                        connection,
                        settlement.SettlementId,
                        cancellationToken);

                    changedLines++;
                    invoiceChanged = true;
                }

                if (invoiceChanged)
                {
                    repairedInvoices++;
                }
            }

            return new WaterLegacyRepairResult(
                repairedInvoices,
                changedLines);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<List<SettlementRow>> LoadSettlementsAsync(
        DbConnection connection,
        Guid householdId,
        string periodKey,
        CancellationToken ct)
    {
        var result = new List<SettlementRow>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts."Id", ts."LeaseContractId", ts."Status",
                   COALESCE(ts."CurrencyCode", lc."CurrencyCode", 'PLN'),
                   lc."LeaseFrom", lc."LeaseTo"
            FROM "TenantSettlements" ts
            JOIN "LeaseContracts" lc
              ON LOWER(lc."Id") = LOWER(ts."LeaseContractId")
            WHERE LOWER(ts."HouseholdId") = LOWER($householdId)
              AND ts."PeriodKey" = $periodKey;
            """;
        Add(cmd, "$householdId", householdId.ToString("D"));
        Add(cmd, "$periodKey", periodKey.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var settlementId)
                || !Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out var leaseId)
                || !DateOnly.TryParse(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var leaseFrom))
            {
                continue;
            }

            DateOnly? leaseTo = null;
            if (!reader.IsDBNull(5)
                && DateOnly.TryParse(Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLeaseTo))
            {
                leaseTo = parsedLeaseTo;
            }

            result.Add(new SettlementRow(
                settlementId,
                leaseId,
                Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "PLN",
                leaseFrom,
                leaseTo));
        }

        return result;
    }

    private static async Task<List<WaterLineRow>> LoadWaterLinesAsync(
        DbConnection connection,
        Guid householdId,
        string periodKey,
        CancellationToken ct)
    {
        var result = new List<WaterLineRow>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT l."Id", l."TenantSettlementId", ts."LeaseContractId",
                   l."AmountMinor", l."SourceId", l."CalculationSnapshotJson",
                   l."CreatedAtUtc"
            FROM "TenantSettlementLines" l
            JOIN "TenantSettlements" ts
              ON LOWER(ts."Id") = LOWER(l."TenantSettlementId")
            WHERE LOWER(ts."HouseholdId") = LOWER($householdId)
              AND ts."PeriodKey" = $periodKey
              AND LOWER(COALESCE(l."SourceType", '')) = 'waterinvoice';
            """;
        Add(cmd, "$householdId", householdId.ToString("D"));
        Add(cmd, "$periodKey", periodKey.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var id)
                || !Guid.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out var settlementId)
                || !Guid.TryParse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture), out var leaseId))
            {
                continue;
            }

            var json = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "{}";
            var snapshot = ParseSnapshot(json);
            if (snapshot.UtilityInvoiceId == Guid.Empty)
            {
                continue;
            }

            Guid? sourceId = null;
            if (!reader.IsDBNull(4)
                && Guid.TryParse(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture), out var parsedSourceId))
            {
                sourceId = parsedSourceId;
            }

            DateTime createdAtUtc = DateTime.MinValue;
            DateTime.TryParse(
                Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out createdAtUtc);

            result.Add(new WaterLineRow(
                id,
                settlementId,
                leaseId,
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                sourceId,
                snapshot,
                createdAtUtc));
        }

        return result;
    }

    private static WaterSnapshot ParseSnapshot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new WaterSnapshot(
                ReadGuid(root, "utilityInvoiceId"),
                ReadString(root, "invoiceNo"),
                ReadInt64(root, "grossAmountMinor"),
                ReadInt32(root, "householdPersonCount"),
                ReadInt32(root, "tenantPersonCount"),
                ReadInt32(root, "tenantPersons"),
                ReadString(root, "allocationMode"),
                ReadDateOnly(root, "periodFrom"),
                ReadDateOnly(root, "periodTo"));
        }
        catch
        {
            return WaterSnapshot.Empty;
        }
    }

    private static async Task<bool> IsHouseholdInvoiceFullyPaidAsync(
        DbConnection connection,
        Guid householdId,
        Guid utilityInvoiceId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT hi."GrossMinor",
                   COALESCE(SUM(p."AmountMinor"), 0)
            FROM "UtilityInvoices" ui
            JOIN "UtilityContracts" uc
              ON LOWER(uc."Id") = LOWER(ui."UtilityContractId")
            JOIN "HouseholdInvoices" hi
              ON LOWER(hi."Id") = LOWER(ui."HouseholdInvoiceId")
            LEFT JOIN "HouseholdInvoicePayments" p
              ON LOWER(p."HouseholdInvoiceId") = LOWER(hi."Id")
            WHERE LOWER(ui."Id") = LOWER($utilityInvoiceId)
              AND LOWER(ui."HouseholdId") = LOWER($householdId)
              AND uc."Medium" = 'Water'
            GROUP BY hi."Id", hi."GrossMinor"
            LIMIT 1;
            """;
        Add(cmd, "$utilityInvoiceId", utilityInvoiceId.ToString("D"));
        Add(cmd, "$householdId", householdId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return false;
        }

        var gross = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var paid = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        return gross > 0 && paid >= gross;
    }

    private static Dictionary<Guid, long> AllocateByPersons(
        long totalMinor,
        IReadOnlyList<SettlementRow> settlements,
        IReadOnlyDictionary<Guid, int> personsByLease)
    {
        var result = new Dictionary<Guid, long>();
        var ordered = settlements
            .OrderBy(x => x.LeaseContractId)
            .ToArray();

        var totalWeight = ordered.Sum(x => personsByLease[x.LeaseContractId]);
        if (totalMinor <= 0 || totalWeight <= 0)
        {
            return result;
        }

        long allocated = 0;
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            var amount = i == ordered.Length - 1
                ? totalMinor - allocated
                : checked((long)Math.Round(
                    totalMinor * personsByLease[item.LeaseContractId] / (decimal)totalWeight,
                    0,
                    MidpointRounding.AwayFromZero));

            amount = Math.Max(0, amount);
            result[item.LeaseContractId] = amount;
            allocated += amount;
        }

        return result;
    }

    private static async Task DeleteWaterLinesAsync(
        DbConnection connection,
        Guid settlementId,
        Guid utilityInvoiceId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM "TenantSettlementLines"
            WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
              AND LOWER(COALESCE("SourceType", '')) = 'waterinvoice'
              AND (
                    LOWER(COALESCE("SourceId", '')) = LOWER($utilityInvoiceId)
                 OR LOWER(COALESCE("CalculationSnapshotJson", '')) LIKE $invoiceToken
              );
            """;
        Add(cmd, "$settlementId", settlementId.ToString("D"));
        Add(cmd, "$utilityInvoiceId", utilityInvoiceId.ToString("D"));
        Add(cmd, "$invoiceToken", $"%{utilityInvoiceId:D}%".ToLowerInvariant());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertWaterLineAsync(
        DbConnection connection,
        Guid settlementId,
        Guid utilityInvoiceId,
        long amountMinor,
        string currency,
        string snapshot,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "TenantSettlementLines"
                ("Id", "TenantSettlementId", "LineType", "AmountMinor",
                 "CurrencyCode", "DueDateOverride", "SourceType", "SourceId",
                 "CalculationSnapshotJson", "Status", "IsSystemGenerated", "CreatedAtUtc")
            VALUES
                ($id, $settlementId, 'Adjustment', $amountMinor,
                 $currency, NULL, 'WaterInvoice', $sourceId,
                 $snapshot, 'Ready', 0, $createdAtUtc);
            """;
        Add(cmd, "$id", Guid.NewGuid().ToString("D"));
        Add(cmd, "$settlementId", settlementId.ToString("D"));
        Add(cmd, "$amountMinor", amountMinor);
        Add(cmd, "$currency", string.IsNullOrWhiteSpace(currency) ? "PLN" : currency);
        Add(cmd, "$sourceId", utilityInvoiceId.ToString("D"));
        Add(cmd, "$snapshot", snapshot);
        Add(cmd, "$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RefreshSettlementTotalsAsync(
        DbConnection connection,
        Guid settlementId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE "TenantSettlements"
            SET "CurrentPeriodMinor" = (
                    SELECT COALESCE(SUM(l."AmountMinor"), 0)
                    FROM "TenantSettlementLines" l
                    WHERE LOWER(l."TenantSettlementId") = LOWER($settlementId)
                ),
                "TotalDueMinor" = "PreviousBalanceMinor" + (
                    SELECT COALESCE(SUM(l."AmountMinor"), 0)
                    FROM "TenantSettlementLines" l
                    WHERE LOWER(l."TenantSettlementId") = LOWER($settlementId)
                )
            WHERE LOWER("Id") = LOWER($settlementId);
            """;
        Add(cmd, "$settlementId", settlementId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static bool IsEditable(string? status) => status is
        "Draft" or "AwaitingData" or "ReadyForApproval";

    private static DateOnly ParsePeriodStart(string periodKey) =>
        DateOnly.ParseExact(
            $"{periodKey.Trim()}-01",
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

    private static DateOnly ParsePeriodEnd(string periodKey) =>
        ParsePeriodStart(periodKey).AddMonths(1).AddDays(-1);

    private static Guid ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && Guid.TryParse(value.GetString(), out var result)
            ? result
            : Guid.Empty;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ToString()
            : "";

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : 0L;

    private static int ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static DateOnly? ReadDateOnly(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return DateOnly.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var result)
            ? result
            : null;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record SettlementRow(
        Guid SettlementId,
        Guid LeaseContractId,
        string Status,
        string CurrencyCode,
        DateOnly LeaseFrom,
        DateOnly? LeaseTo);

    private sealed record WaterLineRow(
        Guid Id,
        Guid SettlementId,
        Guid LeaseContractId,
        long AmountMinor,
        Guid? SourceId,
        WaterSnapshot Snapshot,
        DateTime CreatedAtUtc);

    private sealed record WaterSnapshot(
        Guid UtilityInvoiceId,
        string InvoiceNo,
        long GrossAmountMinor,
        int HouseholdPersonCount,
        int TenantPersonCount,
        int TenantPersons,
        string AllocationMode,
        DateOnly? PeriodFrom,
        DateOnly? PeriodTo)
    {
        public static WaterSnapshot Empty { get; } =
            new(Guid.Empty, "", 0, 0, 0, 0, "", null, null);
    }
}

public sealed record WaterLegacyRepairResult(
    int RepairedInvoices,
    int ChangedLines)
{
    public int AddedOrReplacedLines => ChangedLines;

    public static WaterLegacyRepairResult Empty { get; } = new(0, 0);
}
