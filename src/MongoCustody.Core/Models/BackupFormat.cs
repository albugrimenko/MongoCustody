namespace MongoCustody.Core.Models;

/// <summary>
/// The serialisation format used for backup chunk files.
/// </summary>
public enum BackupFormat
{
    /// <summary>
    /// Binary JSON — preserves all MongoDB native types faithfully
    /// (ObjectId, Date, Decimal128, Binary). This is the only supported
    /// format for production use.
    /// </summary>
    Bson
}
