using System.CommandLine;
using MongoCustody.Core.Abstractions;

namespace MongoCustody.Cli.Commands;

internal static class HealthCommand
{
    internal static Command Build()
    {
        var configOpt = CommonOptions.Config();
        var outputOpt = CommonOptions.Output();

        var cmd = new Command("health",
            "Check connectivity to MongoDB, Blob Storage, and the metadata repository.");
        cmd.Add(configOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configOpt)!;
            var format     = parseResult.GetValue(outputOpt);

            IMongoCustodyService svc;
            try { svc = ServiceFactory.Build(configPath); }
            catch (InvalidOperationException ex) { return Output.ConfigError(ex.Message); }

            var result = await svc.CheckHealthAsync(ct);

            if (format == OutputFormat.Json)
            {
                Output.WriteJson(new
                {
                    isHealthy  = result.IsHealthy,
                    checkedAt  = result.CheckedAtUtc,
                    components = result.ComponentResults.Select(c => new
                    {
                        name      = c.ComponentName,
                        isHealthy = c.IsHealthy,
                        latencyMs = c.LatencyMs,
                        error     = c.ErrorMessage
                    })
                });
            }
            else
            {
                Console.WriteLine($"Health check  {result.CheckedAtUtc:u}");
                Output.WriteSeparator();
                foreach (var c in result.ComponentResults)
                {
                    var icon    = c.IsHealthy ? "✓" : "✗";
                    var detail  = c.IsHealthy ? $"{c.LatencyMs} ms" : c.ErrorMessage ?? "unavailable";
                    Console.WriteLine($"  {icon}  {c.ComponentName,-25} {detail}");
                }
                Output.WriteSeparator();
                Console.WriteLine(result.IsHealthy ? "Overall: HEALTHY" : "Overall: UNHEALTHY");
            }

            return result.IsHealthy ? 0 : 1;
        });

        return cmd;
    }
}
