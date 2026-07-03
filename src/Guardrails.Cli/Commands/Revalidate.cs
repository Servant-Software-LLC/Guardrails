using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails run [folder] --revalidate-task &lt;id&gt;</c> — re-validate-only mode (issue #102):
/// run JUST the named task's guardrails against the CURRENT workspace state, spawning NO agent/action
/// attempt. The use case: a task hit <c>needs-human</c>, a human hand-fixed the artifact, and they
/// want to confirm the gate now passes WITHOUT burning another (possibly expensive, possibly
/// fix-overwriting) agent attempt.
///
/// <para>
/// This is a single-task verification, NOT a run: on pass the task is journaled <c>succeeded</c> (the
/// next normal <c>run</c> resumes the rest of the DAG); on fail the failing guardrails are reported
/// and the task stays a non-green halt. It refuses worktree mode (the in-place fix lives in the user's
/// own checkout, which a fresh isolated segment worktree would not contain) and is eligible only for a
/// not-yet-succeeded task whose dependencies are all already succeeded.
/// </para>
/// </summary>
public static class Revalidate
{
    /// <summary>B2(a) reserved synthetic id (SSOT §7.1): revalidate ONLY the terminal <c>&lt;plan&gt;/guardrails/</c> phase.</summary>
    private const string PlanGuardrailsSyntheticId = "plan:guardrails";

    /// <summary>B2(a) reserved synthetic id (SSOT §7.1): revalidate ONLY the pre-DAG <c>&lt;plan&gt;/preflights/</c> phase.</summary>
    private const string PlanPreflightsSyntheticId = "plan:preflights";

    public static async Task<int> ExecuteAsync(
        string folder, string taskId, IConsoleIo io, CancellationToken cancellationToken)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nValidation failed; nothing was revalidated.");
            return ExitCodes.HarnessError;
        }

        PlanDefinition plan = probe.Plan;

        // B2(a) — reserved synthetic ids (SSOT §7.1): re-run ONLY the named whole-plan phase against
        // the CURRENT merged HEAD, bypassing the per-task machinery (and its worktree-mode refusal)
        // entirely — driven purely as the value of the EXISTING --revalidate-task string option, no new
        // verb. The ':' is already disallowed in a real task id (§3 `^[a-z0-9][a-z0-9._-]*$`), so these
        // can never collide with an authored task.
        if (string.Equals(taskId, PlanGuardrailsSyntheticId, StringComparison.Ordinal))
        {
            return await RevalidatePlanGuardrailsAsync(plan, io, cancellationToken).ConfigureAwait(false);
        }
        if (string.Equals(taskId, PlanPreflightsSyntheticId, StringComparison.Ordinal))
        {
            return await RevalidatePlanPreflightsAsync(plan, io, cancellationToken).ConfigureAwait(false);
        }

        TaskNode? task = plan.Tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (task is null)
        {
            output.WriteLine($"Unknown task '{taskId}'. Known tasks: {string.Join(", ", plan.Tasks.Select(t => t.Id))}");
            return ExitCodes.HarnessError;
        }

        // Refuse worktree mode: a human's in-place fix lives in their own checkout, but a worktree-mode
        // task verifies in a fresh isolated segment worktree forked off the plan branch — which would
        // NOT contain the fix. Verifying there would be meaningless, so refuse with a clear pointer.
        if (SchedulerFactory.WouldUseWorktreeMode(plan))
        {
            output.WriteLine(
                "--revalidate-task is not supported in worktree mode (maxParallelism > 1 on a git workspace): " +
                "a hand-fix in your checkout is not visible to an isolated segment worktree. " +
                "Set maxParallelism to 1 in guardrails.json to verify an in-place fix.");
            return ExitCodes.HarnessError;
        }

        // Eligibility is checked against the DURABLE (pre-resume) journal status: RunJournal.LoadOrCreate
        // would normalize needs-human/failed/blocked → pending, erasing exactly the state we gate on.
        IReadOnlyDictionary<string, JournalTaskStatus> durable = ReadDurableStatuses(plan.PlanDirectory);

        JournalTaskStatus current = durable.GetValueOrDefault(taskId, JournalTaskStatus.Pending);
        if (current == JournalTaskStatus.Succeeded)
        {
            output.WriteLine(
                $"Task '{taskId}' is already succeeded — nothing to revalidate. " +
                $"Use 'guardrails reset {taskId}' to force a fresh attempt.");
            return ExitCodes.HarnessError;
        }

        // DAG invariant: a task may only go green after every dependency is green. Verifying a task
        // whose dependency is still red could mark it succeeded out of order. (Succeeded survives the
        // journal's resume normalization, so an absent/non-succeeded dependency is genuinely not green.)
        List<string> unmetDeps = task.DependsOn
            .Where(d => durable.GetValueOrDefault(d, JournalTaskStatus.Pending) != JournalTaskStatus.Succeeded)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        if (unmetDeps.Count > 0)
        {
            output.WriteLine(
                $"Task '{taskId}' has dependencies that are not yet succeeded: {string.Join(", ", unmetDeps)}. " +
                "Revalidate or run those first.");
            return ExitCodes.HarnessError;
        }

        output.WriteLine($"Revalidating '{taskId}' — running its guardrails against the current workspace (no agent attempt).\n");

        // Reuse the exact run-wiring for the executor (state init, journal load+resume, interpreter
        // map, prompt-runner registry, triage). Serial/shared-workspace path: guardrails run with
        // cwd = the user's checkout where the fix lives.
        (TaskExecutor executor, _) = SchedulerFactory.CreateExecutor(
            plan, new ProcessRunner(), new PathExecutableProbe(), new ConsoleRunObserver(output));

        TaskResult result = await executor.RevalidateAsync(task, cancellationToken).ConfigureAwait(false);

        output.WriteLine();
        output.WriteLine($"  {RunCommand.StatusLabel(result.Outcome),-16} {result.TaskId,-32} {result.Summary}");

        if (result.Outcome == TaskOutcome.Cancelled)
        {
            return ExitCodes.Cancelled;
        }

        if (result.Outcome == TaskOutcome.Succeeded)
        {
            output.WriteLine($"\nGuardrails pass. '{taskId}' marked succeeded — re-run 'guardrails run' to resume the rest of the plan.");
            return ExitCodes.Success;
        }

        // Guardrails still failing — report which, no agent spawned.
        IReadOnlyList<GuardrailResult> failed = result.Guardrails.Where(g => !g.Passed).ToList();
        output.WriteLine("\nGuardrails still failing — fix the workspace and revalidate again:");
        foreach (GuardrailResult g in failed)
        {
            output.WriteLine($"  - {g.Name}: {g.Reason ?? "failed"}");
        }

        return ExitCodes.TaskFailed;
    }

    /// <summary>
    /// B2(a): re-run ONLY the terminal <c>&lt;plan&gt;/guardrails/</c> checks (SSOT §3.3) against the
    /// CURRENT merged HEAD. UNLIKE a per-task revalidate, worktree mode is fully SUPPORTED here: the
    /// terminal gate's subject IS the merged HEAD itself (the integration worktree the harness owns),
    /// not an in-place fix in the user's own checkout that a fresh segment worktree would not contain.
    /// </summary>
    private static async Task<int> RevalidatePlanGuardrailsAsync(
        PlanDefinition plan, IConsoleIo io, CancellationToken cancellationToken)
    {
        TextWriter output = io.Out;

        if (plan.PlanGuardrails.Count == 0)
        {
            output.WriteLine("Plan has no <plan>/guardrails/ terminal checks declared — nothing to revalidate.");
            return ExitCodes.HarnessError;
        }

        output.WriteLine(
            "Revalidating 'plan:guardrails' — running the terminal <plan>/guardrails/ checks against the current merged HEAD (no agent attempt).\n");

        bool passed = await PlanGuardrailPhase.EvaluateAsync(plan, new ProcessRunner(), cancellationToken).ConfigureAwait(false);

        if (passed)
        {
            output.WriteLine("Guardrails pass. Terminal plan-guardrail phase settles green.");
            return ExitCodes.Success;
        }

        output.WriteLine("Guardrails still failing — see \"planGuardrails\" in state/run.json for the failed check(s).");
        return ExitCodes.TaskFailed;
    }

    /// <summary>
    /// B2(a) symmetric analogue: re-run ONLY the pre-DAG <c>&lt;plan&gt;/preflights/</c> checks (SSOT
    /// §7 B1) — re-confirming a hand-fixed starting state without burning an agent attempt.
    /// </summary>
    private static async Task<int> RevalidatePlanPreflightsAsync(
        PlanDefinition plan, IConsoleIo io, CancellationToken cancellationToken)
    {
        TextWriter output = io.Out;

        if (plan.PlanPreflights.Count == 0)
        {
            output.WriteLine("Plan has no <plan>/preflights/ checks declared — nothing to revalidate.");
            return ExitCodes.HarnessError;
        }

        output.WriteLine(
            "Revalidating 'plan:preflights' — running the pre-DAG <plan>/preflights/ checks (no agent attempt).\n");

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        bool passed = await PlanPreflightPhase.EvaluateAsync(plan, journal, new ProcessRunner(), cancellationToken).ConfigureAwait(false);

        if (passed)
        {
            output.WriteLine("Guardrails pass. Pre-DAG plan-preflight phase settles green.");
            return ExitCodes.Success;
        }

        output.WriteLine("Guardrails still failing — see \"planPreflights\" in state/run.json for the failed check(s).");
        return ExitCodes.TaskFailed;
    }

    /// <summary>
    /// Read per-task statuses straight off <c>run.json</c> WITHOUT the resume normalization
    /// <see cref="RunJournal.LoadOrCreate"/> applies (which flips needs-human/failed/blocked →
    /// pending). Eligibility depends on the durable status, so we must read it raw. An absent or
    /// unreadable journal yields no statuses (every task reads as pending — i.e. never-run).
    /// </summary>
    private static IReadOnlyDictionary<string, JournalTaskStatus> ReadDurableStatuses(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath))
        {
            return new Dictionary<string, JournalTaskStatus>(StringComparer.Ordinal);
        }

        JournalDocument document = JournalReader.Read(journalPath);
        return document.Tasks.ToDictionary(p => p.Key, p => p.Value.Status, StringComparer.Ordinal);
    }
}
