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
    /// Builds a mock IStorageConnector that:
    /// - ReadManifestAsync returns the serialised record for any folder in
    ///   <paramref name="manifests"/>, null for any unknown folder.
    /// - ListFoldersAsync returns folders whose prefix matches.
    /// </summary>
    private static IStorageConnector BuildMockStorage(
        IEnumerable<BackupRecord> manifests)
    {
        var dict = manifests.ToDictionary(r => r.BackupFolder, r => Serialize(r));

        var mock = new Mock<IStorageConnector>();

        mock.Setup(s => s.ReadManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((folder, _) =>
                Task.FromResult(dict.TryGetValue(folder, out var json) ? json : null));

        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((prefix, ct) =>
                dict.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .OrderBy(k => k)
                    .ToAsyncEnumerable(ct));

        // WriteManifestAsync and OpenWriteAsync needed for SaveBackupRecordAsync
        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());

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
