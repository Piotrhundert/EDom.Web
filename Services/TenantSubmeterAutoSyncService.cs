using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EDom.Application.Rental;
using EDom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EDom.Web.Services;

/// <summary>
/// PKG-015q-FEAT-04-FIX-03
/// Automatycznie przenosi zużycie prądu z podlicznika pokoju do projektu
/// miesięcznego rozliczenia właściwego lokatora.
/// </summary>
public sealed class TenantSubmeterAutoSyncService(
    EDomDbContext db,
    ITenantSettlementService settlementService)
{
    public async Task<SubmeterAutoSyncResult> SyncAsync(
        RentalActor actor,
        Guid settlementId,
        Guid leaseContractId,
        string periodKey,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(periodKey, out var periodStart, out var periodEnd))
        {
            return SubmeterAutoSyncResult.Empty;
        }

        var intervals = new List<Interval>();
        var hasRoomMeter = false;
        var hasReadingForLease = false;

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var lease = await LoadLeaseAsync(
                connection,
                actor.HouseholdId,
                settlementId,
                leaseContractId,
                cancellationToken);

            if (lease is null
                || !string.Equals(lease.Status, "Signed", StringComparison.OrdinalIgnoreCase))
            {
                return SubmeterAutoSyncResult.Empty;
            }

            var effectiveFrom = lease.LeaseFrom > periodStart ? lease.LeaseFrom : periodStart;
            var effectiveTo = lease.LeaseTo.HasValue && lease.LeaseTo.Value < periodEnd
                ? lease.LeaseTo.Value
                : periodEnd;

            if (effectiveFrom > effectiveTo)
            {
                return SubmeterAutoSyncResult.Empty;
            }

            var meters = await LoadMetersAsync(
                connection,
                actor.HouseholdId,
                lease.RoomId,
                cancellationToken);

            hasRoomMeter = meters.Count > 0;

            foreach (var meter in meters)
            {
                var readings = await LoadReadingsAsync(
                    connection,
                    meter.Id,
                    cancellationToken);

                hasReadingForLease |= readings.Any(x =>
                {
                    var date = DateOnly.FromDateTime(x.AtUtc.ToLocalTime());

                    // FIX-04: odczyt wykonany dzień przed startem najmu
                    // (typowy odczyt "na koniec poprzedniego dnia") może pełnić
                    // rolę stanu początkowego, ale sam nie tworzy kosztu.
                    var openingReadingDayBefore =
                        date < lease.LeaseFrom
                        && lease.LeaseFrom.DayNumber - date.DayNumber <= 1;

                    return (date >= lease.LeaseFrom && date <= effectiveTo)
                           || openingReadingDayBefore;
                });

                for (var i = 0; i < readings.Count; i++)
                {
                    var current = readings[i];
                    var currentDate = DateOnly.FromDateTime(current.AtUtc.ToLocalTime());

                    if (currentDate < effectiveFrom || currentDate > effectiveTo)
                    {
                        continue;
                    }

                    Reading? previous = null;
                    for (var j = i - 1; j >= 0; j--)
                    {
                        var candidate = readings[j];
                        var candidateDate = DateOnly.FromDateTime(candidate.AtUtc.ToLocalTime());

                        if (!string.Equals(
                                candidate.Zone,
                                current.Zone,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (candidateDate < lease.LeaseFrom)
                        {
                            // FIX-04: stan z dnia poprzedzającego start umowy
                            // traktujemy jako techniczny stan początkowy.
                            // Nie naliczamy wcześniejszego interwału.
                            if (lease.LeaseFrom.DayNumber - candidateDate.DayNumber <= 1)
                            {
                                previous = candidate;
                            }

                            break;
                        }

                        previous = candidate;
                        break;
                    }

                    // Odczyt początkowy z FIX-02 sam w sobie nie tworzy kosztu.
                    if (previous is null)
                    {
                        continue;
                    }

                    var consumption = current.Value - previous.Value;
                    if (consumption <= 0m)
                    {
                        continue;
                    }

                    var energyRate = await ResolveRateAsync(
                        connection,
                        meter.ParentId,
                        current.Unit,
                        current.Zone,
                        currentDate,
                        distribution: false,
                        cancellationToken);

                    var distributionRate = await ResolveRateAsync(
                        connection,
                        meter.ParentId,
                        current.Unit,
                        current.Zone,
                        currentDate,
                        distribution: true,
                        cancellationToken);

                    intervals.Add(new Interval(
                        meter,
                        previous,
                        current,
                        consumption,
                        lease.Currency,
                        energyRate,
                        distributionRate));
                }
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }

        // PKG-015q-FEAT-04-FIX-05
        // Usuń duplikaty powstałe przy przejściu ze starego SourceType
        // SubmeterElectricity na nowe SubmeterElectricityEnergy:<licznik>.
        // Operacja jest idempotentna i zachowuje nową, kanoniczną pozycję.
        await RemoveDuplicateEnergyLinesAsync(
            settlementId,
            cancellationToken);

        if (intervals.Count == 0)
        {
            return new SubmeterAutoSyncResult(
                0, 0, 0, false, false,
                hasRoomMeter && hasReadingForLease);
        }

        var addedEnergy = 0;
        var addedDistribution = 0;
        var missingEnergyRate = false;
        var missingDistributionRate = false;

        foreach (var interval in intervals)
        {
            if (!await LineExistsAsync(
                    settlementId,
                    interval.Current.Id,
                    distribution: false,
                    cancellationToken))
            {
                if (interval.EnergyRate is null)
                {
                    missingEnergyRate = true;
                }
                else
                {
                    var amountMinor = ToMinor(interval.Consumption * interval.EnergyRate.Value);
                    if (amountMinor > 0)
                    {
                        await AddAutomaticLineAsync(
                            settlementId,
                            interval.Current.Id,
                            "Electricity",
                            amountMinor,
                            interval.Currency,
                            $"SubmeterElectricityEnergy:{interval.Meter.Name}",
                            BuildSnapshot(interval, amountMinor, distribution: false),
                            cancellationToken);

                        addedEnergy++;
                    }
                }
            }

            if (!await LineExistsAsync(
                    settlementId,
                    interval.Current.Id,
                    distribution: true,
                    cancellationToken))
            {
                if (interval.DistributionRate is null)
                {
                    missingDistributionRate = true;
                }
                else
                {
                    var amountMinor = ToMinor(interval.Consumption * interval.DistributionRate.Value);
                    if (amountMinor > 0)
                    {
                        await AddAutomaticLineAsync(
                            settlementId,
                            interval.Current.Id,
                            "Adjustment",
                            amountMinor,
                            interval.Currency,
                            $"SubmeterElectricityDistribution:{interval.Meter.Name}",
                            BuildSnapshot(interval, amountMinor, distribution: true),
                            cancellationToken);

                        addedDistribution++;
                    }
                }
            }
        }

        await RemoveDuplicateEnergyLinesAsync(
            settlementId,
            cancellationToken);

        return new SubmeterAutoSyncResult(
            intervals.Count,
            addedEnergy,
            addedDistribution,
            missingEnergyRate,
            missingDistributionRate,
            false);
    }

    private async Task<int> RemoveDuplicateEnergyLinesAsync(
        Guid settlementId,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var rows = new List<EnergyLine>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT "Id", COALESCE("SourceType", ''),
                           COALESCE("CalculationSnapshotJson", '{}'),
                           COALESCE("CreatedAtUtc", '')
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND (
                           LOWER(COALESCE("SourceType", '')) = 'submeterelectricity'
                        OR LOWER(COALESCE("SourceType", '')) LIKE 'submeterelectricityenergy%'
                      );
                    """;
                Add(cmd, "$settlementId", settlementId.ToString("D"));

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!Guid.TryParse(
                            Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                            out var id))
                    {
                        continue;
                    }

                    var sourceType = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                    var snapshot = Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "{}";
                    var createdAt = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "";

                    if (!TryBuildEnergyIdentity(snapshot, out var identity))
                    {
                        continue;
                    }

                    rows.Add(new EnergyLine(
                        id,
                        sourceType,
                        identity,
                        createdAt));
                }
            }

            var toDelete = new List<Guid>();

            foreach (var group in rows.GroupBy(x => x.Identity, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() <= 1)
                {
                    continue;
                }

                var keep = group
                    .OrderByDescending(x =>
                        x.SourceType.StartsWith(
                            "SubmeterElectricityEnergy",
                            StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.CreatedAtUtc, StringComparer.OrdinalIgnoreCase)
                    .First();

                toDelete.AddRange(group
                    .Where(x => x.Id != keep.Id)
                    .Select(x => x.Id));
            }

            foreach (var id in toDelete.Distinct())
            {
                await using var delete = connection.CreateCommand();
                delete.CommandText = """
                    DELETE FROM "TenantSettlementLines"
                    WHERE LOWER("Id") = LOWER($id);
                    """;
                Add(delete, "$id", id.ToString("D"));
                await delete.ExecuteNonQueryAsync(ct);
            }

            return toDelete.Distinct().Count();
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static bool TryBuildEnergyIdentity(
        string snapshot,
        out string identity)
    {
        identity = "";

        try
        {
            using var document = JsonDocument.Parse(snapshot);
            var root = document.RootElement;

            var meterId = root.TryGetProperty("meterId", out var meter)
                ? meter.ToString()
                : "";
            var zone = root.TryGetProperty("zoneCode", out var zoneValue)
                ? zoneValue.ToString()
                : "ALL";
            var previousValue = root.TryGetProperty("previousValue", out var previous)
                ? previous.ToString()
                : "";
            var currentValue = root.TryGetProperty("currentValue", out var current)
                ? current.ToString()
                : "";
            var amount = root.TryGetProperty("amountMinor", out var amountValue)
                ? amountValue.ToString()
                : "";

            if (string.IsNullOrWhiteSpace(meterId)
                || string.IsNullOrWhiteSpace(previousValue)
                || string.IsNullOrWhiteSpace(currentValue))
            {
                return false;
            }

            identity = string.Join(
                "|",
                meterId.Trim().ToLowerInvariant(),
                zone.Trim().ToLowerInvariant(),
                previousValue.Trim(),
                currentValue.Trim(),
                amount.Trim());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSnapshot(Interval x, long amountMinor, bool distribution)
    {
        var rate = distribution ? x.DistributionRate! : x.EnergyRate!;
        var sourceType = distribution
            ? $"SubmeterElectricityDistribution:{x.Meter.Name}"
            : $"SubmeterElectricityEnergy:{x.Meter.Name}";

        return JsonSerializer.Serialize(new
        {
            source = "SubmeterReading",
            package = "PKG-015q-FEAT-04-FIX-04",
            automatic = true,
            sourceType,
            settlementLineType = distribution ? "Adjustment" : "Electricity",
            medium = "Electricity",
            component = distribution ? "DistributionVariable" : "Energy",
            displayLabel = distribution
                ? "Przesył / dystrybucja prądu"
                : $"Prąd — {x.Meter.Name}",
            meterId = x.Meter.Id,
            meterName = x.Meter.Name,
            parentMeterId = x.Meter.ParentId,
            previousReadingId = x.Previous.Id,
            currentReadingId = x.Current.Id,
            previousReadingAtUtc = x.Previous.AtUtc,
            currentReadingAtUtc = x.Current.AtUtc,
            zoneCode = x.Current.Zone,
            previousValue = x.Previous.Value,
            currentValue = x.Current.Value,
            consumption = x.Consumption,
            unitCode = x.Current.Unit,
            ratePerUnit = rate.Value,
            rateSource = rate.Source,
            amountMinor,
            currencyCode = x.Currency
        });
    }

    private static async Task<Lease?> LoadLeaseAsync(
        DbConnection connection,
        Guid householdId,
        Guid settlementId,
        Guid leaseContractId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT lc."RoomId", lc."LeaseFrom", lc."LeaseTo", lc."Status",
                   COALESCE(ts."CurrencyCode", lc."CurrencyCode", 'PLN')
            FROM "LeaseContracts" lc
            JOIN "TenantSettlements" ts
              ON LOWER(ts."Id") = LOWER($settlementId)
             AND LOWER(ts."LeaseContractId") = LOWER(lc."Id")
            WHERE LOWER(lc."Id") = LOWER($leaseContractId)
              AND LOWER(lc."HouseholdId") = LOWER($householdId)
            LIMIT 1;
            """;
        Add(cmd, "$settlementId", settlementId.ToString("D"));
        Add(cmd, "$leaseContractId", leaseContractId.ToString("D"));
        Add(cmd, "$householdId", householdId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || !Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var roomId)
            || !DateOnly.TryParse(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture), out var leaseFrom))
        {
            return null;
        }

        DateOnly? leaseTo = null;
        if (!reader.IsDBNull(2)
            && DateOnly.TryParse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture), out var parsedTo))
        {
            leaseTo = parsedTo;
        }

        return new Lease(
            roomId,
            leaseFrom,
            leaseTo,
            Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
            Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "PLN");
    }

    private static async Task<List<Meter>> LoadMetersAsync(
        DbConnection connection,
        Guid householdId,
        Guid roomId,
        CancellationToken ct)
    {
        var result = new List<Meter>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", COALESCE("Name", 'Podlicznik'), COALESCE("ParentMeterId", '')
            FROM "Meters"
            WHERE LOWER("HouseholdId") = LOWER($householdId)
              AND LOWER(COALESCE("LocationId", '')) = LOWER($roomId)
              AND "MeterType" = 'Sub'
              AND "Medium" = 'Electricity'
              AND "LocationType" = 'Room'
              AND "Status" = 'Active';
            """;
        Add(cmd, "$householdId", householdId.ToString("D"));
        Add(cmd, "$roomId", roomId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var id)
                && Guid.TryParse(Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture), out var parentId))
            {
                result.Add(new Meter(
                    id,
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "Podlicznik",
                    parentId));
            }
        }

        return result;
    }

    private static async Task<List<Reading>> LoadReadingsAsync(
        DbConnection connection,
        Guid meterId,
        CancellationToken ct)
    {
        var rows = new List<Reading>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT mr."Id", mr."ReadingAtUtc", COALESCE(rv."ZoneCode", 'ALL'),
                   rv."ValueScaled", rv."Scale", COALESCE(rv."UnitCode", 'kWh')
            FROM "MeterReadings" mr
            JOIN "MeterReadingValues" rv
              ON LOWER(rv."MeterReadingId") = LOWER(mr."Id")
            WHERE LOWER(mr."MeterId") = LOWER($meterId)
              AND mr."Status" = 'Approved'
            ORDER BY mr."ReadingAtUtc", mr."Id";
            """;
        Add(cmd, "$meterId", meterId.ToString("D"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var id)
                || !DateTime.TryParse(
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var atUtc))
            {
                continue;
            }

            var scaled = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            var scale = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
            var value = scaled / (decimal)Math.Pow(10, Math.Max(0, scale));

            rows.Add(new Reading(
                id,
                atUtc,
                Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "ALL",
                value,
                Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "kWh"));
        }

        // Preferuj ALL, tak samo jak ekran rozliczania podlicznika.
        return rows
            .GroupBy(x => x.Id)
            .Select(g => g.OrderBy(x =>
                string.Equals(x.Zone, "ALL", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .OrderBy(x => x.AtUtc)
            .ToList();
    }

    private static async Task<ResolvedRate?> ResolveRateAsync(
        DbConnection connection,
        Guid parentMeterId,
        string unit,
        string zone,
        DateOnly date,
        bool distribution,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr."ComponentCode", tr."ValueScaled", tr."Scale",
                   COALESCE(tr."ZoneCode", 'ALL'), COALESCE(tr."UnitCode", ''),
                   COALESCE(t."Name", 'Taryfa')
            FROM "UtilityContractMeters" ucm
            JOIN "UtilityContracts" uc ON LOWER(uc."Id") = LOWER(ucm."UtilityContractId")
            JOIN "Tariffs" t ON LOWER(t."UtilityContractId") = LOWER(uc."Id")
            JOIN "TariffRates" tr ON LOWER(tr."TariffId") = LOWER(t."Id")
            WHERE LOWER(ucm."MeterId") = LOWER($meterId)
              AND uc."Medium" = 'Electricity'
              AND uc."Status" = 'Active'
              AND t."Status" = 'Active'
              AND ucm."ValidFrom" <= $date AND (ucm."ValidTo" IS NULL OR ucm."ValidTo" >= $date)
              AND uc."ValidFrom" <= $date AND (uc."ValidTo" IS NULL OR uc."ValidTo" >= $date)
              AND t."ValidFrom" <= $date AND (t."ValidTo" IS NULL OR t."ValidTo" >= $date)
              AND tr."ValidFrom" <= $date AND (tr."ValidTo" IS NULL OR tr."ValidTo" >= $date);
            """;
        Add(cmd, "$meterId", parentMeterId.ToString("D"));
        Add(cmd, "$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var candidates = new List<(int Score, ResolvedRate Rate)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var component = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
            var isEnergy = component.Contains("Energy", StringComparison.OrdinalIgnoreCase)
                           || component.Contains("Consumption", StringComparison.OrdinalIgnoreCase);
            var isDistribution = component.Contains("NetworkVariable", StringComparison.OrdinalIgnoreCase)
                                 || component.Contains("DistributionVariable", StringComparison.OrdinalIgnoreCase)
                                 || component.Contains("TransmissionVariable", StringComparison.OrdinalIgnoreCase);

            if ((!distribution && !isEnergy) || (distribution && !isDistribution))
            {
                continue;
            }

            var candidateZone = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "ALL";
            var candidateUnit = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "";
            if ((!string.IsNullOrWhiteSpace(candidateUnit)
                 && !string.Equals(candidateUnit, unit, StringComparison.OrdinalIgnoreCase))
                || (!string.Equals(candidateZone, zone, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(candidateZone, "ALL", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var scaled = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var scale = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            var value = scaled / (decimal)Math.Pow(10, Math.Max(0, scale));
            if (value <= 0m)
            {
                continue;
            }

            var score = 100
                        + (string.Equals(candidateZone, zone, StringComparison.OrdinalIgnoreCase) ? 20 : 10)
                        + (string.Equals(candidateUnit, unit, StringComparison.OrdinalIgnoreCase) ? 20 : 0);
            var tariff = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "Taryfa";
            candidates.Add((score, new ResolvedRate(value, $"Taryfa licznika głównego · {tariff} · {component}")));
        }

        return candidates
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Rate.Value)
            .Select(x => x.Rate)
            .FirstOrDefault();
    }

    private async Task AddAutomaticLineAsync(
        Guid settlementId,
        Guid currentReadingId,
        string lineType,
        long amountMinor,
        string currency,
        string sourceType,
        string snapshot,
        CancellationToken ct)
    {
        // PKG-015q-FEAT-04-FIX-04
        // To jest pozycja wyliczona automatycznie z zatwierdzonych odczytów,
        // a nie pozycja ręczna użytkownika. Starsza walidacja AddManualLineAsync
        // odrzuca część technicznych typów używanych przez rozszerzenia PKG-015q.
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;

        if (closeWhenDone)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "TenantSettlementLines"
                    ("Id",
                     "TenantSettlementId",
                     "LineType",
                     "AmountMinor",
                     "CurrencyCode",
                     "DueDateOverride",
                     "SourceType",
                     "SourceId",
                     "CalculationSnapshotJson",
                     "Status",
                     "IsSystemGenerated",
                     "CreatedAtUtc")
                SELECT
                     $id,
                     $settlementId,
                     $lineType,
                     $amountMinor,
                     $currency,
                     NULL,
                     $sourceType,
                     $sourceId,
                     $snapshot,
                     'Ready',
                     0,
                     $createdAtUtc
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "TenantSettlementLines"
                    WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                      AND LOWER(COALESCE("SourceType", '')) = LOWER($sourceType)
                      AND LOWER(COALESCE("CalculationSnapshotJson", '')) LIKE $readingId
                );
                """;

            Add(cmd, "$id", Guid.NewGuid().ToString("D"));
            Add(cmd, "$settlementId", settlementId.ToString("D"));
            Add(cmd, "$lineType", lineType);
            Add(cmd, "$amountMinor", amountMinor);
            Add(cmd, "$currency", string.IsNullOrWhiteSpace(currency) ? "PLN" : currency);
            Add(cmd, "$sourceType", sourceType);
            Add(cmd, "$sourceId", currentReadingId.ToString("D"));
            Add(cmd, "$snapshot", snapshot);
            Add(cmd, "$createdAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Add(cmd, "$readingId", $"%{currentReadingId:D}%".ToLowerInvariant());

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<bool> LineExistsAsync(
        Guid settlementId,
        Guid currentReadingId,
        bool distribution,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = distribution
                ? """
                  SELECT COUNT(1) FROM "TenantSettlementLines"
                  WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                    AND LOWER(COALESCE("SourceType",'')) LIKE 'submeterelectricitydistribution%'
                    AND LOWER(COALESCE("CalculationSnapshotJson",'')) LIKE $readingId;
                  """
                : """
                  SELECT COUNT(1) FROM "TenantSettlementLines"
                  WHERE LOWER("TenantSettlementId") = LOWER($settlementId)
                    AND (LOWER(COALESCE("SourceType",'')) = 'submeterelectricity'
                         OR LOWER(COALESCE("SourceType",'')) LIKE 'submeterelectricityenergy%')
                    AND LOWER(COALESCE("CalculationSnapshotJson",'')) LIKE $readingId;
                  """;
            Add(cmd, "$settlementId", settlementId.ToString("D"));
            Add(cmd, "$readingId", $"%{currentReadingId:D}%".ToLowerInvariant());
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static bool TryParsePeriod(string? periodKey, out DateOnly start, out DateOnly end)
    {
        start = default;
        end = default;
        if (!DateOnly.TryParseExact(
                $"{periodKey?.Trim()}-01",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out start))
        {
            return false;
        }

        end = start.AddMonths(1).AddDays(-1);
        return true;
    }

    private static long ToMinor(decimal value) =>
        checked((long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero));

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record EnergyLine(
        Guid Id,
        string SourceType,
        string Identity,
        string CreatedAtUtc);

    private sealed record Lease(Guid RoomId, DateOnly LeaseFrom, DateOnly? LeaseTo, string Status, string Currency);
    private sealed record Meter(Guid Id, string Name, Guid ParentId);
    private sealed record Reading(Guid Id, DateTime AtUtc, string Zone, decimal Value, string Unit);
    private sealed record ResolvedRate(decimal Value, string Source);
    private sealed record Interval(Meter Meter, Reading Previous, Reading Current, decimal Consumption, string Currency, ResolvedRate? EnergyRate, ResolvedRate? DistributionRate);
}

public sealed record SubmeterAutoSyncResult(
    int ConsumptionIntervals,
    int AddedEnergyLines,
    int AddedDistributionLines,
    bool MissingEnergyRate,
    bool MissingDistributionRate,
    bool WaitingForNextReading)
{
    public int AddedLines => AddedEnergyLines + AddedDistributionLines;

    public static SubmeterAutoSyncResult Empty => new(0, 0, 0, false, false, false);
}
