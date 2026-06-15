using System.CommandLine;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.State;
using Spectre.Console;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run [folder] [--fresh] [--no-ui]</c> — validate then execute the plan
/// DAG (parallel, retry-aware, resume-aware). <c>--fresh</c> wipes runtime state first
/// (SSOT §6.1). Live Spectre progress when interactive; plain lines otherwise. Exit codes
/// per SSOT §7: 0 green, 1 error, 2 needs-human/failed, 3 cancelled. Defaults to the
/// current directory when the folder is omitted.
/// </summary>
public static class RunCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var freshOption = new Option<bool>("--fresh")
        {
            Description = "Delete runtime state (run.json, state.json, logs) and re-seed before running."
        };

        var noUiOption = new Option<bool>("--no-ui")
        {
            Description = "Plain line-by-line output instead of the live progress table."
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate and preview waves + per-task resolution + resume skips, then exit 0 without running or touching state."
        };

        var command = new Command("run", "Run a plan folder's task DAG to green (parallel; resume-aware).");
        command.Add(folderArgument);
        command.Add(freshOption);
        command.Add(noUiOption);
        command.Add(dryRunOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            bool fresh = parseResult.GetValue(freshOption);
            bool noUi = parseResult.GetValue(noUiOption);
            bool dryRun = parseResult.GetValue(dryRunOption);

            if (dryRun)
            {
                return DryRun.Execute(folder, io);
            }

            return await RunAsync(folder, fresh, noUi, io, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(string folder, bool fresh, bool noUi, IConsoleIo io, CancellationToken cancellationToken)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nValidation failed; nothing was run.");
            return ExitCodes.HarnessError;
        }

        if (fresh)
        {
            RunReset.Fresh(probe.Plan.PlanDirectory);
            io.Out.WriteLine("Fresh run: runtime state cleared and re-seeded.\n");
        }

        bool live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

        RunReport report;
        if (live)
        {
            await using var observer = new LiveRunObserver(probe.Plan.Tasks);
            report = await ExecuteAsync(probe.Plan, observer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            report = await ExecuteAsync(probe.Plan, new ConsoleRunObserver(io.Out), cancellationToken).ConfigureAwait(false);
        }

        PrintSummary(report, probe.Plan.PlanDirectory, io);

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

    private static void PrintSummary(RunReport report, string planDirectory, IConsoleIo io)
    {
        TextWriter output = io.Out;

        output.WriteLine("Summary");
        output.WriteLine("-------");
        foreach (TaskResult result in report.Tasks)
        {
            output.WriteLine($"  {StatusLabel(result.Outcome),-16} {result.TaskId,-32} {result.Summary}");
        }

        int green = report.Tasks.Count(t => t.IsGreen);
        output.WriteLine();
        output.WriteLine(report.Cancelled
            ? $"Run CANCELLED — {green}/{report.Tasks.Count} task(s) green; in-flight tasks journaled pending. Re-run to resume."
            : $"{green}/{report.Tasks.Count} task(s) green (succeeded or skipped).");

        PrintTotalCost(planDirectory, output);

        foreach (TaskResult needsHuman in report.Tasks.Where(t =>
                     t.Outcome is TaskOutcome.ActionFailed or TaskOutcome.GuardrailFailed
                         or TaskOutcome.InvalidFragment or TaskOutcome.NeedsHuman))
        {
            output.WriteLine();
            output.WriteLine($"NEEDS HUMAN: {needsHuman.TaskId} — {needsHuman.Summary}");
            output.WriteLine($"  Inspect state/logs/{needsHuman.TaskId}/ (latest attempt's feedback.md has the full failure detail),");
            output.WriteLine("  fix the action or guardrails, then re-run to resume.");
        }
    }

    /// <summary>
    /// Print the run-level cost line (SSOT §7 <c>costUsd</c>) from the freshly-persisted
    /// journal. Omitted when no attempt recorded a cost, so deterministic-only plans stay
    /// noise-free.
    /// </summary>
    private static void PrintTotalCost(string planDirectory, TextWriter output)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath))
        {
            return;
        }

        JournalDocument document = JournalReader.Read(journalPath);
        if (JournalCost.Total(document) is { } total)
        {
            output.WriteLine($"Total prompt cost: ${total:F4}");
        }
    }

    internal static string StatusLabel(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "OK",
        TaskOutcome.Skipped => "SKIPPED",
        TaskOutcome.ActionFailed => "ACTION FAILED",
        TaskOutcome.GuardrailFailed => "GUARDRAIL FAILED",
        TaskOutcome.InvalidFragment => "INVALID FRAGMENT",
        TaskOutcome.NeedsHuman => "NEEDS HUMAN",
        TaskOutcome.Blocked => "BLOCKED",
        TaskOutcome.Cancelled => "CANCELLED",
        _ => outcome.ToString()
    };
}
