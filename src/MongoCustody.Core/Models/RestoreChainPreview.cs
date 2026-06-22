namespace MongoCustody.Core.Models;

public sealed class RestoreChainPreview
{
    public string DatabaseName { get; init; } = string.Empty;
    public DateTimeOffset RequestedDate { get; init; }

    // The AnchorTs of the last entry in the chain — the actual restore point.
    public DateTimeOffset BestAvailableDate { get; init; }

    // Null when BestAvailableDate is within 1 day of RequestedDate.
    public string? GapWarning { get; init; }

    public IReadOnlyList<BackupRecord> Chain { get; init; } = [];
}
