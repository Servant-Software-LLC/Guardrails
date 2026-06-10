using System.CommandLine;
using Guardrails.Cli.Commands;

var rootCommand = new RootCommand("Guardrails — run a plan folder's task DAG to green.");
rootCommand.Add(ValidateCommand.Create());
rootCommand.Add(RunCommand.Create());

return await rootCommand.Parse(args).InvokeAsync();
