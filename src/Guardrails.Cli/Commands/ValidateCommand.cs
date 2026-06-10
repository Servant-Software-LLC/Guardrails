using System.CommandLine;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails validate &lt;folder&gt;</c> — load + validate a plan folder, print
/// diagnostics, and exit 0 (clean) or 1 (errors).
/// </summary>
public static class ValidateCommand
{
    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Path to the plan folder (contains guardrails.json)."
        };

        var command = new Command("validate", "Validate a plan folder without running it.");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = parseResult.GetRequiredValue(folderArgument);
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
