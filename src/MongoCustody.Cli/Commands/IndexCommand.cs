using System.CommandLine;
using MongoCustody.Core.Abstractions;

namespace MongoCustody.Cli.Commands;

internal static class IndexCommand
{
    internal static Command Build()
    {
        var root = new Command("index", "Manage the secondary metadata index.");
        root.Add(BuildValidate());
        root.Add(BuildRebuild());
        return root;
    }

    private static Command BuildValidate()
    {
        var configOpt = CommonOptions.Config();
        var outputOpt = CommonOptions.Output();

        var cmd = new Command("validate",
            "Check secondary index consistency without making any changes.");
        cmd.Add(configOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configOpt)!;
            var format     = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine("Validating secondary metadata index…");
            var result = await svc.ValidateIndexAsync(ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    isHealthy         = result.IsHealthy,
                    totalIndexEntries = result.TotalIndexEntries,
                    danglingEntries   = result.DanglingEntries,
                    unindexedManifests = result.UnindexedManifests,
                    issues            = result.Issues
                });
            }
            else
            {
                Output.WriteSeparator();
                Console.WriteLine($"Index entries : {result.TotalIndexEntries}");
                Console.WriteLine($"Dangling      : {result.DanglingEntries}");
                Console.WriteLine($"Unindexed     : {result.UnindexedManifests}");
                Console.WriteLine($"Status        : {(result.IsHealthy ? "Healthy" : "UNHEALTHY")}");
                if (result.Issues.Count > 0)
                {
                    Console.WriteLine();
                    foreach (var issue in result.Issues)
                        Console.WriteLine($"  ! {issue}");
                }
                Output.WriteSeparator();
                if (!result.IsHealthy)
                    Console.WriteLine("Run 'mongocustody index rebuild' to repair the index.");
            }

            return result.IsHealthy ? 0 : 1;
        });

        return cmd;
    }

    private static Command BuildRebuild()
    {
        var configOpt  = CommonOptions.Config();
        var outputOpt  = CommonOptions.Output();
        var dryRunOpt  = new Option<bool>("--dry-run",
            "Report what would change without writing anything.");

        var cmd = new Command("rebuild",
            "Rebuild the secondary index from manifests. " +
            "Adds missing entries and removes orphaned ones.");
        cmd.Add(configOpt);
        cmd.Add(outputOpt);
        cmd.Add(dryRunOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configOpt)!;
            var format     = parseResult.GetValue(outputOpt);
            var dryRun     = parseResult.GetValue(dryRunOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            Console.Error.WriteLine(dryRun
                ? "Dry-run: scanning index (no changes will be written)…"
                : "Rebuilding secondary metadata index…");

            var result = await svc.RebuildIndexAsync(dryRun, ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    wasDryRun     = result.WasDryRun,
                    entriesAdded  = result.EntriesAdded,
                    orphansRemoved = result.OrphansRemoved
                });
            }
            else
            {
                Output.WriteSeparator();
                Console.WriteLine(result.WasDryRun ? "Mode          : Dry-run" : "Mode          : Live");
                Console.WriteLine($"Entries added  : {result.EntriesAdded}");
                Console.WriteLine($"Orphans removed: {result.OrphansRemoved}");
                Output.WriteSeparator();
                if (result.WasDryRun && (result.EntriesAdded > 0 || result.OrphansRemoved > 0))
                    Console.WriteLine("Re-run without --dry-run to apply these changes.");
            }

            return 0;
        });

        return cmd;
    }
}
