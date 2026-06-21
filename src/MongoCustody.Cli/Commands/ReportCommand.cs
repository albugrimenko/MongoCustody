using System.CommandLine;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli.Commands;

internal static class ReportCommand
{
    internal static Command Build()
    {
        var cmd = new Command(
            "report",
            "Generate a Markdown audit report for a date range or a single restore operation.");

        cmd.Add(BuildGenerate());
        return cmd;
    }

    private static Command BuildGenerate()
    {
        var configOpt = CommonOptions.Config();

        var restoreIdOpt = new Option<string?>("--restore-id")
        {
            Description = "Restore ID for a single-restore audit report."
        };

        var fromOpt = new Option<DateTimeOffset?>("--from")
        {
            Description = "Start of the date range, inclusive (ISO 8601). Required for range mode."
        };

        var toOpt = new Option<DateTimeOffset?>("--to")
        {
            Description = "End of the date range, inclusive (ISO 8601). Required for range mode."
        };

        var databaseOpt = new Option<string?>("--database", ["-d"])
        {
            Description = "Optional database name filter for range mode."
        };

        var outputFileOpt = new Option<string?>("--output-file", ["-o"])
        {
            Description = "Path for the output Markdown file. Defaults to an auto-generated filename."
        };

        var generatedByOpt = new Option<string?>("--generated-by")
        {
            Description = "Identity of the report requester (recorded in the report header)."
        };

        var generateCmd = new Command("generate", "Generate the audit report.")
        {
            configOpt, restoreIdOpt, fromOpt, toOpt,
            databaseOpt, outputFileOpt, generatedByOpt,
        };

        generateCmd.SetAction(async (parseResult, ct) =>
        {
            var configPath  = parseResult.GetValue(configOpt);
            var restoreId   = parseResult.GetValue(restoreIdOpt);
            var from        = parseResult.GetValue(fromOpt);
            var to          = parseResult.GetValue(toOpt);
            var database    = parseResult.GetValue(databaseOpt);
            var outputFile  = parseResult.GetValue(outputFileOpt);
            var generatedBy = parseResult.GetValue(generatedByOpt)
                              ?? Environment.MachineName;

            // Validate mode
            bool isSingleMode = !string.IsNullOrWhiteSpace(restoreId);
            bool isRangeMode  = from.HasValue && to.HasValue;

            if (!isSingleMode && !isRangeMode)
            {
                Console.Error.WriteLine(
                    "Error: specify either --restore-id (single mode) or " +
                    "both --from and --to (range mode).");
                return 1;
            }

            if (isSingleMode && isRangeMode)
            {
                Console.Error.WriteLine(
                    "Error: --restore-id and --from/--to are mutually exclusive.");
                return 1;
            }

            if (isRangeMode && from > to)
            {
                Console.Error.WriteLine("Error: --from must not be later than --to.");
                return 1;
            }

            var svc = ServiceFactory.Build(configPath ?? string.Empty);

            var request = new AuditReportRequest
            {
                RestoreId   = restoreId,
                From        = from,
                To          = to,
                DatabaseName = database,
                GeneratedBy = generatedBy,
            };

            AuditReport report;
            try
            {
                report = await svc.GenerateAuditReportAsync(request, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }

            // Determine output filename
            if (string.IsNullOrWhiteSpace(outputFile))
            {
                outputFile = isSingleMode
                    ? $"mongocustody-restore-{restoreId![..Math.Min(8, restoreId!.Length)]}.md"
                    : $"mongocustody-audit-{from!.Value:yyyy-MM-dd}_{to!.Value:yyyy-MM-dd}.md";
            }

            var markdown = MarkdownReportRenderer.Render(report);
            await File.WriteAllTextAsync(outputFile, markdown, ct).ConfigureAwait(false);

            // Print brief business summary to stdout
            PrintSummary(report, outputFile);
            return 0;
        });

        return generateCmd;
    }

    private static void PrintSummary(AuditReport report, string outputFile)
    {
        Console.WriteLine();
        if (report.IsSingleRestoreMode)
        {
            var rec = report.Restores[0].Record;
            Console.WriteLine($"Restore Audit Report");
            Console.WriteLine($"  Restore ID : {rec.RestoreId}");
            Console.WriteLine($"  Database   : {rec.DatabaseName} → {rec.TargetDatabaseName}");
            Console.WriteLine($"  Status     : {rec.Status}");
            Console.WriteLine($"  Requested  : {rec.RequestedAtUtc:yyyy-MM-dd HH:mm UTC}");
            Console.WriteLine($"  Chain steps: {report.Restores[0].ChainSteps.Count}");
        }
        else
        {
            var s = report.Summary;
            Console.WriteLine($"Audit Report — {report.From:yyyy-MM-dd} to {report.To:yyyy-MM-dd}");
            if (report.DatabaseFilter is not null)
                Console.WriteLine($"  Database filter  : {report.DatabaseFilter}");
            Console.WriteLine($"  Full backups     : {s.BackupsCompleted} completed, {s.BackupsFailed} failed");
            Console.WriteLine($"  Restore ops      : {s.RestoresCompleted} completed, {s.RestoresFailed} failed");
            Console.WriteLine($"  Documents backed : {s.TotalDocumentsBacked:N0}");
        }

        Console.WriteLine();
        Console.WriteLine($"Full report written to: {Path.GetFullPath(outputFile)}");
        Console.WriteLine();
    }
}
