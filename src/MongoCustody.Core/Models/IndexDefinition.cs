using System.Text.Json;
using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// Captures the full specification of a single MongoDB index as recorded
/// at backup time. Stored inside <see cref="CollectionBackupRecord.Indexes"/>
/// and used during restore to recreate the index with exactly the same
/// key pattern and options.
/// </summary>
public sealed class IndexDefinition
{
    /// <summary>Index name, e.g. <c>email_1</c> or <c>tenant_1_email_1</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> for unique indexes (including compound unique, sparse
    /// unique, and partial unique). Determines restore sequencing:
    /// unique indexes must be created on an empty collection before data
    /// is loaded (required by Cosmos DB for MongoDB and correct for all
    /// MongoDB-compatible targets).
    /// </summary>
    [JsonPropertyName("isUnique")]
    public bool IsUnique { get; set; }

    /// <summary>
    /// Complete index specification as returned by the MongoDB
    /// <c>listIndexes</c> command, serialised in relaxed extended JSON.
    /// Contains the <c>key</c>, <c>name</c>, and all option fields
    /// (<c>unique</c>, <c>sparse</c>, <c>expireAfterSeconds</c>,
    /// <c>partialFilterExpression</c>, <c>collation</c>, etc.).
    /// Stored as an inline JSON node to support any future MongoDB index
    /// option without a schema change.
    /// </summary>
    [JsonPropertyName("spec")]
    public JsonElement Spec { get; set; }
}
