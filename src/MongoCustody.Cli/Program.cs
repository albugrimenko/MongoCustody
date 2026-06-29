using System.CommandLine;
using MongoCustody.Cli.Commands;

var root = new RootCommand("mongocustody — MongoDB backup and restore CLI.");

root.Add(HealthCommand.Build());
root.Add(BackupCommand.Build());
root.Add(RestoreCommand.Build());
root.Add(BackupsCommand.Build());
root.Add(ValidateCommand.Build());
root.Add(ReportCommand.Build());
root.Add(IndexCommand.Build());

return await root.Parse(args).InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
