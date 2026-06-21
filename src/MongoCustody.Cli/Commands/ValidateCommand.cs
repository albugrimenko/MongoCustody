using System.CommandLine;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli.Commands;

internal static class ValidateCommand
{
    internal static Command Build()
    {
        var configOpt    = CommonOptions.Config();
        var outputOpt    = CommonOptions.Output();

        var backupIdOpt = new Option<string>("--backup-id", ["-b"])
        {
            Description = "ID of the backup to validate.",
            Required    = true
        };

        var levelOpt = new Option<string[]>("--level", ["-l"])
        {
            Description =
                "Validation level(s) to run. " +
                "Allowed values: structural (L1), count (L2), incremental (L3), dry-run (L4), all. " +
                "Defaults to structural.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => ["structural"]
        };

        var dryRunConnOpt = new Option<string?>("--dry-run-connection-string")
        {
            Description =
                "MongoDB connection string for the ephemeral restore target. " +
                "Required when --level includes dry-run (L4)."
        };

        var cmd = new Command("validate",
            "Validate an existing backup at one or more verification levels.");
        cmd.Add(configOpt);
        cmd.Add(backupIdOpt);
        cmd.Add(levelOpt);
        cmd.Add(dryRunConnOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath    = parseResult.GetValue(configOpt)!;
            var backupId      = parseResult.GetValue(backupIdOpt)!;
            var levelStrings  = parseResult.GetValue(levelOpt) ?? ["structural"];
            var dryRunConn    = parseResult.GetValue(dryRunConnOpt);
            var format        = parseResult.GetValue(outputOpt);

            ValidationLevel levels;
            try { levels = ParseLevels(levelStrings); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }

            if (levels.HasFlag(ValidationLevel.RestoreDryRun) &&
                string.IsNullOrWhiteSpace(dryRunConn))
            {
                Console.Error.WriteLine(
                    "Error: --dry-run-connection-string is required when " +
                    "--level includes dry-run (L4).");
                return 2;
            }

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine(
                $"Validating backup '{backupId}' at level(s): {levels}…");

            var result = await svc.ValidateBackupAsync(
                new ValidationRequest
                {
                    BackupId                    = backupId,
                    Levels                      = levels,
                    DryRunTargetConnectionString = dryRunConn
                }, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    backupId        = result.BackupId,
                    passed          = result.Passed,
                    levelsExecuted  = result.LevelsExecuted.ToString(),
                    completedAt     = result.CompletedAtUtc,
                    collections     = result.CollectionResults.Select(c => new
                    {
                        collection          = c.CollectionName,
                        passed              = c.Passed,
                        failedLevels        = c.FailedLevels == ValidationLevel.None
                                                 ? null
                                                 : c.FailedLevels.ToString(),
                        failureDetail       = c.FailureDetail,
                        manifestDocuments   = c.ManifestDocumentCount,
                        liveDocuments       = c.LiveDocumentCount
                    })
                });
            }
            else
            {
                var overall = result.Passed ? "PASSED" : "FAILED";
                Output.WriteSeparator();
                Console.WriteLine($"Backup ID  : {result.BackupId}");
                Console.WriteLine($"Result     : {overall}");
                Console.WriteLine($"Levels     : {result.LevelsExecuted}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");

                foreach (var col in result.CollectionResults)
                {
                    var icon = col.Passed ? "✓" : "✗";
                    Console.WriteLine();
                    Console.WriteLine(
                        $"  {icon}  {col.CollectionName,-40} " +
                        $"manifest: {col.ManifestDocumentCount,10:N0} docs" +
                        (col.LiveDocumentCount.HasValue
                            ? $"  live: {col.LiveDocumentCount.Value,10:N0} docs"
                            : string.Empty));
                    if (col.FailureDetail is not null)
                        Console.WriteLine($"       {col.FailureDetail}");
                }
                Output.WriteSeparator();
            }

            return result.Passed ? 0 : 1;
        });

        return cmd;
    }

    private static ValidationLevel ParseLevels(string[] values)
    {
        var result = ValidationLevel.None;
        foreach (var raw in values)
        {
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                   StringSplitOptions.TrimEntries))
            {
                result |= token.ToLowerInvariant() switch
                {
                    "structural" or "l1" => ValidationLevel.StructuralIntegrity,
                    "count"      or "l2" => ValidationLevel.CountConsistency,
                    "incremental"or "l3" => ValidationLevel.IncrementalCompleteness,
                    "dry-run"    or "l4" => ValidationLevel.RestoreDryRun,
                    "all"                => ValidationLevel.StructuralIntegrity |
                                           ValidationLevel.CountConsistency |
                                           ValidationLevel.IncrementalCompleteness |
                                           ValidationLevel.RestoreDryRun,
                    _ => throw new ArgumentException(
                        $"Unknown validation level '{token}'. " +
                        $"Valid values: structural, count, incremental, dry-run, all " +
                        $"(or aliases l1–l4).")
                };
            }
        }
        return result == ValidationLevel.None
            ? ValidationLevel.StructuralIntegrity
            : result;
    }
}
