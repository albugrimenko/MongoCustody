using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// Input to <c>GenerateAuditReportAsync</c>. Supports two modes:
/// <list type="bullet">
///   <item>Range report — set <see cref="From"/> and <see cref="To"/>.</item>
///   <item>Single-restore report — set <see cref="RestoreId"/>.</item>
/// </list>
/// Exactly one mode must be specified.
/// </summary>
public sealed class AuditReportRequest
{
    /// <summary>Start of the date range (inclusive). Required for range mode.</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; set; }

    /// <summary>End of the date range (inclusive). Required for range mode.</summary>
    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Optional database filter for range mode. When <c>null</c>, all
    /// databases are included.
    /// </summary>
    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Restore ID for single-restore mode. When set, <see cref="From"/>,
    /// <see cref="To"/>, and <see cref="DatabaseName"/> are ignored.
    /// </summary>
    [JsonPropertyName("restoreId")]
    public string? RestoreId { get; set; }

    /// <summary>
    /// Identity of the person who requested the report (e.g., auditor name
    /// or employee ID). Recorded in the report header.
    /// </summary>
    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; set; } = Environment.MachineName;
}
