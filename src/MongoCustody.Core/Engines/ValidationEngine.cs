using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the backup validation pipeline as described in
/// "Backup and recovery plan.md" § 8.
///
/// <para>
/// Supported validation levels (executed in order, early-exit on failure per collection):
/// <list type="number">
///   <item><see cref="ValidationLevel.StructuralIntegrity"/> — re-reads each chunk
///         blob, decompresses it, recomputes SHA-256 over the raw BSON stream,
///         and compares the digest to the hash recorded in the manifest.</item>
///   <item><see cref="ValidationLevel.CountConsistency"/> — applies to full backups
///         only. Queries the live database for a soft-delete-aware document count
///         as of the backup's <c>anchorTs</c> and compares it to the manifest's
///         <see cref="CollectionBackupRecord.TotalDocuments"/> within a configurable
///         tolerance (exact for ≤ 10 000 docs; ±0.01 % for larger collections).</item>
///   <item><see cref="ValidationLevel.IncrementalCompleteness"/> — applies to
///         incremental backups only. Verifies the backed-up document count is ≥ the
///         count of live documents modified since the previous anchor.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ValidationEngine
{
    private readonly IBackupSourceConnector    _source;
    private readonly IStorageConnector         _storage;
    private readonly IMetadataRepository       _metadata;
    private readonly MongoCustodyConfiguration _config;

    // Tolerance for Level 2 count comparison on large collections (0.01 %)
    private const double CountToleranceFraction = 0.0001;

    // Collections with ≤ this number of documents are compared exactly.
    private const long ExactCountThreshold = 10_000;

    // Buffer size used when streaming chunks through SHA-256 during L1.
    private const int ReadBufferSize = 81_920;

    internal ValidationEngine(
        IBackupSourceConnector    source,
        IStorageConnector         storage,
        IMetadataRepository       metadata,
        MongoCustodyConfiguration config)
    {
        _source   = source;
        _storage  = storage;
        _metadata = metadata;
        _config   = config;
    }

    /// <summary>
    /// Validates the backup identified by <see cref="ValidationRequest.BackupId"/>
    /// at the levels specified in <see cref="ValidationRequest.Levels"/>.
    /// </summary>
    internal async Task<ValidationResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken ct)
    {
        var record = await _metadata
            .GetBackupRecordAsync(request.BackupId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return new ValidationResult
            {
                BackupId          = request.BackupId,
                LevelsExecuted    = ValidationLevel.None,
                CompletedAtUtc    = DateTimeOffset.UtcNow,
                CollectionResults = []
                // BackupType left at default (Full); Passed=false because Count=0 and type=Full
            };
        }

        return await ValidateRecordAsync(record, request.Levels, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a pre-resolved <paramref name="record"/> without a metadata
    /// lookup. Used by the chain validation path to avoid reading each record twice.
    /// </summary>
    internal Task<ValidationResult> ValidateAsync(
        BackupRecord      record,
        ValidationLevel   levels,
        CancellationToken ct)
        => ValidateRecordAsync(record, levels, ct);

    private async Task<ValidationResult> ValidateRecordAsync(
        BackupRecord      record,
        ValidationLevel   levels,
        CancellationToken ct)
    {
        // Manifest coherence check — incremental records only.
        if (record.BackupType == BackupType.Incremental)
        {
            var (coherent, detail) = await CheckManifestCoherenceAsync(record, ct)
                .ConfigureAwait(false);

            if (!coherent)
                return new ValidationResult
                {
                    BackupId                = record.BackupId,
                    BackupType              = record.BackupType,
                    LevelsExecuted          = levels,
                    CompletedAtUtc          = DateTimeOffset.UtcNow,
                    ManifestCoherenceDetail = detail,
                    CollectionResults       = []
                };
        }

        var collectionResults = new List<CollectionValidationResult>(record.Collections.Count);

        foreach (var collRecord in record.Collections)
        {
            var result = await ValidateCollectionAsync(
                record, collRecord, levels, ct)
                .ConfigureAwait(false);
            collectionResults.Add(result);
        }

        return new ValidationResult
        {
            BackupId          = record.BackupId,
            BackupType        = record.BackupType,
            LevelsExecuted    = levels,
            CompletedAtUtc    = DateTimeOffset.UtcNow,
            CollectionResults = collectionResults
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-collection dispatch
    // ------------------------------------------------------------------ //

    private async Task<CollectionValidationResult> ValidateCollectionAsync(
        BackupRecord             record,
        CollectionBackupRecord   collRecord,
        ValidationLevel          levels,
        CancellationToken        ct)
    {
        var result = new CollectionValidationResult
        {
            CollectionName        = collRecord.CollectionName,
            ManifestDocumentCount = collRecord.TotalDocuments,
            Passed                = true
        };

        // Level 1 — Structural Integrity
        if ((levels & ValidationLevel.StructuralIntegrity) != 0)
        {
            var (l1Passed, l1Detail) = await ValidateStructuralIntegrityAsync(
                collRecord, ct).ConfigureAwait(false);

            if (!l1Passed)
            {
                result.Passed        = false;
                result.FailedLevels |= ValidationLevel.StructuralIntegrity;
                result.FailureDetail = l1Detail;
                return result; // no point running further levels
            }
        }

        // Level 2 — Count Consistency (full backups only)
        if ((levels & ValidationLevel.CountConsistency) != 0
            && record.BackupType == BackupType.Full)
        {
            var (l2Passed, liveCount, l2Detail) = await ValidateCountConsistencyAsync(
                record.DatabaseName, collRecord, record.AnchorTs, ct)
                .ConfigureAwait(false);

            result.LiveDocumentCount = liveCount;

            if (!l2Passed)
            {
                result.Passed        = false;
                result.FailedLevels |= ValidationLevel.CountConsistency;
                result.FailureDetail ??= l2Detail;
                return result;
            }
        }

        // Level 3 — Incremental Completeness (incremental backups only)
        if ((levels & ValidationLevel.IncrementalCompleteness) != 0
            && record.BackupType == BackupType.Incremental)
        {
            if (record.QueryFromTs is null)
            {
                result.Passed        = false;
                result.FailedLevels |= ValidationLevel.IncrementalCompleteness;
                result.FailureDetail = "Cannot validate Level 3: QueryFromTs is absent from the manifest.";
                return result;
            }

            var (l3Passed, liveCount, l3Detail) = await ValidateIncrementalCompletenessAsync(
                record.DatabaseName, collRecord, record.QueryFromTs.Value, ct)
                .ConfigureAwait(false);

            result.LiveDocumentCount = liveCount;

            if (!l3Passed)
            {
                result.Passed        = false;
                result.FailedLevels |= ValidationLevel.IncrementalCompleteness;
                result.FailureDetail ??= l3Detail;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ //
    //  Level 1 — Structural Integrity
    // ------------------------------------------------------------------ //

    private async Task<(bool Passed, string Detail)> ValidateStructuralIntegrityAsync(
        CollectionBackupRecord collRecord,
        CancellationToken      ct)
    {
        if (collRecord.Chunks.Count == 0 && collRecord.TotalDocuments > 0)
            return (false,
                $"Manifest records {collRecord.TotalDocuments} document(s) but no chunks were written.");

        foreach (var chunk in collRecord.Chunks)
        {
            string actualHash;
            try
            {
                actualHash = await ComputeChunkHashAsync(chunk.BlobPath, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false,
                    $"Chunk {chunk.ChunkIndex} ({chunk.BlobPath}): " +
                    $"failed to read blob — {ex.Message}");
            }

            if (!string.Equals(actualHash, chunk.Sha256Hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    $"Chunk {chunk.ChunkIndex} ({chunk.BlobPath}): " +
                    $"hash mismatch — expected {chunk.Sha256Hash}, got {actualHash}");
            }
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// Streams the gzip-compressed chunk blob, decompresses it, and returns
    /// the lowercase hex SHA-256 of the raw BSON content.
    ///
    /// This mirrors the write path in <see cref="ChunkWriter"/> where the hash
    /// is computed over the uncompressed bytes before compression.
    /// </summary>
    private async Task<string> ComputeChunkHashAsync(
        string blobPath, CancellationToken ct)
    {
        await using var blobStream = await _storage
            .OpenReadAsync(blobPath, ct)
            .ConfigureAwait(false);

        await using var gzip = new GZipStream(blobStream, CompressionMode.Decompress);

        using var sha256 = SHA256.Create();
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            int read;
            while ((read = await gzip
                       .ReadAsync(buffer.AsMemory(0, ReadBufferSize), ct)
                       .ConfigureAwait(false)) > 0)
            {
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock([], 0, 0);
            return Convert.ToHexStringLower(sha256.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------------------------------------------------ //
    //  Level 2 — Count Consistency (full backups)
    // ------------------------------------------------------------------ //

    private async Task<(bool Passed, long LiveCount, string Detail)>
        ValidateCountConsistencyAsync(
            string                 databaseName,
            CollectionBackupRecord collRecord,
            long                   anchorTs,
            CancellationToken      ct)
    {
        long liveCount;
        try
        {
            liveCount = await _source
                .CountDocumentsExistingAtAsync(
                    databaseName, collRecord.CollectionName, anchorTs, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0, $"Failed to query live document count: {ex.Message}");
        }

        long manifestCount = collRecord.TotalDocuments;

        if (manifestCount <= ExactCountThreshold)
        {
            bool exact = liveCount == manifestCount;
            return (exact, liveCount, exact ? string.Empty
                : $"Exact count mismatch: manifest={manifestCount}, live={liveCount}");
        }

        // For large collections allow ±0.01 % drift (concurrent writes, etc.)
        long tolerance = (long)Math.Ceiling(manifestCount * CountToleranceFraction);
        long diff = Math.Abs(liveCount - manifestCount);
        bool passed = diff <= tolerance;

        return (passed, liveCount, passed ? string.Empty
            : $"Count mismatch exceeds tolerance: manifest={manifestCount}, " +
              $"live={liveCount}, diff={diff}, tolerance={tolerance}");
    }

    // ------------------------------------------------------------------ //
    //  Manifest Coherence — incremental records only
    // ------------------------------------------------------------------ //

    private async Task<(bool Passed, string Detail)> CheckManifestCoherenceAsync(
        BackupRecord      record,
        CancellationToken ct)
    {
        if (record.QueryFromTs is null)
            return (false,
                "Incremental manifest is missing QueryFromTs — the backup record is incomplete.");

        if (string.IsNullOrWhiteSpace(record.ParentFolder))
            return (false,
                "Incremental manifest is missing ParentFolder — the chain link is broken.");

        if (record.AnchorTs <= record.QueryFromTs.Value)
            return (false,
                $"Manifest timestamps are invalid: AnchorTs ({record.AnchorTs}) " +
                $"must be greater than QueryFromTs ({record.QueryFromTs.Value}).");

        var parentRecord = await _metadata
            .GetBackupRecordByFolderAsync(record.ParentFolder, ct)
            .ConfigureAwait(false);

        if (parentRecord is null)
            return (false,
                $"Parent backup '{record.ParentFolder}' has no manifest — " +
                "the restore chain may be broken or the parent backup failed.");

        return (true, string.Empty);
    }

    // ------------------------------------------------------------------ //
    //  Level 3 — Incremental Completeness
    // ------------------------------------------------------------------ //

    private async Task<(bool Passed, long LiveCount, string Detail)>
        ValidateIncrementalCompletenessAsync(
            string                 databaseName,
            CollectionBackupRecord collRecord,
            long                   queryFromTs,
            CancellationToken      ct)
    {
        long liveChangedCount;
        try
        {
            liveChangedCount = await _source
                .CountDocumentsAsOfAsync(
                    databaseName, collRecord.CollectionName, queryFromTs, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0, $"Failed to query live changed count: {ex.Message}");
        }

        long manifestCount = collRecord.TotalDocuments;
        bool passed = manifestCount >= liveChangedCount;

        return (passed, liveChangedCount, passed ? string.Empty
            : $"Incremental count insufficient: " +
              $"manifest={manifestCount}, live changed={liveChangedCount}");
    }

}
