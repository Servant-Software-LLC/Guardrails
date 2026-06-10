using System.CommandLine;
using Guardrails.Core.Execution;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt;</c> — validate then execute a plan serially (M2),
/// printing line-by-line progress and a summary table. Exit codes per SSOT §7.
/// </summary>
public static class RunCommand
{
    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Path to the plan folder (contains guardrails.json)."
        };

        var command = new Command("run", "Run a plan folder to completion (serial, M2).");
        command.Add(folderArgument);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = parseResult.GetRequiredValue(folderArgument);
            return await RunAsync(folder, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(string folder, CancellationToken cancellationToken)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics);
            Console.WriteLine("\nValidation failed; nothing was run.");
            return ExitCodes.HarnessError;
        }

        var probeImpl = new PathExecutableProbe();
        var executor = new SerialExecutor(new ProcessRunner(), probeImpl, new ConsoleRunObserver());

        RunReport report;
        try
        {
            report = await executor.RunAsync(probe.Plan, cancellationToken).ConfigureAwait(false);
        }
        catch (PromptNotSupportedException ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return ExitCodes.HarnessError;
        }

        PrintSummary(report);
        return report.AllSucceeded ? ExitCodes.Success : ExitCodes.TaskFailed;
    }

    private static void PrintSummary(RunReport report)
    {
        Console.WriteLine("Summary");
        Console.WriteLine("-------");
        foreach (TaskResult result in report.Tasks)
        {
            string status = result.Outcome switch
            {
                TaskOutcome.Succeeded => "OK",
                TaskOutcome.ActionFailed => "ACTION FAILED",
                TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
                TaskOutcome.Blocked => "BLOCKED",
                _ => result.Outcome.ToString()
            };

            Console.WriteLine($"  {status,-16} {result.TaskId,-32} {result.Summary}");
        }

        int succeeded = report.Tasks.Count(t => t.Succeeded);
        Console.WriteLine();
        Console.WriteLine($"{succeeded}/{report.Tasks.Count} task(s) succeeded.");
    }
}
