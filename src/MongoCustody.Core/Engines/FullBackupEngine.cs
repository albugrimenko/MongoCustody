using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.Threading;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the full backup pipeline as described in
/// "Backup and recovery plan.md" § 6.1.
///
/// <para>
/// Execution sequence (per database):
/// <list type="number">
///   <item>Capture <c>anchorTs</c> from the source before any collection scan begins.</item>
///   <item>Discover all collections (excluding any in <see cref="DatabaseConfiguration.ExcludedCollections"/>).</item>
///   <item>Export each collection in parallel (bounded by <see cref="BackupPolicy.MaxParallelCollections"/>)
///         using <see cref="ChunkWriter"/>: streaming BSON → gzip → SHA-256 → blob.</item>
///   <item>Assemble and seal the <see cref="BackupRecord"/> (manifest) via
///         <see cref="IMetadataRepository.SaveBackupRecordAsync"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// The manifest is written <em>only</em> after all collections complete
/// successfully. Its absence in a folder is the failure signal — no
/// partial manifest is ever written.
/// </para>
/// </summary>
internal sealed class FullBackupEngine
{
    private readonly IBackupSourceConnector _source;
    private readonly IStorageConnector      _storage;
    private readonly IMetadataRepository    _metadata;
    private readonly MongoCustodyConfiguration _config;

    internal FullBackupEngine(
        IBackupSourceConnector source,
        IStorageConnector      storage,
        IMetadataRepository    metadata,
        MongoCustodyConfiguration config)
    {
        _source   = source;
        _storage  = storage;
        _metadata = metadata;
        _config   = config;
    }

    /// <summary>
    /// Runs a full backup for all databases listed in
    /// <see cref="BackupRequest.Databases"/> (or all configured databases
    /// when the list is empty).
    /// </summary>
    internal async Task<BackupResult> RunAsync(
        BackupRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var startedAt     = DateTimeOffset.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        var databases = ResolveTargetDatabases(request);

        var dbResults  = new List<DatabaseBackupResult>();
        var overallStatus = BackupStatus.Completed;
        string? overallFailure = null;

        var dbTasks = databases
            .Select(dbName => RunForDatabaseAsync(dbName, request.TriggeredBy, correlationId, request.Label, progress, ct))
            .ToList();
        var dbResultTuples = await Task.WhenAll(dbTasks).ConfigureAwait(false);

        foreach (var (dbResult, backupId) in dbResultTuples)
        {
            dbResult.BackupId = backupId;
            dbResults.Add(dbResult);

            if (dbResult.Status == BackupStatus.Failed)
            {
                overallStatus  = BackupStatus.Failed;
                overallFailure ??= dbResult.FailureReason;
            }
        }

        return new BackupResult
        {
            // correlationId is the stable run-level identifier. Per-database
            // backup IDs are in DatabaseResults[n].BackupId and can be passed
            // to IMetadataRepository.GetBackupRecordAsync for manifest lookup.
            BackupId        = correlationId,
            BackupType      = BackupType.Full,
            Status          = overallStatus,
            StartedAtUtc    = startedAt,
            CompletedAtUtc  = DateTimeOffset.UtcNow,
            FailureReason   = overallFailure,
            DatabaseResults = dbResults,
            CorrelationId   = correlationId
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-database backup
    // ------------------------------------------------------------------ //

    private async Task<(DatabaseBackupResult Result, string BackupId)> RunForDatabaseAsync(
        string databaseName,
        string triggeredBy,
        string correlationId,
        string? label,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var backupId  = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        // Step 1 — capture anchor before any scan begins
        long anchorTs;
        try
        {
            anchorTs = await _source
                .GetServerTimestampAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to capture anchor timestamp: {ex.Message}"), backupId);
        }

        // Step 2 — build the backup folder path
        var folder       = BackupPathBuilder.FullBackupFolder(startedAt, backupId);
        var quarterLabel = BackupPathBuilder.QuarterLabel(startedAt);

        // Step 3 — discover collections
        List<string> collections;
        try
        {
            collections = await _source
                .ListCollectionsAsync(databaseName, ct)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to enumerate collections: {ex.Message}"), backupId);
        }

        // Step 4 — export all collections (parallel, bounded)
        var chunkWriter    = new ChunkWriter(_storage, _config.Policy);
        var collRecords    = new List<CollectionBackupRecord>(collections.Count);
        using var semaphore = new SemaphoreSlim(_config.Policy.MaxParallelCollections);

        int completedCount = 0;
        int totalCount     = collections.Count;

        var exportTasks = collections
            .Select(col => ExportCollectionAsync(chunkWriter, folder, databaseName, col, semaphore, ct)
                .ContinueWith(
                    task =>
                    {
                        int done = Interlocked.Increment(ref completedCount);
                        progress?.Report(new OperationProgress
                        {
                            Completed   = done,
                            Total       = totalCount,
                            CurrentItem = col
                        });
                        return task.GetAwaiter().GetResult();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default))
            .ToList();

        CollectionBackupRecord[] results;
        try
        {
            results = await Task.WhenAll(exportTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Task.WhenAll re-throws only the first exception; inspect every
            // faulted task so all collection failures appear in the reason.
            var errors = exportTasks
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .Select(e => e.Message);
            return (Fail(databaseName, $"Collection export failed: {string.Join("; ", errors)}"), backupId);
        }

        collRecords.AddRange(results);

        // Step 5 — assemble and seal the manifest
        var record = new BackupRecord
        {
            BackupId        = backupId,
            BackupType      = BackupType.Full,
            BackupFolder    = folder,
            ParentFolder    = null,
            StartedAtUtc    = startedAt,
            CompletedAtUtc  = DateTimeOffset.UtcNow,
            QueryFromTs     = null,
            AnchorTs        = anchorTs,
            DatabaseName    = databaseName,
            QuarterLabel    = quarterLabel,
            Format          = _config.Policy.BackupFormat,
            Collections     = collRecords,
            TriggeredBy     = triggeredBy,
            CorrelationId   = correlationId,
            Label           = label
        };

        try
        {
            await _metadata.SaveBackupRecordAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to seal manifest: {ex.Message}"), backupId);
        }

        return (new DatabaseBackupResult
        {
            DatabaseName          = databaseName,
            Status                = BackupStatus.Completed,
            TotalDocuments        = collRecords.Sum(c => c.TotalDocuments),
            TotalCompressedBytes  = collRecords.Sum(c => c.Chunks.Sum(k => k.SizeBytes)),
            CollectionCount       = collRecords.Count
        }, backupId);
    }

    // ------------------------------------------------------------------ //
    //  Single collection export (one Task per collection)
    // ------------------------------------------------------------------ //

    private async Task<CollectionBackupRecord> ExportCollectionAsync(
        ChunkWriter chunkWriter,
        string folder,
        string databaseName,
        string collectionName,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            var docs = _source.StreamAllDocumentsAsync(databaseName, collectionName, ct);
            var collRecord = await chunkWriter
                .WriteCollectionChunksAsync(folder, databaseName, collectionName, docs, ct)
                .ConfigureAwait(false);

            // Capture index definitions after data export — both operations use
            // the same semaphore slot so parallelism stays within policy bounds.
            var indexes = await _source
                .ListIndexesAsync(databaseName, collectionName, ct)
                .ConfigureAwait(false);

            collRecord.Indexes = indexes?.ToList() ?? [];
            return collRecord;
        }
        finally
        {
            if (acquired) semaphore.Release();
        }
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private List<string> ResolveTargetDatabases(BackupRequest request)
    {
        if (request.Databases.Count > 0)
            return request.Databases;

        return _config.Databases
            .Select(d => d.DatabaseName)
            .ToList();
    }

    private static DatabaseBackupResult Fail(string dbName, string reason) =>
        new()
        {
            DatabaseName  = dbName,
            Status        = BackupStatus.Failed,
            FailureReason = reason
        };
}
