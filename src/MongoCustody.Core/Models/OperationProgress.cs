namespace MongoCustody.Core.Models;

/// <summary>
/// Progress snapshot reported after each collection completes during a backup
/// or restore operation. Passed via <see cref="IProgress{T}"/> so callers can
/// render a live progress indicator without polling.
/// </summary>
public sealed class OperationProgress
{
    /// <summary>Number of collections fully processed so far.</summary>
    public int Completed { get; init; }

    /// <summary>Total number of collections to process in this operation.</summary>
    public int Total { get; init; }

    /// <summary>Name of the collection that just completed.</summary>
    public string CurrentItem { get; init; } = string.Empty;

    /// <summary>Completion percentage in the range [0, 100].</summary>
    public double Percentage => Total > 0 ? (double)Completed / Total * 100.0 : 0.0;
}
