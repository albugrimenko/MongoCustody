using System.IO.Compression;
using System.Runtime.CompilerServices;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="RestoreEngine"/>.
///
/// All infrastructure is mocked — no Azure or MongoDB required.
/// </summary>
[TestClass]
public sealed class RestoreEngineTests
{
    // ------------------------------------------------------------------ //
    //  Fixtures and helpers
    // ------------------------------------------------------------------ //

    private const string DbName       = "test-db";
    private const string CollName     = "patients";
    private const string BlobPath     = "full/2025/Q1/backup-id/test-db.patients.part-001.bson.gz";
    private const string FolderPath   = "full/2025/Q1/backup-id";
    private const string BackupId     = "backup-id";

    private static MongoCustodyConfiguration MakeConfig(int maxParallel = 2, string? shardKeyField = null) => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = DbName, ShardKeyField = shardKeyField }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections = maxParallel,
            ChunkSizeGb            = 10,
            BlobBlockSizeMb        = 100,
            CompressionLevel       = MongoCustody.Core.Models.CompressionLevel.Fastest
        }
    };

    /// <summary>Builds a gzip blob containing <paramref name="docCount"/> minimal BSON documents.</summary>
    private static byte[] BuildBlobBytes(int docCount)
    {
        // Minimal valid BSON document: 5 bytes (length=5, null terminator)
        byte[] emptyDoc = [0x05, 0x00, 0x00, 0x00, 0x00];

        using var ms   = new MemoryStream();
        using var gzip = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
        for (int i = 0; i < docCount; i++)
            gzip.Write(emptyDoc, 0, emptyDoc.Length);
        gzip.Flush();
        gzip.Dispose();
        return ms.ToArray();
    }

    private static BackupRecord MakeFullBackupRecord(
        string collectionName = CollName,
        long   totalDocs      = 5,
        string blobPath       = BlobPath)
    {
        return new BackupRecord
        {
            BackupId     = BackupId,
            BackupType   = BackupType.Full,
            BackupFolder = FolderPath,
            DatabaseName = DbName,
            AnchorTs     = 1_700_000_000L,
            QuarterLabel = "2025-Q1",
            Collections  =
            [
                new CollectionBackupRecord
                {
                    CollectionName = collectionName,
                    TotalDocuments = totalDocs,
                    ChunkCount     = 1,
                    Chunks         =
                    [
                        new ChunkRecord
                        {
                            ChunkIndex    = 1,
                            BlobPath      = blobPath,
                            DocumentCount = totalDocs,
                            Sha256Hash    = "abc",
                            SizeBytes     = 1024
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>
    /// Returns a target mock pre-configured with safe defaults for all
    /// <see cref="IRestoreTargetConnector"/> members, including the new
    /// <c>CollectionExistsAsync</c> and <c>CreateIndexesAsync</c> methods.
    /// Individual tests can override any setup on top of this baseline.
    /// </summary>
    private static Mock<IRestoreTargetConnector> MakeTargetMock()
    {
        var mock = new Mock<IRestoreTargetConnector>();

        // Default: every collection is new (not yet created) — safe for full restore.
        mock.Setup(t => t.CollectionExistsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        mock.Setup(t => t.CreateIndexesAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<IndexDefinition>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(t => t.CreateCollectionAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<IndexDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        mock.Setup(t => t.DropCollectionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(t => t.DropAllCollectionsAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static RestoreEngine MakeEngine(
        Mock<IStorageConnector>         storageMock,
        Mock<IRestoreTargetConnector>   targetMock,
        MongoCustodyConfiguration?      config = null)
        => new(storageMock.Object, targetMock.Object, config ?? MakeConfig());

    private static RestoreRequest MakeRequest(string? collectionFilter = null) =>
        new()
        {
            DatabaseName           = DbName,
            TargetDatabaseName     = DbName,
            TargetConnectionString = "mongodb://restore-target",
            TargetDate             = DateTimeOffset.UtcNow,
            Collections            = collectionFilter is null ? [] : [collectionFilter],
            RequestedBy            = "test",
            ApprovedBy             = "test",
            Reason                 = "unit test"
        };

    // ------------------------------------------------------------------ //
    //  Happy path — full backup, single chunk
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_FullBackup_InsertsDocuments_ReturnsCompleted()
    {
        const int docCount = 5;
        var blobBytes      = BuildBlobBytes(docCount);

        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(docCount);

        var engine = MakeEngine(storageMock, targetMock);
        BackupRecord[] chain = [MakeFullBackupRecord(totalDocs: docCount)];

        var result = await engine.RunAsync(MakeRequest(), chain, null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(1, result.CollectionResults.Count);
        Assert.AreEqual(BackupStatus.Completed, result.CollectionResults[0].Status);
        Assert.AreEqual(docCount, result.CollectionResults[0].DocumentsUpserted);
        Assert.AreEqual(CollName, result.CollectionResults[0].CollectionName);
        Assert.AreEqual(FolderPath, result.RestoreChain[0]);

        // InsertMany must be used for the full backup (not UpsertMany)
        targetMock.Verify(
            t => t.InsertManyAsync(DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        targetMock.Verify(
            t => t.UpsertManyAsync(DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Incremental step uses UpsertMany
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_TwoChainSteps_FullThenIncremental_UsesInsertThenUpsert()
    {
        const int fullDocs = 10;
        const int incrDocs = 3;
        const string incrBlobPath = "incremental/2025/Q1/backup-id/2025-01-02/test-db.patients.part-001.bson.gz";

        var fullBlob = BuildBlobBytes(fullDocs);
        var incrBlob = BuildBlobBytes(incrDocs);

        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fullBlob));
        storageMock
            .Setup(s => s.OpenReadAsync(incrBlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(incrBlob));

        targetMock
            .Setup(t => t.InsertManyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, IAsyncEnumerable<ReadOnlyMemory<byte>> _, CancellationToken _) => (long)fullDocs);

        targetMock
            .Setup(t => t.UpsertManyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, IAsyncEnumerable<ReadOnlyMemory<byte>> _, CancellationToken _) => (long)incrDocs);

        var fullRecord = MakeFullBackupRecord(totalDocs: fullDocs);
        var incrRecord = new BackupRecord
        {
            BackupId     = "incr-id",
            BackupType   = BackupType.Incremental,
            BackupFolder = "incremental/2025/Q1/backup-id/2025-01-02",
            DatabaseName = DbName,
            AnchorTs     = 1_700_086_400L,
            QuarterLabel = "2025-Q1",
            Collections  =
            [
                new CollectionBackupRecord
                {
                    CollectionName = CollName,
                    TotalDocuments = incrDocs,
                    ChunkCount     = 1,
                    Chunks         =
                    [
                        new ChunkRecord
                        {
                            ChunkIndex = 1, BlobPath = incrBlobPath,
                            DocumentCount = incrDocs, Sha256Hash = "xyz", SizeBytes = 512
                        }
                    ]
                }
            ]
        };

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [fullRecord, incrRecord], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(2, result.RestoreChain.Count);

        // Total docs across both steps
        var collResult = result.CollectionResults.Single(r => r.CollectionName == CollName);
        Assert.AreEqual(fullDocs + incrDocs, collResult.DocumentsUpserted);

        // First step → InsertMany; second step → UpsertMany
        targetMock.Verify(
            t => t.InsertManyAsync(DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        targetMock.Verify(
            t => t.UpsertManyAsync(DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ //
    //  Collection filter
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_CollectionFilter_OnlyRestoresRequestedCollection()
    {
        const string colA = "col-a";
        const string colB = "col-b";
        const string blobA = "full/2025/Q1/backup-id/test-db.col-a.part-001.bson.gz";
        const string blobB = "full/2025/Q1/backup-id/test-db.col-b.part-001.bson.gz";

        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(blobA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(BuildBlobBytes(5)));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, colA,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        // Record with two collections
        var record = new BackupRecord
        {
            BackupId     = BackupId,
            BackupType   = BackupType.Full,
            BackupFolder = FolderPath,
            DatabaseName = DbName,
            AnchorTs     = 1_700_000_000L,
            QuarterLabel = "2025-Q1",
            Collections  =
            [
                new CollectionBackupRecord
                {
                    CollectionName = colA, TotalDocuments = 5, ChunkCount = 1,
                    Chunks = [new ChunkRecord { ChunkIndex = 1, BlobPath = blobA, DocumentCount = 5, Sha256Hash = "h1", SizeBytes = 512 }]
                },
                new CollectionBackupRecord
                {
                    CollectionName = colB, TotalDocuments = 3, ChunkCount = 1,
                    Chunks = [new ChunkRecord { ChunkIndex = 1, BlobPath = blobB, DocumentCount = 3, Sha256Hash = "h2", SizeBytes = 256 }]
                }
            ]
        };

        var engine = MakeEngine(storageMock, targetMock);
        // Only restore col-a
        var result = await engine.RunAsync(MakeRequest(colA), [record], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(1, result.CollectionResults.Count);
        Assert.AreEqual(colA, result.CollectionResults[0].CollectionName);

        // col-b blob should never be opened
        storageMock.Verify(
            s => s.OpenReadAsync(blobB, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Empty chain step (collection absent from incremental)
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_EmptyChainStep_IsSkippedGracefully()
    {
        var blobBytes   = BuildBlobBytes(5);
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var fullRecord = MakeFullBackupRecord();
        // An incremental with no collections (nothing changed)
        var emptyIncr = new BackupRecord
        {
            BackupId     = "incr-empty",
            BackupType   = BackupType.Incremental,
            BackupFolder = "incremental/2025/Q1/backup-id/2025-01-03",
            DatabaseName = DbName,
            AnchorTs     = 1_700_086_400L,
            QuarterLabel = "2025-Q1",
            Collections  = []
        };

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [fullRecord, emptyIncr], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(2, result.RestoreChain.Count);
        // Only one collection result from the full backup
        Assert.AreEqual(1, result.CollectionResults.Count);
    }

    // ------------------------------------------------------------------ //
    //  Blob read error → collection fails, result is Failed
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_BlobReadError_ReturnsFailedResult()
    {
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blob unavailable"));

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [MakeFullBackupRecord()], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Failed, result.Status);
        Assert.IsNotNull(result.FailureReason);
    }

    // ------------------------------------------------------------------ //
    //  Empty chain → Completed with no collection results
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_EmptyChain_ReturnsCompletedWithNoCollections()
    {
        var engine = MakeEngine(
            new Mock<IStorageConnector>(),
            new Mock<IRestoreTargetConnector>());

        var result = await engine.RunAsync(
            MakeRequest(), [], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(0, result.CollectionResults.Count);
    }

    // ------------------------------------------------------------------ //
    //  CorrelationId propagation
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_CorrelationIdFromRequest_IsPreservedInResult()
    {
        const string correlationId = "my-correlation-id";
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(BuildBlobBytes(2)));

        targetMock
            .Setup(t => t.InsertManyAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2L);

        var request = MakeRequest();
        request.CorrelationId = correlationId;

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(request, [MakeFullBackupRecord()], null, CancellationToken.None);

        Assert.AreEqual(correlationId, result.CorrelationId);
    }

    // ------------------------------------------------------------------ //
    //  Index restore sequencing
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task RunAsync_UniqueIndex_CreatedBeforeData_NonUniqueAfter()
    {
        // Arrange — one collection with one unique and one regular index.
        var spec = System.Text.Json.JsonDocument.Parse(
            """{"key":{"email":1},"name":"email_1","unique":true}""").RootElement.Clone();
        var nonUniqueSpec = System.Text.Json.JsonDocument.Parse(
            """{"key":{"createdAt":-1},"name":"createdAt_-1"}""").RootElement.Clone();

        var indexes = new List<IndexDefinition>
        {
            new() { Name = "email_1",    IsUnique = true,  Spec = spec },
            new() { Name = "createdAt_-1", IsUnique = false, Spec = nonUniqueSpec }
        };

        var record = MakeFullBackupRecord();
        record.Collections[0].Indexes = indexes;

        var blobBytes   = BuildBlobBytes(3);
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3L);

        var createOrder = new List<string>();

        targetMock
            .Setup(t => t.CreateCollectionAsync(
                DbName, CollName,
                It.IsAny<string?>(),
                It.Is<IReadOnlyList<IndexDefinition>>(l => l.Any(i => i.IsUnique)),
                It.IsAny<CancellationToken>()))
            .Callback(() => createOrder.Add("unique"))
            .ReturnsAsync((string?)null);

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => createOrder.Add("data"))
            .ReturnsAsync(3L);

        targetMock
            .Setup(t => t.CreateIndexesAsync(
                DbName, CollName,
                It.Is<IReadOnlyList<IndexDefinition>>(l => l.All(i => !i.IsUnique)),
                It.IsAny<CancellationToken>()))
            .Callback(() => createOrder.Add("non-unique"))
            .Returns(Task.CompletedTask);

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [record], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        Assert.AreEqual(3, createOrder.Count, "Expected unique → data → non-unique calls");
        Assert.AreEqual("unique",     createOrder[0], "Unique indexes must be created first");
        Assert.AreEqual("data",       createOrder[1], "Data load must follow unique index creation");
        Assert.AreEqual("non-unique", createOrder[2], "Non-unique indexes must be created last");
    }

    [TestMethod]
    public async Task RunAsync_ExistingCollection_SkipsUniqueIndexCreation()
    {
        // Arrange — collection already exists (simulates incremental on known collection).
        var uniqueSpec = System.Text.Json.JsonDocument.Parse(
            """{"key":{"email":1},"name":"email_1","unique":true}""").RootElement.Clone();

        var record = MakeFullBackupRecord();
        record.Collections[0].Indexes =
        [
            new IndexDefinition { Name = "email_1", IsUnique = true, Spec = uniqueSpec }
        ];

        var blobBytes   = BuildBlobBytes(2);
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        // Collection exists — unique index creation must be skipped.
        targetMock
            .Setup(t => t.CollectionExistsAsync(DbName, CollName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2L);

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [record], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);

        // CreateCollectionAsync must NOT have been called — the collection already exists.
        targetMock.Verify(
            t => t.CreateCollectionAsync(
                DbName, CollName,
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<IndexDefinition>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "Collection creation (and unique index application) must be skipped for existing collections");
    }

    [TestMethod]
    public async Task RunAsync_MultiStepChain_NonUniqueIndexCreatedOnlyAfterLastStep()
    {
        const string incrBlobPath = "incremental/2025/Q1/backup-id/2025-01-02/test-db.patients.part-001.bson.gz";
        var fullBlob = BuildBlobBytes(5);
        var incrBlob = BuildBlobBytes(2);

        var nonUniqueSpec = System.Text.Json.JsonDocument.Parse(
            """{"key":{"createdAt":-1},"name":"createdAt_-1"}""").RootElement.Clone();

        var fullRecord = MakeFullBackupRecord(totalDocs: 5);
        fullRecord.Collections[0].Indexes =
        [
            new IndexDefinition { Name = "createdAt_-1", IsUnique = false, Spec = nonUniqueSpec }
        ];

        var incrRecord = new BackupRecord
        {
            BackupId     = "incr-id",
            BackupType   = BackupType.Incremental,
            BackupFolder = "incremental/2025/Q1/backup-id/2025-01-02",
            DatabaseName = DbName,
            AnchorTs     = 1_700_086_400L,
            QuarterLabel = "2025-Q1",
            Collections  =
            [
                new CollectionBackupRecord
                {
                    CollectionName = CollName,
                    TotalDocuments = 2,
                    ChunkCount     = 1,
                    Chunks = [new ChunkRecord { ChunkIndex = 1, BlobPath = incrBlobPath,
                        DocumentCount = 2, Sha256Hash = "xyz", SizeBytes = 256 }],
                    // Incremental captures same index set
                    Indexes = [new IndexDefinition { Name = "createdAt_-1", IsUnique = false, Spec = nonUniqueSpec }]
                }
            ]
        };

        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock.Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(fullBlob));
        storageMock.Setup(s => s.OpenReadAsync(incrBlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(incrBlob));

        targetMock.Setup(t => t.InsertManyAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);
        targetMock.Setup(t => t.UpsertManyAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2L);

        // Incremental step: collection already exists
        targetMock.SetupSequence(t => t.CollectionExistsAsync(
                DbName, CollName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)  // full step: not yet created
            .ReturnsAsync(true);  // incremental step: already there

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [fullRecord, incrRecord], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);

        // Non-unique CreateIndexesAsync should be called exactly once (after the last step).
        targetMock.Verify(
            t => t.CreateIndexesAsync(
                DbName, CollName,
                It.Is<IReadOnlyList<IndexDefinition>>(l => l.All(i => !i.IsUnique)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Non-unique indexes must be created exactly once, after the last chain step");
    }

    [TestMethod]
    public async Task RunAsync_DatabaseShardKeyField_PassedToCreateCollectionAsync()
    {
        // Arrange — the database has a ShardKeyField configured (operator-declared,
        // since Cosmos exposes no command to discover it per collection). It must
        // be passed to CreateCollectionAsync for every new collection in that
        // database, even one with no unique indexes of its own.
        const string shardKeyField = "_pk";

        var record      = MakeFullBackupRecord();
        var blobBytes   = BuildBlobBytes(3);
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3L);

        var config = MakeConfig(shardKeyField: shardKeyField);
        var engine = MakeEngine(storageMock, targetMock, config);
        var result = await engine.RunAsync(MakeRequest(), [record], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);

        targetMock.Verify(
            t => t.CreateCollectionAsync(
                DbName, CollName,
                shardKeyField,
                It.IsAny<IReadOnlyList<IndexDefinition>>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "The database's configured ShardKeyField must be passed for every new collection");
    }

    [TestMethod]
    public async Task RunAsync_CreateCollectionWarning_PropagatedToCollectionResult()
    {
        // Arrange — CreateCollectionAsync returns a non-fatal warning (e.g. a
        // standard-MongoDB target that can't apply the database's shard key).
        // The restore must still complete, carrying the warning through.
        const string warning = "shard key not applied on standard MongoDB target";

        var record      = MakeFullBackupRecord();
        var blobBytes   = BuildBlobBytes(3);
        var storageMock = new Mock<IStorageConnector>();
        var targetMock  = MakeTargetMock();

        storageMock
            .Setup(s => s.OpenReadAsync(BlobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        targetMock
            .Setup(t => t.InsertManyAsync(
                DbName, CollName,
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3L);

        targetMock
            .Setup(t => t.CreateCollectionAsync(
                DbName, CollName,
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<IndexDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(warning);

        var engine = MakeEngine(storageMock, targetMock);
        var result = await engine.RunAsync(MakeRequest(), [record], null, CancellationToken.None);

        Assert.AreEqual(BackupStatus.Completed, result.Status);
        var collResult = result.CollectionResults.Single(r => r.CollectionName == CollName);
        Assert.AreEqual(warning, collResult.Warning);
        Assert.AreEqual(BackupStatus.Completed, collResult.Status);
    }

    // ------------------------------------------------------------------ //
    //  BsonChunkReader — unit tests
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task BsonChunkReader_EmptyStream_YieldsNoDocuments()
    {
        using var empty = new MemoryStream();
        var docs = await BsonChunkReader.ReadDocumentsAsync(empty).ToListAsync();
        Assert.AreEqual(0, docs.Count);
    }

    [TestMethod]
    public async Task BsonChunkReader_MultipleDocuments_YieldsAll()
    {
        byte[] emptyDoc = [0x05, 0x00, 0x00, 0x00, 0x00];

        using var ms = new MemoryStream();
        for (int i = 0; i < 3; i++)
            ms.Write(emptyDoc);
        ms.Position = 0;

        var docs = await BsonChunkReader.ReadDocumentsAsync(ms).ToListAsync();

        Assert.AreEqual(3, docs.Count);
        foreach (var doc in docs)
            Assert.AreEqual(5, doc.Length);
    }

    [TestMethod]
    public async Task BsonChunkReader_TruncatedLengthPrefix_ThrowsInvalidDataException()
    {
        // Only 2 bytes — not enough for a 4-byte length prefix
        using var ms = new MemoryStream([0x05, 0x00]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => BsonChunkReader.ReadDocumentsAsync(ms).ToListAsync());
    }

    [TestMethod]
    public async Task BsonChunkReader_InvalidDocumentLength_ThrowsInvalidDataException()
    {
        // Length = 2 (less than minimum 5)
        using var ms = new MemoryStream([0x02, 0x00, 0x00, 0x00]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => BsonChunkReader.ReadDocumentsAsync(ms).ToListAsync());
    }
}

// ------------------------------------------------------------------ //
//  IAsyncEnumerable extension helpers used only in tests
// ------------------------------------------------------------------ //
file static class AsyncEnumerableExtensions
{
    internal static async Task<List<T>> ToListAsync<T>(
        this IAsyncEnumerable<T> source,
        CancellationToken        ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
