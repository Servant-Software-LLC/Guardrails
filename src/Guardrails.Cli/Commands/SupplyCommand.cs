using System.CommandLine;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails supply &lt;plan&gt; &lt;path&gt;...</c> (design 40 §1/§6): stage one or more
/// workspace-relative files into <c>logs/&lt;runId&gt;/supplied/</c> for the harness to drain onto the
/// run's base at its next boundary (a task boundary while the run is live; run start, before
/// scheduling, when the run has already halted and exited — §1's measured case). Draining and
/// committing the staged tree onto the run's base is <c>SuppliedDrain</c>'s job (task 04); this command
/// only validates the request and copies bytes into the staging tree.
/// <para>
/// Refuses when there is no resumable run at all — no journal, or a journal whose every task has
/// already settled (§6) — never merely because no run is currently executing (§1's whole point: the
/// measured incident is a run that already halted and exited).
/// </para>
/// <para>
/// <b>Caller scoping (§5a/§6, DECIDED d40-agent-callable-supply).</b> An operator invocation (no
/// <c>GUARDRAILS_*</c> namespace in its environment) may supply any workspace path. A task invocation
/// (the namespace present, #442's hermetic guarantee) may supply only paths inside its OWN
/// <c>writeScope</c> — enforced via <see cref="SupplyCallerScope.Check"/>, consulted before anything is
/// staged.
/// </para>
/// </summary>
public static class SupplyCommand
{
    /// <summary>
    /// The exact env-var surface <c>TaskExecutor.BuildEnvironment</c> sets for a task action (SSOT
    /// §5.1) — the ONLY vars this command consults to tell a task invocation from an operator's own
    /// shell. Scanning the FULL ambient process environment instead would misclassify an operator's own
    /// shell as a task invocation whenever this process happens to be running inside another guardrails
    /// run itself (which carries its own unrelated <c>GUARDRAILS_*</c> config, e.g.
    /// <c>GUARDRAILS_TELEMETRY_CORPUS_ROOT</c>).
    /// </summary>
    private static readonly string[] TaskEnvironmentKeys =
    [
        "GUARDRAILS_PLAN_DIR", "GUARDRAILS_TASK_ID", "GUARDRAILS_TASK_DIR", "GUARDRAILS_ATTEMPT",
        "GUARDRAILS_STATE_IN", "GUARDRAILS_STATE_OUT", "GUARDRAILS_LOG_DIR", "GUARDRAILS_WORKSPACE",
        "GUARDRAILS_STAGING_DIR", "GUARDRAILS_FEEDBACK"
    ];

    /// <summary>
    /// Build the <c>supply</c> verb. <paramref name="readEnvironment"/> is where the caller-scope check
    /// reads the INVOKING process's environment from; null — every production caller — reads the real
    /// process. A test passes its own reader to simulate an operator or a task caller, because the task
    /// namespace is process-wide state: two test classes that cleared and set it with
    /// <c>Environment.SetEnvironmentVariable</c> raced each other, and this command then saw no task
    /// namespace mid-test and let an out-of-scope path through (#520's convention: a seam first).
    /// </summary>
    public static Command Create(IConsoleIo io, Func<string, string?>? readEnvironment = null)
    {
        Func<string, string?> environment = readEnvironment ?? Environment.GetEnvironmentVariable;

        var planArgument = FolderArgument.Create(
            "Path to the plan folder (contains guardrails.json) whose run to supply.");

        var pathArgument = new Argument<string[]>("path")
        {
            Description = "Workspace-relative path(s) to stage for the run's next drain boundary.",
            Arity = ArgumentArity.OneOrMore
        };

        var resumeOption = new Option<bool>("--resume")
        {
            Description = "Opt-in shorthand (design 40 §3): stage, reset the currently-halted task(s), and "
                + "resume the run — all in one invocation. The explicit three-command path (supply, then "
                + "'guardrails reset', then 'guardrails run') stays the default and is still what the "
                + "needs-human halt text prints; this runs the SAME commands back to back, so it produces "
                + "the identical journal and provenance record."
        };

        var command = new Command("supply",
            "Stage file(s) for an in-flight or halted run to pick up at its next drain boundary.");
        command.Add(planArgument);
        command.Add(pathArgument);
        command.Add(resumeOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(planArgument), io.Out);
            string[] paths = parseResult.GetValue(pathArgument) ?? [];
            bool resume = parseResult.GetValue(resumeOption);
            return resume
                ? await RunResume(folder, paths, io, environment, cancellationToken).ConfigureAwait(false)
                : Run(folder, paths, io, environment);
        });

        return command;
    }

    /// <summary>
    /// <c>--resume</c>: stage exactly as the plain verb does, then RE-ENTER the composition root to run
    /// the already-shipped <c>reset</c> and <c>run</c> verbs — never a bespoke reimplementation of either.
    /// This is what makes the shorthand and the explicit three-command path produce the byte-identical
    /// journal and provenance record the authored tests pin: they run the same commands, so they reach
    /// the same run-start drain that now writes the §4 record (this method never writes one itself).
    /// Resets every task currently <see cref="JournalTaskStatus.NeedsHuman"/> (∪ descendants, via
    /// <c>reset</c>'s own safe-suffix rewind) — the set a halted run actually needs re-armed — then a
    /// plain <c>run</c> resumes it. Propagates the first non-success exit code from staging or resetting
    /// without running anything further. The re-entered roots receive the same environment reader, so
    /// they see the caller this invocation saw.
    /// <para>
    /// The inner <c>run</c> passes <c>--no-merge-on-success</c>, matching the explicit three-command
    /// path's own final <c>run</c>: <c>--resume</c>'s job is to get the DAG itself back to green, never to
    /// also make the separate, more consequential decision to deliver that work to the user's branch as a
    /// side effect of a one-shot convenience shorthand. Delivery stays a deliberate, later act (a plain
    /// <c>guardrails run</c>), exactly as it would be after the explicit three-command path.
    /// </para>
    /// </summary>
    private static async Task<int> RunResume(
        string planFolder, string[] paths, IConsoleIo io, Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        int stageExit = Run(planFolder, paths, io, environment);
        if (stageExit != ExitCodes.Success)
        {
            return stageExit;
        }

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(planFolder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        JournalDocument document = JournalReader.Read(RunJournal.PathFor(probe.Plan.PlanDirectory));
        string[] haltedTaskIds = document.Tasks
            .Where(kv => kv.Value.Status == JournalTaskStatus.NeedsHuman)
            .Select(kv => kv.Key)
            .ToArray();

        if (haltedTaskIds.Length > 0)
        {
            int resetExit = await CommandFactory.BuildRootCommand(io, environment: environment)
                .Parse(["reset", planFolder, .. haltedTaskIds])
                .InvokeAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (resetExit != ExitCodes.Success)
            {
                return resetExit;
            }
        }

        return await CommandFactory.BuildRootCommand(io, environment: environment)
            .Parse(["run", planFolder, "--no-merge-on-success"])
            .InvokeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static int Run(string planFolder, string[] paths, IConsoleIo io, Func<string, string?> environment)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(planFolder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        Core.Model.PlanDefinition plan = probe.Plan;

        string journalPath = RunJournal.PathFor(plan.PlanDirectory);
        if (!File.Exists(journalPath))
        {
            output.WriteLine(
                "REFUSED — no resumable run for this plan: it has never been run. Use 'guardrails run' first.");
            return ExitCodes.HarnessError;
        }

        JournalDocument document = JournalReader.Read(journalPath);

        // §6: refuse only when there is NO resumable run at all — every task has already settled
        // succeeded. Never require a LIVE run: the measured case (§1) is a run that already halted and
        // exited, and 'supply' has to work precisely then.
        bool anyUnfinished = plan.Tasks.Any(task =>
            !document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry)
            || entry.Status != JournalTaskStatus.Succeeded);
        if (!anyUnfinished)
        {
            output.WriteLine(
                "REFUSED — no resumable run for this plan: every task has already settled succeeded.");
            return ExitCodes.HarnessError;
        }

        IReadOnlyDictionary<string, string> callerEnvironment = TaskCallerEnvironment(environment);
        string? callingTaskId = environment("GUARDRAILS_TASK_ID");
        IReadOnlyList<string> callingTaskWriteScope = callingTaskId is null
            ? []
            : plan.Tasks
                .FirstOrDefault(task => string.Equals(task.Id, callingTaskId, StringComparison.Ordinal))
                ?.WriteScope ?? [];

        string workspace = environment("GUARDRAILS_WORKSPACE") is { Length: > 0 } scopedWorkspace
            ? scopedWorkspace
            : plan.Workspace;

        foreach (string path in paths)
        {
            // §5a/§6: consult the caller-scoping rule BEFORE staging anything.
            CallerScopeResult scope = SupplyCallerScope.Check(callerEnvironment, callingTaskWriteScope, path);
            if (scope.Refused)
            {
                output.WriteLine($"REFUSED — {scope.RefusalReason}");
                return ExitCodes.HarnessError;
            }

            StagedPathResult staged = SuppliedStagingTree.StagedPathFor(workspace, document.RunId, path);
            if (staged.Refused)
            {
                output.WriteLine($"REFUSED — {staged.RefusalReason}");
                return ExitCodes.HarnessError;
            }

            string source = Path.Combine(workspace, path);
            if (!File.Exists(source))
            {
                output.WriteLine($"REFUSED — '{path}' does not exist under the workspace ('{source}').");
                return ExitCodes.HarnessError;
            }

            string destination = Path.Combine(
                plan.PlanDirectory, staged.StagedPath!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);

            output.WriteLine($"staged: {path} -> {staged.StagedPath}");
        }

        output.WriteLine(
            "The run will pick this up at its next drain boundary: the next task boundary while it is "
            + "still executing, or at run start if it has halted — supply, then 'guardrails reset' the "
            + "halted task and 'guardrails run' to resume.");

        return ExitCodes.Success;
    }

    private static IReadOnlyDictionary<string, string> TaskCallerEnvironment(Func<string, string?> environment)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in TaskEnvironmentKeys)
        {
            if (environment(key) is { } value)
            {
                env[key] = value;
            }
        }

        return env;
    }
}
