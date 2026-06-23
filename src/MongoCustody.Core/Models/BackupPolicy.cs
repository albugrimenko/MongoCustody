namespace MongoCustody.Core.Models;

/// <summary>
/// Operational policy settings that control how backups are generated
/// and stored. All values have safe production-ready defaults.
/// </summary>
public sealed class BackupPolicy
{
    /// <summary>
    /// Number of seconds subtracted from the previous anchor timestamp
    /// when building the incremental query predicate
    /// (<c>_ts &gt; previousAnchorTs - IncrementalBufferSeconds</c>).
    /// The conservative overlap ensures no document near the boundary
    /// is missed due to clock drift. Duplicate documents are handled
    /// safely by upsert during restore.
    /// Default: 300 (5 minutes).
    /// </summary>
    public int IncrementalBufferSeconds { get; set; } = 300;

    /// <summary>
    /// Serialisation format for backup chunk blobs.
    /// Default: <see cref="BackupFormat.Bson"/>.
    /// </summary>
    public BackupFormat BackupFormat { get; set; } = BackupFormat.Bson;

    /// <summary>
    /// Gzip compression level applied to every chunk blob.
    /// Default: <see cref="CompressionLevel.Fastest"/>.
    /// </summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// Maximum number of collections exported or restored in parallel
    /// within a single backup or restore operation.
    /// Default: 4.
    /// </summary>
    public int MaxParallelCollections { get; set; } = 4;

    /// <summary>
    /// Size in megabytes of each block used when uploading a chunk blob
    /// to Azure Blob Storage via the block blob API.
    /// Reduces the number of block API calls for large chunks and avoids
    /// the 5 GB single-upload limit.
    /// Default: 100 MB.
    /// </summary>
    public int BlobBlockSizeMb { get; set; } = 100;

    /// <summary>
    /// Maximum number of incremental backups allowed between a full backup
    /// (or checkpoint) and the next full backup. When the chain reaches this
    /// depth a warning is emitted but the incremental proceeds — the caller
    /// (scheduler) is responsible for triggering a full backup at the
    /// appropriate cadence.
    /// Default: 19.
    /// </summary>
    public int MaxIncrementalChainDepth { get; set; } = 19;

    /// <summary>
    /// Maximum uncompressed size in gigabytes of a single chunk file.
    /// A new chunk is started after each complete document write when
    /// the accumulated uncompressed byte count meets or exceeds this threshold.
    /// The threshold is always checked after a complete document — never
    /// mid-document.
    /// Default: 10 GB.
    /// </summary>
    public int ChunkSizeGb { get; set; } = 10;
}
