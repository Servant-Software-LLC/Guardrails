using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails validate [folder]</c> — load + validate a plan folder, print
/// diagnostics, and exit 0 (clean) or 1 (errors). Defaults to the current directory.
/// </summary>
public static class ValidateCommand
{
    public static Command Create()
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("validate", "Validate a plan folder without running it.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument));
            return Run(folder);
        });

        return command;
    }

    private static int Run(string folder)
    {
        PlanProbe.Result result = PlanProbe.LoadAndValidate(folder);
        PlanProbe.PrintDiagnostics(result.Diagnostics);

        if (result.HasErrors)
        {
            int errorCount = result.Diagnostics.Count(d => d.Severity == Core.Loading.DiagnosticSeverity.Error);
            Console.WriteLine($"\nFAILED: {errorCount} error(s).");
            return ExitCodes.HarnessError;
        }

        Console.WriteLine("OK: plan is valid.");
        return ExitCodes.Success;
    }
}
