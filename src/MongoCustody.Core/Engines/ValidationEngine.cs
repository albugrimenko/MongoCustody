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
///   <item><see cref="ValidationLevel.RestoreDryRun"/> — restores the backup to the
///         ephemeral target specified in
///         <see cref="ValidationRequest.DryRunTargetConnectionString"/> and
///         compares per-collection document counts against the manifest.
///         Requires <see cref="IRestoreTargetConnectorFactory"/> to be wired in.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ValidationEngine
{
    private readonly IBackupSourceConnector        _source;
    private readonly IStorageConnector             _storage;
    private readonly IMetadataRepository           _metadata;
    private readonly MongoCustodyConfiguration     _config;
    private readonly IRestoreTargetConnectorFactory? _restoreTargetFactory;

    // Tolerance for Level 2 count comparison on large collections (0.01 %)
    private const double CountToleranceFraction = 0.0001;

    // Collections with ≤ this number of documents are compared exactly.
    private const long ExactCountThreshold = 10_000;

    // Buffer size used when streaming chunks through SHA-256 during L1.
    private const int ReadBufferSize = 81_920;

    internal ValidationEngine(
        IBackupSourceConnector          source,
        IStorageConnector               storage,
        IMetadataRepository             metadata,
        MongoCustodyConfiguration       config,
        IRestoreTargetConnectorFactory? restoreTargetFactory = null)
    {
        _source               = source;
        _storage              = storage;
        _metadata             = metadata;
        _config               = config;
        _restoreTargetFactory = restoreTargetFactory;
    }

    /// <summary>
    /// Validates the backup identified by <see cref="ValidationRequest.BackupId"/>
    /// at the levels specified in <see cref="ValidationRequest.Levels"/>.
    /// </summary>
    internal async Task<ValidationResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken ct)
    {
        // Guard: L4 requires the factory to be wired.
        if ((request.Levels & ValidationLevel.RestoreDryRun) != 0
            && _restoreTargetFactory is null)
        {
            throw new NotImplementedException(
                "Level 4 (Restore Dry-Run) requires IRestoreTargetConnectorFactory to be " +
                "registered. Ensure MongoCustody.Connectors is wired into the DI container.");
        }

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
            };
        }

        // Phase A — per-collection L1 / L2 / L3
        var collectionResults = new List<CollectionValidationResult>(record.Collections.Count);
        var nonDryRunLevels   = request.Levels & ~ValidationLevel.RestoreDryRun;

        if (nonDryRunLevels != ValidationLevel.None)
        {
            foreach (var collRecord in record.Collections)
            {
                var result = await ValidateCollectionAsync(
                    record, collRecord, nonDryRunLevels, ct)
                    .ConfigureAwait(false);
                collectionResults.Add(result);
            }
        }

        // Phase B — L4 restore dry-run (runs after L1/L2/L3 so hash failures
        // are visible before wasting a full restore)
        if ((request.Levels & ValidationLevel.RestoreDryRun) != 0)
        {
            if (string.IsNullOrWhiteSpace(request.DryRunTargetConnectionString))
                throw new ArgumentException(
                    "DryRunTargetConnectionString is required for Level 4 (Restore Dry-Run) validation.",
                    nameof(request));

            await RunRestoreDryRunAsync(
                record, request.DryRunTargetConnectionString!, collectionResults, ct)
                .ConfigureAwait(false);
        }

        return new ValidationResult
        {
            BackupId          = request.BackupId,
            LevelsExecuted    = request.Levels,
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

    // ------------------------------------------------------------------ //
    //  Level 4 — Restore Dry-Run
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Restores the backup to the ephemeral target and verifies per-collection
    /// document counts against the manifest. Results are merged into
    /// <paramref name="collectionResults"/> (adding new entries or annotating
    /// existing ones from L1/L2/L3).
    /// </summary>
    private async Task RunRestoreDryRunAsync(
        BackupRecord                    record,
        string                          targetConnectionString,
        List<CollectionValidationResult> collectionResults,
        CancellationToken               ct)
    {
        await using var target = _restoreTargetFactory!.Create(targetConnectionString);
        var restoreEngine = new RestoreEngine(_storage, target, _config);

        var restoreResult = await restoreEngine.RunAsync(
            new RestoreRequest
            {
                DatabaseName           = record.DatabaseName,
                TargetConnectionString = targetConnectionString,
                RequestedBy            = "validation-dry-run",
                ApprovedBy             = "validation-dry-run",
                Reason                 = $"Level 4 dry-run for backup {record.BackupId}"
            },
            [record],
            ct).ConfigureAwait(false);

        if (restoreResult.Status != BackupStatus.Completed)
        {
            // Restore failed — mark every collection as failed for L4.
            foreach (var coll in record.Collections)
                AnnotateOrAddL4Result(
                    collectionResults, coll.CollectionName, coll.TotalDocuments,
                    liveCount: 0, passed: false,
                    $"Restore dry-run failed: {restoreResult.FailureReason}");
            return;
        }

        // Compare restored counts vs manifest counts.
        foreach (var coll in record.Collections)
        {
            long restoredCount;
            try
            {
                restoredCount = await target
                    .CountDocumentsAsync(record.DatabaseName, coll.CollectionName, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnnotateOrAddL4Result(
                    collectionResults, coll.CollectionName, coll.TotalDocuments,
                    liveCount: 0, passed: false,
                    $"Failed to count restored documents: {ex.Message}");
                continue;
            }

            bool passed = restoredCount == coll.TotalDocuments;
            AnnotateOrAddL4Result(
                collectionResults, coll.CollectionName, coll.TotalDocuments,
                liveCount: restoredCount, passed,
                passed ? null
                    : $"Count mismatch after restore dry-run: " +
                      $"manifest={coll.TotalDocuments}, restored={restoredCount}");
        }
    }

    private static void AnnotateOrAddL4Result(
        List<CollectionValidationResult> results,
        string  collectionName,
        long    manifestCount,
        long    liveCount,
        bool    passed,
        string? failureDetail)
    {
        var existing = results.FirstOrDefault(r =>
            string.Equals(r.CollectionName, collectionName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.LiveDocumentCount = liveCount;
            if (!passed)
            {
                existing.Passed        = false;
                existing.FailedLevels |= ValidationLevel.RestoreDryRun;
                existing.FailureDetail ??= failureDetail;
            }
        }
        else
        {
            results.Add(new CollectionValidationResult
            {
                CollectionName        = collectionName,
                ManifestDocumentCount = manifestCount,
                LiveDocumentCount     = liveCount,
                Passed                = passed,
                FailedLevels          = passed ? ValidationLevel.None : ValidationLevel.RestoreDryRun,
                FailureDetail         = failureDetail
            });
        }
    }
}
