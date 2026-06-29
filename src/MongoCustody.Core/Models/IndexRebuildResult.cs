namespace MongoCustody.Core.Models;

/// <summary>
/// Result of a secondary metadata index rebuild operation.
/// </summary>
public sealed class IndexRebuildResult
{
    /// <summary>Secondary index entries written (or that would be written in dry-run).</summary>
    public int EntriesAdded   { get; init; }

    /// <summary>Dangling index entries deleted (or that would be deleted in dry-run).</summary>
    public int OrphansRemoved { get; init; }

    /// <summary><c>true</c> when no changes were persisted.</summary>
    public bool WasDryRun     { get; init; }
}
