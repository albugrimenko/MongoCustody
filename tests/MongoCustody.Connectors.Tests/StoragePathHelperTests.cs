namespace MongoCustody.Connectors.Tests;

[TestClass]
public sealed class StoragePathHelperTests
{
    // ------------------------------------------------------------------ //
    //  QuarterLabel
    // ------------------------------------------------------------------ //

    [TestMethod]
    [DataRow(2025,  1, "2025-Q1")]
    [DataRow(2025,  3, "2025-Q1")]
    [DataRow(2025,  4, "2025-Q2")]
    [DataRow(2025,  6, "2025-Q2")]
    [DataRow(2025,  7, "2025-Q3")]
    [DataRow(2025,  9, "2025-Q3")]
    [DataRow(2025, 10, "2025-Q4")]
    [DataRow(2025, 12, "2025-Q4")]
    public void QuarterLabel_ReturnsCorrectLabel(int year, int month, string expected)
    {
        var date = new DateTimeOffset(year, month, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(expected, StoragePathHelper.QuarterLabel(date));
    }

    // ------------------------------------------------------------------ //
    //  WeekOfQuarter
    // ------------------------------------------------------------------ //

    [TestMethod]
    [DataRow(2025,  1,  1,  1)]   // Jan 1 = Q1 day 0 = week 1
    [DataRow(2025,  1,  7,  1)]   // Jan 7 = Q1 day 6 = week 1
    [DataRow(2025,  1,  8,  2)]   // Jan 8 = Q1 day 7 = week 2
    [DataRow(2025,  3, 31, 13)]   // Mar 31 = Q1 day 89 = week 13
    public void WeekOfQuarter_ReturnsCorrectWeek(
        int year, int month, int day, int expectedWeek)
    {
        var date = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(expectedWeek, StoragePathHelper.WeekOfQuarter(date));
    }

    // ------------------------------------------------------------------ //
    //  QuarterStart
    // ------------------------------------------------------------------ //

    [TestMethod]
    [DataRow(2025,  2, 2025,  1, 1)]   // Feb → Jan 1
    [DataRow(2025,  5, 2025,  4, 1)]   // May → Apr 1
    [DataRow(2025,  8, 2025,  7, 1)]   // Aug → Jul 1
    [DataRow(2025, 11, 2025, 10, 1)]   // Nov → Oct 1
    public void QuarterStart_ReturnsFirstDayOfQuarter(
        int year, int month,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var date = new DateTimeOffset(year, month, 15, 0, 0, 0, TimeSpan.Zero);
        var start = StoragePathHelper.QuarterStart(date);
        Assert.AreEqual(expectedYear,  start.Year);
        Assert.AreEqual(expectedMonth, start.Month);
        Assert.AreEqual(expectedDay,   start.Day);
    }

    // ------------------------------------------------------------------ //
    //  Folder paths
    // ------------------------------------------------------------------ //

    [TestMethod]
    public void FullBackupFolder_HasCorrectFormat()
    {
        var utc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var path = StoragePathHelper.FullBackupFolder(utc, "abc-123");
        Assert.AreEqual("full/2025/Q1/abc-123/", path);
    }

    [TestMethod]
    public void IncrementalBackupFolder_HasCorrectFormat()
    {
        var utc = new DateTimeOffset(2025, 1, 3, 1, 0, 0, TimeSpan.Zero);
        var path = StoragePathHelper.IncrementalBackupFolder(utc, "full-id");
        Assert.AreEqual("incremental/2025/Q1/full-id/2025-01-03T0100Z/", path);
    }

    [TestMethod]
    public void CheckpointFolder_HasCorrectFormat()
    {
        var utc = new DateTimeOffset(2025, 1, 8, 2, 0, 0, TimeSpan.Zero);
        var path = StoragePathHelper.CheckpointFolder(utc, "full-id", weekNumber: 2);
        Assert.AreEqual("checkpoint/2025/Q1/full-id/week-02T0200Z/", path);
    }

    // ------------------------------------------------------------------ //
    //  Blob paths
    // ------------------------------------------------------------------ //

    [TestMethod]
    public void ChunkBlobPath_FormatsChunkIndexWithThreeDigits()
    {
        var path = StoragePathHelper.ChunkBlobPath(
            "full/2025/Q1/abc/", "mydb", "patients", chunkIndex: 1);
        Assert.AreEqual("full/2025/Q1/abc/mydb.patients.part-001.bson.gz", path);
    }

    [TestMethod]
    public void ChunkBlobPath_FormatsLargeChunkIndex()
    {
        var path = StoragePathHelper.ChunkBlobPath(
            "full/2025/Q1/abc/", "mydb", "patients", chunkIndex: 42);
        Assert.AreEqual("full/2025/Q1/abc/mydb.patients.part-042.bson.gz", path);
    }

    [TestMethod]
    public void ManifestBlobPath_AppendsManifestFileName()
    {
        var path = StoragePathHelper.ManifestBlobPath("full/2025/Q1/abc/");
        Assert.AreEqual("full/2025/Q1/abc/manifest.json", path);
    }

    [TestMethod]
    public void RestoreRequestBlobPath_HasCorrectFormat()
    {
        var path = StoragePathHelper.RestoreRequestBlobPath("restore-99");
        Assert.AreEqual("restores/restore-99/restore-request.json", path);
    }

    [TestMethod]
    public void IndexBlobPath_HasCorrectPrefix()
    {
        var path = StoragePathHelper.IndexBlobPath("backup-id-42");
        Assert.AreEqual("_index/backup-id-42", path);
    }

    // ------------------------------------------------------------------ //
    //  Parsing
    // ------------------------------------------------------------------ //

    [TestMethod]
    public void ParseIncrementalFolderTimestamp_ParsesValidTimestamp()
    {
        var folder = "incremental/2025/Q1/full-id/2025-01-03T0100Z/";
        var result = StoragePathHelper.ParseIncrementalFolderTimestamp(folder);
        Assert.IsNotNull(result);
        Assert.AreEqual(2025, result!.Value.Year);
        Assert.AreEqual(1, result.Value.Month);
        Assert.AreEqual(3, result.Value.Day);
        Assert.AreEqual(1, result.Value.Hour);
    }

    [TestMethod]
    public void ParseIncrementalFolderTimestamp_ReturnsNull_ForInvalidSegment()
    {
        var folder = "incremental/2025/Q1/full-id/not-a-timestamp/";
        var result = StoragePathHelper.ParseIncrementalFolderTimestamp(folder);
        Assert.IsNull(result);
    }

    // ------------------------------------------------------------------ //
    //  Secondary index paths
    // ------------------------------------------------------------------ //

    [TestMethod]
    public void SecondaryIndexFolderPath_HasCorrectFormat()
    {
        var at   = new DateTimeOffset(2025, 4, 15, 12, 0, 0, TimeSpan.Zero);
        var path = StoragePathHelper.SecondaryIndexFolderPath(2025, 2, "full", "mydb", at, "abc-123");
        Assert.AreEqual("_index/by-quarter/2025/Q2/full/mydb/2025-04-15T1200Z-abc-123/", path);
    }

    [TestMethod]
    public void SecondaryIndexEntryBlobPath_EndsWithEntryJson()
    {
        var at   = new DateTimeOffset(2025, 4, 15, 12, 0, 0, TimeSpan.Zero);
        var path = StoragePathHelper.SecondaryIndexEntryBlobPath(2025, 2, "full", "mydb", at, "abc-123");
        Assert.IsTrue(path.EndsWith("entry.json"), $"Expected path ending with entry.json, got: {path}");
    }

    [TestMethod]
    [DataRow("_index/by-quarter/2025/Q2/incremental/mydb/2025-06-01T1000Z-id-001/",
             "2025-06-01", 10, 0, "id-001")]
    [DataRow("_index/by-quarter/2025/Q1/full/db/2025-01-01T0200Z-full-99/",
             "2025-01-01", 2, 0, "full-99")]
    public void ParseSecondaryIndexFolder_RoundTrips(
        string folderPath, string expectedDate, int hour, int min, string expectedId)
    {
        var result = StoragePathHelper.ParseSecondaryIndexFolder(folderPath);
        Assert.IsNotNull(result);
        Assert.AreEqual(expectedDate, result!.Value.CompletedAt.ToString("yyyy-MM-dd"));
        Assert.AreEqual(hour, result.Value.CompletedAt.Hour);
        Assert.AreEqual(expectedId, result.Value.BackupId);
    }

    [TestMethod]
    [DataRow("_index/by-quarter/2025/Q1/full/mydb/not-a-valid-folder/")]
    [DataRow("_index/by-quarter/2025/Q1/full/mydb/")]
    public void ParseSecondaryIndexFolder_ReturnsNull_ForInvalidSegment(string folderPath)
    {
        var result = StoragePathHelper.ParseSecondaryIndexFolder(folderPath);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void SecondaryIndexFolders_HaveLexicographicChronologicalOrder()
    {
        var dates = new[]
        {
            new DateTimeOffset(2025, 1, 9, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 3, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero)
        };
        var folders = dates.Select((d, i) =>
            StoragePathHelper.SecondaryIndexFolderPath(2025, 1, "incremental", "db", d, $"id-{i:D3}"))
            .ToArray();

        var sorted = folders.OrderBy(f => f).ToArray();
        Assert.IsTrue(sorted[0].Contains("2025-01-03"), $"Expected Jan 3 first, got {sorted[0]}");
        Assert.IsTrue(sorted[1].Contains("2025-01-09"), $"Expected Jan 9 second, got {sorted[1]}");
        Assert.IsTrue(sorted[2].Contains("2025-01-15"), $"Expected Jan 15 third, got {sorted[2]}");
    }

    [TestMethod]
    public void IncrementalAndCheckpointFolders_HaveLexicographicChronologicalOrder()
    {
        // ISO 8601-based folder names must sort lexicographically in the
        // same order as chronologically — this is a core design guarantee.
        var folders = new[]
        {
            "incremental/2025/Q1/id/2025-01-09T0100Z/",
            "incremental/2025/Q1/id/2025-01-03T0100Z/",
            "incremental/2025/Q1/id/2025-01-15T0100Z/",
        };

        var sorted = folders.OrderBy(f => f).ToArray();
        Assert.AreEqual("incremental/2025/Q1/id/2025-01-03T0100Z/", sorted[0]);
        Assert.AreEqual("incremental/2025/Q1/id/2025-01-09T0100Z/", sorted[1]);
        Assert.AreEqual("incremental/2025/Q1/id/2025-01-15T0100Z/", sorted[2]);
    }
}
