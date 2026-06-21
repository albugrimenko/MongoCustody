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
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath      = parseResult.GetValue(configOpt)!;
            var database        = parseResult.GetValue(databaseOpt)!;
            var targetDate      = parseResult.GetValue(targetDateOpt);
            var targetConn      = parseResult.GetValue(targetConnOpt)!;
            var targetDatabase  = parseResult.GetValue(targetDatabaseOpt)!;
            var requestedBy     = parseResult.GetValue(requestedByOpt)!;
            var approvedBy  = parseResult.GetValue(approvedByOpt)!;
            var reason      = parseResult.GetValue(reasonOpt)!;
            var collections = parseResult.GetValue(collectionsOpt) ?? [];
            var format      = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine(
                $"Starting restore of '{database}' → '{targetDatabase}' as of {targetDate:u}…");

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
                }, ct);

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
                            $"    {icon}  {c.CollectionName,-30} {c.DocumentsUpserted,10:N0} docs upserted");
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
}
