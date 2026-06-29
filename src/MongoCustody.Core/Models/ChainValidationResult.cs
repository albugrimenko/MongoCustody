namespace MongoCustody.Core.Models;

/// <summary>
/// The aggregate result of validating an entire restore chain.
/// </summary>
public sealed class ChainValidationResult
{
    /// <summary>Database that was validated.</summary>
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>The point in time the chain was resolved for.</summary>
    public DateTimeOffset RequestedDate { get; init; }

    /// <summary><c>true</c> when the chain is non-empty and every step passed.</summary>
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Validation.Passed);

    /// <summary>Per-step results ordered oldest-first.</summary>
    public IReadOnlyList<ChainStepValidationResult> Steps { get; init; } = [];
}

/// <summary>
/// Validation result for one step (one <see cref="BackupRecord"/>) within
/// a <see cref="ChainValidationResult"/>.
/// </summary>
public sealed class ChainStepValidationResult
{
    /// <summary>1-based position in the restore chain (1 = full backup).</summary>
    public int StepIndex { get; init; }

    /// <summary>Type of this backup step.</summary>
    public BackupType BackupType { get; init; }

    /// <summary>Backup ID for this step.</summary>
    public string BackupId { get; init; } = string.Empty;

    /// <summary>Storage folder path for this step.</summary>
    public string BackupFolder { get; init; } = string.Empty;

    /// <summary>UTC time this backup completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Validation result for this step.</summary>
    public required ValidationResult Validation { get; init; }
}
