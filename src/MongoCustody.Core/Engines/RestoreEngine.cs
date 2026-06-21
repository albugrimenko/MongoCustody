using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.IO.Compression;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the restore pipeline as described in
/// "Backup and recovery plan.md" § 9.
///
/// <para>
/// The engine accepts a pre-resolved restore chain (a list of
/// <see cref="BackupRecord"/> entries ordered oldest-first) and applies them
/// in sequence to an empty restore target:
/// <list type="number">
///   <item>Full backup record → <c>InsertMany</c> per collection (target is empty).</item>
///   <item>Subsequent records (incremental or checkpoint) →
///         <c>UpsertMany</c> per collection using <c>replaceOne(_id, upsert:true)</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Collections within a single chain step are processed in parallel, bounded by
/// <see cref="BackupPolicy.MaxParallelCollections"/>.  Chain steps are always
/// applied in strict order — parallelism is only safe within a single step.
/// </para>
///
/// <para>
/// Chain resolution and pre/post audit record writes are the caller's
/// responsibility (handled by <see cref="MongoCustodyService"/>), keeping this
/// engine focused solely on I/O and document streaming.
/// </para>
/// </summary>
internal sealed class RestoreEngine
{
    private readonly IStorageConnector       _storage;
    private readonly IRestoreTargetConnector _target;
    private readonly MongoCustodyConfiguration _config;

    internal RestoreEngine(
        IStorageConnector       storage,
        IRestoreTargetConnector target,
        MongoCustodyConfiguration config)
    {
        _storage = storage;
        _target  = target;
        _config  = config;
    }

    /// <summary>
    /// Applies <paramref name="chain"/> in order to the restore target and
    /// returns a <see cref="RestoreResult"/>.
    /// </summary>
    /// <param name="request">
    /// Original restore request (used for correlation ID, collection filter,
    /// and metadata fields in the result).
    /// </param>
    /// <param name="chain">
    /// Pre-resolved list of backup records, oldest first.
    /// Chain resolution is performed by
    /// <see cref="IMetadataRepository.ResolveRestoreChainAsync"/> in the caller.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<RestoreResult> RunAsync(
        RestoreRequest            request,
        IReadOnlyList<BackupRecord> chain,
        CancellationToken         ct)
    {
        var startedAt     = DateTimeOffset.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        var chainFolders = chain.Select(r => r.BackupFolder).ToList();

        var collectionFilter = request.Collections.Count > 0
            ? new HashSet<string>(request.Collections, StringComparer.OrdinalIgnoreCase)
            : null;

        // Accumulates per-collection totals across all chain steps.
        var accumulated = new Dictionary<string, CollectionRestoreResult>(
            StringComparer.OrdinalIgnoreCase);

        bool isFirstStep = true;

        foreach (var record in chain)
        {
            ct.ThrowIfCancellationRequested();

            var collectionsToRestore = record.Collections
                .Where(c => collectionFilter is null
                         || collectionFilter.Contains(c.CollectionName))
                .OrderBy(c => c.CollectionName)
                .ToList();

            if (collectionsToRestore.Count == 0)
            {
                isFirstStep = false;
                continue;
            }

            // First step uses InsertMany (target collection is empty).
            // Subsequent steps use UpsertMany (replaceOne with upsert).
            bool useInsert = isFirstStep && record.BackupType == BackupType.Full;

            using var semaphore = new SemaphoreSlim(_config.Policy.MaxParallelCollections);
            var stepTasks   = collectionsToRestore
                .Select(c => RestoreCollectionAsync(
                    request.TargetDatabaseName, c, semaphore, useInsert, ct))
                .ToList();

            CollectionRestoreResult[] stepResults;
            try
            {
                stepResults = await Task.WhenAll(stepTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                var errors = stepTasks
                    .Where(t => t.IsFaulted)
                    .SelectMany(t => t.Exception!.InnerExceptions)
                    .Select(e => e.Message);
                return Fail(
                    $"Collection restore failed in step '{record.BackupFolder}': " +
                    string.Join("; ", errors),
                    startedAt, chainFolders, correlationId, request.TargetDate);
            }

            // Merge step results — document counts accumulate across chain steps.
            foreach (var r in stepResults)
            {
                if (accumulated.TryGetValue(r.CollectionName, out var existing))
                {
                    existing.DocumentsUpserted += r.DocumentsUpserted;
                    if (r.Status == BackupStatus.Failed)
                    {
                        existing.Status        = BackupStatus.Failed;
                        existing.FailureReason = r.FailureReason;
                    }
                }
                else
                {
                    accumulated[r.CollectionName] = r;
                }
            }

            isFirstStep = false;
        }

        var collectionResults = accumulated.Values.OrderBy(r => r.CollectionName).ToList();
        var failed = collectionResults.Where(r => r.Status == BackupStatus.Failed).ToList();

        return new RestoreResult
        {
            Status            = failed.Count == 0 ? BackupStatus.Completed : BackupStatus.Failed,
            StartedAtUtc      = startedAt,
            CompletedAtUtc    = DateTimeOffset.UtcNow,
            TargetDate        = request.TargetDate,
            RestoreChain      = chainFolders,
            CollectionResults = collectionResults,
            FailureReason     = failed.Count == 0 ? null
                : string.Join("; ", failed.Select(f => f.FailureReason)),
            CorrelationId     = correlationId
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-collection restore
    // ------------------------------------------------------------------ //

    private async Task<CollectionRestoreResult> RestoreCollectionAsync(
        string                 databaseName,
        CollectionBackupRecord collRecord,
        SemaphoreSlim          semaphore,
        bool                   useInsert,
        CancellationToken      ct)
    {
        bool acquired = false;
        try
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            long totalDocs = 0;
            foreach (var chunk in collRecord.Chunks.OrderBy(c => c.ChunkIndex))
            {
                totalDocs += await RestoreChunkAsync(
                    databaseName, collRecord.CollectionName, chunk, useInsert, ct)
                    .ConfigureAwait(false);
            }

            return new CollectionRestoreResult
            {
                CollectionName    = collRecord.CollectionName,
                DocumentsUpserted = totalDocs,
                Status            = BackupStatus.Completed
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CollectionRestoreResult
            {
                CollectionName    = collRecord.CollectionName,
                DocumentsUpserted = 0,
                Status            = BackupStatus.Failed,
                FailureReason     = $"{collRecord.CollectionName}: {ex.Message}"
            };
        }
        finally
        {
            if (acquired) semaphore.Release();
        }
    }

    private async Task<long> RestoreChunkAsync(
        string        databaseName,
        string        collectionName,
        ChunkRecord   chunk,
        bool          useInsert,
        CancellationToken ct)
    {
        await using var blobStream = await _storage
            .OpenReadAsync(chunk.BlobPath, ct)
            .ConfigureAwait(false);

        await using var gzip = new GZipStream(blobStream, CompressionMode.Decompress);

        var docs = BsonChunkReader.ReadDocumentsAsync(gzip, ct);

        return useInsert
            ? await _target
                .InsertManyAsync(databaseName, collectionName, docs, ct)
                .ConfigureAwait(false)
            : await _target
                .UpsertManyAsync(databaseName, collectionName, docs, ct)
                .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static RestoreResult Fail(
        string           reason,
        DateTimeOffset   startedAt,
        List<string>     chainFolders,
        string           correlationId,
        DateTimeOffset   targetDate) =>
        new()
        {
            Status         = BackupStatus.Failed,
            StartedAtUtc   = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            TargetDate     = targetDate,
            RestoreChain   = chainFolders,
            FailureReason  = reason,
            CorrelationId  = correlationId
        };
}
