using System.CommandLine;
using Guardrails.Cli.Commands;

var rootCommand = new RootCommand("Guardrails — run a plan folder's task DAG to green.");
rootCommand.Add(ValidateCommand.Create());
rootCommand.Add(RunCommand.Create());
rootCommand.Add(PlanCommand.Create());
rootCommand.Add(StatusCommand.Create());
rootCommand.Add(ResetCommand.Create());
rootCommand.Add(LockCommand.Create());
rootCommand.Add(SkillsCommand.Create());
rootCommand.Add(SkillsCommand.CreateInstallAlias());

return await rootCommand.Parse(args).InvokeAsync();
