using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails validate [folder]</c> — load + validate a plan folder, print
/// diagnostics, and exit 0 (clean) or 1 (errors). Defaults to the current directory.
/// </summary>
public static class ValidateCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("validate", "Validate a plan folder without running it.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return Run(folder, io);
        });

        return command;
    }

    private static int Run(string folder, IConsoleIo io)
    {
        PlanProbe.Result result = PlanProbe.LoadAndValidate(folder);
        PlanProbe.PrintDiagnostics(result.Diagnostics, io.Out);

        if (result.HasErrors)
        {
            int errorCount = result.Diagnostics.Count(d => d.Severity == Core.Loading.DiagnosticSeverity.Error);
            io.Out.WriteLine($"\nFAILED: {errorCount} error(s).");
            return ExitCodes.HarnessError;
        }

        io.Out.WriteLine("OK: plan is valid.");
        return ExitCodes.Success;
    }
}
