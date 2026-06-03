using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// Metadata for a single chunk blob within a collection backup.
/// A collection is split into one or more sequentially numbered chunks
/// at document boundaries when its size exceeds the configured threshold.
/// </summary>
public sealed class ChunkRecord
{
    /// <summary>
    /// 1-based sequential index of this chunk within the collection export.
    /// Chunks must be applied in ascending order during restore.
    /// </summary>
    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    /// <summary>
    /// Full blob path relative to the storage container root.
    /// Example: <c>full/2025/Q1/{backupId}/{db}.{collection}.part-001.bson.gz</c>
    /// </summary>
    [JsonPropertyName("blobPath")]
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// Number of documents in this chunk.
    /// </summary>
    [JsonPropertyName("documentCount")]
    public long DocumentCount { get; set; }

    /// <summary>
    /// SHA-256 hash of the uncompressed chunk content, computed in a
    /// single streaming pass concurrent with the upload.
    /// Used by Level 1 (Structural Integrity) validation.
    /// </summary>
    [JsonPropertyName("sha256Hash")]
    public string Sha256Hash { get; set; } = string.Empty;

    /// <summary>
    /// Compressed size of the blob in bytes as stored in Blob Storage.
    /// </summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}
