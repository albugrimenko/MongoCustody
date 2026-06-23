namespace MongoCustody.Core.Models;

/// <summary>
/// Parameters for a validation run against an existing backup.
/// </summary>
public sealed class ValidationRequest
{
    /// <summary>
    /// The unique identifier of the backup to validate.
    /// Corresponds to <see cref="BackupRecord.BackupId"/>.
    /// </summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>
    /// One or more validation levels to execute.
    /// Multiple levels can be combined using bitwise OR.
    /// </summary>
    public ValidationLevel Levels { get; set; } = ValidationLevel.StructuralIntegrity;

}
