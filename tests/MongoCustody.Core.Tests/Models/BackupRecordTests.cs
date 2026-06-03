namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class BackupRecordTests
{
    [TestMethod]
    public void DefaultSchemaVersion_Is1()
    {
        var record = new BackupRecord();
        Assert.AreEqual(1, record.SchemaVersion);
    }

    [TestMethod]
    public void DefaultSourceIncrementalFolders_IsEmpty()
    {
        var record = new BackupRecord();
        Assert.IsNotNull(record.SourceIncrementalFolders);
        Assert.IsEmpty(record.SourceIncrementalFolders);
    }

    [TestMethod]
    public void DefaultCollections_IsEmpty()
    {
        var record = new BackupRecord();
        Assert.IsNotNull(record.Collections);
        Assert.IsEmpty(record.Collections);
    }

    [TestMethod]
    public void QueryFromTs_IsNullByDefault()
    {
        var record = new BackupRecord();
        Assert.IsNull(record.QueryFromTs);
    }

    [TestMethod]
    public void ParentFolder_IsNullByDefault()
    {
        var record = new BackupRecord();
        Assert.IsNull(record.ParentFolder);
    }

    [TestMethod]
    public void WeekNumber_IsNullByDefault()
    {
        var record = new BackupRecord();
        Assert.IsNull(record.WeekNumber);
    }
}
