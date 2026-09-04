using System.CommandLine;
using Guardrails.Cli;

RootCommand rootCommand = CommandFactory.BuildRootCommand(SystemConsoleIo.Instance);

// The configuration is NOT optional here (#603). Omitting it does not mean "no process-termination
// handling" — it means System.CommandLine's 2-second default, under which the log server's own 5-second
// shutdown drain cannot finish. See CliInvocation for the value and what it costs.
return await rootCommand.Parse(args).InvokeAsync(CliInvocation.Create());
