using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.IO.Compression;
using System.Threading;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the restore pipeline as described in
/// "Backup and recovery plan.md" § 9.
///
/// <para>
/// The engine accepts a pre-resolved restore chain (a list of
/// <see cref="BackupRecord"/> entries ordered oldest-first) and applies them
/// in sequence to an empty restore target using a three-phase index strategy:
/// </para>
///
/// <list type="number">
///   <item>
///     <term>Pre-pass</term>
///     <description>
///       Walk the entire chain and build a <em>merged index map</em> —
///       the latest <see cref="IndexDefinition"/> list per collection,
///       where later steps override earlier ones (last writer wins).
///     </description>
///   </item>
///   <item>
///     <term>Per-step, per-collection</term>
///     <description>
///       For each collection in the current step, check whether the
///       collection already exists in the target:
///       <list type="bullet">
///         <item>
///           <em>Not yet created</em> (new collection) — create unique
///           indexes first while the collection is empty (required by
///           Cosmos DB for MongoDB), then load data.
///         </item>
///         <item>
///           <em>Already exists</em> — load data only (upsert).
///           Unique indexes were created when the collection was first
///           seen and remain in place.
///         </item>
///       </list>
///       Collections within a single chain step are processed in parallel,
///       bounded by <see cref="BackupPolicy.MaxParallelCollections"/>.
///     </description>
///   </item>
///   <item>
///     <term>Post-last-step</term>
///     <description>
///       After all data for the final chain step has been written, create
///       non-unique indexes (regular, text, TTL, compound) for every
///       collection in the merged map. Building non-unique indexes after a
///       full data load is significantly faster than building them inline.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Chain resolution and pre/post audit record writes are the caller's
/// responsibility (handled by <see cref="MongoCustodyService"/>), keeping
/// this engine focused solely on I/O, document streaming, and index management.
/// </para>
/// </summary>
internal sealed class RestoreEngine
{
    private readonly IStorageConnector        _storage;
    private readonly IRestoreTargetConnector  _target;
    private readonly MongoCustodyConfiguration _config;

    internal RestoreEngine(
        IStorageConnector         storage,
        IRestoreTargetConnector   target,
        MongoCustodyConfiguration config)
    {
        _storage = storage;
        _target  = target;
        _config  = config;
    }

    // ------------------------------------------------------------------ //
    //  Main entry point
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Applies <paramref name="chain"/> in order to the restore target and
    /// returns a <see cref="RestoreResult"/>.
    /// </summary>
    internal async Task<RestoreResult> RunAsync(
        RestoreRequest              request,
        IReadOnlyList<BackupRecord> chain,
        IProgress<OperationProgress>? progress,
        CancellationToken           ct)
    {
        var startedAt     = DateTimeOffset.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        var chainFolders = chain.Select(r => r.BackupFolder).ToList();
        var targetDb     = request.TargetDatabaseName;

        var collectionFilter = request.Collections.Count > 0
            ? new HashSet<string>(request.Collections, StringComparer.OrdinalIgnoreCase)
            : null;

        if (chain.Count == 0)
            return BuildResult(BackupStatus.Completed, startedAt, chainFolders,
                [], correlationId, request.TargetDate);

        // PRE-PASS — build merged index map across all chain steps.
        // Later steps override earlier ones so the map always holds the most
        // recent index definitions for each collection.
        var mergedIndexMap = BuildMergedIndexMap(chain, collectionFilter);

        // Shard key is declared once per database (Cosmos DB for MongoDB
        // exposes no command to discover an existing collection's shard key,
        // so it can't be captured per-collection at backup time — see
        // DatabaseConfiguration.ShardKeyField) and applies uniformly to every
        // collection restored into this database.
        var shardKeyField = _config.Databases
            .FirstOrDefault(d => string.Equals(d.DatabaseName, request.DatabaseName, StringComparison.OrdinalIgnoreCase))
            ?.ShardKeyField;

        // Pre-compute total collection operations for progress reporting.
        int totalCollOps = chain.Sum(r => r.Collections.Count(c =>
            collectionFilter is null || collectionFilter.Contains(c.CollectionName)));
        int completedCollOps = 0;

        var accumulated = new Dictionary<string, CollectionRestoreResult>(
            StringComparer.OrdinalIgnoreCase);

        bool isFirstStep = true;

        for (int stepIdx = 0; stepIdx < chain.Count; stepIdx++)
        {
            ct.ThrowIfCancellationRequested();

            var record     = chain[stepIdx];
            bool isLastStep = stepIdx == chain.Count - 1;

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

            // Full backup baseline uses InsertMany; everything else uses UpsertMany.
            bool useInsert = isFirstStep && record.BackupType == BackupType.Full;

            using var semaphore = new SemaphoreSlim(_config.Policy.MaxParallelCollections);

            var stepTasks = collectionsToRestore
                .Select(c =>
                {
                    // Capture the merged indexes for this specific collection
                    // before entering the lambda — safe for parallel use.
                    var indexes = mergedIndexMap.GetValueOrDefault(
                        c.CollectionName, (IReadOnlyList<IndexDefinition>)[]);

                    return RestoreCollectionAsync(
                            targetDb, c, semaphore, useInsert, indexes, shardKeyField, ct)
                        .ContinueWith(
                            task =>
                            {
                                int done = Interlocked.Increment(ref completedCollOps);
                                progress?.Report(new OperationProgress
                                {
                                    Completed   = done,
                                    Total       = totalCollOps,
                                    CurrentItem = c.CollectionName
                                });
                                return task.GetAwaiter().GetResult();
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                })
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
                return BuildResult(BackupStatus.Failed,
                    startedAt, chainFolders, accumulated.Values,
                    correlationId, request.TargetDate,
                    $"Collection restore failed in step '{record.BackupFolder}': " +
                    string.Join("; ", errors));
            }

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

            // POST-LAST-STEP — create non-unique indexes after all data is loaded.
            // This maximises bulk insert throughput while still applying all indexes.
            if (isLastStep)
            {
                try
                {
                    await CreateNonUniqueIndexesAsync(targetDb, mergedIndexMap, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return BuildResult(BackupStatus.Failed,
                        startedAt, chainFolders, accumulated.Values,
                        correlationId, request.TargetDate,
                        $"Non-unique index creation failed after data load: {ex.Message}");
                }
            }

            isFirstStep = false;
        }

        var collectionResults = accumulated.Values.OrderBy(r => r.CollectionName).ToList();
        var failed            = collectionResults.Where(r => r.Status == BackupStatus.Failed).ToList();

        return BuildResult(
            failed.Count == 0 ? BackupStatus.Completed : BackupStatus.Failed,
            startedAt, chainFolders, collectionResults,
            correlationId, request.TargetDate,
            failed.Count == 0 ? null : string.Join("; ", failed.Select(f => f.FailureReason)));
    }

    // ------------------------------------------------------------------ //
    //  Per-collection restore (data + unique indexes)
    // ------------------------------------------------------------------ //

    private async Task<CollectionRestoreResult> RestoreCollectionAsync(
        string                         databaseName,
        CollectionBackupRecord         collRecord,
        SemaphoreSlim                  semaphore,
        bool                           useInsert,
        IReadOnlyList<IndexDefinition> indexes,
        string?                        shardKeyField,
        CancellationToken              ct)
    {
        bool acquired = false;
        try
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            var collName = collRecord.CollectionName;

            // Determine whether the collection already exists in the target.
            // New collections (not yet created) must have their shard key and
            // unique indexes applied at creation time — both Cosmos DB for
            // MongoDB and sharded MongoDB clusters reject adding either
            // afterward.
            bool exists = await _target
                .CollectionExistsAsync(databaseName, collName, ct)
                .ConfigureAwait(false);

            string? warning = null;
            if (!exists)
            {
                var uniqueIndexes = indexes.Where(i => i.IsUnique).ToList();
                warning = await _target
                    .CreateCollectionAsync(databaseName, collName, shardKeyField, uniqueIndexes, ct)
                    .ConfigureAwait(false);
            }
            // If the collection already exists its shard key and unique indexes are
            // already in place. Adding either to a non-empty collection afterward is
            // not supported by Cosmos DB for MongoDB or sharded MongoDB clusters; such
            // a scenario is surfaced as a restore warning in production but is not
            // retried here.

            long totalDocs = 0;
            foreach (var chunk in collRecord.Chunks.OrderBy(c => c.ChunkIndex))
            {
                totalDocs += await RestoreChunkAsync(
                    databaseName, collName, chunk, useInsert, ct)
                    .ConfigureAwait(false);
            }

            return new CollectionRestoreResult
            {
                CollectionName    = collName,
                DocumentsUpserted = totalDocs,
                Status            = BackupStatus.Completed,
                Warning           = warning
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
            ? await _target.InsertManyAsync(databaseName, collectionName, docs, ct)
                .ConfigureAwait(false)
            : await _target.UpsertManyAsync(databaseName, collectionName, docs, ct)
                .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Non-unique index creation (after all data is loaded)
    // ------------------------------------------------------------------ //

    private async Task CreateNonUniqueIndexesAsync(
        string databaseName,
        Dictionary<string, IReadOnlyList<IndexDefinition>> mergedIndexMap,
        CancellationToken ct)
    {
        // Run per-collection index creation in parallel — collections are independent.
        var tasks = mergedIndexMap
            .Select(kvp =>
            {
                var nonUnique = kvp.Value.Where(i => !i.IsUnique).ToList();
                if (nonUnique.Count == 0) return Task.CompletedTask;
                return _target.CreateIndexesAsync(databaseName, kvp.Key, nonUnique, ct);
            })
            .ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Pre-pass: build merged index map
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Walks the chain oldest-first and builds a map of
    /// <c>collectionName → latest IndexDefinition list</c>.
    /// Later steps always overwrite earlier ones so the terminal step
    /// wins, which is correct because each backup captures the current
    /// index state at that point in time.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<IndexDefinition>> BuildMergedIndexMap(
        IReadOnlyList<BackupRecord> chain,
        HashSet<string>?            collectionFilter)
    {
        var map = new Dictionary<string, IReadOnlyList<IndexDefinition>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var record in chain)
        {
            foreach (var coll in record.Collections)
            {
                if (collectionFilter is not null &&
                    !collectionFilter.Contains(coll.CollectionName))
                    continue;

                // Always update — an empty list means the collection genuinely
                // has no user-defined indexes at this point in the chain.
                map[coll.CollectionName] = coll.Indexes;
            }
        }

        return map;
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static RestoreResult BuildResult(
        BackupStatus                         status,
        DateTimeOffset                       startedAt,
        List<string>                         chainFolders,
        IEnumerable<CollectionRestoreResult> collections,
        string                               correlationId,
        DateTimeOffset                       targetDate,
        string?                              failureReason = null) =>
        new()
        {
            Status            = status,
            StartedAtUtc      = startedAt,
            CompletedAtUtc    = DateTimeOffset.UtcNow,
            TargetDate        = targetDate,
            RestoreChain      = chainFolders,
            CollectionResults = collections.OrderBy(r => r.CollectionName).ToList(),
            FailureReason     = failureReason,
            CorrelationId     = correlationId
        };
}
