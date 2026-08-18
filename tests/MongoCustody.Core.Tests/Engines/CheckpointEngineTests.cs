using System.Runtime.CompilerServices;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="CheckpointEngine"/>.
/// All infrastructure is mocked — no Azure or MongoDB required.
/// </summary>
[TestClass]
public sealed class CheckpointEngineTests
{
    // ------------------------------------------------------------------ //
    //  Fixtures and helpers
    // ------------------------------------------------------------------ //

    private const string DbName         = "test-db";
    private const long   FullAnchorTs   = 1_700_000_000L;
    private const long   NewAnchorTs    = 1_700_100_000L;
    private const string FullBackupId   = "full-backup-id";

    private static readonly DateTimeOffset FullCompletedAt =
        new DateTimeOffset(2025, 4, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly BackupRecord FullRecord = new()
    {
        BackupId       = FullBackupId,
        BackupType     = BackupType.Full,
        BackupFolder   = $"full/2025/Q2/{FullBackupId}/",
        AnchorTs       = FullAnchorTs,
        CompletedAtUtc = FullCompletedAt,
        DatabaseName   = DbName
    };

    private static MongoCustodyConfiguration MakeConfig() => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = DbName }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections   = 2,
            ChunkSizeGb              = 10,
            BlobBlockSizeMb          = 100,
            CompressionLevel         = CompressionLevel.Fastest,
            IncrementalBufferSeconds = 300,
            MaxIncrementalChainDepth = 19
        }
    };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> YieldDocs(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00 };
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> YieldNoDocs(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<string> YieldCollections(params string[] names)
    {
        foreach (var n in names) yield return n;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<string> YieldFolders(params string[] folders)
    {
        foreach (var f in folders) yield return f;
        await Task.CompletedTask;
    }

    // Incremental folder that is after FullCompletedAt
    private static string IncrementalFolder(int dayOffset = 1)
    {
        var ts = FullCompletedAt.AddDays(dayOffset).ToUniversalTime().ToString("yyyy-MM-ddTHHmmZ");
        return $"incremental/2025/Q2/{FullBackupId}/{ts}/";
    }

    private static Mock<IStorageConnector> MakeStorageMock(string[]? folders = null)
    {
        var mock = new Mock<IStorageConnector>();

        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());

        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
                YieldFolders(folders ?? [IncrementalFolder(1)]));

        return mock;
    }

    private static Mock<IBackupSourceConnector> MakeSourceMock()
    {
        var mock = new Mock<IBackupSourceConnector>();

        mock.Setup(s => s.ListIndexesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexDefinition>());

        mock.Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewAnchorTs);

        return mock;
    }

    private static Mock<IMetadataRepository> MakeMetaMock(
        BackupRecord? latestFull       = null,
        BackupRecord? latestCheckpoint = null)
    {
        var mock = new Mock<IMetadataRepository>();

        mock.Setup(m => m.GetLatestBackupAsync(DbName, BackupType.Full, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestFull);

        mock.Setup(m => m.GetLatestBackupAsync(DbName, BackupType.Checkpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestCheckpoint);

        mock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static CheckpointEngine MakeEngine(
        Mock<IBackupSourceConnector> sourceMock,
        Mock<IStorageConnector>      storageMock,
        Mock<IMetadataRepository>    metaMock)
        => new(sourceMock.Object, storageMock.Object, metaMock.Object, MakeConfig());

    // ------------------------------------------------------------------ //
    //  Happy path
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_HappyPath_ReturnsCompleted()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(latestFull: FullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a", "col-b"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, It.IsAny<string>(),
                FullAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(4, ct));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(BackupType.Checkpoint, result.BackupType);
        Assert.AreEqual(1, result.DatabaseResults.Count);
        Assert.AreEqual(BackupStatus.Completed, result.DatabaseResults[0].Status);
        Assert.AreEqual(8, result.DatabaseResults[0].TotalDocuments); // 2 cols × 4 docs
    }

    // ------------------------------------------------------------------ //
    //  No full backup
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_NoFullBackup_ReturnsFailed()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(); // all nulls

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        StringAssert.Contains(result.FailureReason, "No full backup");
    }

    // ------------------------------------------------------------------ //
    //  No incrementals since anchor
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_NoIncrementalsSinceAnchor_ReturnsFailed()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock([]); // no incremental folders
        var metaMock    = MakeMetaMock(latestFull: FullRecord);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        StringAssert.Contains(result.FailureReason, "No incremental backups");
    }

    // ------------------------------------------------------------------ //
    //  Manifest fields
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_ManifestHasCorrectFields()
    {
        var sourceMock  = MakeSourceMock();
        var folder1     = IncrementalFolder(1);
        var folder2     = IncrementalFolder(2);
        var storageMock = MakeStorageMock([folder1, folder2]);
        var metaMock    = MakeMetaMock(latestFull: FullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-a",
                FullAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(5, ct));

        BackupRecord? savedRecord = null;
        metaMock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test", Label = "pre-deploy" },
            CancellationToken.None);

        Assert.IsNotNull(savedRecord);
        Assert.AreEqual(BackupType.Checkpoint, savedRecord!.BackupType);
        Assert.AreEqual(NewAnchorTs, savedRecord.AnchorTs);
        Assert.AreEqual(FullAnchorTs - MakeConfig().Policy.IncrementalBufferSeconds, savedRecord.QueryFromTs);
        Assert.AreEqual(FullRecord.BackupFolder, savedRecord.ParentFolder);
        Assert.IsNotNull(savedRecord.WeekNumber);
        Assert.IsTrue(savedRecord.WeekNumber > 0);
        Assert.AreEqual("pre-deploy", savedRecord.Label);

        // Both incremental folders recorded as sources
        CollectionAssert.AreEquivalent(
            new[] { folder1, folder2 },
            savedRecord.SourceIncrementalFolders);

        // Checkpoint folder follows the expected path scheme
        StringAssert.StartsWith(savedRecord.BackupFolder, "checkpoint/");
        StringAssert.Contains(savedRecord.BackupFolder, FullBackupId);
    }

    // ------------------------------------------------------------------ //
    //  Checkpoint after a previous checkpoint uses that as anchor
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_WithPreviousCheckpoint_UsesCheckpointAsAnchor()
    {
        var sourceMock = MakeSourceMock();

        // Previous checkpoint completed after the full backup
        var prevCheckpointAnchorTs = FullAnchorTs + 50_000L;
        var prevCheckpointCompletedAt = FullCompletedAt.AddDays(7);
        var previousCheckpoint = new BackupRecord
        {
            BackupId       = "chk-id",
            BackupType     = BackupType.Checkpoint,
            BackupFolder   = $"checkpoint/2025/Q2/{FullBackupId}/week-01T1000Z/",
            AnchorTs       = prevCheckpointAnchorTs,
            CompletedAtUtc = prevCheckpointCompletedAt,
            DatabaseName   = DbName
        };

        // Only a folder after the checkpoint should be picked up
        var folderAfterCheckpoint = $"incremental/2025/Q2/{FullBackupId}/" +
            $"{prevCheckpointCompletedAt.AddDays(1):yyyy-MM-ddTHHmmZ}/";
        var folderBeforeCheckpoint = IncrementalFolder(1); // before checkpoint

        var storageMock = MakeStorageMock([folderBeforeCheckpoint, folderAfterCheckpoint]);
        var metaMock    = MakeMetaMock(latestFull: FullRecord, latestCheckpoint: previousCheckpoint);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        // Engine queries with prevCheckpointAnchorTs, not FullAnchorTs
        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-a",
                prevCheckpointAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(3, ct));

        BackupRecord? savedRecord = null;
        metaMock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.IsNotNull(savedRecord);
        // ParentFolder should be the previous checkpoint, not the full
        Assert.AreEqual(previousCheckpoint.BackupFolder, savedRecord!.ParentFolder);
        // Only the folder after the checkpoint should be in source list
        Assert.AreEqual(1, savedRecord.SourceIncrementalFolders.Count);
        Assert.AreEqual(folderAfterCheckpoint, savedRecord.SourceIncrementalFolders[0]);
    }

    // ------------------------------------------------------------------ //
    //  Collections with no changes are excluded
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_CollectionWithNoChanges_IsExcludedFromManifest()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(latestFull: FullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-changed", "col-empty"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-changed",
                FullAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(7, ct));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-empty",
                FullAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldNoDocs(ct));

        BackupRecord? savedRecord = null;
        metaMock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(1, result.DatabaseResults[0].CollectionCount);
        Assert.IsNotNull(savedRecord);
        Assert.AreEqual(1, savedRecord!.Collections.Count);
        Assert.AreEqual("col-changed", savedRecord.Collections[0].CollectionName);
    }
}
