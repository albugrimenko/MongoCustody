namespace MongoCustody.Core.Models;

/// <summary>
/// The terminal status of a backup or restore operation.
/// </summary>
public enum BackupStatus
{
    /// <summary>The operation has been accepted and is queued.</summary>
    Pending,

    /// <summary>The operation is actively running.</summary>
    Running,

    /// <summary>
    /// The operation completed successfully and a <c>manifest.json</c>
    /// was written to blob storage.
    /// </summary>
    Completed,

    /// <summary>
    /// The operation failed. No <c>manifest.json</c> was written.
    /// Subsequent dependent operations are blocked until the failure
    /// is resolved.
    /// </summary>
    Failed,

    /// <summary>
    /// The operation was skipped (e.g., checkpoint deferred because a
    /// source incremental is still running).
    /// </summary>
    Skipped
}
