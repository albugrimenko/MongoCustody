using System.Globalization;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Implements the weekly checkpoint pipeline.
///
/// <para>
/// A checkpoint is a deduplicated snapshot of all documents changed since
/// the last full backup or previous checkpoint. It collapses the daily
/// incremental chain into a single step, keeping restore complexity bounded.
/// </para>
///
/// <para>
/// Execution sequence (per database):
/// <list type="number">
///   <item>Find the most recent full backup — required for the folder path
///         and for listing incrementals.</item>
///   <item>Find the most recent full or checkpoint backup — this is the
///         <em>anchor</em>, the merge start point.</item>
///   <item>List incremental folders since the anchor from storage — recorded
///         in the manifest as <c>sourceIncrementalFolders</c>.</item>
///   <item>Fail early when no incrementals exist since the anchor (nothing
///         to checkpoint).</item>
///   <item>Capture a new <c>anchorTs</c> from the server before scanning.</item>
///   <item>Export all documents changed since the anchor's <c>anchorTs</c>
///         in parallel, applying the configured overlap buffer. Collections
///         with no changes are omitted from the manifest.</item>
///   <item>Assemble and seal the checkpoint manifest.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class CheckpointEngine
{
    private readonly IBackupSourceConnector    _source;
    private readonly IStorageConnector         _storage;
    private readonly IMetadataRepository       _metadata;
    private readonly MongoCustodyConfiguration _config;

    internal CheckpointEngine(
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
            .Select(db => RunForDatabaseAsync(db, request.TriggeredBy, correlationId, request.Label, ct))
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
            BackupType      = BackupType.Checkpoint,
            Status          = overallStatus,
            StartedAtUtc    = startedAt,
            CompletedAtUtc  = DateTimeOffset.UtcNow,
            FailureReason   = overallFailure,
            DatabaseResults = dbResults,
            CorrelationId   = correlationId
        };
    }

    // ------------------------------------------------------------------ //
    //  Per-database checkpoint
    // ------------------------------------------------------------------ //

    private async Task<(DatabaseBackupResult Result, string BackupId)> RunForDatabaseAsync(
        string databaseName,
        string triggeredBy,
        string correlationId,
        string? label,
        CancellationToken ct)
    {
        var backupId  = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        // Step 1 — locate the most recent full backup (needed for folder path).
        var latestFull = await _metadata
            .GetLatestBackupAsync(databaseName, BackupType.Full, ct)
            .ConfigureAwait(false);

        if (latestFull is null)
            return (Fail(databaseName,
                "No full backup found. Run a full backup before running a checkpoint."),
                backupId);

        // Step 2 — determine the anchor: most recent checkpoint or full backup,
        // whichever is later. Incrementals are merged from this point forward.
        var latestCheckpoint = await _metadata
            .GetLatestBackupAsync(databaseName, BackupType.Checkpoint, ct)
            .ConfigureAwait(false);

        var anchor = latestCheckpoint is not null &&
                     latestCheckpoint.CompletedAtUtc > latestFull.CompletedAtUtc
            ? latestCheckpoint
            : latestFull;

        // Step 3 — collect incremental folders since the anchor.
        string fullBackupId;
        try { fullBackupId = ExtractSegment(latestFull.BackupFolder, 3); }
        catch (InvalidOperationException ex) { return (Fail(databaseName, ex.Message), backupId); }

        var incrementalPrefix = BackupPathBuilder.IncrementalPrefixForChainRoot(
            latestFull.CompletedAtUtc, fullBackupId);

        List<string> sourceIncrementalFolders;
        try
        {
            sourceIncrementalFolders = await CollectIncrementalsSinceAsync(
                incrementalPrefix, anchor.CompletedAtUtc, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to list incrementals: {ex.Message}"), backupId);
        }

        if (sourceIncrementalFolders.Count == 0)
            return (Fail(databaseName,
                "No incremental backups found since the last checkpoint or full backup. " +
                "Run at least one incremental backup before creating a checkpoint."),
                backupId);

        // Step 4 — capture anchor timestamp before any scan.
        long anchorTs;
        try
        {
            anchorTs = await _source.GetServerTimestampAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Fail(databaseName, $"Failed to capture anchor timestamp: {ex.Message}"), backupId);
        }

        // Step 5 — build paths.
        var weekNumber   = BackupPathBuilder.WeekOfQuarter(startedAt);
        var folder       = BackupPathBuilder.CheckpointFolder(startedAt, fullBackupId, weekNumber);
        var quarterLabel = BackupPathBuilder.QuarterLabel(startedAt);
        var queryFromTs  = anchor.AnchorTs - _config.Policy.IncrementalBufferSeconds;

        // Step 6 — discover collections.
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

        // Step 7 — export changed docs since anchor (parallel, bounded).
        var chunkWriter = new ChunkWriter(_storage, _config.Policy);
        var collRecords = new List<CollectionBackupRecord>(collections.Count);
        using var semaphore = new SemaphoreSlim(_config.Policy.MaxParallelCollections);

        int completedCount = 0;
        int totalCount     = collections.Count;

        async Task<CollectionBackupRecord?> ExportWithProgressAsync(string col)
        {
            try
            {
                return await ExportChangedCollectionAsync(
                    chunkWriter, folder, databaseName, col,
                    anchor.AnchorTs, semaphore, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref completedCount);
            }
        }

        var exportTasks = collections.Select(ExportWithProgressAsync).ToList();

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

        collRecords.AddRange(rawResults.Where(r => r is not null)!);

        // Step 8 — seal the manifest.
        var record = new BackupRecord
        {
            BackupId                  = backupId,
            BackupType                = BackupType.Checkpoint,
            BackupFolder              = folder,
            ParentFolder              = anchor.BackupFolder,
            SourceIncrementalFolders  = sourceIncrementalFolders,
            StartedAtUtc              = startedAt,
            CompletedAtUtc            = DateTimeOffset.UtcNow,
            QueryFromTs               = queryFromTs,
            AnchorTs                  = anchorTs,
            DatabaseName              = databaseName,
            QuarterLabel              = quarterLabel,
            WeekNumber                = weekNumber,
            Format                    = _config.Policy.BackupFormat,
            Collections               = collRecords,
            TriggeredBy               = triggeredBy,
            CorrelationId             = correlationId,
            Label                     = label
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
            CollectionCount      = collRecords.Count
        }, backupId);
    }

    // ------------------------------------------------------------------ //
    //  Collect incremental folders since an anchor time
    // ------------------------------------------------------------------ //

    private async Task<List<string>> CollectIncrementalsSinceAsync(
        string prefix,
        DateTimeOffset since,
        CancellationToken ct)
    {
        var result = new List<string>();

        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct).ConfigureAwait(false))
        {
            // Folder: incremental/{year}/Q{q}/{rootId}/{yyyy-MM-ddTHHmmZ}/
            // We parse the timestamp segment (index 4) to filter.
            var parts = folder.TrimEnd('/').Split('/');
            if (parts.Length < 5) continue;

            if (TryParseIncrementalTimestamp(parts[4], out var ts) && ts > since)
                result.Add(folder);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static bool TryParseIncrementalTimestamp(string segment, out DateTimeOffset result)
    {
        // Format: yyyy-MM-ddTHHmmZ where Z is literal
        return DateTimeOffset.TryParseExact(
            segment,
            "yyyy-MM-ddTHHmmZ",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out result);
    }

    // ------------------------------------------------------------------ //
    //  Single collection — changed docs only
    // ------------------------------------------------------------------ //

    private async Task<CollectionBackupRecord?> ExportChangedCollectionAsync(
        ChunkWriter chunkWriter,
        string folder,
        string databaseName,
        string collectionName,
        long anchorAnchorTs,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var docs = _source.StreamChangedDocumentsAsync(
                databaseName, collectionName,
                anchorAnchorTs, _config.Policy.IncrementalBufferSeconds, ct);

            var collRecord = await chunkWriter
                .WriteCollectionChunksAsync(folder, databaseName, collectionName, docs, ct)
                .ConfigureAwait(false);

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

    // Folder format: {type}/{year}/Q{q}/{backupId}/... — backupId is index 3.
    private static string ExtractSegment(string folder, int index)
    {
        var parts = folder.TrimEnd('/').Split('/');
        if (parts.Length <= index)
            throw new InvalidOperationException(
                $"Backup folder '{folder}' has an unexpected path format.");
        return parts[index];
    }

    private static DatabaseBackupResult Fail(string dbName, string reason) =>
        new()
        {
            DatabaseName  = dbName,
            Status        = BackupStatus.Failed,
            FailureReason = reason
        };
}
