using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace MongoCustody.Core.Models;

/// <summary>
/// Metadata for all chunks belonging to a single collection within
/// one backup operation.
/// </summary>
public sealed class CollectionBackupRecord
{
    /// <summary>Name of the MongoDB collection.</summary>
    [JsonPropertyName("collectionName")]
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Total number of documents across all chunks in this collection export.
    /// </summary>
    [JsonPropertyName("totalDocuments")]
    public long TotalDocuments { get; set; }

    /// <summary>Number of chunk blobs produced for this collection.</summary>
    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; set; }

    /// <summary>UTC time this collection's export completed.</summary>
    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; set; }

    /// <summary>
    /// Ordered list of chunk records for this collection.
    /// Must be applied in ascending <see cref="ChunkRecord.ChunkIndex"/>
    /// order during restore.
    /// </summary>
    [JsonPropertyName("chunks")]
    public List<ChunkRecord> Chunks { get; set; } = [];

    /// <summary>
    /// All user-defined indexes on this collection at the time of backup,
    /// excluding the auto-managed <c>_id</c> index.
    ///
    /// <para>
    /// Captured in every backup type (full, incremental, checkpoint) so
    /// the restore chain always reflects the latest index state. During
    /// restore the engine builds a merged map of the most-recent definition
    /// per collection across all chain steps.
    /// </para>
    ///
    /// <para>
    /// <see cref="IndexDefinition.IsUnique"/> drives restore sequencing:
    /// unique indexes are created before data is loaded (Cosmos DB
    /// requirement); non-unique indexes are created after all data is in
    /// place for maximum insert throughput.
    /// </para>
    /// </summary>
    [JsonPropertyName("indexes")]
    public List<IndexDefinition> Indexes { get; set; } = [];
}
