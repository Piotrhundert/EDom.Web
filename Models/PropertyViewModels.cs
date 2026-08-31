using System.ComponentModel.DataAnnotations;

namespace EDom.Web.Models;

public sealed class CreateParcelViewModel
{
    [Required] public string Name { get; set; } = "";
    public string? AddressText { get; set; }
    public string? RegistryNo { get; set; }
    public decimal? Area { get; set; }
    public string? OwnershipType { get; set; }
    [DataType(DataType.Date)] public DateOnly? AcquiredOn { get; set; }
}

public sealed class CreateBuildingViewModel
{
    public Guid ParcelId { get; set; }
    [Required] public string Name { get; set; } = "";
    public string BuildingType { get; set; } = "Residential";
    public decimal? UsableArea { get; set; }
    public int? Floors { get; set; }
    public int? BuildYear { get; set; }
    public string FunctionType { get; set; } = "FamilyHome";
}

public sealed class CreateRoomViewModel
{
    public Guid BuildingId { get; set; }
    [Required] public string Name { get; set; } = "";
    public string RoomType { get; set; } = "Room";
    public decimal? Area { get; set; }
    public int? FloorNo { get; set; }
    public bool IsRentable { get; set; }
    public bool IsCommonArea { get; set; }
    public int Capacity { get; set; } = 1;
}

public sealed class ChangePropertyStatusViewModel
{
    public Guid Id { get; set; }
    public string ObjectType { get; set; } = "Room";
    [Required] public string NewStatus { get; set; } = "Active";
    [Required] public string Reason { get; set; } = "";
}

public sealed class CreateAssetViewModel
{
    [Required] public string Name { get; set; } = "";
    public string CategoryCode { get; set; } = "Other";
    public string OwnershipType { get; set; } = "Household";
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNo { get; set; }
    public Guid? RoomId { get; set; }
}

public sealed class AssignAssetViewModel
{
    public Guid AssetId { get; set; }
    public Guid RoomId { get; set; }
    [DataType(DataType.Date)] public DateOnly ValidFrom { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? ConditionAtStart { get; set; }
}

public sealed class WithdrawAssetViewModel
{
    public Guid AssetId { get; set; }
    [DataType(DataType.Date)] public DateOnly EndedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string? ConditionAtEnd { get; set; }
}

public sealed class CreateMeterViewModel
{
    [Required] public string Name { get; set; } = "";
    public string Medium { get; set; } = "Electricity";
    public string MeterType { get; set; } = "Main";
    public string UnitCode { get; set; } = "kWh";
    public string LocationType { get; set; } = "Building";
    public Guid? LocationId { get; set; }
    public Guid? ParentMeterId { get; set; }
    public string? SerialNo { get; set; }
    [DataType(DataType.Date)] public DateOnly? InstalledOn { get; set; }
}

public sealed class UpdatePropertyRecordViewModel
{
    public string RecordType { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SecondaryText { get; set; }
    public decimal? Area { get; set; }
    public int? NumberValue { get; set; }
}
