using EDom.Application.Utilities;
using EDom.Domain.Property;

namespace EDom.Web.Models;

public sealed record UtilitiesPageViewModel(
    UtilityOverview Overview,
    IReadOnlyList<Parcel> Parcels,
    IReadOnlyList<Building> Buildings,
    IReadOnlyList<Room> Rooms,
    bool CanManage);
