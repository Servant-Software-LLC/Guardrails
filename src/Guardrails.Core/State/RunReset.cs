using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.State;

/// <summary>
/// Implements the two reset modes behind <c>guardrails reset</c> and <c>run --fresh</c>:
/// a full reset that wipes runtime state and re-seeds, and a single-task reset that pushes
/// one task back to <c>pending</c> while keeping its attempt history. Kept in Core (not the
/// CLI) so it is unit-testable and reusable by <c>--fresh</c>.
/// </summary>
public static class RunReset
{
    /// <summary>
    /// Full fresh reset (SSOT §6.1): delete <c>run.json</c>, <c>state.json</c>,
    /// <c>merge-conflicts.log</c>, the plan-root <c>logs/</c> tree (all runs' attempt artifacts and
    /// any exported static log site, §8) and the <c>captured/</c> baseline store; tear down the plan
    /// branch <c>guardrails/&lt;plan-name&gt;</c> and its worktrees (issue #274, part B — the plan branch
    /// is the durable cross-run resume record, so a genuine fresh slate must clear it, not just the
    /// runtime-state files); then re-seed <c>state.json</c> from <c>seed.json</c> (or <c>{}</c>). The
    /// committed <c>seed.json</c>, the task folders, and the committed review marker
    /// <c>state/guardrails-review.json</c> (§13) are untouched — the marker is a committed plan artifact
    /// (planHash-keyed, self-invalidating on any edit), NOT per-run runtime state, so a fresh slate keeps
    /// the prior review attestation.
    /// </summary>
    /// <remarks>
    /// <c>captured/</c> (issue #51, the restore-on-retry baselines) MUST be wiped: a stale baseline
    /// surviving <c>--fresh</c> would revert a legitimately re-authored file on the next run, before
    /// any task re-snapshots its current bytes. It is harness-owned runtime state like the rest of
    /// <c>state/</c>, never committed.
    /// </remarks>
    public static void Fresh(string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        string stateDir = Path.Combine(planDirectory, "state");

        // #383 (Windows short-junction worktree root): capture the recorded junction root BEFORE run.json is
        // deleted — git canonicalizes the junction away in its own worktree registrations, so the journal is
        // the ONLY record of the chosen short link. Threaded into the prune below so the junction LINK is
        // removed (link-only, never the target) after the worktrees are pruned.
        string? junctionRoot = TryReadRecordedJunctionRoot(planDirectory);

        DeleteFileIfExists(Path.Combine(stateDir, "run.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "state.json"));
        DeleteFileIfExists(Path.Combine(stateDir, "merge-conflicts.log"));
        // Issue #274 Part C: a stale rewind-intent marker (a crash mid safe-drift-resolution) must not
        // survive a fresh slate — it would replay a reset for a plan the fresh run already cleared.
        DeleteFileIfExists(RewindIntent.PathFor(planDirectory));
        // The per-attempt artifacts (and any exported static log site) live under the PLAN-ROOT
        // logs/<runId>/ tree (SSOT §8, plan-08: a sibling of state/, divided by runId) — NOT the
        // pre-plan-08 state/logs/. Delete the whole logs/ tree so a fresh run starts clean and the
        // tree never grows unbounded across runs.
        DeleteDirectoryIfExists(Path.Combine(planDirectory, "logs"));
        // The review marker (state/guardrails-review.json, §13) is deliberately NOT deleted: it is a
        // committed plan artifact, planHash-keyed so it self-invalidates on any task/guardrail edit
        // (the nudge returns) — a fresh run must keep the prior review attestation, not erase it.
        DeleteDirectoryIfExists(Path.Combine(stateDir, "captured"));

        // F3 + issue #274: prune stale guardrails/<runId>/* segment+fork branches and their worktrees
        // left behind by a crashed worktree-mode run, AND tear down the plan branch guardrails/<plan-name>
        // itself. State-file deletion alone never touches git refs, so a crashed run's segment branches —
        // and, crucially, the plan branch whose trailers drive resume — would survive --fresh, silently
        // reusing already-succeeded segments even for edited tasks. Best-effort: a non-git workspace or a
        // load failure must not abort the reset.
        PruneStaleWorktreesAndBranches(planDirectory, junctionRoot);

        // Re-seed immediately so a subsequent run starts from the seed-derived state.
        new StateManager(planDirectory).Initialize();
    }

    /// <summary>
    /// Reset a single task to <c>pending</c> (keeping attempt history), so the next
    /// <c>run</c> re-executes just that task. Also clears that task's captured baseline subdir
    /// (issue #51) so a re-run re-snapshots the file's current bytes rather than reverting to a
    /// stale baseline.
    /// <para>
    /// <b>Return contract (#311 NIT-3):</b> <c>true</c> when the task was reset. <c>false</c> covers
    /// SEVERAL distinct outcomes — the task is unknown to the journal, OR (worktree mode) the delegated
    /// safe <see cref="ScopedReset"/> REFUSED (an unattributed commit in the range) or hit a concurrent
    /// modification. The bool cannot distinguish them; a caller that needs the richer outcome (refusal
    /// reason, concurrent-modification) must call <see cref="ScopedReset"/> directly (as the CLI does).
    /// This helper is a convenience for a serial/flat single-task reset where a plain success/fail suffices.
    /// </para>
    /// <para>
    /// WEAK-3 (#311): in WORKTREE mode a journal-only reset is a SILENT NO-OP for resume — the task's
    /// plan-branch <c>Guardrails-Task:</c> trailer survives, so the resume pre-pass (and, for a waved plan,
    /// <c>EvaluateWaveCompletion</c>) still reads it green and never re-runs it. So when a plan branch
    /// exists, DELEGATE to the safe <see cref="ScopedReset"/> (which rewinds the branch too, or safely
    /// refuses); only fall back to the journal-only reset when there is no plan branch (serial / non-git),
    /// where the journal is authoritative and the reset genuinely takes effect.
    /// </para>
    /// </summary>
    public static bool Task(PlanDefinition plan, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        if (!File.Exists(RunJournal.PathFor(plan.PlanDirectory)))
        {
            return false;
        }

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        if (!journal.Document.Tasks.ContainsKey(taskId))
        {
            return false;
        }

        // Worktree mode (a plan branch exists) → the branch is the durable resume record, so a journal-only
        // reset would not take effect. Route through ScopedReset (safe rewind / refuse). Serial / non-git
        // (no branch) → journal-only, which is authoritative there and unchanged.
        string planBranch = $"guardrails/{Path.GetFileName(plan.PlanDirectory)}";
        if (GitWorktreeProvider.CurrentPlanBranchTip(plan.Workspace, planBranch).Length > 0)
        {
            return ScopedReset(plan, [taskId]).Outcome == ScopedResetOutcome.Done;
        }

        journal.ResetTask(taskId);
        DeleteDirectoryIfExists(Path.Combine(plan.PlanDirectory, "state", "captured", taskId));
        return true;
    }

    /// <summary>The outcome of a Part C scoped reset (issue #274, SSOT §7.2).</summary>
    public enum ScopedResetOutcome
    {
        /// <summary>The named set (∪ descendants) was reset — rewound off the plan branch when it formed a safe suffix, else journal-only.</summary>
        Done,

        /// <summary>The set is NOT a provably-safe trailing suffix — refused; the plan branch was left untouched.</summary>
        Refused,

        /// <summary>The plan branch moved between the safe-suffix decision and the rewind (a concurrent same-plan session) — aborted without touching the branch; re-run the command (issue #274 Part C compare-and-swap).</summary>
        ConcurrentModification,

        /// <summary>A named task id is unknown to the run journal — nothing was changed.</summary>
        UnknownTask,

        /// <summary>No run journal exists yet (the plan has not been run) — nothing to reset.</summary>
        NoJournal
    }

    /// <summary>The result of <see cref="ScopedReset"/> (issue #274 Part C).</summary>
    public sealed record ScopedResetResult
    {
        /// <summary>Which outcome applies.</summary>
        public required ScopedResetOutcome Outcome { get; init; }

        /// <summary>The full reset set S (named ∪ descendants), in plan order — populated on <see cref="ScopedResetOutcome.Done"/>.</summary>
        public IReadOnlyList<string> ResetTasks { get; init; } = [];

        /// <summary>The commit the plan branch was rewound to; null for a journal-only reset (serial / no plan branch).</summary>
        public string? RewindTarget { get; init; }

        /// <summary>The refusal reason, on <see cref="ScopedResetOutcome.Refused"/>.</summary>
        public string? Refusal { get; init; }

        /// <summary>The out-of-set task that blocked the rewind, on <see cref="ScopedResetOutcome.Refused"/>.</summary>
        public string? BlockingTask { get; init; }

        /// <summary>The unknown task id, on <see cref="ScopedResetOutcome.UnknownTask"/>.</summary>
        public string? UnknownTaskId { get; init; }
    }

    /// <summary>
    /// Part C scoped reset (issue #274, SSOT §7.2): reset the named task(s) ∪ their transitive descendants,
    /// applying the SAME safety-check + rewind primitive as the run-time auto-resolve — the second consumer
    /// of the one primitive. When the set is a provably-safe trailing suffix of the plan branch, DESTRUCTIVELY
    /// rewind the plan branch past it (so a later <c>guardrails run</c> forks a clean base) and journal-reset
    /// the set; when it is NOT (a non-suffix, an uncontained fan-in, a trailer-less commit), REFUSE and name
    /// the blocking task — the caller points the user at the always-sound <c>guardrails reset &lt;folder&gt; -y</c>
    /// full rebuild. In serial mode / a non-git plan folder there is no plan branch to carry a stale commit,
    /// so it degrades to a sound journal-only reset of the set. Records a <c>drift</c>-boundary entry
    /// (SSOT §2.1/§7) in the durable <c>decisions[]</c> journal section whenever it resets anything.
    /// </summary>
    public static ScopedResetResult ScopedReset(PlanDefinition plan, IReadOnlyList<string> taskIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(taskIds);

        if (!File.Exists(RunJournal.PathFor(plan.PlanDirectory)))
        {
            return new ScopedResetResult { Outcome = ScopedResetOutcome.NoJournal };
        }

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        foreach (string id in taskIds)
        {
            if (!journal.Document.Tasks.ContainsKey(id))
            {
                return new ScopedResetResult { Outcome = ScopedResetOutcome.UnknownTask, UnknownTaskId = id };
            }
        }

        // S = named tasks ∪ transitive descendants (a changed producer can change a consumer's inputs).
        var graph = new DependencyGraph(plan.Tasks);
        var set = new HashSet<string>(taskIds, StringComparer.Ordinal);
        foreach (string id in taskIds)
        {
            foreach (string dependent in graph.TransitiveDependentsOf(id))
            {
                set.Add(dependent);
            }
        }

        string planName = Path.GetFileName(plan.PlanDirectory);
        string planBranch = $"guardrails/{planName}";
        // #322: corroborate each removed commit's Guardrails-Task-Hash: trailer against the journal-recorded
        // settle hashes — a copied-trailer #197 hand-fix in the range REFUSES rather than being discarded.
        SafeSuffixDecision decision = GitWorktreeProvider.EvaluateSafeSuffix(
            plan.Workspace, planBranch, set, journal.RecordedDefinitionHashes());

        // Refuse floor: an unsafe rewind is never performed — the plan branch is left untouched.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return new ScopedResetResult
            {
                Outcome = ScopedResetOutcome.Refused,
                Refusal = decision.Refusal,
                BlockingTask = decision.BlockingTask
            };
        }

        bool willRewind = decision.Outcome == SafeSuffixOutcome.Safe && decision.ResetTarget is not null;

        // Compare-and-swap (issue #274 Part C): the branch must still be exactly where the safe-suffix
        // decision was computed — a concurrent same-plan session that advanced/rewound it since would make
        // us discard its work. On a mismatch, abort WITHOUT touching the branch (the user re-runs).
        if (willRewind)
        {
            string currentTip = GitWorktreeProvider.CurrentPlanBranchTip(plan.Workspace, planBranch);
            if (!string.Equals(currentTip, decision.ExpectedTip, StringComparison.Ordinal))
            {
                return new ScopedResetResult { Outcome = ScopedResetOutcome.ConcurrentModification };
            }
        }

        // CRASH-ATOMIC destructive section (issue #274 Part C): write the rewind-intent marker BEFORE the
        // rewind so a kill between the rewind and the journal-resets is replayed idempotently on the next
        // `guardrails run`; clear it only AFTER both effects persist.
        var setInOrder = plan.Tasks.Where(t => set.Contains(t.Id)).Select(t => t.Id).ToList();
        if (willRewind)
        {
            RewindIntent.Write(plan.PlanDirectory, new RewindIntent
            {
                SafeSet = setInOrder,
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = decision.ResetTarget
            });
            GitWorktreeProvider.RewindPlanBranch(plan.Workspace, planBranch, decision.ResetTarget!);
        }

        // Journal-reset every member of S (in plan order), clearing each task's captured baseline (as the
        // single-task RunReset.Task does), and record the per-task old→new hash audit.
        var resolvedTasks = new List<DriftResolvedTask>();
        var resetInOrder = new List<string>();
        var byId = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (!set.Contains(task.Id))
            {
                continue;
            }

            resetInOrder.Add(task.Id);
            DeleteDirectoryIfExists(Path.Combine(plan.PlanDirectory, "state", "captured", task.Id));

            string oldHash = journal.RecordedDefinitionHash(task.Id) ?? "(none recorded)";
            string newHash = SafeComputeHash(byId.GetValueOrDefault(task.Id));
            journal.ResetTask(task.Id);
            resolvedTasks.Add(new DriftResolvedTask { TaskId = task.Id, OldHash = oldHash, NewHash = newHash });
        }

        journal.RecordDecision(DriftDecisions.ManualReset(
            plan.Config.AutonomyPolicy, willRewind ? decision.ResetTarget : null, resolvedTasks));

        if (willRewind)
        {
            RewindIntent.Clear(plan.PlanDirectory);
        }

        return new ScopedResetResult
        {
            Outcome = ScopedResetOutcome.Done,
            ResetTasks = resetInOrder,
            RewindTarget = willRewind ? decision.ResetTarget : null
        };
    }

    /// <summary>The outcome of a wave-scoped reset (SSOT §14.8, #254 M2b).</summary>
    public enum WaveResetOutcome
    {
        /// <summary>The wave + its downstream waves were reset — the plan branch rewound past them when one exists, else journal-only.</summary>
        Done,

        /// <summary>The removed range holds an unattributed (trailer-less NON-marker) commit — a human hand-fix — so the rewind was REFUSED; the plan branch was left untouched (#311 BLOCKER).</summary>
        Refused,

        /// <summary>The plan branch moved between the safe-suffix decision and the rewind (a concurrent same-plan session) — aborted without touching the branch (#311 WEAK-4).</summary>
        ConcurrentModification,

        /// <summary>The named wave directory is not a wave in this plan.</summary>
        UnknownWave,

        /// <summary>No run journal exists yet (the plan has not been run).</summary>
        NoJournal
    }

    /// <summary>The result of <see cref="WaveReset"/> (SSOT §14.8).</summary>
    public sealed record WaveResetResult
    {
        /// <summary>Which outcome applies.</summary>
        public required WaveResetOutcome Outcome { get; init; }

        /// <summary>The wave dirs reset (the named wave + its downstream waves), in strict order — on <see cref="WaveResetOutcome.Done"/>.</summary>
        public IReadOnlyList<string> ResetWaves { get; init; } = [];

        /// <summary>The task ids reset across those waves, in plan order — on <see cref="WaveResetOutcome.Done"/>.</summary>
        public IReadOnlyList<string> ResetTasks { get; init; } = [];

        /// <summary>The commit the plan branch was rewound to; null for a journal-only reset (serial / no plan branch).</summary>
        public string? RewindTarget { get; init; }

        /// <summary>The refusal reason, on <see cref="WaveResetOutcome.Refused"/>.</summary>
        public string? Refusal { get; init; }

        /// <summary>The unknown wave dir, on <see cref="WaveResetOutcome.UnknownWave"/>.</summary>
        public string? UnknownWaveDir { get; init; }
    }

    /// <summary>
    /// Wave-scoped reset (SSOT §14.8, #254 M2b): rewind the plan branch past <paramref name="waveDir"/> AND
    /// every downstream wave, then journal-reset every task + the wave records so a later <c>guardrails run</c>
    /// re-runs them from a clean base. Like the task-scoped <see cref="ScopedReset"/>, it ROUTES THROUGH the
    /// marker-aware <see cref="SafeSuffixEvaluator"/> (via <see cref="GitWorktreeProvider.EvaluateSafeSuffix"/>,
    /// #311 BLOCKER): the evaluator DERIVES the reset target from the live first-parent history (always an
    /// ancestor — no dangling <c>MarkerSha</c> sideways-reset, BLOCKER-1b), EXEMPTS the harness's own
    /// <c>Guardrails-Wave:</c> markers, and REFUSES if a trailer-less NON-marker commit (a human #197
    /// hand-fix) is in the removed range. A tip compare-and-swap guards a concurrent same-plan run (WEAK-4).
    /// Crash-atomic via <see cref="RewindIntent"/> carrying the wave dirs (BLOCKER-1b). In serial mode / a
    /// non-git plan folder there is no plan branch, so it degrades to a sound journal-only reset. Records a
    /// <c>wave</c>-boundary <see cref="DecisionEntry"/>.
    /// </summary>
    public static WaveResetResult WaveReset(PlanDefinition plan, string waveDir)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(waveDir);

        if (!File.Exists(RunJournal.PathFor(plan.PlanDirectory)))
        {
            return new WaveResetResult { Outcome = WaveResetOutcome.NoJournal };
        }

        int index = -1;
        for (int i = 0; i < plan.Waves.Count; i++)
        {
            if (string.Equals(plan.Waves[i].Dir, waveDir, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return new WaveResetResult { Outcome = WaveResetOutcome.UnknownWave, UnknownWaveDir = waveDir };
        }

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        List<WaveNode> affected = plan.Waves.Skip(index).ToList();
        List<string> affectedWaveDirs = affected.Select(w => w.Dir).ToList();
        var set = new HashSet<string>(affected.SelectMany(w => w.Tasks.Select(t => t.Id)), StringComparer.Ordinal);

        string planName = Path.GetFileName(plan.PlanDirectory);
        string planBranch = $"guardrails/{planName}";

        // Safe-suffix check against the plan branch (marker-aware): DERIVES the target from history, EXEMPTS
        // the wave markers, REFUSES a trailer-less human hand-fix in range. Serial / no branch → Nothing.
        // #322: also corroborate each removed commit's Guardrails-Task-Hash: against the journal-recorded
        // settle hashes so a copied-trailer #197 hand-fix in the range REFUSES.
        SafeSuffixDecision decision = GitWorktreeProvider.EvaluateSafeSuffix(
            plan.Workspace, planBranch, set, journal.RecordedDefinitionHashes());

        // Refuse floor (un-overridable): a human hand-fix / unattributed commit in the range is never discarded.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return new WaveResetResult { Outcome = WaveResetOutcome.Refused, Refusal = decision.Refusal };
        }

        bool willRewind = decision.Outcome == SafeSuffixOutcome.Safe && decision.ResetTarget is not null;

        // Compare-and-swap (WEAK-4): the branch must still be where the decision saw it, else a concurrent
        // same-plan session moved it — abort WITHOUT touching the branch.
        if (willRewind)
        {
            string currentTip = GitWorktreeProvider.CurrentPlanBranchTip(plan.Workspace, planBranch);
            if (!string.Equals(currentTip, decision.ExpectedTip, StringComparison.Ordinal))
            {
                return new WaveResetResult { Outcome = WaveResetOutcome.ConcurrentModification };
            }
        }

        // Crash-atomic: write the intent (task ids AND wave dirs, BLOCKER-1b) BEFORE the rewind, clear AFTER.
        var taskIdsInOrder = affected.SelectMany(w => w.Tasks.Select(t => t.Id)).ToList();
        if (willRewind)
        {
            RewindIntent.Write(plan.PlanDirectory, new RewindIntent
            {
                SafeSet = taskIdsInOrder.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Waves = affectedWaveDirs,
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = decision.ResetTarget
            });
            GitWorktreeProvider.RewindPlanBranch(plan.Workspace, planBranch, decision.ResetTarget!);
        }

        var resetTasksInOrder = new List<string>();
        foreach (WaveNode w in affected)
        {
            journal.ResetWaveToPending(w.Dir);
            foreach (TaskNode t in w.Tasks)
            {
                resetTasksInOrder.Add(t.Id);
                DeleteDirectoryIfExists(Path.Combine(plan.PlanDirectory, "state", "captured", t.Id));
                journal.ResetTask(t.Id);
            }
        }

        journal.RecordDecision(DriftDecisions.WaveReset(
            plan.Config.AutonomyPolicy, waveDir, willRewind ? decision.ResetTarget : null, affectedWaveDirs));

        if (willRewind)
        {
            RewindIntent.Clear(plan.PlanDirectory);
        }

        return new WaveResetResult
        {
            Outcome = WaveResetOutcome.Done,
            ResetWaves = affectedWaveDirs,
            ResetTasks = resetTasksInOrder,
            RewindTarget = willRewind ? decision.ResetTarget : null
        };
    }

    /// <summary>Compute a task's current definition hash, degrading to a sentinel on a read failure (audit only, never the gate).</summary>
    private static string SafeComputeHash(TaskNode? task)
    {
        if (task is null)
        {
            return "(unknown)";
        }

        try
        {
            return TaskDefinitionHash.Compute(task);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "(unreadable)";
        }
    }

    /// <summary>
    /// Load the plan (best-effort) to resolve its workspace + worktree root, then prune any stale
    /// <c>guardrails/&lt;runId&gt;/*</c> segment/fork branches and worktrees from the workspace repo AND
    /// tear down the plan branch <c>guardrails/&lt;plan-name&gt;</c> itself (issue #274, part B). Swallows
    /// every failure (unloadable plan, non-git workspace) so <c>--fresh</c> never aborts.
    /// </summary>
    private static void PruneStaleWorktreesAndBranches(string planDirectory, string? junctionRoot)
    {
        try
        {
            PlanLoadResult load = new PlanLoader().Load(planDirectory);
            if (load.Plan is { } plan)
            {
                // Prune with the REAL root: git canonicalizes the short #383 junction back to the real
                // worktree path in its own registrations, so the git-authoritative teardown already keys on
                // the real root regardless of whether a junction aliased it during the run.
                GitWorktreeProvider.PruneStaleSegmentBranches(
                    plan.Workspace, SchedulerFactory.WorktreeRootFor(plan));

                // Issue #274 (part B): tear down the plan branch itself. It is the DURABLE cross-run resume
                // record — its Guardrails-Task: trailers drive the "already succeeded, skip it" pre-pass — and
                // PruneStaleSegmentBranches DELIBERATELY preserves it (it is a 2-component plan branch, not a
                // segment branch). Correct for a normal resume, but it meant --fresh / reset -y never actually
                // cleared it, so a "fresh" run silently reused the stale trailers. This runs ONLY on the
                // fresh/full-reset path (Fresh is the sole caller) — a normal resume never reaches here, so the
                // plan branch is preserved and resumed against exactly as before. The plan name matches the
                // Scheduler's branch-creating derivation (Path.GetFileName(plan.PlanDirectory)).
                GitWorktreeProvider.TeardownPlanBranch(
                    plan.Workspace, SchedulerFactory.WorktreeRootFor(plan), Path.GetFileName(plan.PlanDirectory));

                // #195 retry-salvage pruning (deliverable 6): a --fresh reset also clears every preserved
                // salvage ref across the whole repo, alongside the existing stale segment/fork branch
                // prune — a fresh run's tasks get fresh attempt numbers, so any surviving salvage ref would
                // be orphaned bookkeeping with no corresponding attempt to reference it.
                GitWorktreeProvider.PruneAllSalvageRefs(plan.Workspace);
            }
        }
        catch
        {
            // Best-effort cleanup — a fresh reset must succeed even if the prune cannot run.
        }

        // #383: remove the short worktree junction LINK itself (link-only — the target/real worktrees were
        // just pruned above via the real root). Runs even if the prune could not load the plan: the link is
        // recorded independently in the journal, and RemoveJunctionLink is a no-op unless the path is an
        // actual reparse point, so it can never recurse into or delete a real directory.
        if (!string.IsNullOrWhiteSpace(junctionRoot))
        {
            WorktreeJunction.RemoveJunctionLink(junctionRoot);
        }
    }

    /// <summary>
    /// Read the recorded Windows short-junction worktree root (#383) from <c>state/run.json</c> without
    /// loading the whole plan. Null when the journal is absent/unreadable or the field was never written (a
    /// run that used the real root, a non-Windows run). Best-effort — a corrupt journal must never abort
    /// <c>--fresh</c>.
    /// </summary>
    private static string? TryReadRecordedJunctionRoot(string planDirectory)
    {
        string journalPath = RunJournal.PathFor(planDirectory);
        if (!File.Exists(journalPath))
        {
            return null;
        }

        try
        {
            return JournalReader.Read(journalPath).WorktreeJunctionRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Windows-safe (issue #109): the logs/ and captured/ trees never hold git objects today, but
    // the --fresh worktree-root wipe can; route every reset delete through SafeDelete so a future
    // git-bearing path (or a read-only file dropped by a tool) cannot abort the reset.
    private static void DeleteDirectoryIfExists(string path) => SafeDelete.DeleteDirectory(path);
}
