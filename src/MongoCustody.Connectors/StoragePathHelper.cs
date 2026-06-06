namespace MongoCustody.Connectors;

/// <summary>
/// Builds and parses all blob storage paths used by MongoCustody,
/// encoding the conventions described in README.md § Storage Layout
/// and "Backup and recovery plan.md" § 7.1.
///
/// All paths are relative to the container root and use forward-slash
/// separators regardless of the host OS.
/// </summary>
internal static class StoragePathHelper
{
    internal const string ManifestFileName = "manifest.json";
    internal const string RestoreRequestFileName = "restore-request.json";
    internal const string IndexPrefix = "_index/";

    // ------------------------------------------------------------------ //
    //  Quarter helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the quarter label for a given UTC date, e.g. <c>2025-Q1</c>.
    /// </summary>
    internal static string QuarterLabel(DateTimeOffset utc)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"{utc.Year}-Q{q}";
    }

    /// <summary>
    /// Returns the 1-based week number within the quarter (1–13) for a
    /// given UTC date. Week 1 starts on the first day of the quarter.
    /// </summary>
    internal static int WeekOfQuarter(DateTimeOffset utc)
    {
        var quarterStart = QuarterStart(utc);
        int dayOfQuarter = (int)(utc.Date - quarterStart.Date).TotalDays;
        return (dayOfQuarter / 7) + 1;
    }

    /// <summary>
    /// Returns midnight UTC of the first day of the quarter containing
    /// the supplied date.
    /// </summary>
    internal static DateTimeOffset QuarterStart(DateTimeOffset utc)
    {
        int startMonth = ((utc.Month - 1) / 3 * 3) + 1;
        return new DateTimeOffset(utc.Year, startMonth, 1, 0, 0, 0, TimeSpan.Zero);
    }

    // ------------------------------------------------------------------ //
    //  Folder paths
    // ------------------------------------------------------------------ //

    /// <summary>
    /// <c>full/{year}/Q{q}/{backupId}/</c>
    /// </summary>
    internal static string FullBackupFolder(DateTimeOffset utc, string backupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"full/{utc.Year}/Q{q}/{backupId}/";
    }

    /// <summary>
    /// <c>incremental/{year}/Q{q}/{fullBackupId}/{yyyy-MM-ddTHHmmZ}/</c>
    /// The timestamp is truncated to the minute and formatted in ISO 8601 UTC.
    /// Lexicographic sort order equals chronological order.
    /// </summary>
    internal static string IncrementalBackupFolder(
        DateTimeOffset utc,
        string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        string ts = utc.ToUniversalTime().ToString("yyyy-MM-ddTHHmmZ");
        return $"incremental/{utc.Year}/Q{q}/{fullBackupId}/{ts}/";
    }

    /// <summary>
    /// <c>checkpoint/{year}/Q{q}/{fullBackupId}/week-{nn}T{HHmmZ}/</c>
    /// </summary>
    internal static string CheckpointFolder(
        DateTimeOffset utc,
        string fullBackupId,
        int weekNumber)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        string time = utc.ToUniversalTime().ToString("HHmmZ");
        return $"checkpoint/{utc.Year}/Q{q}/{fullBackupId}/week-{weekNumber:D2}T{time}/";
    }

    // ------------------------------------------------------------------ //
    //  Blob paths
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the blob path for a numbered chunk file within a backup folder.
    /// <example><c>full/2025/Q1/{id}/{db}.{col}.part-001.bson.gz</c></example>
    /// </summary>
    internal static string ChunkBlobPath(
        string folderPath,
        string databaseName,
        string collectionName,
        int chunkIndex)
        => $"{folderPath}{databaseName}.{collectionName}.part-{chunkIndex:D3}.bson.gz";

    /// <summary>
    /// Returns the <c>manifest.json</c> blob path for a backup folder.
    /// </summary>
    internal static string ManifestBlobPath(string folderPath)
        => $"{folderPath}{ManifestFileName}";

    /// <summary>
    /// Returns the <c>restore-request.json</c> blob path for a restore
    /// folder. The restore folder mirrors the full backup folder path
    /// with a <c>restores/{restoreId}/</c> suffix.
    /// </summary>
    internal static string RestoreRequestBlobPath(string restoreId)
        => $"restores/{restoreId}/{RestoreRequestFileName}";

    /// <summary>
    /// Returns the index pointer blob path used for O(1) lookup by backup ID.
    /// <example><c>_index/{backupId}</c></example>
    /// </summary>
    internal static string IndexBlobPath(string backupId)
        => $"{IndexPrefix}{backupId}";

    // ------------------------------------------------------------------ //
    //  Container listing prefixes
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the listing prefix for all incremental folders that belong
    /// to a given full backup within a quarter.
    /// <example><c>incremental/2025/Q1/{fullBackupId}/</c></example>
    /// </summary>
    internal static string IncrementalQuarterPrefix(
        DateTimeOffset utc,
        string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"incremental/{utc.Year}/Q{q}/{fullBackupId}/";
    }

    /// <summary>
    /// Returns the listing prefix for all checkpoint folders that belong
    /// to a given full backup within a quarter.
    /// </summary>
    internal static string CheckpointQuarterPrefix(
        DateTimeOffset utc,
        string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"checkpoint/{utc.Year}/Q{q}/{fullBackupId}/";
    }

    // ------------------------------------------------------------------ //
    //  Parsing
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Extracts the UTC timestamp embedded in an incremental folder name
    /// of the form <c>yyyy-MM-ddTHHmmZ</c>.
    /// Returns <c>null</c> if the segment cannot be parsed.
    /// </summary>
    internal static DateTimeOffset? ParseIncrementalFolderTimestamp(string folderPath)
    {
        // Last non-empty segment is the timestamp folder name
        var segment = folderPath.TrimEnd('/').Split('/').LastOrDefault();
        if (segment is null) return null;

        if (DateTimeOffset.TryParseExact(
                segment,
                "yyyy-MM-ddTHHmmZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var result))
            return result;

        return null;
    }
}
