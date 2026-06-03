namespace MongoCustody.Core.Models;

/// <summary>
/// The type of a backup operation.
/// </summary>
public enum BackupType
{
    /// <summary>
    /// Complete export of all collections. Starts a new incremental chain.
    /// Runs quarterly (Jan 1, Apr 1, Jul 1, Oct 1).
    /// </summary>
    Full,

    /// <summary>
    /// Captures all documents changed since the previous backup using the
    /// server-managed <c>_ts</c> field. Runs daily.
    /// </summary>
    Incremental,

    /// <summary>
    /// Deduplicated merge of the preceding six daily incrementals.
    /// Bounds restore complexity to a maximum chain depth of 19. Runs weekly.
    /// </summary>
    Checkpoint
}
