namespace MongoCustody.Core.Models;

/// <summary>
/// The level of verification to perform against a backup.
/// Levels are not mutually exclusive; multiple can be requested per run.
/// </summary>
[Flags]
public enum ValidationLevel
{
    /// <summary>
    /// No validation.
    /// </summary>
    None = 0,

    /// <summary>
    /// Level 1 — Structural Integrity.
    /// Recomputes the SHA-256 hash of each collection chunk blob and
    /// compares it to the hash stored in <c>manifest.json</c> at write time.
    /// Runs automatically after every backup type.
    /// </summary>
    StructuralIntegrity = 1 << 0,

    /// <summary>
    /// Level 2 — Count Consistency.
    /// Verifies that the document count in the backup matches a
    /// soft-delete-aware count query against the live database as of
    /// the anchor timestamp.
    /// Runs automatically after every full backup.
    /// </summary>
    CountConsistency = 1 << 1,

    /// <summary>
    /// Level 3 — Incremental Completeness.
    /// Verifies that the incremental document count is ≥ the count of
    /// documents modified in the live database since the previous anchor.
    /// Runs automatically after every incremental backup.
    /// </summary>
    IncrementalCompleteness = 1 << 2
}
