using System.CommandLine;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli.Commands;

internal static class ValidateCommand
{
    internal static Command Build()
    {
        var cmd = new Command("validate", "Validation commands for backups and restore chains.");
        cmd.Add(BuildBackupSubcommand());
        cmd.Add(BuildChainSubcommand());
        cmd.Add(BuildDryRunSubcommand());
        return cmd;
    }

    // validate backup --backup-id <id> [--level ...]
    private static Command BuildBackupSubcommand()
    {
        var configOpt   = CommonOptions.Config();
        var outputOpt   = CommonOptions.Output();

        var backupIdOpt = new Option<string>("--backup-id", ["-b"])
        {
            Description = "ID of the backup to validate.",
            Required    = true
        };

        var levelOpt = new Option<string[]>("--level", ["-l"])
        {
            Description =
                "Validation level(s): structural (L1), count (L2), incremental (L3), all. " +
                "Defaults to structural.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => ["structural"]
        };

        var cmd = new Command("backup", "Validate an existing backup at one or more verification levels.");
        cmd.Add(configOpt);
        cmd.Add(backupIdOpt);
        cmd.Add(levelOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath   = parseResult.GetValue(configOpt)!;
            var backupId     = parseResult.GetValue(backupIdOpt)!;
            var levelStrings = parseResult.GetValue(levelOpt) ?? ["structural"];
            var format       = parseResult.GetValue(outputOpt);

            ValidationLevel levels;
            try { levels = ParseLevels(levelStrings); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine($"Validating backup '{backupId}' at level(s): {levels}…");

            var result = await svc.ValidateBackupAsync(
                new ValidationRequest
                {
                    BackupId = backupId,
                    Levels   = levels
                }, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    backupId       = result.BackupId,
                    passed         = result.Passed,
                    levelsExecuted = result.LevelsExecuted.ToString(),
                    completedAt    = result.CompletedAtUtc,
                    collections    = result.CollectionResults.Select(c => new
                    {
                        collection        = c.CollectionName,
                        passed            = c.Passed,
                        failedLevels      = c.FailedLevels == ValidationLevel.None ? null : c.FailedLevels.ToString(),
                        failureDetail     = c.FailureDetail,
                        manifestDocuments = c.ManifestDocumentCount,
                        liveDocuments     = c.LiveDocumentCount
                    })
                });
            }
            else
            {
                Output.WriteSeparator();
                Console.WriteLine($"Backup ID  : {result.BackupId}");
                Console.WriteLine($"Result     : {(result.Passed ? "PASSED" : "FAILED")}");
                Console.WriteLine($"Levels     : {result.LevelsExecuted}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");

                foreach (var col in result.CollectionResults)
                {
                    var icon = col.Passed ? "✓" : "✗";
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

    // validate chain --database <db> [--date <iso>] [--validate] [--level ...]
    private static Command BuildChainSubcommand()
    {
        var configOpt   = CommonOptions.Config();
        var databaseOpt = CommonOptions.Database();
        var outputOpt   = CommonOptions.Output();

        var dateOpt = new Option<DateTimeOffset?>("--date")
        {
            Description =
                "Target restore point (ISO 8601). Defaults to now — shows the latest restorable point."
        };

        var validateOpt = new Option<bool>("--validate", ["-v"])
        {
            Description =
                "Run validation on every step in the resolved chain. " +
                "When omitted the command shows the chain preview only."
        };

        var levelOpt = new Option<string[]>("--level", ["-l"])
        {
            Description =
                "Validation level(s) when --validate is set: structural (L1), count (L2), " +
                "incremental (L3), all. Defaults to structural.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => ["structural"]
        };

        var cmd = new Command("chain",
            "Show the restore chain that would be applied for a given database and date. " +
            "Optionally validate every step with --validate. Read-only — no data is written.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(dateOpt);
        cmd.Add(validateOpt);
        cmd.Add(levelOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath   = parseResult.GetValue(configOpt)!;
            var database     = parseResult.GetValue(databaseOpt)!;
            var date         = parseResult.GetValue(dateOpt) ?? DateTimeOffset.UtcNow;
            var doValidate   = parseResult.GetValue(validateOpt);
            var levelStrings = parseResult.GetValue(levelOpt) ?? ["structural"];
            var format       = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            // ---- Preview-only path ----
            if (!doValidate)
            {
                RestoreChainPreview preview;
                try { preview = await svc.PreviewRestoreChainAsync(database, date, ct); }
                catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }

                if (format == OutputFormat.Json)
                {
                    Output.WriteJson(new
                    {
                        database          = preview.DatabaseName,
                        requestedDate     = preview.RequestedDate,
                        bestAvailableDate = preview.BestAvailableDate,
                        gapWarning        = preview.GapWarning,
                        chain             = preview.Chain.Select((r, i) => new
                        {
                            step        = i + 1,
                            type        = r.BackupType.ToString(),
                            completedAt = r.CompletedAtUtc,
                            folder      = r.BackupFolder,
                            backupId    = r.BackupId
                        })
                    });
                }
                else
                {
                    if (preview.Chain.Count == 0)
                    {
                        Console.WriteLine($"No backup found for '{database}' on or before {date:u}.");
                        return 1;
                    }

                    RestoreCommand.PrintChainPreview(preview, format);

                    if (preview.GapWarning is not null)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"WARNING: {preview.GapWarning}");
                    }
                }

                return preview.Chain.Count > 0 ? 0 : 1;
            }

            // ---- Validation path ----
            ValidationLevel levels;
            try { levels = ParseLevels(levelStrings); }
            catch (ArgumentException ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 2; }

            if (!await svc.IsSecondaryIndexPopulatedAsync(ct))
                Console.Error.WriteLine(
                    "WARNING: Secondary metadata index is missing. " +
                    "This operation will be slower. Run 'mongocustody index rebuild' to fix.");

            Console.Error.WriteLine(
                $"Validating restore chain for '{database}' as of {date:u} at level(s): {levels}…");

            ChainValidationResult chainResult;
            try { chainResult = await svc.ValidateRestoreChainAsync(database, date, levels, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    database      = chainResult.DatabaseName,
                    requestedDate = chainResult.RequestedDate,
                    passed        = chainResult.Passed,
                    error         = chainResult.Steps.Count == 0
                        ? $"No backup chain found for '{chainResult.DatabaseName}' on or before {chainResult.RequestedDate:u}."
                        : null,
                    steps         = chainResult.Steps.Select(s => new
                    {
                        step            = s.StepIndex,
                        type            = s.BackupType.ToString(),
                        backupId        = s.BackupId,
                        folder          = s.BackupFolder,
                        completedAt     = s.CompletedAtUtc,
                        passed          = s.Validation.Passed,
                        levelsExecuted  = s.Validation.LevelsExecuted.ToString(),
                        coherenceDetail = s.Validation.ManifestCoherenceDetail,
                        collections     = s.Validation.CollectionResults.Select(c => new
                        {
                            collection        = c.CollectionName,
                            passed            = c.Passed,
                            failedLevels      = c.FailedLevels == ValidationLevel.None ? null : c.FailedLevels.ToString(),
                            failureDetail     = c.FailureDetail,
                            manifestDocuments = c.ManifestDocumentCount,
                            liveDocuments     = c.LiveDocumentCount
                        })
                    })
                });
            }
            else
            {
                if (chainResult.Steps.Count == 0)
                {
                    Console.WriteLine($"No backup chain found for '{database}' on or before {date:u}.");
                    return 1;
                }

                Output.WriteSeparator();
                Console.WriteLine($"Database   : {chainResult.DatabaseName}");
                Console.WriteLine($"Date       : {chainResult.RequestedDate:u}");
                Console.WriteLine($"Result     : {(chainResult.Passed ? "PASSED" : "FAILED")}");
                Console.WriteLine($"Steps      : {chainResult.Steps.Count}");
                Console.WriteLine();

                foreach (var step in chainResult.Steps)
                {
                    var stepIcon = step.Validation.Passed ? "✓" : "✗";
                    Console.WriteLine(
                        $"  {stepIcon} Step {step.StepIndex}: [{step.BackupType,-12}] " +
                        $"{step.BackupId}  ({step.CompletedAtUtc:u})");

                    if (step.Validation.ManifestCoherenceDetail is not null)
                    {
                        Console.WriteLine(
                            $"       COHERENCE FAILURE: {step.Validation.ManifestCoherenceDetail}");
                    }

                    foreach (var col in step.Validation.CollectionResults)
                    {
                        var colIcon = col.Passed ? "✓" : "✗";
                        Console.WriteLine(
                            $"       {colIcon}  {col.CollectionName,-40} " +
                            $"manifest: {col.ManifestDocumentCount,10:N0} docs" +
                            (col.LiveDocumentCount.HasValue
                                ? $"  live: {col.LiveDocumentCount.Value,10:N0} docs"
                                : string.Empty));
                        if (col.FailureDetail is not null)
                            Console.WriteLine($"            {col.FailureDetail}");
                    }
                }
                Output.WriteSeparator();
            }

            return chainResult.Passed ? 0 : 1;
        });

        return cmd;
    }

    // validate dry-run --database <db> [--date <iso>] [--target ...] [--target-db ...] [--auto-confirm]
    private static Command BuildDryRunSubcommand()
    {
        var configOpt   = CommonOptions.Config();
        var databaseOpt = CommonOptions.Database();
        var outputOpt   = CommonOptions.Output();

        var dateOpt = new Option<DateTimeOffset?>("--date")
        {
            Description = "Target restore point (ISO 8601). Defaults to now."
        };

        var targetConnOpt = new Option<string?>("--target-connection-string", ["--target"])
        {
            Description =
                "MongoDB connection string for the dry-run target. " +
                "Falls back to dryRun.connectionString in the config file."
        };

        var targetDbOpt = new Option<string?>("--target-database", ["--target-db"])
        {
            Description =
                "Name of the pre-provisioned empty database to restore into. " +
                "Falls back to dryRun.databaseName in the config file."
        };

        var requestedByOpt = new Option<string>("--requested-by")
        {
            Description       = "Principal requesting the dry-run (written to the audit record).",
            DefaultValueFactory = _ => Environment.UserName
        };

        var autoConfirmOpt = new Option<bool>("--auto-confirm", ["-y"])
        {
            Description = "Skip the chain preview confirmation prompt. Required for unattended use."
        };

        var cmd = new Command("dry-run",
            "Restore a database to a temporary target, verify document counts, then clean up. " +
            "Results are recorded as a RestoreRecord with IsDryRun=true and appear in audit reports.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(dateOpt);
        cmd.Add(targetConnOpt);
        cmd.Add(targetDbOpt);
        cmd.Add(requestedByOpt);
        cmd.Add(autoConfirmOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath  = parseResult.GetValue(configOpt)!;
            var database    = parseResult.GetValue(databaseOpt)!;
            var date        = parseResult.GetValue(dateOpt) ?? DateTimeOffset.UtcNow;
            var targetConn  = parseResult.GetValue(targetConnOpt);
            var targetDb    = parseResult.GetValue(targetDbOpt);
            var requestedBy = parseResult.GetValue(requestedByOpt)!;
            var autoConfirm = parseResult.GetValue(autoConfirmOpt);
            var format      = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            MongoCustody.Core.Models.MongoCustodyConfiguration config;
            try
            {
                svc    = ServiceFactory.Build(configPath);
                config = ServiceFactory.LoadConfig(configPath);
            }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            // Resolve target connection string — CLI arg takes precedence.
            // Config supplies a key name that is resolved through the secret provider.
            if (targetConn is null && config.DryRun?.ConnectionStringKey is { Length: > 0 } key)
                targetConn = await ServiceFactory.ResolveSecretAsync(configPath, key, ct);
            targetDb ??= config.DryRun?.DatabaseName;

            if (string.IsNullOrWhiteSpace(targetConn))
            {
                Console.Error.WriteLine(
                    "Error: --target-connection-string is required (or set dryRun.connectionString in config).");
                return 2;
            }
            if (string.IsNullOrWhiteSpace(targetDb))
            {
                Console.Error.WriteLine(
                    "Error: --target-database is required (or set dryRun.databaseName in config).");
                return 2;
            }

            // Preview the chain before proceeding.
            RestoreChainPreview preview;
            try { preview = await svc.PreviewRestoreChainAsync(database, date, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }

            if (preview.Chain.Count == 0)
            {
                Console.Error.WriteLine($"Error: {preview.GapWarning}");
                return 1;
            }

            RestoreCommand.PrintChainPreview(preview, format);

            if (preview.GapWarning is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: {preview.GapWarning}");
                Console.WriteLine();
            }

            if (!autoConfirm)
            {
                Console.Write("Proceed with dry-run? [y/N] ");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is not "y" and not "yes")
                {
                    Console.WriteLine("Dry-run cancelled.");
                    return 0;
                }
            }

            Console.Error.WriteLine(
                $"Starting dry-run for '{database}' → '{targetDb}' as of {preview.BestAvailableDate:u}…");

            IProgress<MongoCustody.Core.Models.OperationProgress>? progress = null;
            using var consoleProgress = format == OutputFormat.Text
                ? new ConsoleProgress($"Dry-run '{database}' → '{targetDb}'")
                : null;
            if (consoleProgress is not null)
                progress = consoleProgress.AsProgress();

            var result = await svc.RunDryRunAsync(
                new MongoCustody.Core.Models.RestoreRequest
                {
                    DatabaseName           = database,
                    TargetConnectionString = targetConn,
                    TargetDatabaseName     = targetDb,
                    TargetDate             = date,
                    RequestedBy            = requestedBy,
                    ApprovedBy             = requestedBy,
                    Reason                 = "Automated dry-run validation",
                    CorrelationId          = Guid.NewGuid().ToString()
                },
                progress, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    status        = result.Status.ToString(),
                    startedAt     = result.StartedAtUtc,
                    completedAt   = result.CompletedAtUtc,
                    durationMs    = (result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds,
                    restoreId     = result.RestoreId,
                    restoreChain  = result.RestoreChain,
                    failureReason = result.FailureReason,
                    collections   = result.CollectionResults.Select(c => new
                    {
                        collection    = c.CollectionName,
                        status        = c.Status.ToString(),
                        documents     = c.DocumentsUpserted,
                        failureReason = c.FailureReason
                    })
                });
            }
            else
            {
                Output.WriteSeparator();
                Console.WriteLine($"Status     : {result.Status}");
                Console.WriteLine($"Started    : {result.StartedAtUtc:u}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");
                Console.WriteLine($"Duration   : {(result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds:F1}s");
                Console.WriteLine($"Restore ID : {result.RestoreId}");
                if (result.FailureReason is not null)
                    Console.WriteLine($"Failure    : {result.FailureReason}");

                if (result.CollectionResults.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Collections:");
                    foreach (var c in result.CollectionResults)
                    {
                        var icon = c.Status == BackupStatus.Completed ? "✓" : "✗";
                        Console.WriteLine(
                            $"    {icon}  {c.CollectionName,-30} {c.DocumentsUpserted,10:N0} docs");
                        if (c.FailureReason is not null)
                            Console.WriteLine($"         {c.FailureReason}");
                    }
                }
                Output.WriteSeparator();
            }

            return result.Status == BackupStatus.Completed ? 0 : 1;
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
                    "structural"  or "l1" => ValidationLevel.StructuralIntegrity,
                    "count"       or "l2" => ValidationLevel.CountConsistency,
                    "incremental" or "l3" => ValidationLevel.IncrementalCompleteness,
                    "all"                 => ValidationLevel.StructuralIntegrity |
                                            ValidationLevel.CountConsistency |
                                            ValidationLevel.IncrementalCompleteness,
                    _ => throw new ArgumentException(
                        $"Unknown validation level '{token}'. " +
                        $"Valid values: structural, count, incremental, all (or l1–l3).")
                };
            }
        }
        return result == ValidationLevel.None
            ? ValidationLevel.StructuralIntegrity
            : result;
    }
}
