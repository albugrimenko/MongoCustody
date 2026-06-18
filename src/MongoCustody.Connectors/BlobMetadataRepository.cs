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

        // The manifest itself is already written by the engine as the final
        // seal step. Here we write the lightweight index pointer so lookups
        // by backupId don't require scanning the container.
        var indexPath = StoragePathHelper.IndexBlobPath(record.BackupId);
        await WriteTextBlobAsync(indexPath, record.BackupFolder, ct)
            .ConfigureAwait(false);

        // Persist the full record to the manifest path as well, so this
        // repository is self-contained when used independently of the engine.
        await _storage
            .WriteManifestAsync(record.BackupFolder, json, ct)
            .ConfigureAwait(false);
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
        // Enumerate folders for the current and previous quarter,
        // pick the latest manifest that matches type + database.
        var now = DateTimeOffset.UtcNow;
        var quartersToSearch = new[] { now, now.AddMonths(-3) };

        foreach (var quarter in quartersToSearch)
        {
            var prefix = backupType switch
            {
                BackupType.Full =>
                    $"full/{quarter.Year}/Q{((quarter.Month - 1) / 3) + 1}/",
                BackupType.Incremental =>
                    $"incremental/{quarter.Year}/Q{((quarter.Month - 1) / 3) + 1}/",
                BackupType.Checkpoint =>
                    $"checkpoint/{quarter.Year}/Q{((quarter.Month - 1) / 3) + 1}/",
                _ => throw new ArgumentOutOfRangeException(nameof(backupType))
            };

            BackupRecord? latest = null;

            await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                               .ConfigureAwait(false))
            {
                var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
                if (record is null) continue;
                if (record.DatabaseName != databaseName) continue;
                if (record.BackupType != backupType) continue;

                if (latest is null ||
                    record.CompletedAtUtc > latest.CompletedAtUtc)
                    latest = record;
            }

            if (latest is not null) return latest;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<BackupRecord?> GetFullBackupForQuarterAsync(
        string databaseName,
        string quarterLabel,
        CancellationToken ct = default)
    {
        // Parse year and quarter number from label, e.g. "2025-Q1"
        var parts = quarterLabel.Split('-');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int year) ||
            parts[1].Length < 2)
            return null;

        int q = parts[1][1] - '0';
        var prefix = $"full/{year}/Q{q}/";

        BackupRecord? result = null;

        await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                           .ConfigureAwait(false))
        {
            var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
            if (record is null) continue;
            if (record.DatabaseName != databaseName) continue;
            if (record.BackupType != BackupType.Full) continue;

            if (result is null ||
                record.CompletedAtUtc > result.CompletedAtUtc)
                result = record;
        }

        return result;
    }

    // ------------------------------------------------------------------ //
    //  Full backup listing
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        // Enumerate every quarter whose date range overlaps [from, to].
        var quarters = QuartersInRange(from, to);
        var results  = new List<BackupRecord>();

        foreach (var (year, q) in quarters)
        {
            var prefix = $"full/{year}/Q{q}/";
            await foreach (var folder in _storage.ListFoldersAsync(prefix, ct)
                               .ConfigureAwait(false))
            {
                var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
                if (record is null) continue;
                if (!string.Equals(record.DatabaseName, databaseName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (record.BackupType != BackupType.Full) continue;
                if (record.CompletedAtUtc < from || record.CompletedAtUtc > to) continue;

                results.Add(record);
            }
        }

        results.Sort((a, b) => a.CompletedAtUtc.CompareTo(b.CompletedAtUtc));
        return results.AsReadOnly();
    }

    // Returns distinct (year, quarter) pairs covering the full [from, to] span.
    private static IEnumerable<(int Year, int Quarter)> QuartersInRange(
        DateTimeOffset from, DateTimeOffset to)
    {
        var current = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end     = new DateTimeOffset(to.Year,   to.Month,   1, 0, 0, 0, TimeSpan.Zero);

        var seen = new HashSet<(int, int)>();
        while (current <= end)
        {
            int q = ((current.Month - 1) / 3) + 1;
            if (seen.Add((current.Year, q)))
                yield return (current.Year, q);
            current = current.AddMonths(1);
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
        // 1. Find the full backup that anchors the quarter containing targetDate
        var quarterLabel = StoragePathHelper.QuarterLabel(targetDate);
        var fullBackup = await GetFullBackupForQuarterAsync(
            databaseName, quarterLabel, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No full backup found for database '{databaseName}' " +
                $"in quarter {quarterLabel}. Cannot resolve restore chain.");

        var chain = new List<BackupRecord> { fullBackup };
        var fullFolder = fullBackup.BackupFolder;

        // 2. Walk checkpoints: include all checkpoints whose anchorTs <= targetDate unix
        var targetTs = targetDate.ToUnixTimeSeconds();
        var checkpointPrefix = StoragePathHelper.CheckpointQuarterPrefix(
            targetDate, ExtractBackupId(fullFolder));

        var checkpoints = new List<BackupRecord>();
        await foreach (var folder in _storage.ListFoldersAsync(checkpointPrefix, ct)
                           .ConfigureAwait(false))
        {
            var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
            if (record is null) continue;
            if (record.AnchorTs <= targetTs)
                checkpoints.Add(record);
        }

        // Sort checkpoints chronologically; include all that precede target
        checkpoints.Sort((a, b) =>
            a.CompletedAtUtc.CompareTo(b.CompletedAtUtc));
        chain.AddRange(checkpoints);

        // 3. Determine the anchor after the last checkpoint (or full backup)
        var lastAnchorTs = chain[^1].AnchorTs;

        // 4. Add daily incrementals between last checkpoint anchor and targetTs
        var incrementalPrefix = StoragePathHelper.IncrementalQuarterPrefix(
            targetDate, ExtractBackupId(fullFolder));

        var incrementals = new List<BackupRecord>();
        await foreach (var folder in _storage.ListFoldersAsync(incrementalPrefix, ct)
                           .ConfigureAwait(false))
        {
            var record = await ReadManifestAsync(folder, ct).ConfigureAwait(false);
            if (record is null) continue;
            // Include incrementals that were generated after the last chain
            // element (full or checkpoint) and don't exceed the target date.
            if (record.AnchorTs > lastAnchorTs && record.AnchorTs <= targetTs)
                incrementals.Add(record);
        }

        incrementals.Sort((a, b) =>
            a.CompletedAtUtc.CompareTo(b.CompletedAtUtc));
        chain.AddRange(incrementals);

        return chain.AsReadOnly();
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
    }

    /// <inheritdoc />
    public async Task UpdateRestoreRecordAsync(
        RestoreRecord record,
        CancellationToken ct = default)
    {
        // Overwrite the same blob with updated status/completedAt
        await SaveRestoreRecordAsync(record, ct).ConfigureAwait(false);
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
}
