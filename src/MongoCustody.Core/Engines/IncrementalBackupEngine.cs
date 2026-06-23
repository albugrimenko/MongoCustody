using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the incremental backup pipeline.
///
/// <para>
/// Execution sequence (per database):
/// <list type="number">
///   <item>Resolve the most recent backup of any type to obtain its
///         <c>anchorTs</c> — this is the starting point for changed-doc
///         queries.</item>
///   <item>Count existing incrementals since the last full/checkpoint.
///         Emit a warning in the result when the count reaches
///         <see cref="BackupPolicy.MaxIncrementalChainDepth"/> but
///         proceed — the scheduler decides when to run a full.</item>
///   <item>Capture a new <c>anchorTs</c> from the server immediately
///         before the collection scan (anchors the end of this window).</item>
///   <item>Export only changed documents per collection in parallel,
///         applying the configured overlap buffer. Collections with zero
///         changed documents are skipped — no chunk blob is written and
///         no entry appears in the manifest for that collection.</item>
///   <item>Assemble and seal the manifest.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class IncrementalBackupEngine
{
    private readonly IBackupSourceConnector   _source;
    private readonly IStorageConnector        _storage;
    private readonly IMetadataRepository      _metadata;
    private readonly MongoCustodyConfiguration _config;

    internal IncrementalBackupEngine(
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

    internal async Task<BackupResult> RunAsync(
        BackupRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var startedAt     = DateTimeOffset.UtcNow;
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        var databases = request.Databases.Count > 0
            ? request.Databases
            : _config.Databases.Select(d => d.DatabaseName).ToList();

        var dbTasks = databases
            .Select(db => RunForDatabaseAsync(db, request.TriggeredBy, correlationId, progress, ct))
            .ToList();

        var dbResultTuples = await Task.WhenAll(dbTasks).ConfigureAwait(false);

        var dbResults     = new List<DatabaseBackupResult>();
        var overallStatus = BackupStatus.Completed;
        string? overallFailure = null;

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
            BackupId        = correlationId,
            BackupType      = BackupType.Incremental,
            Status          = overallStatus,
            StartedAtUtc    = startedAt,
            CompletedAtUtc  = DateTimeOffset.UtcNow,
            FailureReason   = overallFailure,
            DatabaseResults = dbResults,
            CorrelationId   = correlationId
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-database incremental backup
    // ------------------------------------------------------------------ //

    private async Task<(DatabaseBackupResult Result, string BackupId)> RunForDatabaseAsync(
        string databaseName,
        string triggeredBy,
        string correlationId,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
    {
        var backupId  = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        // Step 1 — find the most recent backup to anchor from.
        // We prefer the most recent record of any type; full or checkpoint
        // starts a new chain anchor, incremental extends it.
        BackupRecord? previousFull = await _metadata
            .GetLatestBackupAsync(databaseName, BackupType.Full, ct)
            .ConfigureAwait(false);

        BackupRecord? previousCheckpoint = await _metadata
            .GetLatestBackupAsync(databaseName, BackupType.Checkpoint, ct)
            .ConfigureAwait(false);

        BackupRecord? previousIncremental = await _metadata
            .GetLatestBackupAsync(databaseName, BackupType.Incremental, ct)
            .ConfigureAwait(false);

        // Pick the latest of the three as the direct predecessor.
        var predecessor = PickLatest(previousFull, previousCheckpoint, previousIncremental);
        if (predecessor is null)
            return (Fail(databaseName,
                "No previous backup found. Run a full backup before running an incremental."),
                backupId);

        var previousAnchorTs = predecessor.AnchorTs;

        // Step 2 — determine which full/checkpoint is the chain root for this
        // incremental, then count how many incrementals already exist since it.
        var chainRoot = PickLatest(previousFull, previousCheckpoint)
                        ?? predecessor; // fallback: predecessor is itself a full

        var chainDepth = await CountIncrementalsSinceAsync(chainRoot, databaseName, ct)
            .ConfigureAwait(false);

        string? depthWarning = null;
        if (chainDepth >= _config.Policy.MaxIncrementalChainDepth)
            depthWarning =
                $"Incremental chain depth {chainDepth} has reached the configured maximum " +
                $"({_config.Policy.MaxIncrementalChainDepth}). " +
                $"Consider running a full backup soon to reset the chain.";

        // Step 3 — capture the end-of-window anchor BEFORE the scan so that
        // any document modified after this point is picked up by the next run.
        long anchorTs;
        try
        {
            anchorTs = await _source.GetServerTimestampAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to capture anchor timestamp: {ex.Message}"), backupId);
        }

        // Step 4 — build paths
        var fullBackupId = ExtractBackupId(chainRoot.BackupFolder);
        var folder       = BackupPathBuilder.IncrementalBackupFolder(startedAt, fullBackupId);
        var quarterLabel = BackupPathBuilder.QuarterLabel(startedAt);
        var queryFromTs  = previousAnchorTs - _config.Policy.IncrementalBufferSeconds;

        // Step 5 — discover collections
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

        // Step 6 — export changed docs per collection (parallel, bounded)
        var chunkWriter = new ChunkWriter(_storage, _config.Policy);
        var collRecords = new List<CollectionBackupRecord>(collections.Count);
        using var semaphore = new SemaphoreSlim(_config.Policy.MaxParallelCollections);

        int completedCount = 0;
        int totalCount     = collections.Count;

        var exportTasks = collections
            .Select(col => ExportChangedCollectionAsync(
                    chunkWriter, folder, databaseName, col,
                    previousAnchorTs, semaphore, ct)
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

        CollectionBackupRecord?[] rawResults;
        try
        {
            rawResults = await Task.WhenAll(exportTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var errors = exportTasks
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .Select(e => e.Message);
            return (Fail(databaseName, $"Collection export failed: {string.Join("; ", errors)}"), backupId);
        }

        // Null means no changed documents — exclude from manifest.
        collRecords.AddRange(rawResults.Where(r => r is not null)!);

        // Step 7 — seal the manifest
        var record = new BackupRecord
        {
            BackupId       = backupId,
            BackupType     = BackupType.Incremental,
            BackupFolder   = folder,
            ParentFolder   = predecessor.BackupFolder,
            StartedAtUtc   = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            QueryFromTs    = queryFromTs,
            AnchorTs       = anchorTs,
            DatabaseName   = databaseName,
            QuarterLabel   = quarterLabel,
            Format         = _config.Policy.BackupFormat,
            Collections    = collRecords,
            TriggeredBy    = triggeredBy,
            CorrelationId  = correlationId
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
            DatabaseName         = databaseName,
            Status               = BackupStatus.Completed,
            TotalDocuments       = collRecords.Sum(c => c.TotalDocuments),
            TotalCompressedBytes = collRecords.Sum(c => c.Chunks.Sum(k => k.SizeBytes)),
            CollectionCount      = collRecords.Count,
            FailureReason        = depthWarning   // non-null → caller can surface warning
        }, backupId);
    }

    // ------------------------------------------------------------------ //
    //  Single collection — changed docs only
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a <see cref="CollectionBackupRecord"/> for the collection, or
    /// <c>null</c> when no documents changed since the previous anchor.
    /// </summary>
    private async Task<CollectionBackupRecord?> ExportChangedCollectionAsync(
        ChunkWriter chunkWriter,
        string folder,
        string databaseName,
        string collectionName,
        long previousAnchorTs,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var docs = _source.StreamChangedDocumentsAsync(
                databaseName, collectionName,
                previousAnchorTs, _config.Policy.IncrementalBufferSeconds, ct);

            var collRecord = await chunkWriter
                .WriteCollectionChunksAsync(folder, databaseName, collectionName, docs, ct)
                .ConfigureAwait(false);

            // No changed documents — skip this collection entirely.
            if (collRecord.TotalDocuments == 0)
                return null;

            var indexes = await _source
                .ListIndexesAsync(databaseName, collectionName, ct)
                .ConfigureAwait(false);

            collRecord.Indexes = indexes?.ToList() ?? [];
            return collRecord;
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Counts incremental manifests that exist under the chain root's
    /// incremental prefix and whose <c>anchorTs</c> is greater than the
    /// chain root's <c>anchorTs</c>.
    /// </summary>
    private async Task<int> CountIncrementalsSinceAsync(
        BackupRecord chainRoot,
        string databaseName,
        CancellationToken ct)
    {
        var rootId      = ExtractBackupId(chainRoot.BackupFolder);
        var quarterParts = BackupPathBuilder.QuarterLabel(chainRoot.CompletedAtUtc).Split('-');
        var prefix      = $"incremental/{quarterParts[0]}/{quarterParts[1]}/{rootId}/";

        int count = 0;
        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct).ConfigureAwait(false))
        {
            var record = await _metadata
                .GetBackupRecordByFolderAsync(folder, ct)
                .ConfigureAwait(false);

            if (record is null) continue;
            if (!string.Equals(record.DatabaseName, databaseName,
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (record.AnchorTs > chainRoot.AnchorTs)
                count++;
        }

        return count;
    }

    private static BackupRecord? PickLatest(params BackupRecord?[] candidates)
        => candidates
            .Where(r => r is not null)
            .MaxBy(r => r!.CompletedAtUtc);

    // The full backup folder is full/{year}/Q{q}/{backupId}/ — backupId is the 4th segment.
    private static string ExtractBackupId(string folder)
    {
        var parts = folder.TrimEnd('/').Split('/');
        return parts.Length >= 4 ? parts[3] : folder;
    }

    private static DatabaseBackupResult Fail(string dbName, string reason) =>
        new()
        {
            DatabaseName  = dbName,
            Status        = BackupStatus.Failed,
            FailureReason = reason
        };
}
