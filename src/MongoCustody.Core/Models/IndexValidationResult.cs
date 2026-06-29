namespace MongoCustody.Core.Models;

/// <summary>
/// Result of a secondary metadata index validation pass.
/// </summary>
public sealed class IndexValidationResult
{
    /// <summary>Total secondary index entries examined.</summary>
    public int TotalIndexEntries  { get; init; }

    /// <summary>Index entries whose manifest blob is missing.</summary>
    public int DanglingEntries    { get; init; }

    /// <summary>Manifests that have no corresponding secondary index entry.</summary>
    public int UnindexedManifests { get; init; }

    /// <summary><c>true</c> when the index is fully consistent.</summary>
    public bool IsHealthy => DanglingEntries == 0 && UnindexedManifests == 0;

    /// <summary>Human-readable description of each inconsistency found.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}
