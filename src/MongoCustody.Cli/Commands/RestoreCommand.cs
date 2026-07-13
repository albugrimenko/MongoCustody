using System.CommandLine;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli.Commands;

internal static class RestoreCommand
{
    internal static Command Build()
    {
        var configOpt     = CommonOptions.Config();
        var databaseOpt   = CommonOptions.Database();
        var outputOpt     = CommonOptions.Output();

        var targetDateOpt = new Option<DateTimeOffset>("--target-date", ["--to"])
        {
            Description = "Point-in-time to restore to (ISO 8601, e.g. 2025-04-15T00:00:00Z).",
            Required    = true
        };

        var targetConnOpt = new Option<string>("--target-connection-string", ["--target"])
        {
            Description = "MongoDB connection string for the target MongoDB instance.",
            Required    = true
        };

        var targetDatabaseOpt = new Option<string>("--target-database", ["--target-db"])
        {
            Description =
                "Name of the database in the target instance to restore into. " +
                "The database must exist and contain no collections.",
            Required = true
        };

        var requestedByOpt = new Option<string>("--requested-by")
        {
            Description       = "Principal requesting the restore (written to the audit record).",
            DefaultValueFactory = _ => Environment.UserName
        };

        var approvedByOpt = new Option<string>("--approved-by")
        {
            Description       = "Principal who approved the restore (written to the audit record).",
            DefaultValueFactory = _ => Environment.UserName
        };

        var reasonOpt = new Option<string>("--reason")
        {
            Description       = "Business reason for the restore (written to the audit record).",
            DefaultValueFactory = _ => "CLI-initiated restore"
        };

        var collectionsOpt = new Option<string[]>("--collections")
        {
            Description                  = "Restore only the named collections (repeatable). Omit to restore all.",
            AllowMultipleArgumentsPerToken = true
        };

        var autoConfirmOpt = new Option<bool>("--auto-confirm", ["-y"])
        {
            Description =
                "Skip the chain preview confirmation prompt. " +
                "Required for unattended / batch use. " +
                "Always aborts if no eligible backup exists."
        };

        var cmd = new Command("restore",
            "Restore a database to a point in time by applying the resolved backup chain.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(targetDateOpt);
        cmd.Add(targetConnOpt);
        cmd.Add(targetDatabaseOpt);
        cmd.Add(requestedByOpt);
        cmd.Add(approvedByOpt);
        cmd.Add(reasonOpt);
        cmd.Add(collectionsOpt);
        cmd.Add(autoConfirmOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath     = parseResult.GetValue(configOpt)!;
            var database       = parseResult.GetValue(databaseOpt)!;
            var targetDate     = parseResult.GetValue(targetDateOpt);
            var targetConn     = parseResult.GetValue(targetConnOpt)!;
            var targetDatabase = parseResult.GetValue(targetDatabaseOpt)!;
            var requestedBy    = parseResult.GetValue(requestedByOpt)!;
            var approvedBy     = parseResult.GetValue(approvedByOpt)!;
            var reason         = parseResult.GetValue(reasonOpt)!;
            var collections    = parseResult.GetValue(collectionsOpt) ?? [];
            var autoConfirm    = parseResult.GetValue(autoConfirmOpt);
            var format         = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            // --- Preview the chain before writing anything ---
            RestoreChainPreview preview;
            try
            {
                preview = await svc.PreviewRestoreChainAsync(database, targetDate, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }

            if (preview.Chain.Count == 0)
            {
                Console.Error.WriteLine($"Error: {preview.GapWarning}");
                return 1;
            }

            // Print the resolved chain so the operator knows exactly what will be applied.
            PrintChainPreview(preview, format);

            // Warn and require confirmation when the gap exceeds 1 day.
            if (preview.GapWarning is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: {preview.GapWarning}");
                Console.WriteLine();

                if (!autoConfirm)
                {
                    Console.Write("Proceed with restore? [y/N] ");
                    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (answer is not "y" and not "yes")
                    {
                        Console.WriteLine("Restore cancelled.");
                        return 0;
                    }
                }
                else
                {
                    Console.WriteLine("--auto-confirm set; proceeding despite gap.");
                }
            }
            else if (!autoConfirm)
            {
                Console.Write("Proceed with restore? [y/N] ");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (answer is not "y" and not "yes")
                {
                    Console.WriteLine("Restore cancelled.");
                    return 0;
                }
            }

            if (!await svc.IsSecondaryIndexPopulatedAsync(ct))
                Console.Error.WriteLine(
                    "WARNING: Secondary metadata index is missing. " +
                    "This operation will be slower. Run 'mongocustody index rebuild' to fix.");

            // --- Execute the restore ---
            Console.Error.WriteLine(
                $"Starting restore of '{database}' → '{targetDatabase}' " +
                $"as of {preview.BestAvailableDate:u}…");

            IProgress<OperationProgress>? progress = null;
            using var consoleProgress = format == OutputFormat.Text
                ? new ConsoleProgress($"Restoring '{database}' → '{targetDatabase}'")
                : null;
            if (consoleProgress is not null)
                progress = consoleProgress.AsProgress();

            var result = await svc.RestoreAsync(
                new RestoreRequest
                {
                    DatabaseName           = database,
                    TargetConnectionString = targetConn,
                    TargetDatabaseName     = targetDatabase,
                    TargetDate             = targetDate,
                    Collections            = [.. collections],
                    RequestedBy            = requestedBy,
                    ApprovedBy             = approvedBy,
                    Reason                 = reason,
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
                Console.WriteLine();
                Output.WriteSeparator();
                Console.WriteLine($"Status     : {result.Status}");
                Console.WriteLine($"Started    : {result.StartedAtUtc:u}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");
                Console.WriteLine($"Duration   : {(result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds:F1}s");
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

    internal static void PrintChainPreview(RestoreChainPreview preview, OutputFormat format)
    {
        if (format == OutputFormat.Json) return; // JSON output handled by caller if needed

        Console.WriteLine();
        Output.WriteSeparator();
        Console.WriteLine($"Restore chain for '{preview.DatabaseName}'");
        Console.WriteLine($"  Requested : {preview.RequestedDate:u}");
        Console.WriteLine($"  Best point: {preview.BestAvailableDate:u}");
        Console.WriteLine();
        Console.WriteLine($"  {"#",-3}  {"Type",-12}  {"Completed",-22}  Folder");
        Console.WriteLine($"  {"---",-3}  {"------------",-12}  {"----------------------",-22}  ------");

        for (int i = 0; i < preview.Chain.Count; i++)
        {
            var r = preview.Chain[i];
            Console.WriteLine(
                $"  {i + 1,-3}  {r.BackupType,-12}  {r.CompletedAtUtc:u,-22}  {r.BackupFolder}");
        }
        Output.WriteSeparator();
    }
}
