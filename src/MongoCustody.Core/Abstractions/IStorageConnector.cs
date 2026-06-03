namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Abstracts all read and write operations against the backup artifact
/// store (Azure Blob Storage).
/// </summary>
public interface IStorageConnector
{
    /// <summary>
    /// Opens a write stream for a single chunk blob. The blob is not
    /// visible in storage until the stream is disposed (all blocks committed).
    /// The caller streams compressed BSON content into the returned stream.
    /// </summary>
    /// <param name="blobPath">
    /// Full blob path relative to the container root.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenWriteAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for an existing chunk blob.
    /// </summary>
    /// <param name="blobPath">
    /// Full blob path relative to the container root.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Writes the <c>manifest.json</c> content for a completed backup.
    /// Called as the final step of every successful backup seal.
    /// </summary>
    /// <param name="folderPath">
    /// Backup folder path relative to the container root.
    /// </param>
    /// <param name="manifestJson">Serialised JSON content.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteManifestAsync(
        string folderPath,
        string manifestJson,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the <c>manifest.json</c> for a backup folder.
    /// Returns <c>null</c> if the manifest does not exist (failed backup).
    /// </summary>
    /// <param name="folderPath">
    /// Backup folder path relative to the container root.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> ReadManifestAsync(
        string folderPath,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all backup folder paths directly under a given prefix.
    /// Returns paths in lexicographic order (which equals chronological
    /// order for ISO 8601-named incremental and checkpoint folders).
    /// </summary>
    /// <param name="prefix">
    /// Container-relative path prefix to enumerate.
    /// Example: <c>incremental/2025/Q1/{fullBackupId}/</c>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<string> ListFoldersAsync(
        string prefix,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if a blob at the specified path exists.
    /// </summary>
    Task<bool> BlobExistsAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Deletes a blob. Used only by the orphaned-chunk cleanup process.
    /// </summary>
    Task DeleteBlobAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Verifies the connector can reach Blob Storage.
    /// Returns <c>true</c> and an optional latency if reachable.
    /// </summary>
    Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}
