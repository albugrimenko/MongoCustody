using MongoCustody.Core.Engines;

namespace MongoCustody.Connectors;

/// <summary>
/// Builds and parses all blob storage paths used by MongoCustody,
/// encoding the conventions described in README.md § Storage Layout
/// and "Backup and recovery plan.md" § 7.1.
///
/// All paths are relative to the container root and use forward-slash
/// separators regardless of the host OS.
///
/// Path-building logic shared with the Core backup engines is delegated
/// to <see cref="BackupPathBuilder"/> — the single source of truth for
/// the path format contract. Connector-specific paths (index, restore
/// request, quarter listing prefixes) are defined here.
/// </summary>
internal static class StoragePathHelper
{
    internal const string ManifestFileName            = BackupPathBuilder.ManifestFileName;
    internal const string RestoreRequestFileName      = "restore-request.json";
    internal const string IndexPrefix                 = "_index/";
    internal const string RestoreIndexPrefix          = "_restore_index/";
    internal const string RestoreIndexPointerFileName = "pointer.json";
    internal const string SecondaryIndexRootPrefix    = "_index/by-quarter/";
    internal const string SecondaryIndexEntryFileName = "entry.json";

    // ------------------------------------------------------------------ //
    //  Quarter helpers — delegate to BackupPathBuilder (single source of truth)
    // ------------------------------------------------------------------ //

    internal static string QuarterLabel(DateTimeOffset utc)
        => BackupPathBuilder.QuarterLabel(utc);

    internal static int WeekOfQuarter(DateTimeOffset utc)
        => BackupPathBuilder.WeekOfQuarter(utc);

    internal static DateTimeOffset QuarterStart(DateTimeOffset utc)
        => BackupPathBuilder.QuarterStart(utc);

    // ------------------------------------------------------------------ //
    //  Folder paths — delegate to BackupPathBuilder
    // ------------------------------------------------------------------ //

    internal static string FullBackupFolder(DateTimeOffset utc, string backupId)
        => BackupPathBuilder.FullBackupFolder(utc, backupId);

    internal static string IncrementalBackupFolder(DateTimeOffset utc, string fullBackupId)
        => BackupPathBuilder.IncrementalBackupFolder(utc, fullBackupId);

    internal static string CheckpointFolder(DateTimeOffset utc, string fullBackupId, int weekNumber)
        => BackupPathBuilder.CheckpointFolder(utc, fullBackupId, weekNumber);

    // ------------------------------------------------------------------ //
    //  Blob paths — delegate shared paths; define Connector-specific ones
    // ------------------------------------------------------------------ //

    internal static string ChunkBlobPath(
        string folderPath,
        string databaseName,
        string collectionName,
        int chunkIndex)
        => BackupPathBuilder.ChunkBlobPath(folderPath, databaseName, collectionName, chunkIndex);

    internal static string ManifestBlobPath(string folderPath)
        => BackupPathBuilder.ManifestBlobPath(folderPath);

    /// <summary>
    /// Returns the <c>restore-request.json</c> blob path for a restore ID.
    /// <example><c>restores/{restoreId}/restore-request.json</c></example>
    /// </summary>
    internal static string RestoreRequestBlobPath(string restoreId)
        => $"restores/{restoreId}/{RestoreRequestFileName}";

    /// <summary>
    /// Returns the index pointer blob path used for O(1) lookup by backup ID.
    /// <example><c>_index/{backupId}</c></example>
    /// </summary>
    internal static string IndexBlobPath(string backupId)
        => $"{IndexPrefix}{backupId}";

    /// <summary>
    /// Returns the folder path for a restore index pointer.
    /// <example><c>_restore_index/2025/02/{restoreId}/</c></example>
    /// </summary>
    internal static string RestoreIndexFolder(string restoreId, DateTimeOffset requestedAt)
        => $"{RestoreIndexPrefix}{requestedAt.Year:D4}/{requestedAt.Month:D2}/{restoreId}/";

    /// <summary>
    /// Returns the pointer blob path within a restore index folder.
    /// <example><c>_restore_index/2025/02/{restoreId}/pointer.json</c></example>
    /// </summary>
    internal static string RestoreIndexPointerBlobPath(string restoreId, DateTimeOffset requestedAt)
        => $"{RestoreIndexFolder(restoreId, requestedAt)}{RestoreIndexPointerFileName}";

    /// <summary>
    /// Returns the listing prefix for all restore index entries in a given month.
    /// <example><c>_restore_index/2025/02/</c></example>
    /// </summary>
    internal static string RestoreIndexMonthPrefix(int year, int month)
        => $"{RestoreIndexPrefix}{year:D4}/{month:D2}/";

    // ------------------------------------------------------------------ //
    //  Container listing prefixes (Connector-specific)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the listing prefix for all incremental folders that belong
    /// to a given full backup within a quarter.
    /// <example><c>incremental/2025/Q1/{fullBackupId}/</c></example>
    /// </summary>
    internal static string IncrementalQuarterPrefix(DateTimeOffset utc, string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"incremental/{utc.Year}/Q{q}/{fullBackupId}/";
    }

    /// <summary>
    /// Returns the listing prefix for all checkpoint folders that belong
    /// to a given full backup within a quarter.
    /// <example><c>checkpoint/2025/Q1/{fullBackupId}/</c></example>
    /// </summary>
    internal static string CheckpointQuarterPrefix(DateTimeOffset utc, string fullBackupId)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"checkpoint/{utc.Year}/Q{q}/{fullBackupId}/";
    }

    // ------------------------------------------------------------------ //
    //  Secondary index paths
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the listing prefix for all secondary index entries of a given
    /// backup type and database in a quarter.
    /// <example><c>_index/by-quarter/2025/Q2/incremental/mydb/</c></example>
    /// </summary>
    internal static string SecondaryIndexQuarterPrefix(
        int year, int q, string type, string databaseName)
        => $"{SecondaryIndexRootPrefix}{year}/Q{q}/{type}/{databaseName}/";

    /// <summary>
    /// Returns the folder path for a single secondary index entry. The folder
    /// name encodes <paramref name="completedAt"/> so listings are chronologically
    /// sorted without any blob reads.
    /// <example><c>_index/by-quarter/2025/Q2/incremental/mydb/2025-06-28T1200Z-{backupId}/</c></example>
    /// </summary>
    internal static string SecondaryIndexFolderPath(
        int year, int q, string type, string databaseName,
        DateTimeOffset completedAt, string backupId)
    {
        string ts = completedAt.ToUniversalTime().ToString("yyyy-MM-ddTHHmmZ");
        return $"{SecondaryIndexRootPrefix}{year}/Q{q}/{type}/{databaseName}/{ts}-{backupId}/";
    }

    /// <summary>
    /// Returns the full blob path for a secondary index entry file.
    /// <example><c>_index/by-quarter/2025/Q2/incremental/mydb/2025-06-28T1200Z-{backupId}/entry.json</c></example>
    /// </summary>
    internal static string SecondaryIndexEntryBlobPath(
        int year, int q, string type, string databaseName,
        DateTimeOffset completedAt, string backupId)
        => $"{SecondaryIndexFolderPath(year, q, type, databaseName, completedAt, backupId)}{SecondaryIndexEntryFileName}";

    /// <summary>
    /// Parses <c>(CompletedAt, BackupId)</c> from the last segment of a secondary
    /// index folder path. Returns <c>null</c> if the segment does not match the
    /// expected <c>{yyyy-MM-ddTHHmmZ}-{backupId}</c> format.
    /// </summary>
    internal static (DateTimeOffset CompletedAt, string BackupId)? ParseSecondaryIndexFolder(
        string folderPath)
    {
        var segment = folderPath.TrimEnd('/').Split('/').LastOrDefault();
        if (segment is null) return null;
        // Format: yyyy-MM-ddTHHmmZ-{backupId}  (timestamp is 16 chars: "yyyy-MM-ddTHHmmZ")
        if (segment.Length <= 16) return null;
        var dash = segment.IndexOf('-', 16); // find the separator after the timestamp
        if (dash < 0) return null;
        var tsPart = segment[..dash];
        var idPart = segment[(dash + 1)..];
        if (string.IsNullOrEmpty(idPart)) return null;
        if (!DateTimeOffset.TryParseExact(
                tsPart, "yyyy-MM-ddTHHmmZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var ts))
            return null;
        return (ts, idPart);
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
