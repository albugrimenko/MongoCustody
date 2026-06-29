using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace MongoCustody.Connectors;

/// <summary>
/// <see cref="IMetadataRepository"/> implementation backed entirely by
/// Azure Blob Storage — no separate database required.
///
/// Every <see cref="BackupRecord"/> is the <c>manifest.json</c> blob
/// already written by the backup engine. This repository reads, writes,
/// and queries those manifests. A lightweight index blob at
/// <c>_index/{backupId}</c> stores only the folder path, enabling O(1)
/// lookup by backup ID without scanning the entire container.
///
/// Chain resolution walks the <c>parentFolder</c> chain stored in each
/// manifest and reads at most 19 manifests for the worst-case restore
/// scenario (1 full + 12 checkpoints + 6 daily incrementals).
/// </summary>
public sealed class BlobMetadataRepository : IMetadataRepository
{
    private readonly IStorageConnector _storage;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public BlobMetadataRepository(IStorageConnector storage)
    {
        _storage = storage;
    }

    // ------------------------------------------------------------------ //
    //  BackupRecord persistence
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task SaveBackupRecordAsync(
        BackupRecord record,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);

        // Write order: manifest first (source of truth), then index pointers.
        // A partial failure leaves a manifest with no index entry — recoverable
        // via `index rebuild`.
        await _storage
            .WriteManifestAsync(record.BackupFolder, json, ct)
            .ConfigureAwait(false);

        var indexPath = StoragePathHelper.IndexBlobPath(record.BackupId);
        await WriteTextBlobAsync(indexPath, record.BackupFolder, ct)
            .ConfigureAwait(false);

        int q    = ((record.CompletedAtUtc.Month - 1) / 3) + 1;
        string type = record.BackupType.ToString().ToLowerInvariant();
        var entryPath = StoragePathHelper.SecondaryIndexEntryBlobPath(
            record.CompletedAtUtc.Year, q, type,
            record.DatabaseName, record.CompletedAtUtc, record.BackupId);
        var entryJson = JsonSerializer.Serialize(
            new SecondaryIndexEntry(record.BackupId, record.BackupFolder, record.AnchorTs),
            JsonOptions);
        await WriteTextBlobAsync(entryPath, entryJson, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BackupRecord?> GetBackupRecordAsync(
        string backupId,
        CancellationToken ct = default)
    {
        var indexPath = StoragePathHelper.IndexBlobPath(backupId);
        var folderPath = await ReadTextBlobAsync(indexPath, ct).ConfigureAwait(false);
        if (folderPath is null) return null;

        return await ReadManifestAsync(folderPath, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BackupRecord?> GetLatestBackupAsync(
        string databaseName,
        BackupType backupType,
        CancellationToken ct = default)
    {
        var now  = DateTimeOffset.UtcNow;
        string type = backupType.ToString().ToLowerInvariant();

        // Search up to 4 quarters back. Secondary index folders are sorted
        // lexicographically = chronologically, so the last folder is the latest.
        for (int i = 0; i < 4; i++)
        {
            var quarter = now.AddMonths(-3 * i);
            int q       = ((quarter.Month - 1) / 3) + 1;
            var prefix  = StoragePathHelper.SecondaryIndexQuarterPrefix(
                quarter.Year, q, type, databaseName);

            string? latestFolder = null;
            await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                               .ConfigureAwait(false))
                latestFolder = folder;   // last wins — lexicographic = chronological

            if (latestFolder is null) continue;

            var entry = await ReadSecondaryIndexEntryAsync(latestFolder, ct)
                .ConfigureAwait(false);
            if (entry is null) continue;

            var record = await ReadManifestAsync(entry.Folder, ct).ConfigureAwait(false);
            if (record is not null) return record;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<BackupRecord?> GetFullBackupForQuarterAsync(
        string databaseName,
        string quarterLabel,
        CancellationToken ct = default)
    {
        var parts = quarterLabel.Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int year) ||
            parts[1].Length < 2)
            return null;

        int q = parts[1][1] - '0';
        if (q < 1 || q > 4) return null;

        var prefix = StoragePathHelper.SecondaryIndexQuarterPrefix(year, q, "full", databaseName);

        string? latestFolder = null;
        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                           .ConfigureAwait(false))
            latestFolder = folder;

        if (latestFolder is null) return null;

        var entry = await ReadSecondaryIndexEntryAsync(latestFolder, ct).ConfigureAwait(false);
        if (entry is null) return null;

        return await ReadManifestAsync(entry.Folder, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Full backup listing
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var quarters = QuartersInRange(from, to);
        var entryTasks = new List<Task<BackupRecord?>>();

        foreach (var (year, q) in quarters)
        {
            // When databaseName is null we must search all databases; enumerate
            // the type-level prefix and iterate per-database sub-prefixes.
            var dbNames = await ListSecondaryIndexDatabasesAsync(year, q, "full", ct)
                .ConfigureAwait(false);

            foreach (var db in dbNames)
            {
                if (databaseName is not null &&
                    !string.Equals(db, databaseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var prefix = StoragePathHelper.SecondaryIndexQuarterPrefix(year, q, "full", db);

                await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                                   .ConfigureAwait(false))
                {
                    var parsed = StoragePathHelper.ParseSecondaryIndexFolder(folder);
                    if (parsed is null) continue;
                    if (parsed.Value.CompletedAt < from || parsed.Value.CompletedAt > to) continue;

                    // Capture for parallel manifest load.
                    var captured = folder;
                    entryTasks.Add(LoadManifestViaSecondaryIndexAsync(captured, ct));
                }
            }
        }

        var records = await Task.WhenAll(entryTasks).ConfigureAwait(false);
        var results = records.Where(r => r is not null).Cast<BackupRecord>().ToList();
        results.Sort((a, b) => a.CompletedAtUtc.CompareTo(b.CompletedAtUtc));
        return results.AsReadOnly();
    }

    // Returns distinct (year, quarter) pairs covering the full [from, to] span.
    private static IEnumerable<(int Year, int Quarter)> QuartersInRange(
        DateTimeOffset from, DateTimeOffset to)
    {
        var year = from.Year;
        var q    = ((from.Month - 1) / 3) + 1;
        var endYear = to.Year;
        var endQ    = ((to.Month - 1) / 3) + 1;

        while (year < endYear || (year == endYear && q <= endQ))
        {
            yield return (year, q);
            q++;
            if (q > 4) { q = 1; year++; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Restore chain resolution
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupRecord>> ResolveRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default)
    {
        var fullBackup = await FindLatestEligibleFullBackupAsync(
            databaseName, targetDate, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No full backup found for database '{databaseName}' " +
                $"on or before {targetDate:yyyy-MM-dd}. Cannot restore.");

        var targetTs  = targetDate.ToUnixTimeSeconds();
        var chain     = new List<BackupRecord> { fullBackup };
        int q         = ((fullBackup.CompletedAtUtc.Month - 1) / 3) + 1;

        // Collect eligible checkpoint and incremental index folders in parallel.
        var checkpointFolders = new List<string>();
        var incrementalFolders = new List<string>();

        var checkpointPrefix = StoragePathHelper.SecondaryIndexQuarterPrefix(
            fullBackup.CompletedAtUtc.Year, q, "checkpoint", databaseName);
        var incrementalPrefix = StoragePathHelper.SecondaryIndexQuarterPrefix(
            fullBackup.CompletedAtUtc.Year, q, "incremental", databaseName);

        await foreach (var folder in _storage.ListFoldersAsync(checkpointPrefix, ct)
                           .ConfigureAwait(false))
        {
            var parsed = StoragePathHelper.ParseSecondaryIndexFolder(folder);
            if (parsed is null) continue;
            long completedTs = parsed.Value.CompletedAt.ToUnixTimeSeconds();
            if (completedTs > fullBackup.AnchorTs && completedTs <= targetTs)
                checkpointFolders.Add(folder);
        }

        await foreach (var folder in _storage.ListFoldersAsync(incrementalPrefix, ct)
                           .ConfigureAwait(false))
        {
            var parsed = StoragePathHelper.ParseSecondaryIndexFolder(folder);
            if (parsed is null) continue;
            long completedTs = parsed.Value.CompletedAt.ToUnixTimeSeconds();
            if (completedTs > fullBackup.AnchorTs && completedTs <= targetTs)
                incrementalFolders.Add(folder);
        }

        // Load manifests for eligible records in parallel.
        var checkpointTasks  = checkpointFolders.Select(f => LoadManifestViaSecondaryIndexAsync(f, ct));
        var incrementalTasks = incrementalFolders.Select(f => LoadManifestViaSecondaryIndexAsync(f, ct));

        var checkpoints  = (await Task.WhenAll(checkpointTasks).ConfigureAwait(false))
            .Where(r => r is not null).Cast<BackupRecord>()
            .OrderBy(r => r.AnchorTs).ToList();
        var incrementals = (await Task.WhenAll(incrementalTasks).ConfigureAwait(false))
            .Where(r => r is not null).Cast<BackupRecord>()
            .OrderBy(r => r.CompletedAtUtc).ToList();

        var lastAnchorTs = checkpoints.Count > 0
            ? checkpoints[^1].AnchorTs
            : fullBackup.AnchorTs;

        chain.AddRange(checkpoints);
        chain.AddRange(incrementals.Where(r => r.AnchorTs > lastAnchorTs));

        return chain.AsReadOnly();
    }

    // Searches backwards through quarters (up to 2 years) for the most recent
    // full backup on or before targetDate, using the secondary index.
    private async Task<BackupRecord?> FindLatestEligibleFullBackupAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct)
    {
        int year = targetDate.Year;
        int q    = ((targetDate.Month - 1) / 3) + 1;

        for (int i = 0; i < 8; i++)
        {
            var prefix = StoragePathHelper.SecondaryIndexQuarterPrefix(year, q, "full", databaseName);

            // Iterate in order; last eligible folder is the latest on or before targetDate.
            string? bestFolder = null;
            await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                               .ConfigureAwait(false))
            {
                var parsed = StoragePathHelper.ParseSecondaryIndexFolder(folder);
                if (parsed is null) continue;
                if (parsed.Value.CompletedAt <= targetDate)
                    bestFolder = folder;
            }

            if (bestFolder is not null)
            {
                var entry = await ReadSecondaryIndexEntryAsync(bestFolder, ct).ConfigureAwait(false);
                if (entry is not null)
                    return await ReadManifestAsync(entry.Folder, ct).ConfigureAwait(false);
            }

            q--;
            if (q < 1) { q = 4; year--; }
        }

        return null;
    }

    // ------------------------------------------------------------------ //
    //  RestoreRecord persistence
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task SaveRestoreRecordAsync(
        RestoreRecord record,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var blobPath = StoragePathHelper.RestoreRequestBlobPath(record.RestoreId);
        await WriteTextBlobAsync(blobPath, json, ct).ConfigureAwait(false);

        // Write the monthly index pointer so restore records can be listed
        // by date range without scanning the full restores/ prefix.
        var pointerPath = StoragePathHelper.RestoreIndexPointerBlobPath(
            record.RestoreId, record.RequestedAtUtc);
        var pointerJson = JsonSerializer.Serialize(
            new RestoreIndexPointer(record.RestoreId, record.RequestedAtUtc),
            JsonOptions);
        await WriteTextBlobAsync(pointerPath, pointerJson, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateRestoreRecordAsync(
        RestoreRecord record,
        CancellationToken ct = default)
    {
        // Overwrite the same blob with updated status/completedAt.
        // The index pointer is never updated — it records creation time only.
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var blobPath = StoragePathHelper.RestoreRequestBlobPath(record.RestoreId);
        await WriteTextBlobAsync(blobPath, json, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Restore record listing and lookup
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<IReadOnlyList<RestoreRecord>> ListRestoreRecordsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var results = new List<RestoreRecord>();

        foreach (var (year, month) in MonthsInRange(from, to))
        {
            var prefix = StoragePathHelper.RestoreIndexMonthPrefix(year, month);
            await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                               .ConfigureAwait(false))
            {
                var pointerPath = $"{folder}{StoragePathHelper.RestoreIndexPointerFileName}";
                var pointerJson = await ReadTextBlobAsync(pointerPath, ct).ConfigureAwait(false);
                if (pointerJson is null) continue;

                RestoreIndexPointer? pointer;
                try { pointer = JsonSerializer.Deserialize<RestoreIndexPointer>(pointerJson, JsonOptions); }
                catch { continue; }
                if (pointer is null) continue;

                if (pointer.RequestedAtUtc < from || pointer.RequestedAtUtc > to) continue;

                var record = await GetRestoreRecordAsync(pointer.RestoreId, ct).ConfigureAwait(false);
                if (record is not null)
                    results.Add(record);
            }
        }

        results.Sort((a, b) => a.RequestedAtUtc.CompareTo(b.RequestedAtUtc));
        return results.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<RestoreRecord?> GetRestoreRecordAsync(
        string restoreId,
        CancellationToken ct = default)
    {
        var blobPath = StoragePathHelper.RestoreRequestBlobPath(restoreId);
        var json = await ReadTextBlobAsync(blobPath, ct).ConfigureAwait(false);
        if (json is null) return null;

        try { return JsonSerializer.Deserialize<RestoreRecord>(json, JsonOptions); }
        catch { return null; }
    }

    /// <inheritdoc />
    public Task<BackupRecord?> GetBackupRecordByFolderAsync(
        string folderPath,
        CancellationToken ct = default)
        => ReadManifestAsync(folderPath, ct);

    // ------------------------------------------------------------------ //
    //  Secondary index queries
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<int> CountIncrementalsSinceAsync(
        string databaseName,
        int year,
        int quarter,
        DateTimeOffset chainRootCompletedAt,
        CancellationToken ct = default)
    {
        var prefix = StoragePathHelper.SecondaryIndexQuarterPrefix(
            year, quarter, "incremental", databaseName);
        int count = 0;
        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                           .ConfigureAwait(false))
        {
            var parsed = StoragePathHelper.ParseSecondaryIndexFolder(folder);
            if (parsed is not null && parsed.Value.CompletedAt > chainRootCompletedAt)
                count++;
        }
        return count;
    }

    /// <inheritdoc />
    public async Task<bool> IsSecondaryIndexPopulatedAsync(CancellationToken ct = default)
    {
        await foreach (var _ in _storage
            .ListFoldersAsync(StoragePathHelper.SecondaryIndexRootPrefix, ct)
            .ConfigureAwait(false))
            return true;
        return false;
    }

    // ------------------------------------------------------------------ //
    //  Index validate / rebuild
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<IndexValidationResult> ValidateIndexAsync(CancellationToken ct = default)
    {
        var issues = new List<string>();
        int totalEntries = 0;
        int dangling     = 0;
        int unindexed    = 0;

        // Phase 1: walk every secondary index entry and verify its manifest exists.
        var typeNames = new[] { "full", "incremental", "checkpoint" };
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 8; i++)
        {
            var quarter = now.AddMonths(-3 * i);
            int q       = ((quarter.Month - 1) / 3) + 1;

            foreach (var type in typeNames)
            {
                var typePfx = $"{StoragePathHelper.SecondaryIndexRootPrefix}{quarter.Year}/Q{q}/{type}/";
                var databases = await ListSecondaryIndexDatabasesAsync(quarter.Year, q, type, ct)
                    .ConfigureAwait(false);

                foreach (var db in databases)
                {
                    var dbPfx = StoragePathHelper.SecondaryIndexQuarterPrefix(quarter.Year, q, type, db);
                    await foreach (var folder in _storage.ListFoldersAsync(dbPfx, ct)
                                       .ConfigureAwait(false))
                    {
                        totalEntries++;
                        var entry = await ReadSecondaryIndexEntryAsync(folder, ct).ConfigureAwait(false);
                        if (entry is null)
                        {
                            dangling++;
                            issues.Add($"Secondary index entry has no content: {folder}");
                            continue;
                        }
                        var manifestPath = StoragePathHelper.ManifestBlobPath(entry.Folder);
                        if (!await _storage.BlobExistsAsync(manifestPath, ct).ConfigureAwait(false))
                        {
                            dangling++;
                            issues.Add($"Index entry points to missing manifest: {entry.Folder}");
                        }
                    }
                }
            }
        }

        // Phase 2: walk manifests and verify each has a secondary index entry.
        foreach (var type in typeNames)
        {
            for (int i = 0; i < 8; i++)
            {
                var quarter = now.AddMonths(-3 * i);
                int q       = ((quarter.Month - 1) / 3) + 1;
                var prefix  = $"{type}/{quarter.Year}/Q{q}/";

                await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                                   .ConfigureAwait(false))
                {
                    var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
                    if (record is null) continue;

                    int rq = ((record.CompletedAtUtc.Month - 1) / 3) + 1;
                    var expectedFolder = StoragePathHelper.SecondaryIndexFolderPath(
                        record.CompletedAtUtc.Year, rq,
                        record.BackupType.ToString().ToLowerInvariant(),
                        record.DatabaseName, record.CompletedAtUtc, record.BackupId);

                    var entryPath = $"{expectedFolder}{StoragePathHelper.SecondaryIndexEntryFileName}";
                    if (!await _storage.BlobExistsAsync(entryPath, ct).ConfigureAwait(false))
                    {
                        unindexed++;
                        issues.Add($"Manifest has no secondary index entry: {folder}");
                    }
                }
            }
        }

        return new IndexValidationResult
        {
            TotalIndexEntries  = totalEntries,
            DanglingEntries    = dangling,
            UnindexedManifests = unindexed,
            Issues             = issues.AsReadOnly()
        };
    }

    /// <inheritdoc />
    public async Task<IndexRebuildResult> RebuildIndexAsync(
        bool dryRun = false,
        CancellationToken ct = default)
    {
        int added   = 0;
        int removed = 0;

        var typeNames = new[] { "full", "incremental", "checkpoint" };
        var now = DateTimeOffset.UtcNow;

        // Phase 1: for every manifest without a secondary index entry, write one.
        foreach (var type in typeNames)
        {
            for (int i = 0; i < 8; i++)
            {
                var quarter = now.AddMonths(-3 * i);
                int q       = ((quarter.Month - 1) / 3) + 1;
                var prefix  = $"{type}/{quarter.Year}/Q{q}/";

                await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                                   .ConfigureAwait(false))
                {
                    var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
                    if (record is null) continue;

                    int rq = ((record.CompletedAtUtc.Month - 1) / 3) + 1;
                    var entryPath = StoragePathHelper.SecondaryIndexEntryBlobPath(
                        record.CompletedAtUtc.Year, rq,
                        record.BackupType.ToString().ToLowerInvariant(),
                        record.DatabaseName, record.CompletedAtUtc, record.BackupId);

                    if (!await _storage.BlobExistsAsync(entryPath, ct).ConfigureAwait(false))
                    {
                        added++;
                        if (!dryRun)
                        {
                            var entryJson = JsonSerializer.Serialize(
                                new SecondaryIndexEntry(
                                    record.BackupId, record.BackupFolder, record.AnchorTs),
                                JsonOptions);
                            await WriteTextBlobAsync(entryPath, entryJson, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        // Phase 2: remove dangling secondary index entries (no manifest).
        for (int i = 0; i < 8; i++)
        {
            var quarter = now.AddMonths(-3 * i);
            int q       = ((quarter.Month - 1) / 3) + 1;

            foreach (var type in typeNames)
            {
                var databases = await ListSecondaryIndexDatabasesAsync(quarter.Year, q, type, ct)
                    .ConfigureAwait(false);

                foreach (var db in databases)
                {
                    var dbPfx = StoragePathHelper.SecondaryIndexQuarterPrefix(quarter.Year, q, type, db);
                    await foreach (var folder in _storage.ListFoldersAsync(dbPfx, ct)
                                       .ConfigureAwait(false))
                    {
                        var entry = await ReadSecondaryIndexEntryAsync(folder, ct).ConfigureAwait(false);
                        if (entry is null) continue;

                        var manifestPath = StoragePathHelper.ManifestBlobPath(entry.Folder);
                        if (!await _storage.BlobExistsAsync(manifestPath, ct).ConfigureAwait(false))
                        {
                            removed++;
                            if (!dryRun)
                            {
                                var entryPath = $"{folder}{StoragePathHelper.SecondaryIndexEntryFileName}";
                                await _storage.DeleteBlobAsync(entryPath, ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }

        return new IndexRebuildResult
        {
            EntriesAdded   = added,
            OrphansRemoved = removed,
            WasDryRun      = dryRun
        };
    }

    // ------------------------------------------------------------------ //
    //  Health check
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)>
        CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // A lightweight probe: check whether the index prefix exists
            await foreach (var _ in _storage
                .ListFoldersAsync(StoragePathHelper.IndexPrefix, ct)
                .ConfigureAwait(false))
            {
                break; // We only need the first result to confirm connectivity
            }
            sw.Stop();
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds,
                $"Metadata repository health check failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    private async Task<SecondaryIndexEntry?> ReadSecondaryIndexEntryAsync(
        string indexFolder, CancellationToken ct)
    {
        var path = $"{indexFolder}{StoragePathHelper.SecondaryIndexEntryFileName}";
        var json = await ReadTextBlobAsync(path, ct).ConfigureAwait(false);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<SecondaryIndexEntry>(json, JsonOptions); }
        catch { return null; }
    }

    // Reads the secondary index entry for a folder then loads the full manifest.
    private async Task<BackupRecord?> LoadManifestViaSecondaryIndexAsync(
        string indexFolder, CancellationToken ct)
    {
        var entry = await ReadSecondaryIndexEntryAsync(indexFolder, ct).ConfigureAwait(false);
        if (entry is null) return null;
        return await ReadManifestAsync(entry.Folder, ct).ConfigureAwait(false);
    }

    // Lists database names that appear exactly one level under the type prefix.
    // Filters to depth+1 so that the mock (which returns all descendants) and
    // real Azure storage (which returns one level) both behave correctly.
    private async Task<List<string>> ListSecondaryIndexDatabasesAsync(
        int year, int q, string type, CancellationToken ct)
    {
        var prefix     = $"{StoragePathHelper.SecondaryIndexRootPrefix}{year}/Q{q}/{type}/";
        int prefixDepth = prefix.TrimEnd('/').Split('/').Length;
        var dbs        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct).ConfigureAwait(false))
        {
            var parts = folder.TrimEnd('/').Split('/');
            if (parts.Length == prefixDepth + 1)
                dbs.Add(parts[^1]);
        }
        return [.. dbs];
    }

    private async Task<BackupRecord?> ReadManifestAsync(
        string folderPath,
        CancellationToken ct)
    {
        var json = await _storage
            .ReadManifestAsync(folderPath, ct)
            .ConfigureAwait(false);
        if (json is null) return null;

        return JsonSerializer.Deserialize<BackupRecord>(json, JsonOptions);
    }

    private async Task WriteTextBlobAsync(
        string blobPath,
        string content,
        CancellationToken ct)
    {
        await using var stream = await _storage
            .OpenWriteAsync(blobPath, ct)
            .ConfigureAwait(false);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadTextBlobAsync(
        string blobPath,
        CancellationToken ct)
    {
        try
        {
            await using var stream = await _storage
                .OpenReadAsync(blobPath, ct)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string ExtractBackupId(string folderPath)
    {
        // folder path format: full/{year}/Q{q}/{backupId}/
        var segments = folderPath.TrimEnd('/').Split('/');
        return segments[^1];
    }

    /// <summary>
    /// Enumerates all (year, month) pairs covered by the inclusive range [from, to].
    /// </summary>
    private static IEnumerable<(int Year, int Month)> MonthsInRange(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var current = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end     = new DateTimeOffset(to.Year,   to.Month,   1, 0, 0, 0, TimeSpan.Zero);
        while (current <= end)
        {
            yield return (current.Year, current.Month);
            current = current.AddMonths(1);
        }
    }

    // Lightweight pointer stored in _restore_index for date-range listing.
    private sealed record RestoreIndexPointer(
        [property: System.Text.Json.Serialization.JsonPropertyName("restoreId")]
        string RestoreId,
        [property: System.Text.Json.Serialization.JsonPropertyName("requestedAtUtc")]
        DateTimeOffset RequestedAtUtc);
}
