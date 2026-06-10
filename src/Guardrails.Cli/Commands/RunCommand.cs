using System.CommandLine;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.State;
using Spectre.Console;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run &lt;folder&gt; [--fresh] [--no-ui]</c> — validate then execute the plan
/// DAG (parallel, retry-aware, resume-aware). <c>--fresh</c> wipes runtime state first
/// (SSOT §6.1). Live Spectre progress when interactive; plain lines otherwise. Exit codes
/// per SSOT §7: 0 green, 1 error, 2 needs-human/failed, 3 cancelled.
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

        var noUiOption = new Option<bool>("--no-ui")
        {
            Description = "Plain line-by-line output instead of the live progress table."
        };

        var command = new Command("run", "Run a plan folder's task DAG to green (parallel; resume-aware).");
        command.Add(folderArgument);
        command.Add(freshOption);
        command.Add(noUiOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = parseResult.GetRequiredValue(folderArgument);
            bool fresh = parseResult.GetValue(freshOption);
            bool noUi = parseResult.GetValue(noUiOption);
            return await RunAsync(folder, fresh, noUi, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(string folder, bool fresh, bool noUi, CancellationToken cancellationToken)
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

        bool live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

        RunReport report;
        try
        {
            if (live)
            {
                await using var observer = new LiveRunObserver(probe.Plan.Tasks);
                report = await ExecuteAsync(probe.Plan, observer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                report = await ExecuteAsync(probe.Plan, new ConsoleRunObserver(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PromptNotSupportedException ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return ExitCodes.HarnessError;
        }

        PrintSummary(report);

        if (report.Cancelled)
        {
            return ExitCodes.Cancelled;
        }

        return report.AllSucceeded ? ExitCodes.Success : ExitCodes.TaskFailed;
    }

    private static Task<RunReport> ExecuteAsync(
        Core.Model.PlanDefinition plan,
        IRunObserver observer,
        CancellationToken cancellationToken)
    {
        Scheduler scheduler = SchedulerFactory.Create(plan, new ProcessRunner(), new PathExecutableProbe(), observer);
        return scheduler.RunAsync(plan, cancellationToken);
    }

    private static void PrintSummary(RunReport report)
    {
        Console.WriteLine("Summary");
        Console.WriteLine("-------");
        foreach (TaskResult result in report.Tasks)
        {
            Console.WriteLine($"  {StatusLabel(result.Outcome),-16} {result.TaskId,-32} {result.Summary}");
        }

        int green = report.Tasks.Count(t => t.IsGreen);
        Console.WriteLine();
        Console.WriteLine(report.Cancelled
            ? $"Run CANCELLED — {green}/{report.Tasks.Count} task(s) green; in-flight tasks journaled pending. Re-run to resume."
            : $"{green}/{report.Tasks.Count} task(s) green (succeeded or skipped).");

        foreach (TaskResult needsHuman in report.Tasks.Where(t =>
                     t.Outcome is TaskOutcome.ActionFailed or TaskOutcome.GuardrailFailed or TaskOutcome.InvalidFragment))
        {
            Console.WriteLine();
            Console.WriteLine($"NEEDS HUMAN: {needsHuman.TaskId} — {needsHuman.Summary}");
            Console.WriteLine($"  Inspect state/logs/{needsHuman.TaskId}/ (latest attempt's feedback.md has the full failure detail),");
            Console.WriteLine("  fix the action or guardrails, then re-run to resume.");
        }
    }

    internal static string StatusLabel(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "OK",
        TaskOutcome.Skipped => "SKIPPED",
        TaskOutcome.ActionFailed => "ACTION FAILED",
        TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
        TaskOutcome.InvalidFragment => "INVALID FRAGMENT",
        TaskOutcome.Blocked => "BLOCKED",
        TaskOutcome.Cancelled => "CANCELLED",
        _ => outcome.ToString()
    };
}
