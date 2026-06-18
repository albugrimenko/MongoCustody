using System.CommandLine;
using MongoCustody.Core.Abstractions;

namespace MongoCustody.Cli.Commands;

internal static class BackupsCommand
{
    internal static Command Build()
    {
        var cmd = new Command("backups", "Query backup records.");
        cmd.Add(BuildList());
        return cmd;
    }

    private static Command BuildList()
    {
        var configOpt   = CommonOptions.Config();
        var databaseOpt = CommonOptions.Database();
        var outputOpt   = CommonOptions.Output();

        var fromOpt = new Option<DateTimeOffset>("--from")
        {
            Description = "Start of the date range, inclusive (ISO 8601, e.g. 2025-01-01T00:00:00Z).",
            Required    = true
        };

        var toOpt = new Option<DateTimeOffset>("--to")
        {
            Description = "End of the date range, inclusive (ISO 8601, e.g. 2025-03-31T23:59:59Z).",
            Required    = true
        };

        var cmd = new Command("list",
            "List all completed full backups for a database within a date range.");
        cmd.Add(configOpt);
        cmd.Add(databaseOpt);
        cmd.Add(fromOpt);
        cmd.Add(toOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configOpt)!;
            var database   = parseResult.GetValue(databaseOpt)!;
            var from       = parseResult.GetValue(fromOpt);
            var to         = parseResult.GetValue(toOpt);
            var format     = parseResult.GetValue(outputOpt);

            if (from > to)
                return Output.ConfigError("--from must be earlier than or equal to --to.");

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            var records = await svc.ListFullBackupsAsync(database, from, to, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    database   = database,
                    from,
                    to,
                    totalCount = records.Count,
                    backups    = records.Select(r => new
                    {
                        backupId    = r.BackupId,
                        completedAt = r.CompletedAtUtc,
                        folder      = r.BackupFolder,
                        quarter     = r.QuarterLabel,
                        collections = r.Collections.Select(c => new
                        {
                            name      = c.CollectionName,
                            documents = c.TotalDocuments,
                            chunks    = c.ChunkCount
                        })
                    })
                });
            }
            else
            {
                Console.WriteLine(
                    $"Full backups for '{database}'  {from:yyyy-MM-dd} → {to:yyyy-MM-dd}");
                Output.WriteSeparator();

                if (records.Count == 0)
                {
                    Console.WriteLine("  No full backups found in this date range.");
                }
                else
                {
                    foreach (var r in records)
                    {
                        Console.WriteLine($"  {r.BackupId}");
                        Console.WriteLine($"    Completed : {r.CompletedAtUtc:u}");
                        Console.WriteLine($"    Quarter   : {r.QuarterLabel}");
                        Console.WriteLine($"    Folder    : {r.BackupFolder}");
                        Console.WriteLine($"    Collections ({r.Collections.Count}):");
                        foreach (var c in r.Collections)
                            Console.WriteLine(
                                $"      • {c.CollectionName,-30} {c.TotalDocuments,10:N0} docs  {c.ChunkCount} chunk(s)");
                        Console.WriteLine();
                    }
                }

                Output.WriteSeparator();
                Console.WriteLine($"Total: {records.Count} backup(s)");
            }

            return 0;
        });

        return cmd;
    }
}
