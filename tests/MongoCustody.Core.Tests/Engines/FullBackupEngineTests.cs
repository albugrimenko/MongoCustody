using System.Runtime.CompilerServices;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="FullBackupEngine"/>.
///
/// All infrastructure is mocked — no Azure or MongoDB required.
/// </summary>
[TestClass]
public sealed class FullBackupEngineTests
{
    // ------------------------------------------------------------------ //
    //  Fixtures and helpers
    // ------------------------------------------------------------------ //

    private const string DbName = "test-db";

    private static MongoCustodyConfiguration MakeConfig(
        string dbName = DbName,
        int maxParallel = 2,
        int chunkSizeGb = 10) => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = dbName }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections = maxParallel,
            ChunkSizeGb            = chunkSizeGb,
            BlobBlockSizeMb        = 100,
            CompressionLevel       = CompressionLevel.Fastest
        }
    };

    /// <summary>Minimal 5-byte empty BSON document.</summary>
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

    private static async IAsyncEnumerable<string> YieldCollections(
        params string[] names)
    {
        foreach (var n in names)
            yield return n;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Returns a mock IStorageConnector whose OpenWriteAsync always
    /// returns an in-memory stream that silently absorbs all bytes.
    /// </summary>
    private static Mock<IStorageConnector> MakeStorageMock()
    {
        var mock = new Mock<IStorageConnector>();

        mock.Setup(s => s.OpenWriteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());

        mock.Setup(s => s.WriteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static FullBackupEngine MakeEngine(
        Mock<IBackupSourceConnector> sourceMock,
        Mock<IStorageConnector>      storageMock,
        Mock<IMetadataRepository>    metaMock,
        MongoCustodyConfiguration?   config = null)
        => new(sourceMock.Object, storageMock.Object, metaMock.Object,
               config ?? MakeConfig());

    // ------------------------------------------------------------------ //
    //  Happy path
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_HappyPath_ReturnsCompleted()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a", "col-b"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => YieldDocs(5, ct));

        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(BackupType.Full, result.BackupType);
        Assert.AreEqual(1, result.DatabaseResults.Count);
        Assert.AreEqual(BackupStatus.Completed, result.DatabaseResults[0].Status);
        Assert.AreEqual(10, result.DatabaseResults[0].TotalDocuments); // 2 collections × 5 docs
        Assert.AreEqual(2,  result.DatabaseResults[0].CollectionCount);
        Assert.IsFalse(string.IsNullOrEmpty(result.BackupId));
        Assert.IsFalse(string.IsNullOrEmpty(result.CorrelationId));
    }

    [TestMethod]
    public async Task RunAsync_EmptyCollection_ProducesZeroDocuments()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("empty-col"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, "empty-col", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => YieldDocs(0, ct));

        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(0, result.DatabaseResults[0].TotalDocuments);
    }

    [TestMethod]
    public async Task RunAsync_ManifestSealedExactlyOnce_PerDatabase()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, "col-a", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => YieldDocs(1, ct));

        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        metaMock.Verify(
            m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task RunAsync_SavedRecord_HasCorrectMetadata()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        const long expectedAnchor = 1_750_000_000L;

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedAnchor);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, "col-a", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => YieldDocs(2, ct));

        BackupRecord? capturedRecord = null;
        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Callback<BackupRecord, CancellationToken>((r, _) => capturedRecord = r)
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "scheduler" },
            null, CancellationToken.None);

        Assert.IsNotNull(capturedRecord);
        Assert.AreEqual(BackupType.Full, capturedRecord.BackupType);
        Assert.AreEqual(DbName,          capturedRecord.DatabaseName);
        Assert.AreEqual(expectedAnchor,  capturedRecord.AnchorTs);
        Assert.IsNull(capturedRecord.QueryFromTs, "Full backups have no QueryFromTs.");
        Assert.IsNull(capturedRecord.ParentFolder, "Full backups have no parent folder.");
        Assert.AreEqual("scheduler",     capturedRecord.TriggeredBy);
        Assert.AreEqual(1,               capturedRecord.Collections.Count);
        Assert.AreEqual("col-a",         capturedRecord.Collections[0].CollectionName);
        Assert.AreEqual(2,               capturedRecord.Collections[0].TotalDocuments);
        StringAssert.StartsWith(capturedRecord.BackupFolder, "full/");
    }

    // ------------------------------------------------------------------ //
    //  Failure cases
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_AnchorCaptureFailure_ReturnsFailed()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mongo unavailable"));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        Assert.AreEqual(BackupStatus.Failed, result.DatabaseResults[0].Status);
        StringAssert.Contains(result.DatabaseResults[0].FailureReason!, "anchor timestamp");

        // Manifest must NOT be written when anchor capture fails
        metaMock.Verify(
            m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_CollectionEnumerationFailure_ReturnsFailed()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Throws(new TimeoutException("listing timed out"));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        StringAssert.Contains(result.DatabaseResults[0].FailureReason!, "collections");
        metaMock.Verify(
            m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_ManifestSealFailure_ReturnsFailed()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, "col-a", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => YieldDocs(2, ct));

        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage sealed"));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.RunAsync(
            new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        StringAssert.Contains(result.DatabaseResults[0].FailureReason!, "manifest");
    }

    // ------------------------------------------------------------------ //
    //  Multi-database
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_MultipleDatabases_ProducesOneResultPerDatabase()
    {
        const string db1 = "db-one";
        const string db2 = "db-two";

        var config = new MongoCustodyConfiguration
        {
            Databases = [
                new DatabaseConfiguration { DatabaseName = db1 },
                new DatabaseConfiguration { DatabaseName = db2 }
            ],
            Policy = new BackupPolicy
            {
                MaxParallelCollections = 2,
                ChunkSizeGb            = 10,
                BlobBlockSizeMb        = 100,
                CompressionLevel       = CompressionLevel.Fastest
            }
        };

        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        foreach (var db in new[] { db1, db2 })
        {
            var dbCapture = db;
            sourceMock
                .Setup(s => s.ListCollectionsAsync(dbCapture, It.IsAny<CancellationToken>()))
                .Returns(YieldCollections("col"));
            sourceMock
                .Setup(s => s.StreamAllDocumentsAsync(dbCapture, "col", It.IsAny<CancellationToken>()))
                .Returns((string _, string _, CancellationToken ct) => YieldDocs(1, ct));
        }

        metaMock
            .Setup(m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(sourceMock, storageMock, metaMock, config);
        var result = await engine.RunAsync(
            new BackupRequest { TriggeredBy = "test" }, // empty Databases → use all configured
            null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(2, result.DatabaseResults.Count);
        metaMock.Verify(
            m => m.SaveBackupRecordAsync(It.IsAny<BackupRecord>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ------------------------------------------------------------------ //
    //  Cancellation
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_Cancelled_PropagatesCancellation()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = MakeStorageMock();
        var metaMock    = new Mock<IMetadataRepository>();

        using var cts = new CancellationTokenSource();

        sourceMock
            .Setup(s => s.GetServerTimestampAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1_700_000_000L);

        sourceMock
            .Setup(s => s.ListCollectionsAsync(DbName, It.IsAny<CancellationToken>()))
            .Returns(YieldCollections("col-a"));

        sourceMock
            .Setup(s => s.StreamAllDocumentsAsync(DbName, "col-a", It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) =>
            {
                // Cancel while streaming
                cts.Cancel();
                return YieldDocs(100, ct);
            });

        var engine = MakeEngine(sourceMock, storageMock, metaMock);

        bool threw = false;
        try
        {
            await engine.RunAsync(
                new BackupRequest { Databases = [DbName], TriggeredBy = "test" },
                null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.IsTrue(threw, "Expected OperationCanceledException to propagate.");
    }
}
