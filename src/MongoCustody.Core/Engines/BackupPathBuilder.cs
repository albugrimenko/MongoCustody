namespace MongoCustody.Core.Engines;

/// <summary>
/// Builds blob storage paths used by the backup engines.
///
/// Mirrors the conventions documented in README.md § Storage Layout and
/// "Backup and recovery plan.md" § 7.1. This class lives in Core so
/// that the engines have zero dependency on the Connectors assembly.
///
/// All paths are relative to the container root and use forward-slash
/// separators regardless of the host OS.
/// </summary>
public static class BackupPathBuilder
{
    public const string ManifestFileName  = "manifest.json";

    // ------------------------------------------------------------------ //
    //  Quarter helpers
    // ------------------------------------------------------------------ //

    /// <summary>Returns <c>2025-Q1</c> style label for a UTC date.</summary>
    public static string QuarterLabel(DateTimeOffset utc)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"{utc.Year}-Q{q}";
    }

    /// <summary>Returns midnight UTC on the first day of the quarter.</summary>
    public static DateTimeOffset QuarterStart(DateTimeOffset utc)
    {
        int startMonth = ((utc.Month - 1) / 3 * 3) + 1;
        return new DateTimeOffset(utc.Year, startMonth, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Returns the 1-based week number within the quarter (1–13).</summary>
    public static int WeekOfQuarter(DateTimeOffset utc)
    {
        var quarterStart = QuarterStart(utc);
        int dayOfQuarter = (int)(utc.Date - quarterStart.Date).TotalDays;
        return (dayOfQuarter / 7) + 1;
    }

    // ------------------------------------------------------------------ //
    //  Folder paths
    // ------------------------------------------------------------------ //

    /// <summary>
    /// <c>full/{year}/Q{q}/{backupId}/</c>
    /// </summary>
    public static string FullBackupFolder(DateTimeOffset utc, string backupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"full/{utc.Year}/Q{q}/{backupId}/";
    }

    /// <summary>
    /// Returns the storage prefix under which all incremental backups for a given
    /// chain root are stored: <c>incremental/{year}/Q{q}/{rootBackupId}/</c>.
    /// </summary>
    public static string IncrementalPrefixForChainRoot(DateTimeOffset rootCompletedAt, string rootBackupId)
    {
        int q = ((rootCompletedAt.Month - 1) / 3) + 1;
        return $"incremental/{rootCompletedAt.Year}/Q{q}/{rootBackupId}/";
    }

    /// <summary>
    /// <c>incremental/{year}/Q{q}/{fullBackupId}/{yyyy-MM-ddTHHmmZ}/</c>
    /// Lexicographic order equals chronological order.
    /// </summary>
    public static string IncrementalBackupFolder(DateTimeOffset utc, string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        string ts = utc.ToUniversalTime().ToString("yyyy-MM-ddTHHmmZ");
        return $"incremental/{utc.Year}/Q{q}/{fullBackupId}/{ts}/";
    }

    /// <summary>
    /// <c>checkpoint/{year}/Q{q}/{fullBackupId}/week-{nn}T{HHmmZ}/</c>
    /// </summary>
    public static string CheckpointFolder(
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
    /// <c>{folder}{db}.{collection}.part-{NNN}.bson.gz</c>
    /// </summary>
    public static string ChunkBlobPath(
        string folderPath,
        string databaseName,
        string collectionName,
        int chunkIndex)
        => $"{folderPath}{databaseName}.{collectionName}.part-{chunkIndex:D3}.bson.gz";

    /// <summary>
    /// <c>{folder}manifest.json</c>
    /// </summary>
    public static string ManifestBlobPath(string folderPath)
        => $"{folderPath}{ManifestFileName}";
}
