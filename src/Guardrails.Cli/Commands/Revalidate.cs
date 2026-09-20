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
/// and the task stays a non-green halt. It is eligible only for a not-yet-succeeded task whose
/// dependencies are all already succeeded.
/// </para>
/// <para>
/// <b>Worktree mode is conditionally supported (issue #456).</b> It was refused outright, which was
/// right for the case the refusal named — an uncommitted fix in the operator's checkout, invisible to
/// the plan branch's tree — but wrong whenever the task's work is ALREADY integrated, which is the
/// common shape: a completed task stranded by a defective guardrail. There the checkout never held the
/// work, so the old remedy ("set maxParallelism to 1") pointed at a tree where the task's own output
/// does not exist. Eligibility is now read from the plan branch's <c>Guardrails-Task:</c> trailers, and
/// the refusals that remain say which situation applies. See <see cref="ResolveWorktreeVerifyPath"/>.
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

        // Issue #704 — the refusal that guards `run` guards this verb too, because it drives the SAME journal.
        // Against a live run, the RunJournal.LoadOrCreate below applies the resume normalization and PERSISTS it —
        // flipping that run's `running` task back to `pending` and clearing its halt record — and then runs that
        // task's guardrails in the same workspace, concurrently with the run that owns it. Placed before every read
        // and write of the journal, so a refusal leaves run.json byte-identical.
        if (RunCommand.RefuseWhileOwnerIsRunning(probe.Plan, folder, output))
        {
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

        // Worktree mode is CONDITIONALLY supported (issue #456). The original refusal was written for one
        // real case — an uncommitted fix in the operator's checkout, invisible to a tree forked off the
        // plan branch — but it fired unconditionally, and its remedy ("set maxParallelism to 1") actively
        // misleads when the work is on the plan branch: in-place mode then verifies the CHECKOUT, where the
        // task's own output does not exist, and the guardrail passes vacuously or fails for the wrong
        // reason. The distinguishing fact is whether this task is ALREADY integrated.
        WorktreeVerifyTarget? worktreeTarget = null;
        if (SchedulerFactory.WouldUseWorktreeMode(plan))
        {
            worktreeTarget = ResolveWorktreeVerifyPath(plan, task, output);
            if (worktreeTarget is null)
            {
                return ExitCodes.HarnessError;
            }
        }

        // A MATERIALIZED tree (issue #456) is this verb's own temporary artifact, so it is removed on
        // EVERY exit below — the two eligibility refusals, cancellation, pass, fail, and any throw. A
        // leak here would be a checkout-sized directory left behind by a read-only verification verb.
        try
        {
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

            // Name the tree honestly: in worktree mode the subject is the plan branch's tree, NOT "the
            // current workspace" — reporting the wrong one would be exactly the class of false statement
            // this verb exists to help an operator escape.
            output.WriteLine(worktreeTarget is null
                ? $"Revalidating '{taskId}' — running its guardrails against the current workspace (no agent attempt).\n"
                : $"Revalidating '{taskId}' — running its guardrails against the plan branch's integrated tree\n" +
                  $"  {worktreeTarget.Path}\n" +
                  "(its work is already integrated there; no agent attempt).\n");

            // Reuse the exact run-wiring for the executor (state init, journal load+resume, interpreter
            // map, prompt-runner registry, triage). Serial path: cwd = the user's checkout where the fix
            // lives. Worktree path (#456): cwd = the plan branch's tree, supplied as the override.
            (TaskExecutor executor, _) = SchedulerFactory.CreateExecutor(
                plan, new ProcessRunner(), new PathExecutableProbe(), new ConsoleRunObserver(output));

            TaskResult result = await executor
                .RevalidateAsync(task, cancellationToken, worktreeTarget?.Path)
                .ConfigureAwait(false);

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
        finally
        {
            if (worktreeTarget is { Ephemeral: true } ephemeral)
            {
                GitWorktreeProvider.RemoveDetachedWorktree(plan.Workspace, ephemeral.Path);
            }
        }
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

        // Issue #432: capture this gate's per-check output under the EXISTING run's logs/<runId>/ tree —
        // read non-normalizing (like ReadDurableStatuses) so a revalidate never mutates resume state just
        // to learn the run id. No journal on disk ⇒ null ⇒ no capture (unchanged behaviour).
        bool passed = await PlanGuardrailPhase
            .EvaluateAsync(plan, new ProcessRunner(), output, ReadRunId(plan.PlanDirectory), cancellationToken)
            .ConfigureAwait(false);

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
        bool passed = await PlanPreflightPhase.EvaluateAsync(plan, journal, new ProcessRunner(), output, cancellationToken).ConfigureAwait(false);

        if (passed)
        {
            output.WriteLine("Guardrails pass. Pre-DAG plan-preflight phase settles green.");
            return ExitCodes.Success;
        }

        output.WriteLine("Guardrails still failing — see \"planPreflights\" in state/run.json for the failed check(s).");
        return ExitCodes.TaskFailed;
    }

    /// <summary>
    /// The tree a WORKTREE-mode revalidate should verify against (issue #456), or null when the verb must
    /// refuse — having printed the reason, which names WHICH of the situations applies rather than the old
    /// blanket "set maxParallelism to 1".
    /// <para>
    /// Eligibility is "this task's work is already integrated on the plan branch", read from the branch
    /// itself via its <c>Guardrails-Task:</c> trailers — NOT from the journal, which records only the sha a
    /// segment forked FROM (<c>BaseCommit</c>) and never the commit an attempt produced. The trailer is the
    /// same durable record the resume pre-pass trusts, and the query is read-only and degrades to an empty
    /// map rather than throwing when the workspace is not a git repo or the branch does not exist.
    /// </para>
    /// </summary>
    private static WorktreeVerifyTarget? ResolveWorktreeVerifyPath(
        PlanDefinition plan, TaskNode task, TextWriter output)
    {
        // A declared workingDirectory resolves against the PLAN FOLDER, which lives outside any worktree
        // (see TaskExecutor.ResolveRevalidateWorkingDirectory). Verifying there would silently grade the
        // wrong tree, so this case keeps the refusal instead of guessing at a mapping.
        if (!string.IsNullOrWhiteSpace(task.Action.WorkingDirectory))
        {
            output.WriteLine(
                $"--revalidate-task cannot verify '{task.Id}' in worktree mode: it declares an " +
                "action.workingDirectory, which resolves against the plan folder rather than the plan " +
                "branch's tree. Verify it with maxParallelism 1, or revalidate a task that declares none.");
            return null;
        }

        string planName = Path.GetFileName(plan.PlanDirectory);
        IReadOnlyDictionary<string, PlanBranchTaskRecord> integrated =
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.Workspace, planName);

        if (!integrated.TryGetValue(task.Id, out PlanBranchTaskRecord? record))
        {
            // The case the original refusal was written for: nothing of this task is on the plan branch,
            // so whatever was fixed lives only in the operator's checkout — which the plan-branch tree
            // genuinely does not contain.
            output.WriteLine(
                $"--revalidate-task cannot verify '{task.Id}' in worktree mode: the plan branch " +
                $"'guardrails/{planName}' carries no Guardrails-Task: trailer for it, so its work was " +
                "never integrated and a hand-fix in your checkout is not visible to the branch's tree. " +
                "Re-run the task with 'guardrails run' so it produces its work, or verify an in-place fix " +
                "with maxParallelism 1.");
            return null;
        }

        string planBranch = $"guardrails/{planName}";
        if (GitWorktreeProvider.WorktreeForBranch(plan.Workspace, planBranch) is { } existing)
        {
            // The integration worktree is still standing — the common case, since a task stranded
            // needs-human means the run was not wholly green and WorktreeReclaim kept its root.
            return new WorktreeVerifyTarget(existing, Ephemeral: false);
        }

        // Integrated, but no worktree currently holds the plan branch (a --fresh teardown, a fresh clone,
        // or the #407 B startup GC having reclaimed an abandoned run's root). MATERIALIZE the tree at the
        // plan tip rather than refusing: the work is provably on the branch, so there is a correct subject
        // to verify — it simply is not checked out anywhere yet. Detached, so it neither collides with a
        // concurrent run's integration worktree nor strands the branch if this process dies; torn down by
        // the caller's finally.
        string materialized = GitWorktreeProvider.AddDetachedWorktreeAtBranchTip(
            plan.Workspace, SchedulerFactory.WorktreeRootFor(plan), planBranch);

        output.WriteLine(
            $"'{task.Id}' is integrated on '{planBranch}' " +
            $"(commit {record.CommitSha[..Math.Min(8, record.CommitSha.Length)]}) but no worktree held that " +
            "branch, so a temporary detached tree was materialized at the plan tip to verify in.");

        return new WorktreeVerifyTarget(materialized, Ephemeral: true);
    }

    /// <summary>
    /// The tree a worktree-mode revalidate verifies in, and whether this verb CREATED it (issue #456).
    /// An ephemeral tree was materialized at the plan tip because nothing held the plan branch, so the
    /// caller must remove it on every exit path; a non-ephemeral one is the run's own integration
    /// worktree, which must survive for the resume.
    /// </summary>
    private sealed record WorktreeVerifyTarget(string Path, bool Ephemeral);

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

    /// <summary>
    /// The existing run's id read straight off <c>run.json</c> — the <c>logs/&lt;runId&gt;/</c> tree a
    /// revalidated gate captures its per-check output under (issue #432). Read the same non-normalizing
    /// way as <see cref="ReadDurableStatuses"/>: <see cref="RunJournal.LoadOrCreate"/> would rewrite
    /// resume state as a side effect of merely learning the id. Null when no journal exists yet.
    /// </summary>
    private static string? ReadRunId(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        return File.Exists(journalPath) ? JournalReader.Read(journalPath).RunId : null;
    }
}
