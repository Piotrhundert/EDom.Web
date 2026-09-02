using System.Text.Json;

namespace EDom.Web.Services;

public sealed class HouseholdInvoiceAssignmentStore
{
    private const string ActiveStatus = "Assigned";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public HouseholdInvoiceAssignmentStore(string contentRootPath)
    {
        var directory = Path.Combine(contentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "household-invoice-assignments.json");
    }

    public async Task<IReadOnlyList<HouseholdInvoiceAssignmentRecord>> GetForHouseholdAsync(
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all
                .Where(x => x.HouseholdId == householdId)
                .OrderByDescending(x => x.AssignedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<HouseholdInvoiceAssignmentRecord>> GetForPersonAsync(
        Guid householdId,
        Guid personId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all
                .Where(x => x.HouseholdId == householdId && x.AssigneePersonId == personId)
                .OrderByDescending(x => x.AssignedAtUtc)
                .ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<HouseholdInvoiceAssignmentRecord?> GetAsync(
        Guid assignmentId,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            return all.FirstOrDefault(x => x.Id == assignmentId && x.HouseholdId == householdId);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<HouseholdInvoiceAssignmentRecord> AssignAsync(
        Guid householdId,
        Guid invoiceId,
        string invoiceNo,
        string supplier,
        long amountMinor,
        string currencyCode,
        DateOnly dueDate,
        Guid assigneePersonId,
        string assigneeName,
        Guid assignedByUserAccountId,
        string? note,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            var now = DateTime.UtcNow;

            foreach (var previous in all.Where(x =>
                         x.HouseholdId == householdId &&
                         x.InvoiceId == invoiceId &&
                         x.Status == ActiveStatus))
            {
                previous.Status = "Reassigned";
                previous.ClosedAtUtc = now;
            }

            var item = new HouseholdInvoiceAssignmentRecord
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                InvoiceId = invoiceId,
                InvoiceNo = invoiceNo,
                Supplier = supplier,
                AmountMinor = amountMinor,
                CurrencyCode = currencyCode,
                DueDate = dueDate,
                AssigneePersonId = assigneePersonId,
                AssigneeName = assigneeName,
                AssignedByUserAccountId = assignedByUserAccountId,
                AssignedAtUtc = now,
                Status = ActiveStatus,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
            };

            all.Add(item);
            await SaveUnsafeAsync(all, cancellationToken);
            return item;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> MarkSubmittedAsync(
        Guid assignmentId,
        Guid householdId,
        Guid personId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            var item = all.FirstOrDefault(x =>
                x.Id == assignmentId &&
                x.HouseholdId == householdId &&
                x.AssigneePersonId == personId);

            if (item is null || item.Status != ActiveStatus)
            {
                return false;
            }

            item.Status = "Submitted";
            item.SubmittedAtUtc = DateTime.UtcNow;
            await SaveUnsafeAsync(all, cancellationToken);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> CancelAsync(
        Guid assignmentId,
        Guid householdId,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await LoadUnsafeAsync(cancellationToken);
            var item = all.FirstOrDefault(x => x.Id == assignmentId && x.HouseholdId == householdId);
            if (item is null || item.Status != ActiveStatus)
            {
                return false;
            }

            item.Status = "Cancelled";
            item.ClosedAtUtc = DateTime.UtcNow;
            await SaveUnsafeAsync(all, cancellationToken);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<List<HouseholdInvoiceAssignmentRecord>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<HouseholdInvoiceAssignmentRecord>>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveUnsafeAsync(
        List<HouseholdInvoiceAssignmentRecord> items,
        CancellationToken cancellationToken)
    {
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _filePath, true);
    }
}

public sealed class HouseholdInvoiceAssignmentRecord
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid InvoiceId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public string Supplier { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "PLN";
    public DateOnly DueDate { get; set; }
    public Guid AssigneePersonId { get; set; }
    public string AssigneeName { get; set; } = string.Empty;
    public Guid AssignedByUserAccountId { get; set; }
    public DateTime AssignedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string Status { get; set; } = "Assigned";
    public string? Note { get; set; }
}
