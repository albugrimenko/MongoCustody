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

            var result = await svc.RunFullBackupAsync(
                new BackupRequest
                {
                    Databases     = [database],
                    TriggeredBy   = triggeredBy,
                    CorrelationId = Guid.NewGuid().ToString()
                }, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    backupId      = result.BackupId,
                    status        = result.Status.ToString(),
                    startedAt     = result.StartedAtUtc,
                    completedAt   = result.CompletedAtUtc,
                    durationMs    = (result.CompletedAtUtc - result.StartedAtUtc).TotalMilliseconds,
                    failureReason = result.FailureReason,
                    databases     = result.DatabaseResults.Select(db => new
                    {
                        database        = db.DatabaseName,
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
                Output.WriteSeparator();
                Console.WriteLine($"Backup ID  : {result.BackupId}");
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
}
