namespace MongoCustody.Core.Models;

/// <summary>
/// The aggregate result of a validation run across one or more levels.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>The backup ID that was validated.</summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>The validation levels that were executed.</summary>
    public ValidationLevel LevelsExecuted { get; set; }

    /// <summary>
    /// <c>true</c> if every collection passed every requested level.
    /// </summary>
    public bool Passed => CollectionResults.Count > 0
        && CollectionResults.All(r => r.Passed);

    /// <summary>UTC time the validation run completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>Per-collection validation results.</summary>
    public List<CollectionValidationResult> CollectionResults { get; set; } = [];
}

/// <summary>
/// Validation result for a single collection within a
/// <see cref="ValidationResult"/>.
/// </summary>
public sealed class CollectionValidationResult
{
    /// <summary>Name of the collection.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> if this collection passed all requested validation levels.
    /// </summary>
    public bool Passed { get; set; }

    /// <summary>
    /// The validation levels that failed for this collection.
    /// <see cref="ValidationLevel.None"/> when all levels passed.
    /// </summary>
    public ValidationLevel FailedLevels { get; set; }

    /// <summary>
    /// Human-readable description of the first failure encountered.
    /// <c>null</c> when <see cref="Passed"/> is <c>true</c>.
    /// </summary>
    public string? FailureDetail { get; set; }

    /// <summary>
    /// Document count recorded in the backup manifest for this collection.
    /// </summary>
    public long ManifestDocumentCount { get; set; }

    /// <summary>
    /// Document count from the live database query (for count-based levels).
    /// <c>null</c> when <see cref="ValidationLevel.CountConsistency"/> and
    /// <see cref="ValidationLevel.IncrementalCompleteness"/> were not executed.
    /// </summary>
    public long? LiveDocumentCount { get; set; }
}
