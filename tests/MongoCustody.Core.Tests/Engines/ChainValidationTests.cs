using System.IO.Compression;
using System.Security.Cryptography;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using Moq;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Tests for <see cref="MongoCustodyService.ValidateRestoreChainAsync"/>.
///
/// The service is used directly so that chain resolution and per-step
/// validation are exercised together. All infrastructure is mocked.
/// </summary>
[TestClass]
public sealed class ChainValidationTests
{
    // ------------------------------------------------------------------ //
    //  Constants and helpers
    // ------------------------------------------------------------------ //

    private const string DbName      = "test-db";
    private const long   FullAnchor  = 1_700_000_000L;
    private const long   IncrAnchor  = 1_700_010_000L;
    private const string FullId      = "full-backup-id";
    private const string IncrId      = "incr-backup-id";
    private const string FullFolder  = "full/2025/Q1/full-backup-id/";
    private const string IncrFolder  = "incremental/2025/Q1/full-backup-id/incr-backup-id/";
    private const string CollName    = "patients";

    private static MongoCustodyConfiguration DefaultConfig() => new()
    {
        StorageContainerName = "backups",
        Databases            = [new DatabaseConfiguration { DatabaseName = DbName }],
        Policy               = new BackupPolicy
        {
            MaxParallelCollections  = 2,
            ChunkSizeGb             = 10,
            BlobBlockSizeMb         = 100,
            CompressionLevel        = MongoCustody.Core.Models.CompressionLevel.Fastest,
            IncrementalBufferSeconds = 300,
            MaxIncrementalChainDepth = 19
        }
    };

    /// <summary>Builds a valid gzip blob + SHA-256 hash for a single empty BSON doc.</summary>
    private static (byte[] BlobBytes, string Hash) BuildBlob()
    {
        var raw  = new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(raw);
        return (ms.ToArray(), hash);
    }

    /// <summary>Full backup record with one collection and one valid chunk blob.</summary>
    private static BackupRecord MakeFullRecord(string blobPath, string hash) => new()
    {
        BackupId       = FullId,
        BackupType     = BackupType.Full,
        BackupFolder   = FullFolder,
        DatabaseName   = DbName,
        AnchorTs       = FullAnchor,
        QuarterLabel   = "2025-Q1",
        CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
        Collections    =
        [
            new CollectionBackupRecord
            {
                CollectionName = CollName,
                TotalDocuments = 1,
                ChunkCount     = 1,
                Chunks         =
                [
                    new ChunkRecord
                    {
                        ChunkIndex    = 1,
                        BlobPath      = blobPath,
                        DocumentCount = 1,
                        Sha256Hash    = hash,
                        SizeBytes     = 10
                    }
                ]
            }
        ]
    };

    /// <summary>Incremental backup record with one collection and one valid chunk blob.</summary>
    private static BackupRecord MakeIncrRecord(string blobPath, string hash) => new()
    {
        BackupId       = IncrId,
        BackupType     = BackupType.Incremental,
        BackupFolder   = IncrFolder,
        DatabaseName   = DbName,
        AnchorTs       = IncrAnchor,
        QueryFromTs    = FullAnchor - 300,   // FullAnchor - bufferSeconds
        ParentFolder   = FullFolder,
        QuarterLabel   = "2025-Q1",
        CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        Collections    =
        [
            new CollectionBackupRecord
            {
                CollectionName = CollName,
                TotalDocuments = 1,
                ChunkCount     = 1,
                Chunks         =
                [
                    new ChunkRecord
                    {
                        ChunkIndex    = 1,
                        BlobPath      = blobPath,
                        DocumentCount = 1,
                        Sha256Hash    = hash,
                        SizeBytes     = 10
                    }
                ]
            }
        ]
    };

    private static IMongoCustodyService BuildService(
        Mock<IMetadataRepository>    metaMock,
        Mock<IStorageConnector>      storageMock,
        Mock<IBackupSourceConnector> sourceMock)
        => new MongoCustodyService(
            sourceMock.Object,
            storageMock.Object,
            metaMock.Object,
            DefaultConfig());

    // ------------------------------------------------------------------ //
    //  Happy path — full + incremental chain, both pass L1
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateRestoreChainAsync_TwoStepChain_BothPassL1()
    {
        var (blobBytes, hash) = BuildBlob();
        const string blobPath = "some/blob.bson.gz";

        var fullRecord = MakeFullRecord(blobPath, hash);
        var incrRecord = MakeIncrRecord(blobPath, hash);

        var metaMock    = new Mock<IMetadataRepository>();
        var storageMock = new Mock<IStorageConnector>();
        var sourceMock  = new Mock<IBackupSourceConnector>();

        var targetDate = DateTimeOffset.UtcNow;

        // Chain resolution returns [full, incr] ordered oldest-first.
        metaMock
            .Setup(m => m.ResolveRestoreChainAsync(DbName, targetDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupRecord> { fullRecord, incrRecord });

        // Per-step validation looks up each record by ID.
        metaMock
            .Setup(m => m.GetBackupRecordAsync(FullId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRecord);
        metaMock
            .Setup(m => m.GetBackupRecordAsync(IncrId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(incrRecord);

        // The incremental coherence check needs the parent manifest.
        metaMock
            .Setup(m => m.GetBackupRecordByFolderAsync(FullFolder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRecord);

        // Both blobs return valid bytes.
        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var svc    = BuildService(metaMock, storageMock, sourceMock);
        var result = await svc.ValidateRestoreChainAsync(
            DbName, targetDate, ValidationLevel.StructuralIntegrity,
            CancellationToken.None);

        Assert.IsTrue(result.Passed, "Both steps should pass L1.");
        Assert.AreEqual(2, result.Steps.Count);

        Assert.AreEqual(BackupType.Full,        result.Steps[0].BackupType);
        Assert.AreEqual(BackupType.Incremental, result.Steps[1].BackupType);
        Assert.IsTrue(result.Steps[0].Validation.Passed);
        Assert.IsTrue(result.Steps[1].Validation.Passed);
        Assert.IsNull(result.Steps[1].Validation.ManifestCoherenceDetail);
    }

    // ------------------------------------------------------------------ //
    //  Failing step propagates to chain result
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateRestoreChainAsync_IncrementalStep_CoherenceFailure_ChainFails()
    {
        var (blobBytes, hash) = BuildBlob();
        const string blobPath = "some/blob.bson.gz";

        var fullRecord = MakeFullRecord(blobPath, hash);

        // Incremental with broken parent (ParentFolder points to a non-existent manifest).
        var baseIncr   = MakeIncrRecord(blobPath, hash);
        var brokenIncr = new BackupRecord
        {
            BackupId       = baseIncr.BackupId,
            BackupType     = baseIncr.BackupType,
            BackupFolder   = baseIncr.BackupFolder,
            DatabaseName   = baseIncr.DatabaseName,
            AnchorTs       = baseIncr.AnchorTs,
            QueryFromTs    = baseIncr.QueryFromTs,
            ParentFolder   = "full/2025/Q1/missing-parent/",
            QuarterLabel   = baseIncr.QuarterLabel,
            CompletedAtUtc = baseIncr.CompletedAtUtc,
            Collections    = baseIncr.Collections
        };

        var metaMock    = new Mock<IMetadataRepository>();
        var storageMock = new Mock<IStorageConnector>();
        var sourceMock  = new Mock<IBackupSourceConnector>();

        var targetDate = DateTimeOffset.UtcNow;

        metaMock
            .Setup(m => m.ResolveRestoreChainAsync(DbName, targetDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupRecord> { fullRecord, brokenIncr });

        metaMock
            .Setup(m => m.GetBackupRecordAsync(FullId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRecord);
        metaMock
            .Setup(m => m.GetBackupRecordAsync(IncrId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brokenIncr);

        // Parent manifest is absent.
        metaMock
            .Setup(m => m.GetBackupRecordByFolderAsync(
                "full/2025/Q1/missing-parent/", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BackupRecord?)null);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var svc    = BuildService(metaMock, storageMock, sourceMock);
        var result = await svc.ValidateRestoreChainAsync(
            DbName, targetDate, ValidationLevel.StructuralIntegrity,
            CancellationToken.None);

        Assert.IsFalse(result.Passed, "Chain must fail when any step fails.");
        Assert.IsTrue(result.Steps[0].Validation.Passed,   "Full backup step must still pass.");
        Assert.IsFalse(result.Steps[1].Validation.Passed,  "Incremental step must fail.");
        Assert.IsNotNull(result.Steps[1].Validation.ManifestCoherenceDetail);
        StringAssert.Contains(result.Steps[1].Validation.ManifestCoherenceDetail, "no manifest");
    }

    // ------------------------------------------------------------------ //
    //  Empty chain (no backup found)
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateRestoreChainAsync_NoChainFound_ReturnsFailedEmptyResult()
    {
        var metaMock   = new Mock<IMetadataRepository>();
        var targetDate = DateTimeOffset.UtcNow;

        metaMock
            .Setup(m => m.ResolveRestoreChainAsync(DbName, targetDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupRecord>());

        var svc    = BuildService(metaMock, new Mock<IStorageConnector>(), new Mock<IBackupSourceConnector>());
        var result = await svc.ValidateRestoreChainAsync(
            DbName, targetDate, ValidationLevel.StructuralIntegrity,
            CancellationToken.None);

        Assert.IsFalse(result.Passed, "An empty chain must not pass.");
        Assert.AreEqual(0, result.Steps.Count);
        Assert.AreEqual(DbName, result.DatabaseName);
    }

    // ------------------------------------------------------------------ //
    //  Step metadata is correctly populated
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task ValidateRestoreChainAsync_StepMetadata_IsCorrect()
    {
        var (blobBytes, hash) = BuildBlob();
        const string blobPath = "some/blob.bson.gz";

        var fullRecord = MakeFullRecord(blobPath, hash);
        var incrRecord = MakeIncrRecord(blobPath, hash);

        var metaMock    = new Mock<IMetadataRepository>();
        var storageMock = new Mock<IStorageConnector>();
        var targetDate  = DateTimeOffset.UtcNow;

        metaMock
            .Setup(m => m.ResolveRestoreChainAsync(DbName, targetDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupRecord> { fullRecord, incrRecord });
        metaMock
            .Setup(m => m.GetBackupRecordAsync(FullId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRecord);
        metaMock
            .Setup(m => m.GetBackupRecordAsync(IncrId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(incrRecord);
        metaMock
            .Setup(m => m.GetBackupRecordByFolderAsync(FullFolder, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullRecord);

        storageMock
            .Setup(s => s.OpenReadAsync(blobPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(blobBytes));

        var svc    = BuildService(metaMock, storageMock, new Mock<IBackupSourceConnector>());
        var result = await svc.ValidateRestoreChainAsync(
            DbName, targetDate, ValidationLevel.StructuralIntegrity,
            CancellationToken.None);

        var step1 = result.Steps[0];
        Assert.AreEqual(1,              step1.StepIndex);
        Assert.AreEqual(BackupType.Full, step1.BackupType);
        Assert.AreEqual(FullId,         step1.BackupId);
        Assert.AreEqual(FullFolder,     step1.BackupFolder);

        var step2 = result.Steps[1];
        Assert.AreEqual(2,                   step2.StepIndex);
        Assert.AreEqual(BackupType.Incremental, step2.BackupType);
        Assert.AreEqual(IncrId,              step2.BackupId);
        Assert.AreEqual(IncrFolder,          step2.BackupFolder);
    }
}
