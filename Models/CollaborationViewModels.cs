using EDom.Domain.Collaboration;
using EDom.Domain.Households;

namespace EDom.Web.Models;

public sealed record DocumentsPageModel(IReadOnlyList<DocumentItem> Documents, bool CanCreateOwn, bool CanCreateShared);
public sealed record CalendarPageModel(IReadOnlyList<CalendarEvent> Events, IReadOnlyList<FamilyGroup> FamilyGroups, IReadOnlyList<Person> Children);
public sealed record NotificationsPageModel(IReadOnlyList<Notification> Notifications);
