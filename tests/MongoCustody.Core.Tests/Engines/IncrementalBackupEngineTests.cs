using System.Runtime.CompilerServices;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="IncrementalBackupEngine"/>.
/// All infrastructure is mocked — no Azure or MongoDB required.
/// </summary>
[TestClass]
public sealed class IncrementalBackupEngineTests
{
    // ------------------------------------------------------------------ //
    //  Fixtures and helpers
    // ------------------------------------------------------------------ //

    private const string DbName = "test-db";
    private const long   PreviousAnchorTs = 1_700_000_000L;
    private const long   NewAnchorTs      = 1_700_010_000L;

    private static MongoCustodyConfiguration MakeConfig(int maxChainDepth = 19) => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = DbName }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections  = 2,
            ChunkSizeGb             = 10,
            BlobBlockSizeMb         = 100,
            CompressionLevel        = CompressionLevel.Fastest,
            IncrementalBufferSeconds = 300,
            MaxIncrementalChainDepth = maxChainDepth
        }
    };

    private static readonly BackupRecord PreviousFullRecord = new()
    {
        BackupId       = "full-backup-id",
        BackupType     = BackupType.Full,
        BackupFolder   = $"full/2025/Q1/full-backup-id/",
        AnchorTs       = PreviousAnchorTs,
        CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        DatabaseName   = DbName
    };

    private static ReadOnlyMemory<byte> EmptyBsonDoc()
        => new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00 };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> YieldDocs(
        int count,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return EmptyBsonDoc();
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

    private static Mock<IStorageConnector> MakeStorageMock(string[]? incrementalFolders = null)
    {
        var mock = new Mock<IStorageConnector>();

        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());

        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Chain-depth counting uses ListFoldersAsync on the incremental prefix.
        mock.Setup(s => s.ListFoldersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
                YieldFolders(incrementalFolders ?? []));

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
        BackupRecord? latestFull        = null,
        BackupRecord? latestCheckpoint  = null,
        BackupRecord? latestIncremental = null,
        BackupRecord? folderRecord      = null)
    {
        var mock = new Mock<IMetadataRepository>();

        mock.Setup(m => m.GetLatestBackupAsync(DbName, BackupType.Full, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestFull);

        mock.Setup(m => m.GetLatestBackupAsync(DbName, BackupType.Checkpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestCheckpoint);

        mock.Setup(m => m.GetLatestBackupAsync(DbName, BackupType.Incremental, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestIncremental);

        mock.Setup(m => m.GetBackupRecordByFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(folderRecord);

        mock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static IncrementalBackupEngine MakeEngine(
        Mock<IBackupSourceConnector> sourceMock,
        Mock<IStorageConnector>      storageMock,
        Mock<IMetadataRepository>    metaMock,
        MongoCustodyConfiguration?   config = null)
        => new(sourceMock.Object, storageMock.Object, metaMock.Object, config ?? MakeConfig());

    // ------------------------------------------------------------------ //
    //  Happy path
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_HappyPath_ReturnsCompleted()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(latestFull: PreviousFullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a", "col-b"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, It.IsAny<string>(),
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(3, ct));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(BackupType.Incremental, result.BackupType);
        Assert.AreEqual(1, result.DatabaseResults.Count);
        Assert.AreEqual(BackupStatus.Completed, result.DatabaseResults[0].Status);
        Assert.AreEqual(6, result.DatabaseResults[0].TotalDocuments); // 2 cols × 3 docs
        Assert.AreEqual(2, result.DatabaseResults[0].CollectionCount);
    }

    // ------------------------------------------------------------------ //
    //  No prior backup
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_NoPreviousBackup_ReturnsFailed()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(); // all nulls

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        StringAssert.Contains(result.FailureReason, "No previous backup");
    }

    // ------------------------------------------------------------------ //
    //  Empty collection skipped
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_CollectionWithNoChanges_IsExcludedFromManifest()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(latestFull: PreviousFullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-changed", "col-unchanged"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-changed",
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(5, ct));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-unchanged",
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldNoDocs(ct));

        BackupRecord? savedRecord = null;
        metaMock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(1, result.DatabaseResults[0].CollectionCount); // only col-changed
        Assert.IsNotNull(savedRecord);
        Assert.AreEqual(1, savedRecord!.Collections.Count);
        Assert.AreEqual("col-changed", savedRecord.Collections[0].CollectionName);
    }

    // ------------------------------------------------------------------ //
    //  Manifest fields
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_ManifestHasCorrectAnchorAndQueryFromTs()
    {
        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock();
        var metaMock    = MakeMetaMock(latestFull: PreviousFullRecord);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-a",
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(1, ct));

        BackupRecord? savedRecord = null;
        metaMock.Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => savedRecord = r)
            .Returns(Task.CompletedTask);

        var config = MakeConfig();
        var engine = MakeEngine(sourceMock, storageMock, metaMock, config);
        await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.IsNotNull(savedRecord);
        Assert.AreEqual(BackupType.Incremental, savedRecord!.BackupType);
        Assert.AreEqual(NewAnchorTs, savedRecord.AnchorTs);
        // QueryFromTs = previousAnchorTs - bufferSeconds
        Assert.AreEqual(PreviousAnchorTs - config.Policy.IncrementalBufferSeconds,
            savedRecord.QueryFromTs);
        Assert.AreEqual(PreviousFullRecord.BackupFolder, savedRecord.ParentFolder);
    }

    // ------------------------------------------------------------------ //
    //  Chain depth warning
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_ChainDepthAtMax_SucceedsWithWarning()
    {
        // Report one existing incremental folder per depth check call.
        var existingFolders = Enumerable
            .Range(1, 19)
            .Select(i => $"incremental/2025/Q1/full-backup-id/inc-{i:D3}/")
            .ToArray();

        var sourceMock  = MakeSourceMock();
        var storageMock = MakeStorageMock(existingFolders);
        var metaMock    = MakeMetaMock(latestFull: PreviousFullRecord);

        // Each folder read returns a record counted toward depth.
        metaMock.Setup(m => m.GetBackupRecordByFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string folder, CancellationToken _) => new BackupRecord
            {
                DatabaseName = DbName,
                BackupType   = BackupType.Incremental,
                AnchorTs     = PreviousAnchorTs + 1  // > chainRoot.AnchorTs
            });

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-a",
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(2, ct));

        var engine = MakeEngine(sourceMock, storageMock, metaMock, MakeConfig(maxChainDepth: 19));
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        // Must succeed (option B — warn, don't fail).
        Assert.AreEqual(BackupStatus.Completed, result.Status);

        var dbResult = result.DatabaseResults[0];
        Assert.AreEqual(BackupStatus.Completed, dbResult.Status);
        Assert.IsNotNull(dbResult.FailureReason,
            "Expected a depth warning on FailureReason when at max depth.");
        StringAssert.Contains(dbResult.FailureReason, "maximum");
    }

    [TestMethod]
    public async Task RunAsync_ChainDepthBelowMax_NoWarning()
    {
        var sourceMock  = MakeSourceMock();
        // Only 5 existing incrementals — well below limit of 19.
        var existingFolders = Enumerable
            .Range(1, 5)
            .Select(i => $"incremental/2025/Q1/full-backup-id/inc-{i:D3}/")
            .ToArray();

        var storageMock = MakeStorageMock(existingFolders);
        var metaMock    = MakeMetaMock(latestFull: PreviousFullRecord);

        metaMock.Setup(m => m.GetBackupRecordByFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new BackupRecord
            {
                DatabaseName = DbName,
                BackupType   = BackupType.Incremental,
                AnchorTs     = PreviousAnchorTs + 1
            });

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamChangedDocumentsAsync(
                DbName, "col-a",
                PreviousAnchorTs, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, long _, int _, CancellationToken ct) => YieldDocs(2, ct));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.IsNull(result.DatabaseResults[0].FailureReason,
            "No warning expected when depth is below max.");
    }
}
