using System.CommandLine;
using Guardrails.Cli;

RootCommand rootCommand = CommandFactory.BuildRootCommand(SystemConsoleIo.Instance);

return await rootCommand.Parse(args).InvokeAsync();
