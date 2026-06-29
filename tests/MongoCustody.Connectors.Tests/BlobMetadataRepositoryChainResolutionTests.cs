using System.Text.Json;
using System.Text.Json.Serialization;

namespace MongoCustody.Connectors.Tests;

/// <summary>
/// Unit tests for <see cref="BlobMetadataRepository.ResolveRestoreChainAsync"/>
/// and related metadata query methods.
/// All I/O is intercepted via a mocked <see cref="IStorageConnector"/>.
/// No Azure infrastructure is required.
/// </summary>
[TestClass]
public sealed class BlobMetadataRepositoryChainResolutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private static string Serialize(BackupRecord r)
        => JsonSerializer.Serialize(r, JsonOptions);

    private static BackupRecord MakeFullBackup(
        string backupId,
        string db,
        DateTimeOffset completedAt,
        long anchorTs)
        => new()
        {
            BackupId = backupId,
            BackupType = BackupType.Full,
            BackupFolder = StoragePathHelper.FullBackupFolder(completedAt, backupId),
            DatabaseName = db,
            QuarterLabel = StoragePathHelper.QuarterLabel(completedAt),
            CompletedAtUtc = completedAt,
            AnchorTs = anchorTs,
            QueryFromTs = null
        };

    private static BackupRecord MakeCheckpoint(
        string backupId,
        string db,
        string fullBackupId,
        DateTimeOffset completedAt,
        long queryFromTs,
        long anchorTs,
        int week)
        => new()
        {
            BackupId = backupId,
            BackupType = BackupType.Checkpoint,
            BackupFolder = StoragePathHelper.CheckpointFolder(completedAt, fullBackupId, week),
            DatabaseName = db,
            QuarterLabel = StoragePathHelper.QuarterLabel(completedAt),
            CompletedAtUtc = completedAt,
            QueryFromTs = queryFromTs,
            AnchorTs = anchorTs
        };

    private static BackupRecord MakeIncremental(
        string backupId,
        string db,
        string fullBackupId,
        DateTimeOffset completedAt,
        long queryFromTs,
        long anchorTs)
        => new()
        {
            BackupId = backupId,
            BackupType = BackupType.Incremental,
            BackupFolder = StoragePathHelper.IncrementalBackupFolder(completedAt, fullBackupId),
            DatabaseName = db,
            QuarterLabel = StoragePathHelper.QuarterLabel(completedAt),
            CompletedAtUtc = completedAt,
            QueryFromTs = queryFromTs,
            AnchorTs = anchorTs
        };

    /// <summary>
    /// Builds a mock IStorageConnector that serves both manifest blobs and
    /// secondary index entry blobs for the supplied records.
    /// ListFoldersAsync returns all registered folders (manifest + index) that
    /// match the given prefix — intentionally returns all depths so that
    /// callers must filter to the depth they need.
    /// </summary>
    private static IStorageConnector BuildMockStorage(
        IEnumerable<BackupRecord> records)
    {
        var recordList = records.ToList();
        var manifestsByFolder = recordList.ToDictionary(r => r.BackupFolder, r => Serialize(r));

        // Build secondary index: folder path → entry JSON, plus all listable folders.
        var entryBlobs  = new Dictionary<string, string>();  // entryBlobPath → json
        var allFolders  = new HashSet<string>(manifestsByFolder.Keys);

        foreach (var r in recordList)
        {
            int q     = ((r.CompletedAtUtc.Month - 1) / 3) + 1;
            string tp = r.BackupType.ToString().ToLowerInvariant();
            var indexFolder = StoragePathHelper.SecondaryIndexFolderPath(
                r.CompletedAtUtc.Year, q, tp, r.DatabaseName, r.CompletedAtUtc, r.BackupId);
            var entryPath = $"{indexFolder}{StoragePathHelper.SecondaryIndexEntryFileName}";
            var entryJson = JsonSerializer.Serialize(
                new SecondaryIndexEntry(r.BackupId, r.BackupFolder, r.AnchorTs), JsonOptions);

            entryBlobs[entryPath] = entryJson;
            allFolders.Add(indexFolder);
        }

        var mock = new Mock<IStorageConnector>();

        mock.Setup(s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((folder, _) =>
                Task.FromResult(manifestsByFolder.TryGetValue(folder, out var json) ? json : null));

        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
                allFolders
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(k => k)
                    .ToAsyncEnumerable(ct));

        mock.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                if (entryBlobs.TryGetValue(path, out var json))
                    return Task.FromResult<Stream>(
                        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
                throw new FileNotFoundException($"Blob not found in mock: {path}");
            });

        mock.Setup(s => s.BlobExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
                Task.FromResult(entryBlobs.ContainsKey(path) ||
                    manifestsByFolder.ContainsKey(path.Replace(
                        StoragePathHelper.ManifestFileName, string.Empty))));

        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());

        mock.Setup(s => s.DeleteBlobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock.Object;
    }

    // ------------------------------------------------------------------ //
    //  ResolveRestoreChainAsync tests
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ResolveChain_FullOnly_ReturnsDepth1()
    {
        // Target date = full backup date (quarter start) → chain depth 1
        var fullAt = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var full = MakeFullBackup("full-01", "mydb", fullAt, anchorTs: 1000);

        var repo = new BlobMetadataRepository(BuildMockStorage([full]));

        var chain = await repo.ResolveRestoreChainAsync(
            "mydb", fullAt.AddMinutes(30));

        Assert.AreEqual(1, chain.Count);
        Assert.AreEqual(BackupType.Full, chain[0].BackupType);
    }

    [TestMethod]
    public async Task ResolveChain_FullPlusTwoIncrementals_ReturnsDepth3()
    {
        var fullAt  = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var inc1At  = new DateTimeOffset(2025, 1, 2, 1, 0, 0, TimeSpan.Zero);
        var inc2At  = new DateTimeOffset(2025, 1, 3, 1, 0, 0, TimeSpan.Zero);
        var target  = inc2At.AddHours(3);   // still Q1 2025

        var full = MakeFullBackup("full-01", "mydb", fullAt,
            anchorTs: fullAt.ToUnixTimeSeconds());

        var inc1 = MakeIncremental("inc-01", "mydb", "full-01", inc1At,
            queryFromTs: fullAt.ToUnixTimeSeconds() - 300,
            anchorTs:    inc1At.ToUnixTimeSeconds());

        var inc2 = MakeIncremental("inc-02", "mydb", "full-01", inc2At,
            queryFromTs: inc1At.ToUnixTimeSeconds() - 300,
            anchorTs:    inc2At.ToUnixTimeSeconds());

        var repo = new BlobMetadataRepository(
            BuildMockStorage([full, inc1, inc2]));

        var chain = await repo.ResolveRestoreChainAsync("mydb", target);

        Assert.AreEqual(3, chain.Count);
        Assert.AreEqual(BackupType.Full,        chain[0].BackupType);
        Assert.AreEqual(BackupType.Incremental, chain[1].BackupType);
        Assert.AreEqual(BackupType.Incremental, chain[2].BackupType);
    }

    [TestMethod]
    public async Task ResolveChain_FullPlusCheckpointPlusIncrementals_CorrectOrder()
    {
        var fullAt  = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var ckpAt   = new DateTimeOffset(2025, 1, 8, 2, 0, 0, TimeSpan.Zero);
        var inc1At  = new DateTimeOffset(2025, 1, 9, 1, 0, 0, TimeSpan.Zero);
        var inc2At  = new DateTimeOffset(2025, 1, 10, 1, 0, 0, TimeSpan.Zero);
        var target  = inc2At.AddHours(3);   // still Q1 2025

        var full = MakeFullBackup("full-01", "mydb", fullAt,
            anchorTs: fullAt.ToUnixTimeSeconds());

        var ckp = MakeCheckpoint("ckp-01", "mydb", "full-01", ckpAt,
            queryFromTs: fullAt.ToUnixTimeSeconds() - 300,
            anchorTs:    ckpAt.ToUnixTimeSeconds(),
            week: 1);

        var inc1 = MakeIncremental("inc-09", "mydb", "full-01", inc1At,
            queryFromTs: ckpAt.ToUnixTimeSeconds() - 300,
            anchorTs:    inc1At.ToUnixTimeSeconds());

        var inc2 = MakeIncremental("inc-10", "mydb", "full-01", inc2At,
            queryFromTs: inc1At.ToUnixTimeSeconds() - 300,
            anchorTs:    inc2At.ToUnixTimeSeconds());

        var repo = new BlobMetadataRepository(
            BuildMockStorage([full, ckp, inc1, inc2]));

        var chain = await repo.ResolveRestoreChainAsync("mydb", target);

        Assert.AreEqual(4, chain.Count);
        Assert.AreEqual(BackupType.Full,        chain[0].BackupType);
        Assert.AreEqual(BackupType.Checkpoint,  chain[1].BackupType);
        Assert.AreEqual(BackupType.Incremental, chain[2].BackupType);
        Assert.AreEqual(BackupType.Incremental, chain[3].BackupType);
        Assert.IsTrue(chain[0].CompletedAtUtc < chain[1].CompletedAtUtc);
        Assert.IsTrue(chain[1].CompletedAtUtc < chain[2].CompletedAtUtc);
        Assert.IsTrue(chain[2].CompletedAtUtc < chain[3].CompletedAtUtc);
    }

    [TestMethod]
    public async Task ResolveChain_ThrowsWhenNoFullBackupExists()
    {
        var storage = BuildMockStorage([]); // empty
        var repo = new BlobMetadataRepository(storage);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await repo.ResolveRestoreChainAsync(
                "mydb",
                new DateTimeOffset(2025, 1, 10, 0, 0, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    public async Task ResolveChain_ExcludesCheckpointsAfterTargetDate()
    {
        var fullAt  = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var ckp1At  = new DateTimeOffset(2025, 1, 8, 2, 0, 0, TimeSpan.Zero);
        var ckp2At  = new DateTimeOffset(2025, 1, 15, 2, 0, 0, TimeSpan.Zero);
        // Target falls between ckp1 and ckp2 anchor timestamps
        var target  = ckp1At.AddDays(3);   // Jan 11 — after ckp1 (Jan 8), before ckp2 (Jan 15)

        var full = MakeFullBackup("full-01", "mydb", fullAt,
            anchorTs: fullAt.ToUnixTimeSeconds());

        var ckp1 = MakeCheckpoint("ckp-01", "mydb", "full-01", ckp1At,
            queryFromTs: fullAt.ToUnixTimeSeconds() - 300,
            anchorTs:    ckp1At.ToUnixTimeSeconds(),
            week: 1);

        // ckp2.anchorTs > target.ToUnixTimeSeconds() → must be excluded
        var ckp2 = MakeCheckpoint("ckp-02", "mydb", "full-01", ckp2At,
            queryFromTs: ckp1At.ToUnixTimeSeconds() - 300,
            anchorTs:    ckp2At.ToUnixTimeSeconds(),
            week: 2);

        var repo = new BlobMetadataRepository(
            BuildMockStorage([full, ckp1, ckp2]));

        var chain = await repo.ResolveRestoreChainAsync("mydb", target);

        Assert.AreEqual(2, chain.Count);
        Assert.AreEqual(BackupType.Full,       chain[0].BackupType);
        Assert.AreEqual(BackupType.Checkpoint, chain[1].BackupType);
        Assert.AreEqual("ckp-01", chain[1].BackupId);
    }
}

// ------------------------------------------------------------------ //
//  Secondary index tests
// ------------------------------------------------------------------ //

[TestClass]
public sealed class BlobMetadataRepositorySecondaryIndexTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Serialize(BackupRecord r) => JsonSerializer.Serialize(r, JsonOptions);

    private static BackupRecord MakeFull(string id, string db, DateTimeOffset at, long anchor) => new()
    {
        BackupId = id, BackupType = BackupType.Full,
        BackupFolder = StoragePathHelper.FullBackupFolder(at, id),
        DatabaseName = db, QuarterLabel = StoragePathHelper.QuarterLabel(at),
        CompletedAtUtc = at, AnchorTs = anchor
    };

    private static BackupRecord MakeIncremental(
        string id, string db, string fullId, DateTimeOffset at, long queryFrom, long anchor) => new()
    {
        BackupId = id, BackupType = BackupType.Incremental,
        BackupFolder = StoragePathHelper.IncrementalBackupFolder(at, fullId),
        DatabaseName = db, QuarterLabel = StoragePathHelper.QuarterLabel(at),
        CompletedAtUtc = at, QueryFromTs = queryFrom, AnchorTs = anchor
    };

    // Builds a mock that serves manifests + secondary index entries for all records.
    private static IStorageConnector BuildMock(IEnumerable<BackupRecord> records)
    {
        var recordList = records.ToList();
        var manifests  = recordList.ToDictionary(r => r.BackupFolder, r => Serialize(r));
        var entryBlobs = new Dictionary<string, string>();
        var allFolders = new HashSet<string>(manifests.Keys);

        foreach (var r in recordList)
        {
            int q   = ((r.CompletedAtUtc.Month - 1) / 3) + 1;
            string tp = r.BackupType.ToString().ToLowerInvariant();
            var indexFolder = StoragePathHelper.SecondaryIndexFolderPath(
                r.CompletedAtUtc.Year, q, tp, r.DatabaseName, r.CompletedAtUtc, r.BackupId);
            var entryPath   = $"{indexFolder}{StoragePathHelper.SecondaryIndexEntryFileName}";
            entryBlobs[entryPath] = JsonSerializer.Serialize(
                new SecondaryIndexEntry(r.BackupId, r.BackupFolder, r.AnchorTs), JsonOptions);
            allFolders.Add(indexFolder);
        }

        var mock = new Mock<IStorageConnector>();
        mock.Setup(s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((f, _) =>
                Task.FromResult(manifests.TryGetValue(f, out var j) ? j : null));
        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
                allFolders.Where(k => k.StartsWith(prefix)).OrderBy(k => k).ToAsyncEnumerable(ct));
        mock.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                if (entryBlobs.TryGetValue(path, out var j))
                    return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(j)));
                throw new FileNotFoundException(path);
            });
        mock.Setup(s => s.BlobExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
                Task.FromResult(entryBlobs.ContainsKey(path) ||
                    manifests.ContainsKey(path.Replace(StoragePathHelper.ManifestFileName, string.Empty))));
        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());
        mock.Setup(s => s.DeleteBlobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    [TestMethod]
    public async Task GetLatestBackupAsync_ReturnsLatestViaSecondaryIndex()
    {
        // Use recent dates so they fall within the 4-quarter search window.
        var at1  = DateTimeOffset.UtcNow.AddDays(-10);
        var at2  = DateTimeOffset.UtcNow.AddDays(-3);
        var full1 = MakeFull("full-01", "mydb", at1, anchor: 1000);
        var full2 = MakeFull("full-02", "mydb", at2, anchor: 2000);

        var repo   = new BlobMetadataRepository(BuildMock([full1, full2]));
        var result = await repo.GetLatestBackupAsync("mydb", BackupType.Full);

        Assert.IsNotNull(result);
        Assert.AreEqual("full-02", result!.BackupId);
    }

    [TestMethod]
    public async Task GetLatestBackupAsync_ReturnsNull_WhenDatabaseHasNoEntries()
    {
        var at   = DateTimeOffset.UtcNow.AddDays(-3);
        var full = MakeFull("full-01", "otherdb", at, anchor: 1000);

        var repo   = new BlobMetadataRepository(BuildMock([full]));
        var result = await repo.GetLatestBackupAsync("mydb", BackupType.Full);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task CountIncrementalsSinceAsync_CountsFromFolderNamesOnly()
    {
        var fullAt  = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var inc1At  = new DateTimeOffset(2025, 1, 2, 1, 0, 0, TimeSpan.Zero);
        var inc2At  = new DateTimeOffset(2025, 1, 3, 1, 0, 0, TimeSpan.Zero);
        var full    = MakeFull("full-01", "mydb", fullAt, anchor: 1000);
        var inc1    = MakeIncremental("inc-01", "mydb", "full-01", inc1At, 900, 1500);
        var inc2    = MakeIncremental("inc-02", "mydb", "full-01", inc2At, 1400, 2000);

        var storageMock = new Mock<IStorageConnector>();
        var allFolders  = new HashSet<string>();
        foreach (var r in new[] { full, inc1, inc2 })
        {
            int q   = ((r.CompletedAtUtc.Month - 1) / 3) + 1;
            string tp = r.BackupType.ToString().ToLowerInvariant();
            allFolders.Add(StoragePathHelper.SecondaryIndexFolderPath(
                r.CompletedAtUtc.Year, q, tp, r.DatabaseName, r.CompletedAtUtc, r.BackupId));
        }
        storageMock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
                allFolders.Where(k => k.StartsWith(prefix)).OrderBy(k => k).ToAsyncEnumerable(ct));

        var repo  = new BlobMetadataRepository(storageMock.Object);
        int count = await repo.CountIncrementalsSinceAsync("mydb", 2025, 1, fullAt);

        Assert.AreEqual(2, count);
        // Verify ReadManifestAsync was never called — counting uses names only.
        storageMock.Verify(
            s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task SaveBackupRecordAsync_WritesSecondaryIndexEntry()
    {
        var at     = new DateTimeOffset(2025, 4, 1, 3, 0, 0, TimeSpan.Zero);
        var record = MakeFull("full-99", "mydb", at, anchor: 5000);

        var writtenPaths = new List<string>();
        var mock = new Mock<IStorageConnector>();
        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                writtenPaths.Add(path);
                return Task.FromResult<Stream>(new MemoryStream());
            });

        var repo = new BlobMetadataRepository(mock.Object);
        await repo.SaveBackupRecordAsync(record);

        // Expect: _index/{backupId} pointer + secondary index entry
        Assert.IsTrue(writtenPaths.Any(p => p == $"_index/{record.BackupId}"),
            "_index/{backupId} pointer missing");
        Assert.IsTrue(writtenPaths.Any(p => p.StartsWith("_index/by-quarter/") && p.EndsWith("entry.json")),
            "Secondary index entry.json missing");
    }

    [TestMethod]
    public async Task ValidateIndexAsync_ReportsUnindexedManifest()
    {
        var at   = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var full = MakeFull("full-01", "mydb", at, anchor: 1000);

        // Storage has the manifest but no secondary index entry.
        var manifests = new Dictionary<string, string> { [full.BackupFolder] = Serialize(full) };
        var mock = new Mock<IStorageConnector>();
        mock.Setup(s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((f, _) =>
                Task.FromResult(manifests.TryGetValue(f, out var j) ? j : null));
        // Secondary index prefix returns nothing.
        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
            {
                // Return manifest folder for backup-type prefix scans; empty for index prefix.
                if (prefix.StartsWith("_index/"))
                    return Enumerable.Empty<string>().ToAsyncEnumerable(ct);
                return manifests.Keys
                    .Where(k => k.StartsWith(prefix)).OrderBy(k => k).ToAsyncEnumerable(ct);
            });
        mock.Setup(s => s.BlobExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
                Task.FromResult(manifests.ContainsKey(path.Replace(StoragePathHelper.ManifestFileName, string.Empty))));

        var repo   = new BlobMetadataRepository(mock.Object);
        var result = await repo.ValidateIndexAsync();

        Assert.IsFalse(result.IsHealthy);
        Assert.AreEqual(0, result.DanglingEntries);
        Assert.IsTrue(result.UnindexedManifests > 0);
    }

    [TestMethod]
    public async Task RebuildIndexAsync_DryRun_DoesNotWrite()
    {
        var at   = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var full = MakeFull("full-01", "mydb", at, anchor: 1000);

        var manifests = new Dictionary<string, string> { [full.BackupFolder] = Serialize(full) };
        var writeCalls = 0;
        var mock = new Mock<IStorageConnector>();
        mock.Setup(s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((f, _) =>
                Task.FromResult(manifests.TryGetValue(f, out var j) ? j : null));
        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
            {
                if (prefix.StartsWith("_index/"))
                    return Enumerable.Empty<string>().ToAsyncEnumerable(ct);
                return manifests.Keys.Where(k => k.StartsWith(prefix))
                    .OrderBy(k => k).ToAsyncEnumerable(ct);
            });
        mock.Setup(s => s.BlobExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(false));
        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) => { writeCalls++; return Task.FromResult<Stream>(new MemoryStream()); });

        var repo   = new BlobMetadataRepository(mock.Object);
        var result = await repo.RebuildIndexAsync(dryRun: true);

        Assert.IsTrue(result.WasDryRun);
        Assert.IsTrue(result.EntriesAdded > 0);
        Assert.AreEqual(0, writeCalls, "Dry-run must not write any blobs");
    }

    [TestMethod]
    public async Task IsSecondaryIndexPopulatedAsync_ReturnsFalse_WhenEmpty()
    {
        var mock = new Mock<IStorageConnector>();
        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, ct) =>
                Enumerable.Empty<string>().ToAsyncEnumerable(ct));

        var repo   = new BlobMetadataRepository(mock.Object);
        var result = await repo.IsSecondaryIndexPopulatedAsync();
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsSecondaryIndexPopulatedAsync_ReturnsTrue_WhenEntriesExist()
    {
        var at   = new DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var full = MakeFull("full-01", "mydb", at, anchor: 1000);
        var repo = new BlobMetadataRepository(BuildMock([full]));
        var result = await repo.IsSecondaryIndexPopulatedAsync();
        Assert.IsTrue(result);
    }
}

// ------------------------------------------------------------------ //
//  Extension helper for tests — turns IEnumerable<T> into IAsyncEnumerable<T>
// ------------------------------------------------------------------ //

file static class AsyncEnumerableExtensions
{
    internal static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken _ = default)
    {
        foreach (var item in source)
            yield return item;

        await Task.CompletedTask;
    }
}
