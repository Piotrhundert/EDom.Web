namespace EDom.Web.Models;

public sealed record UtilityOperatorInvoiceSelectOption(
    Guid Id,
    string Label);

public sealed record UtilityOperatorInvoiceLineViewModel(
    string ComponentCode,
    string Label,
    string ZoneCode,
    decimal Quantity,
    string UnitCode,
    decimal RateNet,
    decimal AmountNet);

public sealed record UtilityOperatorInvoiceHistoryItem(
    Guid Id,
    Guid HouseholdId,
    Guid MeterId,
    string MeterName,
    Guid ContractId,
    string ContractLabel,
    string InvoiceNo,
    string TariffCode,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    string? MeterSerialNo,
    string? Ppe,
    decimal PreviousDay,
    decimal CurrentDay,
    decimal PreviousNight,
    decimal CurrentNight,
    decimal ConsumptionDay,
    decimal ConsumptionNight,
    decimal NetAmount,
    decimal VatRate,
    decimal VatAmount,
    decimal GrossAmount,
    string CurrencyCode,
    decimal? ExciseAmount,
    bool ReadingSubmitted,
    bool TariffSnapshotSaved,
    DateTime CreatedAtUtc,
    IReadOnlyList<UtilityOperatorInvoiceLineViewModel> Lines);

public sealed record UtilityOperatorInvoicePageViewModel(
    string HouseholdName,
    IReadOnlyList<UtilityOperatorInvoiceSelectOption> ElectricityMeters,
    IReadOnlyList<UtilityOperatorInvoiceSelectOption> ElectricityContracts,
    IReadOnlyList<UtilityOperatorInvoiceHistoryItem> History);
