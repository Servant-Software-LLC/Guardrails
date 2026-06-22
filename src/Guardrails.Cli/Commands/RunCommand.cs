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

        var noLogServerOption = new Option<bool>("--no-log-server")
        {
            Description = "Do not start the local log server / clickable per-task log links (for headless or CI use)."
        };

        var logPortOption = new Option<int>("--log-port")
        {
            Description = "Port for the local log server (default 0 = an automatically chosen free port). Bound to localhost only."
        };

        var mergeOnSuccessOption = new Option<bool>("--merge-on-success")
        {
            Description = "On a wholly-green run, merge the plan branch into your original branch at run end (SSOT §5.3). Forces mergeOnSuccess on regardless of guardrails.json."
        };

        var revalidateTaskOption = new Option<string?>("--revalidate-task")
        {
            Description = "Re-validate-only (issue #102): run ONLY this task's guardrails against the current workspace, spawning NO agent attempt — for confirming a hand-fix to a needs-human task. On pass the task is marked succeeded; serial mode only."
        };

        var command = new Command("run", "Run a plan folder's task DAG to green (parallel; resume-aware).");
        command.Add(folderArgument);
        command.Add(freshOption);
        command.Add(noUiOption);
        command.Add(dryRunOption);
        command.Add(noLogServerOption);
        command.Add(logPortOption);
        command.Add(mergeOnSuccessOption);
        command.Add(revalidateTaskOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            bool fresh = parseResult.GetValue(freshOption);
            bool noUi = parseResult.GetValue(noUiOption);
            bool dryRun = parseResult.GetValue(dryRunOption);
            bool noLogServer = parseResult.GetValue(noLogServerOption);
            int logPort = parseResult.GetValue(logPortOption);
            bool mergeOnSuccess = parseResult.GetValue(mergeOnSuccessOption);
            string? revalidateTask = parseResult.GetValue(revalidateTaskOption);

            // Re-validate-only (issue #102) is a single-task verification, not a run: it spawns no
            // agent attempt and ignores the run-shaped flags. Reject the combinations that would
            // otherwise silently no-op (or, for --fresh, destroy the very state being verified).
            if (!string.IsNullOrWhiteSpace(revalidateTask))
            {
                if (fresh || dryRun)
                {
                    io.Out.WriteLine("--revalidate-task cannot be combined with --fresh or --dry-run.");
                    return ExitCodes.HarnessError;
                }

                return await Revalidate.ExecuteAsync(folder, revalidateTask, io, cancellationToken).ConfigureAwait(false);
            }

            if (dryRun)
            {
                return DryRun.Execute(folder, io);
            }

            return await RunAsync(folder, fresh, noUi, noLogServer, logPort, mergeOnSuccess, io, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string folder, bool fresh, bool noUi, bool noLogServer, int logPort, bool mergeOnSuccess, IConsoleIo io, CancellationToken cancellationToken)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nValidation failed; nothing was run.");
            return ExitCodes.HarnessError;
        }

        // --merge-on-success forces end-of-run delivery on (SSOT §5.3 / §2: "CLI --merge-on-success
        // overrides"). The flag only augments — it can turn the config value on, never off — so a
        // plan that already set mergeOnSuccess: true is unaffected.
        if (mergeOnSuccess && !probe.Plan.Config.MergeOnSuccess)
        {
            probe = probe with { Plan = probe.Plan with { Config = probe.Plan.Config with { MergeOnSuccess = true } } };
        }

        if (fresh)
        {
            RunReset.Fresh(probe.Plan.PlanDirectory);
            io.Out.WriteLine("Fresh run: runtime state cleared and re-seeded.\n");
        }

        bool live = !noUi && AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

        // The log server is a companion to the live table: start it only in the interactive path
        // (nobody clicks links in CI / redirected output), and never let a binding failure abort
        // the run — TryStart returns null and prints one warning.
        LogServer? logServer = (live && !noLogServer)
            ? LogServer.TryStart(probe.Plan.PlanDirectory, probe.Plan.Tasks, logPort, io.Out)
            : null;

        try
        {
            if (logServer is not null)
            {
                io.Out.WriteLine($"Live task logs: {logServer.BaseUrl} (Ctrl/Cmd-click a task's \"view log\" link)\n");
            }

            Func<string, string?>? logUrlForTask = logServer is null ? null : logServer.UrlForTask;

            RunReport report;
            if (live)
            {
                await using var observer = new LiveRunObserver(probe.Plan.Tasks, logUrlForTask, probe.Plan.PlanDirectory);
                report = await ExecuteAsync(probe.Plan, observer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                report = await ExecuteAsync(probe.Plan, new ConsoleRunObserver(io.Out), cancellationToken).ConfigureAwait(false);
            }

            return Finish(report, probe.Plan.PlanDirectory, io);
        }
        finally
        {
            if (logServer is not null)
            {
                await logServer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Print the summary and map the report to the process exit code (SSOT §7).</summary>
    private static int Finish(RunReport report, string planDirectory, IConsoleIo io)
    {
        PrintSummary(report, planDirectory, io);

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

        // Post-mortem pointer for EVERY task, not just failures: a green task whose guardrails
        // turned out too weak is reviewed from the same on-disk logs (action output, guardrail
        // stdout, feedback per attempt). The link target is the ABSOLUTE state/logs root so it is
        // clickable (issue #59); the <task-id>/attempt-N/ layout follows as guidance text.
        string logsRoot = Path.GetFullPath(Path.Combine(planDirectory, "state", "logs"));
        string sep = Path.DirectorySeparatorChar.ToString();
        // Emit a clickable OSC 8 link only when the terminal can actually render one — matching the
        // live table's gate. Redirection alone is too weak: a non-redirected but hyperlink-incapable
        // TTY would get raw escape bytes as visible garbage, which Spectre's link capability check
        // avoids. Also require the target to exist — a full-resume/all-skipped run writes no logs, so
        // don't advertise a link that 404s. When not linkable the plain absolute path still serves as
        // copy-pasteable guidance and fixes the #59 regression (it was relative with literal placeholders).
        bool linkable = !Console.IsOutputRedirected
                        && AnsiConsole.Profile.Capabilities.Links
                        && Directory.Exists(logsRoot);
        output.WriteLine();
        output.WriteLine($"Logs (post-mortem any task — pass or fail): {Hyperlink(logsRoot, linkable)}");
        output.WriteLine($"  each task's attempts are under <task-id>{sep}attempt-N{sep}");

        foreach (TaskResult needsHuman in report.Tasks.Where(t =>
                     t.Outcome is TaskOutcome.ActionFailed or TaskOutcome.GuardrailFailed
                         or TaskOutcome.InvalidFragment or TaskOutcome.NeedsHuman))
        {
            output.WriteLine();
            output.WriteLine($"NEEDS HUMAN: {needsHuman.TaskId} — {needsHuman.Summary}");
            output.WriteLine($"  Inspect {logsRoot}{Path.DirectorySeparatorChar}{needsHuman.TaskId}{Path.DirectorySeparatorChar} (latest attempt's feedback.md has the full failure detail),");
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

    /// <summary>
    /// Render <paramref name="absolutePath"/> as an OSC 8 hyperlink (clickable in capable terminals —
    /// Windows Terminal, VS Code, iTerm2) targeting its <c>file://</c> URI, mirroring the per-task
    /// links in the live table. When <paramref name="enabled"/> is false — output redirected, the
    /// terminal can't render hyperlinks, or the target doesn't exist — the escape sequence would be
    /// noise, so emit the plain absolute path instead. The caller owns the capability decision so this
    /// stays a pure, testable function. Public (not private) because the Cli assembly ships no
    /// InternalsVisibleTo — same rationale as <see cref="LogsCommand"/>'s test seams.
    /// </summary>
    public static string Hyperlink(string absolutePath, bool enabled)
    {
        if (!enabled)
        {
            return absolutePath;
        }

        const string esc = "\u001b";
        string uri = new Uri(absolutePath).AbsoluteUri;
        return $"{esc}]8;;{uri}{esc}\\{absolutePath}{esc}]8;;{esc}\\";
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
