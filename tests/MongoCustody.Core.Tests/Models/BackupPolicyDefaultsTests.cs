namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class BackupPolicyDefaultsTests
{
    [TestMethod]
    public void DefaultIncrementalBufferSeconds_Is300()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(300, policy.IncrementalBufferSeconds);
    }

    [TestMethod]
    public void DefaultBackupFormat_IsBson()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(BackupFormat.Bson, policy.BackupFormat);
    }

    [TestMethod]
    public void DefaultCompressionLevel_IsFastest()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(CompressionLevel.Fastest, policy.CompressionLevel);
    }

    [TestMethod]
    public void DefaultMaxParallelCollections_Is4()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(4, policy.MaxParallelCollections);
    }

    [TestMethod]
    public void DefaultBlobBlockSizeMb_Is100()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(100, policy.BlobBlockSizeMb);
    }

    [TestMethod]
    public void DefaultChunkSizeGb_Is10()
    {
        var policy = new BackupPolicy();
        Assert.AreEqual(10, policy.ChunkSizeGb);
    }
}
