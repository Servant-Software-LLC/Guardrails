using System.CommandLine;
using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt; [--fresh]</c> — validate then execute a plan serially,
/// resuming from the journal by default. <c>--fresh</c> wipes runtime state first (SSOT
/// §6.1). Exit codes per SSOT §7.
/// </summary>
public static class RunCommand
{
    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Path to the plan folder (contains guardrails.json)."
        };

        var freshOption = new Option<bool>("--fresh")
        {
            Description = "Delete runtime state (run.json, state.json, logs) and re-seed before running."
        };

        var command = new Command("run", "Run a plan folder to completion (serial; resume-aware).");
        command.Add(folderArgument);
        command.Add(freshOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = parseResult.GetRequiredValue(folderArgument);
            bool fresh = parseResult.GetValue(freshOption);
            return await RunAsync(folder, fresh, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(string folder, bool fresh, CancellationToken cancellationToken)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics);
            Console.WriteLine("\nValidation failed; nothing was run.");
            return ExitCodes.HarnessError;
        }

        if (fresh)
        {
            RunReset.Fresh(probe.Plan.PlanDirectory);
            Console.WriteLine("Fresh run: runtime state cleared and re-seeded.\n");
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
            Console.WriteLine($"  {StatusLabel(result.Outcome),-16} {result.TaskId,-32} {result.Summary}");
        }

        int succeeded = report.Tasks.Count(t => t.Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped);
        Console.WriteLine();
        Console.WriteLine($"{succeeded}/{report.Tasks.Count} task(s) green (succeeded or skipped).");
    }

    internal static string StatusLabel(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "OK",
        TaskOutcome.Skipped => "SKIPPED",
        TaskOutcome.ActionFailed => "ACTION FAILED",
        TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
        TaskOutcome.InvalidFragment => "INVALID FRAGMENT",
        TaskOutcome.Blocked => "BLOCKED",
        _ => outcome.ToString()
    };
}
