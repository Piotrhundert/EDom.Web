namespace EDom.Web.Models;

public sealed record RentalAnnexUiRecord(
    Guid Id,
    Guid HouseholdId,
    Guid ContractId,
    int AnnexNumber,
    DateOnly EffectiveOn,
    string TenantName,
    string RoomName,
    string CurrencyCode,
    long OldRentAmountMinor,
    long? NewRentAmountMinor,
    DateOnly? OldLeaseTo,
    DateOnly? NewLeaseTo,
    string? ClauseTitle,
    string? ClauseText,
    string? Reason,
    DateTime CreatedAtUtc,
    Guid CreatedByUserAccountId);

public sealed record RentalAnnexPreviewViewModel(
    RentalAnnexUiRecord Annex,
    string HouseholdName);
