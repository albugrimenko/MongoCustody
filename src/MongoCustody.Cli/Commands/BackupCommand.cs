using System.CommandLine;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli.Commands;

internal static class BackupCommand
{
    internal static Command Build()
    {
        var cmd = new Command("backup", "Backup operations.");
        cmd.Add(BuildFull());
        cmd.Add(BuildIncremental());
        return cmd;
    }

    private static Command BuildFull()
    {
        var configOpt      = CommonOptions.Config();
        var databaseOpt    = CommonOptions.Database();
        var outputOpt      = CommonOptions.Output();
        var triggeredByOpt = new Option<string>("--triggered-by")
        {
            Description       = "Label identifying the principal or scheduler that triggered the backup.",
            DefaultValueFactory = _ => Environment.UserName
        };

        var cmd = new Command("full",
            "Run a full backup of all collections in the specified database.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(triggeredByOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath  = parseResult.GetValue(configOpt)!;
            var database    = parseResult.GetValue(databaseOpt)!;
            var triggeredBy = parseResult.GetValue(triggeredByOpt)!;
            var format      = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine($"Starting full backup of '{database}'…");

            IProgress<MongoCustody.Core.Models.OperationProgress>? progress = null;
            using var consoleProgress = format == OutputFormat.Text
                ? new ConsoleProgress($"Backing up '{database}'")
                : null;
            if (consoleProgress is not null)
                progress = consoleProgress.AsProgress();

            var result = await svc.RunFullBackupAsync(
                new BackupRequest
                {
                    Databases     = [database],
                    TriggeredBy   = triggeredBy,
                    CorrelationId = Guid.NewGuid().ToString()
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
                    failureReason = result.FailureReason,
                    databases     = result.DatabaseResults.Select(db => new
                    {
                        database        = db.DatabaseName,
                        backupId        = db.BackupId,
                        status          = db.Status.ToString(),
                        totalDocuments  = db.TotalDocuments,
                        collectionCount = db.CollectionCount,
                        compressedBytes = db.TotalCompressedBytes,
                        failureReason   = db.FailureReason
                    })
                });
            }
            else
            {
                Console.WriteLine();
                Output.WriteSeparator();
                var printedId = result.DatabaseResults.Count == 1
                    ? result.DatabaseResults[0].BackupId
                    : result.BackupId;
                Console.WriteLine($"Backup ID  : {printedId}");
                Console.WriteLine($"Status     : {result.Status}");
                Console.WriteLine($"Started    : {result.StartedAtUtc:u}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");
                Console.WriteLine($"Duration   : {(result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds:F1}s");
                if (result.FailureReason is not null)
                    Console.WriteLine($"Failure    : {result.FailureReason}");

                foreach (var db in result.DatabaseResults)
                {
                    var icon = db.Status == BackupStatus.Completed ? "✓" : "✗";
                    Console.WriteLine();
                    Console.WriteLine(
                        $"  {icon}  {db.DatabaseName,-30} {db.TotalDocuments,10:N0} docs  " +
                        $"{db.CollectionCount} collection(s)  " +
                        $"{db.TotalCompressedBytes / 1024.0 / 1024.0:F1} MB compressed");
                    if (db.FailureReason is not null)
                        Console.WriteLine($"       {db.FailureReason}");
                }
                Output.WriteSeparator();
            }

            return result.Status == BackupStatus.Completed ? 0 : 1;
        });

        return cmd;
    }

    private static Command BuildIncremental()
    {
        var configOpt      = CommonOptions.Config();
        var databaseOpt    = CommonOptions.Database();
        var outputOpt      = CommonOptions.Output();
        var triggeredByOpt = new Option<string>("--triggered-by")
        {
            Description       = "Label identifying the principal or scheduler that triggered the backup.",
            DefaultValueFactory = _ => Environment.UserName
        };

        var cmd = new Command("incremental",
            "Run an incremental backup — exports only documents changed since the previous backup.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(triggeredByOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath  = parseResult.GetValue(configOpt)!;
            var database    = parseResult.GetValue(databaseOpt)!;
            var triggeredBy = parseResult.GetValue(triggeredByOpt)!;
            var format      = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            if (!await svc.IsSecondaryIndexPopulatedAsync(ct))
                Console.Error.WriteLine(
                    "WARNING: Secondary metadata index is missing. " +
                    "This operation will be slower. Run 'mongocustody index rebuild' to fix.");

            Console.Error.WriteLine($"Starting incremental backup of '{database}'…");

            IProgress<MongoCustody.Core.Models.OperationProgress>? progress = null;
            using var consoleProgress = format == OutputFormat.Text
                ? new ConsoleProgress($"Incremental backup '{database}'")
                : null;
            if (consoleProgress is not null)
                progress = consoleProgress.AsProgress();

            var result = await svc.RunIncrementalBackupAsync(
                new BackupRequest
                {
                    Databases     = [database],
                    TriggeredBy   = triggeredBy,
                    CorrelationId = Guid.NewGuid().ToString()
                },
                progress, ct);

            // Surface per-database chain-depth warnings even on success.
            foreach (var db in result.DatabaseResults)
            {
                if (db.Status == BackupStatus.Completed &&
                    db.FailureReason is not null)
                    Console.Error.WriteLine($"WARNING ({db.DatabaseName}): {db.FailureReason}");
            }

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    status        = result.Status.ToString(),
                    startedAt     = result.StartedAtUtc,
                    completedAt   = result.CompletedAtUtc,
                    durationMs    = (result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds,
                    failureReason = result.FailureReason,
                    databases     = result.DatabaseResults.Select(db => new
                    {
                        database         = db.DatabaseName,
                        backupId         = db.BackupId,
                        status           = db.Status.ToString(),
                        changedDocuments = db.TotalDocuments,
                        collectionCount  = db.CollectionCount,
                        compressedBytes  = db.TotalCompressedBytes,
                        warning          = db.Status == BackupStatus.Completed ? db.FailureReason : null,
                        failureReason    = db.Status == BackupStatus.Failed    ? db.FailureReason : null
                    })
                });
            }
            else
            {
                Console.WriteLine();
                Output.WriteSeparator();
                var printedId = result.DatabaseResults.Count == 1
                    ? result.DatabaseResults[0].BackupId
                    : result.BackupId;
                Console.WriteLine($"Backup ID  : {printedId}");
                Console.WriteLine($"Status     : {result.Status}");
                Console.WriteLine($"Started    : {result.StartedAtUtc:u}");
                Console.WriteLine($"Completed  : {result.CompletedAtUtc:u}");
                Console.WriteLine($"Duration   : {(result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds:F1}s");
                if (result.FailureReason is not null)
                    Console.WriteLine($"Failure    : {result.FailureReason}");

                foreach (var db in result.DatabaseResults)
                {
                    var icon = db.Status == BackupStatus.Completed ? "✓" : "✗";
                    Console.WriteLine();
                    Console.WriteLine(
                        $"  {icon}  {db.DatabaseName,-30} {db.TotalDocuments,10:N0} changed docs  " +
                        $"{db.CollectionCount} collection(s) with changes  " +
                        $"{db.TotalCompressedBytes / 1024.0 / 1024.0:F1} MB compressed");
                    if (db.Status == BackupStatus.Failed && db.FailureReason is not null)
                        Console.WriteLine($"       {db.FailureReason}");
                    else if (db.Status == BackupStatus.Completed && db.FailureReason is not null)
                        Console.WriteLine($"       WARNING: {db.FailureReason}");
                }
                Output.WriteSeparator();
            }

            return result.Status == BackupStatus.Completed ? 0 : 1;
        });

        return cmd;
    }
}
