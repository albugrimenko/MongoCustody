using System.IO.Compression;
using System.Security.Cryptography;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="ValidationEngine"/>.
///
/// All infrastructure is mocked — no Azure or MongoDB required.
/// </summary>
[TestClass]
public sealed class ValidationEngineTests
{
    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    private const string BackupId     = "test-backup-id";
    private const string DbName       = "test-db";
    private const string CollName     = "patients";
    private const long   AnchorTs     = 1_700_000_000L;
    private const string FolderPath   = "full/2025/Q1/test-backup-id";

    /// <summary>
    /// Builds a gzip blob whose raw BSON content is <paramref name="rawBytes"/>,
    /// and returns the blob bytes together with the SHA-256 of the raw content.
    /// </summary>
    private static (byte[] BlobBytes, string ExpectedHash) BuildChunkBlob(
        byte[] rawBytes)
    {
        // Compute hash over uncompressed bytes — this mirrors ChunkWriter.
        var hashBytes  = SHA256.HashData(rawBytes);
        var expectHash = Convert.ToHexStringLower(hashBytes);

        // Compress
        using var ms   = new MemoryStream();
        using (var gz  = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(rawBytes, 0, rawBytes.Length);

        return (ms.ToArray(), expectHash);
    }

    /// <summary>Minimal 5-byte empty BSON document.</summary>
    private static byte[] EmptyBsonDoc() => [0x05, 0x00, 0x00, 0x00, 0x00];

    private static BackupRecord MakeFullBackupRecord(
        string     collectionName = CollName,
        long       totalDocs      = 100,
        string     chunkBlobPath  = "full/2025/Q1/chunk.bson.gz",
        string     sha256Hash     = "abc123",
        BackupType backupType     = BackupType.Full,
        long?      queryFromTs    = null)
    {
        return new BackupRecord
        {
            BackupId     = BackupId,
            BackupType   = backupType,
            BackupFolder = FolderPath,
            DatabaseName = DbName,
            AnchorTs     = AnchorTs,
            QueryFromTs  = queryFromTs,
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
                            BlobPath      = chunkBlobPath,
                            DocumentCount = totalDocs,
                            Sha256Hash    = sha256Hash,
                            SizeBytes     = 1024
                        }
                    ]
                }
            ]
        };
    }

    private static MongoCustodyConfiguration DefaultConfig() => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = DbName }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections = 2,
            ChunkSizeGb            = 10,
            BlobBlockSizeMb        = 100,
            CompressionLevel       = MongoCustody.Core.Models.CompressionLevel.Fastest
        }
    };

    private static ValidationEngine MakeEngine(
        Mock<IBackupSourceConnector>  sourceMock,
        Mock<IStorageConnector>       storageMock,
        Mock<IMetadataRepository>     metaMock)
        => new(sourceMock.Object, storageMock.Object, metaMock.Object, DefaultConfig());

    // ------------------------------------------------------------------ //
    //  Backup not found
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_BackupNotFound_ReturnsEmptyResult()
    {
        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupRecord?)null);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.StructuralIntegrity },
            CancellationToken.None);

        Assert.AreEqual(BackupId, result.BackupId);
        Assert.AreEqual(ValidationLevel.None, result.LevelsExecuted);
        Assert.AreEqual(0, result.CollectionResults.Count);
    }

    // ------------------------------------------------------------------ //
    //  Level 1 — Structural Integrity
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_L1_HashMatch_PassesAllCollections()
    {
        var rawBytes           = EmptyBsonDoc();
        var (blobBytes, hash) = BuildChunkBlob(rawBytes);
        const string blobPath  = "full/2025/Q1/chunk.bson.gz";

        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(sha256Hash: hash, chunkBlobPath: blobPath);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.StructuralIntegrity },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(1, result.CollectionResults.Count);
        Assert.IsTrue(result.CollectionResults[0].Passed);
        Assert.AreEqual(ValidationLevel.None, result.CollectionResults[0].FailedLevels);
    }

    [TestMethod]
    public async Task ValidateAsync_L1_HashMismatch_FailsWithDetail()
    {
        var rawBytes  = EmptyBsonDoc();
        var (blobBytes, _) = BuildChunkBlob(rawBytes);
        const string blobPath = "full/2025/Q1/chunk.bson.gz";

        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        // Store wrong hash in the manifest
        var record = MakeFullBackupRecord(sha256Hash: "deadbeef", chunkBlobPath: blobPath);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var engine = MakeEngine(
            new Mock<IBackupSourceConnector>(), storageMock, metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.StructuralIntegrity },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        var colResult = result.CollectionResults[0];
        Assert.IsFalse(colResult.Passed);
        Assert.IsTrue((colResult.FailedLevels & ValidationLevel.StructuralIntegrity) != 0);
        StringAssert.Contains(colResult.FailureDetail, "hash mismatch");
        StringAssert.Contains(colResult.FailureDetail, "deadbeef");
    }

    [TestMethod]
    public async Task ValidateAsync_L1_BlobReadError_FailsWithDetail()
    {
        const string blobPath = "full/2025/Q1/chunk.bson.gz";

        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(chunkBlobPath: blobPath);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blob not found"));

        var engine = MakeEngine(
            new Mock<IBackupSourceConnector>(), storageMock, metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.StructuralIntegrity },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        var colResult = result.CollectionResults[0];
        Assert.IsFalse(colResult.Passed);
        Assert.IsTrue((colResult.FailedLevels & ValidationLevel.StructuralIntegrity) != 0);
        StringAssert.Contains(colResult.FailureDetail, "failed to read blob");
    }

    // ------------------------------------------------------------------ //
    //  Level 2 — Count Consistency
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_L2_FullBackup_ExactMatch_Passes()
    {
        const long docCount = 500;
        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(totalDocs: docCount, sha256Hash: "ignored");

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsExistingAtAsync(
                DbName, CollName, AnchorTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(docCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.CountConsistency },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(docCount, result.CollectionResults[0].LiveDocumentCount);
    }

    [TestMethod]
    public async Task ValidateAsync_L2_FullBackup_SmallCollection_ExactMismatch_Fails()
    {
        // 9 000 docs → exact comparison, no tolerance
        const long manifestCount = 9_000;
        const long liveCount     = 8_999;

        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(totalDocs: manifestCount, sha256Hash: "ignored");

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsExistingAtAsync(
                DbName, CollName, AnchorTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.CountConsistency },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        var colResult = result.CollectionResults[0];
        Assert.IsFalse(colResult.Passed);
        Assert.IsTrue((colResult.FailedLevels & ValidationLevel.CountConsistency) != 0);
        StringAssert.Contains(colResult.FailureDetail, "mismatch");
        Assert.AreEqual(liveCount, colResult.LiveDocumentCount);
    }

    [TestMethod]
    public async Task ValidateAsync_L2_FullBackup_LargeCollection_WithinTolerance_Passes()
    {
        // 1 000 000 docs; tolerance = 100 docs (0.01 %)
        const long manifestCount = 1_000_000;
        const long liveCount     = 1_000_050; // within tolerance

        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(totalDocs: manifestCount, sha256Hash: "ignored");

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsExistingAtAsync(
                DbName, CollName, AnchorTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.CountConsistency },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
    }

    [TestMethod]
    public async Task ValidateAsync_L2_FullBackup_LargeCollection_ExceedsTolerance_Fails()
    {
        // 1 000 000 docs; tolerance = 100 docs; diff = 200
        const long manifestCount = 1_000_000;
        const long liveCount     = 1_000_200; // exceeds tolerance

        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(totalDocs: manifestCount, sha256Hash: "ignored");

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsExistingAtAsync(
                DbName, CollName, AnchorTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.CountConsistency },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        StringAssert.Contains(result.CollectionResults[0].FailureDetail, "exceeds tolerance");
    }

    [TestMethod]
    public async Task ValidateAsync_L2_IncrementalBackup_CountConsistency_IsSkipped()
    {
        // Level 2 only applies to full backups; requesting it for incremental
        // should not fail — it simply does nothing.
        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(
            totalDocs:   50,
            sha256Hash:  "ignored",
            backupType:  BackupType.Incremental,
            queryFromTs: AnchorTs - 86_400);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.CountConsistency
            },
            CancellationToken.None);

        // No failure — level was silently skipped
        Assert.IsTrue(result.Passed);
        sourceMock.Verify(
            s => s.CountDocumentsExistingAtAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Level 3 — Incremental Completeness
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_L3_IncrementalBackup_SufficientCount_Passes()
    {
        const long queryFromTs     = AnchorTs - 86_400;
        const long manifestCount   = 50;
        const long liveChangedCount = 40; // manifest >= live — OK

        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(
            totalDocs:   manifestCount,
            sha256Hash:  "ignored",
            backupType:  BackupType.Incremental,
            queryFromTs: queryFromTs);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsAsOfAsync(
                DbName, CollName, queryFromTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveChangedCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.IncrementalCompleteness
            },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(liveChangedCount, result.CollectionResults[0].LiveDocumentCount);
    }

    [TestMethod]
    public async Task ValidateAsync_L3_IncrementalBackup_InsufficientCount_Fails()
    {
        const long queryFromTs      = AnchorTs - 86_400;
        const long manifestCount    = 30;
        const long liveChangedCount = 50; // manifest < live — gap!

        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(
            totalDocs:   manifestCount,
            sha256Hash:  "ignored",
            backupType:  BackupType.Incremental,
            queryFromTs: queryFromTs);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        sourceMock
            .Setup(s => s.CountDocumentsAsOfAsync(
                DbName, CollName, queryFromTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveChangedCount);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.IncrementalCompleteness
            },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        var colResult = result.CollectionResults[0];
        Assert.IsTrue((colResult.FailedLevels & ValidationLevel.IncrementalCompleteness) != 0);
        StringAssert.Contains(colResult.FailureDetail, "Incremental count insufficient");
    }

    [TestMethod]
    public async Task ValidateAsync_L3_IncrementalBackup_MissingQueryFromTs_Fails()
    {
        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        // QueryFromTs is null — manifest is malformed
        var record = MakeFullBackupRecord(
            totalDocs:   50,
            sha256Hash:  "ignored",
            backupType:  BackupType.Incremental,
            queryFromTs: null);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.IncrementalCompleteness
            },
            CancellationToken.None);

        Assert.IsFalse(result.Passed);
        StringAssert.Contains(result.CollectionResults[0].FailureDetail, "QueryFromTs is absent");
    }

    [TestMethod]
    public async Task ValidateAsync_L3_FullBackup_IncrementalCompleteness_IsSkipped()
    {
        // Level 3 only applies to incremental backups.
        var sourceMock = new Mock<IBackupSourceConnector>();
        var metaMock   = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(
            totalDocs: 100, sha256Hash: "ignored"); // BackupType.Full by default

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var engine = MakeEngine(
            sourceMock, new Mock<IStorageConnector>(), metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.IncrementalCompleteness
            },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
        sourceMock.Verify(
            s => s.CountDocumentsAsOfAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Combined levels
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_L1AndL2_BothPass()
    {
        var rawBytes           = EmptyBsonDoc();
        var (blobBytes, hash) = BuildChunkBlob(rawBytes);
        const string blobPath = "full/2025/Q1/chunk.bson.gz";
        const long   docCount = 200;

        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(
            totalDocs: docCount, sha256Hash: hash, chunkBlobPath: blobPath);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        sourceMock
            .Setup(s => s.CountDocumentsExistingAtAsync(
                DbName, CollName, AnchorTs, It.IsAny<CancellationToken>()))
            .ReturnsAsync(docCount);

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        var result = await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.StructuralIntegrity | ValidationLevel.CountConsistency
            },
            CancellationToken.None);

        Assert.IsTrue(result.Passed);
        Assert.AreEqual(
            ValidationLevel.StructuralIntegrity | ValidationLevel.CountConsistency,
            result.LevelsExecuted);
    }

    [TestMethod]
    public async Task ValidateAsync_L1Fails_L2IsNotExecuted()
    {
        // When L1 fails, the engine short-circuits and does not run L2.
        const string blobPath = "full/2025/Q1/chunk.bson.gz";

        // Blob bytes that will NOT match the stored hash
        var (blobBytes, _) = BuildChunkBlob(EmptyBsonDoc());

        var sourceMock  = new Mock<IBackupSourceConnector>();
        var storageMock = new Mock<IStorageConnector>();
        var metaMock    = new Mock<IMetadataRepository>();

        var record = MakeFullBackupRecord(sha256Hash: "wronghash", chunkBlobPath: blobPath);

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var engine = MakeEngine(sourceMock, storageMock, metaMock);
        await engine.ValidateAsync(
            new ValidationRequest
            {
                BackupId = BackupId,
                Levels   = ValidationLevel.StructuralIntegrity | ValidationLevel.CountConsistency
            },
            CancellationToken.None);

        sourceMock.Verify(
            s => s.CountDocumentsExistingAtAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Level 2 must not run when Level 1 fails");
    }

    // ------------------------------------------------------------------ //
    //  ValidationResult.Passed computed property
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateAsync_EmptyCollections_Passed_IsFalse()
    {
        // A backup with no collections: Passed must be false (no results → can't pass).
        var metaMock = new Mock<IMetadataRepository>();
        var record = new BackupRecord
        {
            BackupId     = BackupId,
            BackupType   = BackupType.Full,
            BackupFolder = FolderPath,
            DatabaseName = DbName,
            AnchorTs     = AnchorTs,
            QuarterLabel = "2025-Q1",
            Collections  = []
        };

        metaMock
            .Setup(m => m.GetBackupRecordAsync(BackupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var engine = MakeEngine(
            new Mock<IBackupSourceConnector>(),
            new Mock<IStorageConnector>(),
            metaMock);

        var result = await engine.ValidateAsync(
            new ValidationRequest { BackupId = BackupId, Levels = ValidationLevel.StructuralIntegrity },
            CancellationToken.None);

        Assert.IsFalse(result.Passed,
            "ValidationResult.Passed requires at least one collection result");
    }
}
