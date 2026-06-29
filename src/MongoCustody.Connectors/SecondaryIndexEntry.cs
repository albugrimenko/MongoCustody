namespace MongoCustody.Connectors;

// Lightweight record stored in each secondary index entry blob (~120 bytes).
// The completedAt timestamp is encoded in the containing folder name; only
// the folder path and anchorTs need to be stored here.
internal sealed record SecondaryIndexEntry(
    string BackupId,
    string Folder,
    long   AnchorTs);
