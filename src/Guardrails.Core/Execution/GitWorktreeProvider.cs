using System.Diagnostics;
using Guardrails.Core.Io;

namespace Guardrails.Core.Execution;

/// <summary>
/// Real git worktree lifecycle for plan 08 M2. Creates and manages git worktrees for
/// parallel plan execution using actual git operations.
/// </summary>
/// <remarks>
/// One instance per run. <see cref="CreateIntegration"/> must be called before any
/// topology methods that need the run id (<see cref="ForkFromTip"/>).
/// </remarks>
public sealed class GitWorktreeProvider : IWorktreeProvider
{
    private readonly string _repoPath;
    private readonly string _worktreeRoot;
    private IntegrationHandle? _integration;

    // Saved before a non-FF --no-commit merge so RollbackMerge can reset --hard to this sha.
    // Safe as a field because Integrate is serialized by the Scheduler's _integrationLock.
    private string _preMergeIntegHead = "";

    public GitWorktreeProvider(string repoPath, string worktreeRoot)
    {
        _repoPath = repoPath;
        _worktreeRoot = worktreeRoot;
    }

    /// <inheritdoc />
    public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct)
    {
        // Defense in depth (issue #160): a plan-name component that is empty/whitespace would build
        // the invalid branch name "guardrails/" and let git reject it with a raw exit-128 stack
        // trace. Fail with a clear, diagnosed message instead so the CLI's honest-halt path can
        // render it. The CLI normalizes a trailing path separator upstream (FolderArgument), so
        // reaching here empty signals a genuinely unnameable plan folder.
        if (string.IsNullOrWhiteSpace(planName))
        {
            throw new InvalidOperationException(
                $"could not derive a plan name from '{planName}' — the plan folder has no usable name component.");
        }

        string originalBranch = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        string originalHead = Git("rev-parse", "HEAD").Trim();
        string planBranch = $"guardrails/{planName}";

        // B1_1/F1: CreateIntegration must be IDEMPOTENT for an existing plan branch so a resumed
        // run (after a journal loss/reset) attaches to the durable plan branch instead of throwing
        // on `git branch <existing>`. Create the plan branch only when it does not already exist.
        if (!BranchExists(planBranch))
        {
            Git("branch", planBranch);
        }

        // Reuse an integration worktree already checked out on the plan branch (a prior run's,
        // surviving the journal reset) — git refuses to check the same branch out twice, so a
        // resume must adopt the existing checkout rather than add a second one.
        string integPath = WorktreeForBranch(_repoPath, planBranch)
            ?? AddIntegrationWorktree(runId, planBranch);

        _integration = new IntegrationHandle
        {
            IntegrationWorktreePath = integPath,
            PlanBranchName = planBranch,
            OriginalBranch = originalBranch,
            OriginalHeadSha = originalHead,
            RunId = runId
        };
        return _integration;
    }

    /// <inheritdoc />
    public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct)
    {
        string planHead = Git("rev-parse", integ.PlanBranchName).Trim();
        string segBranch = $"guardrails/{integ.RunId}/{taskId}/attempt-{attempt}";
        string segPath = Path.Combine(_worktreeRoot, integ.RunId, taskId, $"attempt-{attempt}");
        Directory.CreateDirectory(Path.GetDirectoryName(segPath)!);
        Git("worktree", "add", "-b", segBranch, segPath, planHead);

        return new WorktreeHandle
        {
            WorktreePath = segPath,
            SegmentBranchName = segBranch,
            TaskBase = planHead,
            RecordedCommitSha = "",
            PlanBranchHead = planHead,
            TaskId = taskId
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// No git operations — the same physical worktree is reused. The new handle's
    /// <c>TaskBase</c> is set to the upstream's <c>RecordedCommitSha</c> so a retry
    /// can reset to just before this task's WIP (W-2 invariant).
    /// </remarks>
    public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
        new()
        {
            WorktreePath = upstreamSegment.WorktreePath,
            SegmentBranchName = upstreamSegment.SegmentBranchName,
            TaskBase = upstreamSegment.RecordedCommitSha,
            RecordedCommitSha = upstreamSegment.RecordedCommitSha,
            PlanBranchHead = upstreamSegment.PlanBranchHead,
            // The inheritor's commit must carry ITS OWN task id as the integration trailer (resume
            // truth, §7) — not the producer's segment-branch name. Integrate falls back to the
            // branch name only when TaskId is empty, so this must be set on the reused handle.
            TaskId = taskId
        };

    /// <inheritdoc />
    /// <remarks>
    /// W-2 gate: forks from the producer's RECORDED commit sha, not the live tip of the
    /// segment branch (which a linear inherit-one successor may have already advanced).
    /// </remarks>
    public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt)
    {
        string runId = _integration?.RunId
            ?? throw new InvalidOperationException("CreateIntegration must be called before ForkFromTip");
        string forkBranch = $"guardrails/{runId}/fork/{taskId}/attempt-{attempt}";
        string forkPath = Path.Combine(_worktreeRoot, runId, "fork", taskId, $"attempt-{attempt}");
        Directory.CreateDirectory(Path.GetDirectoryName(forkPath)!);
        Git("worktree", "add", "-b", forkBranch, forkPath, producerRecordedSha);

        return new WorktreeHandle
        {
            WorktreePath = forkPath,
            SegmentBranchName = forkBranch,
            TaskBase = producerRecordedSha,
            RecordedCommitSha = producerRecordedSha,
            PlanBranchHead = producerRecordedSha,
            // Carry the fork's own task id so its integrated commit's trailer is the task id (§7),
            // not the fork-branch name (Integrate's empty-TaskId fallback).
            TaskId = taskId
        };
    }

    /// <inheritdoc />
    public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
    {
        string integPath = integ.IntegrationWorktreePath;
        string segBranch = segment.SegmentBranchName;
        string taskId = string.IsNullOrEmpty(segment.TaskId) ? segBranch : segment.TaskId;

        // Stage and commit all changes in the segment (--allow-empty for tasks that only write
        // to GUARDRAILS_STATE_OUT with no file changes in the working tree).
        // --no-verify (issue #149): this is an INTERNAL plumbing commit in a throwaway segment
        // worktree — machine bookkeeping, never the user's deliverable. A global user git hook
        // (e.g. GitGuardian's pre-commit, which fired offline and crashed the run in the incident)
        // must NOT gate it. User hooks run only on the user-facing merge (MergePlanBranchIntoUserBranch).
        GitIn(segment.WorktreePath, "add", "-A");
        string commitMsg = $"Guardrails-Task: {taskId}\nGuardrails-Run: {integ.RunId}";
        GitIn(segment.WorktreePath, "commit", "--no-verify", "--allow-empty", "-m", commitMsg);

        // C2: capture the segment's commit sha so a downstream fan-out ForkFromTip forks off the
        // producer's recorded sha (W-2), never a live rev-parse of an inheritor-advanced branch.
        // The segment commit is a real, reachable object regardless of the FF/non-FF outcome below.
        segment.RecordedCommitSha = GitIn(segment.WorktreePath, "rev-parse", "HEAD").Trim();

        // Try fast-forward merge into the integration worktree.
        try
        {
            GitIn(integPath, "merge", "--ff-only", segBranch);
            return IntegrationResult.FastForward;
        }
        catch (InvalidOperationException)
        {
            // FF-only failed; fall through to non-FF merge.
        }

        // Non-FF: save the integration HEAD for potential rollback, then do a --no-commit merge
        // so the Scheduler can run re-verify on the merged bytes before committing.
        _preMergeIntegHead = GitIn(integPath, "rev-parse", "HEAD").Trim();
        var (_, mergeExit) = TryGitIn(integPath, "merge", "--no-commit", "--no-ff", segBranch);
        if (mergeExit == 0) return IntegrationResult.Merged;

        // Non-zero exit: check whether MERGE_HEAD exists (= conflict) vs some other git error.
        var (_, mergeHeadExit) = TryGitIn(integPath, "rev-parse", "MERGE_HEAD");
        if (mergeHeadExit == 0) return IntegrationResult.Conflict;

        throw new InvalidOperationException(
            $"git merge --no-commit --no-ff {segBranch} (in {integPath}) failed unexpectedly.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// B2: commit the staged (<c>merge --no-commit</c>) union in the integration worktree as a
    /// merge commit carrying the <c>Guardrails-Task:</c>/<c>Guardrails-Run:</c> trailers, so the
    /// settled task leaves a first-parent trailer on the plan branch exactly like the FF path.
    /// Called by the Scheduler only after the union re-verify passes. Clears the rollback anchor.
    /// </remarks>
    public string CommitStagedMerge(IntegrationHandle integ, string taskId, CancellationToken ct)
    {
        string commitMsg = $"Guardrails-Task: {taskId}\nGuardrails-Run: {integ.RunId}";
        // --no-verify (issue #149): the union merge commit in the harness-owned integration worktree
        // is an INTERNAL plumbing commit (machine bookkeeping on the plan branch), not the user's
        // deliverable — user git hooks must NOT gate it. They run only on the user-facing merge.
        GitIn(integ.IntegrationWorktreePath, "commit", "--no-verify", "-m", commitMsg);
        _preMergeIntegHead = "";
        return GitIn(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();
    }

    /// <inheritdoc />
    public void RollbackMerge(IntegrationHandle integ, CancellationToken ct)
    {
        // Reset the integration worktree to the pre-merge HEAD, clearing the staged merge state.
        if (!string.IsNullOrEmpty(_preMergeIntegHead))
        {
            GitIn(integ.IntegrationWorktreePath, "reset", "--hard", _preMergeIntegHead);
            _preMergeIntegHead = "";
        }
    }

    /// <inheritdoc />
    public void Discard(WorktreeHandle handle)
    {
        Git("worktree", "remove", "--force", handle.WorktreePath);

        // Issue #109: `git worktree remove --force` handles git's own bookkeeping, but on Windows
        // it can leave the directory on disk when a read-only loose object refuses deletion
        // (Access Denied). Sweep any surviving tree with the read-only-clearing SafeDelete so a
        // discarded segment never lingers.
        SafeDelete.DeleteDirectory(handle.WorktreePath);
    }

    /// <inheritdoc />
    public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ)
    {
        Git("worktree", "prune");
    }

    /// <inheritdoc />
    public void PruneSalvageRefs(string taskId) => PruneSalvageRefs(_repoPath, taskId);

    /// <inheritdoc />
    public string? LastMergeOnSuccessDetail { get; private set; }

    /// <inheritdoc />
    public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct)
    {
        // Reset any rejection detail captured by a prior call (this provider is run-scoped, but a
        // resumed run could call the merge more than once over its lifetime).
        LastMergeOnSuccessDetail = null;

        // F4: never run git over uncommitted user work. A dirty working tree refuses the merge —
        // even an ff-only merge that adds only new files (no textual conflict with the dirty paths)
        // would silently interleave the user's WIP with the plan's output. Halt to needs-human.
        if (IsWorkingTreeDirty())
        {
            return MergeOnSuccessResult.DirtyWorkingTree;
        }

        // Try fast-forward first — the common case when the user's branch didn't advance.
        // CAVEAT (issue #149, intended): a fast-forward creates NO commit, so no commit hook fires
        // here — identical to a manual `git merge --ff-only`. The user's hooks (GitGuardian/lint)
        // therefore run only on the non-FF MERGE COMMIT below, never on the FF path.
        try
        {
            Git("merge", "--ff-only", integ.PlanBranchName);
            return MergeOnSuccessResult.FastForwarded;
        }
        catch (InvalidOperationException)
        {
            // FF failed: user branch advanced mid-run. AI-merge is withheld (SSOT §5.3).
        }

        // Attempt a real merge to detect whether a conflict actually exists.
        try
        {
            Git("merge", "--no-commit", integ.PlanBranchName);
        }
        catch (InvalidOperationException)
        {
            // Conflict detected — abort cleanly and leave the user's branch untouched.
            try { Git("merge", "--abort"); } catch (InvalidOperationException) { /* already clean */ }
            return MergeOnSuccessResult.Conflict;
        }

        // No conflict: commit the merge. This is the USER-FACING commit on the user's REAL branch,
        // so it KEEPS the user's git hooks (issue #149 — the product owner's explicit decision):
        // GitGuardian/lint SHOULD run here exactly like a manual `git merge`. A non-throwing call
        // captures the hook's exit code AND stderr (the existing TryGitIn discards stderr).
        var (_, stderr, exit) = TryGitInWithStderr(_repoPath, "commit", "--no-edit");
        if (exit != 0)
        {
            // The user's hook rejected the merge commit (e.g. GitGuardian found a secret, or ran
            // offline and failed — issues #149/#150). Abort the staged merge (best-effort) so the
            // user's branch is left CLEAN at its original HEAD; leave the plan branch intact. This
            // is a graceful halt: the work all passed and is durable on the plan branch.
            try { Git("merge", "--abort"); } catch (InvalidOperationException) { /* already clean */ }
            LastMergeOnSuccessDetail = stderr.Trim();
            return MergeOnSuccessResult.HookRejected;
        }

        return MergeOnSuccessResult.Merged;
    }

    /// <summary>
    /// F3 / <c>--fresh</c> + crash recovery: delete every stale SEGMENT/fork branch
    /// (<c>guardrails/&lt;runId&gt;/&lt;task&gt;/attempt-N</c> and <c>guardrails/&lt;runId&gt;/fork/…</c>)
    /// in the repo at <paramref name="repoPath"/>, plus any of their registered worktrees rooted
    /// under <paramref name="worktreeRoot"/>. A plan branch (<c>guardrails/&lt;plan-name&gt;</c>,
    /// exactly two path components) is NOT a segment branch and is left untouched. Best-effort and
    /// static so <see cref="State.RunReset"/> can prune without constructing a run-scoped provider;
    /// any individual git failure is swallowed so a partial repo never aborts a fresh reset.
    /// </summary>
    public static void PruneStaleSegmentBranches(string repoPath, string worktreeRoot)
    {
        string[] segmentBranches;
        try
        {
            // List ALL local branches without a glob (avoids git wildmatch path-separator
            // ambiguity) and filter in C#: a segment/fork branch is guardrails/<runId>/<rest…>
            // (≥ 3 '/'-separated components); a plan branch guardrails/<plan-name> has exactly 2
            // and is preserved.
            segmentBranches = GitIn(repoPath, "for-each-ref", "--format=%(refname:short)", "refs/heads")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.StartsWith("guardrails/", StringComparison.Ordinal) && s.Count(c => c == '/') >= 2)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            // Not a git repo, or git unavailable — nothing to prune.
            return;
        }

        if (segmentBranches.Length == 0) return;

        // Remove any registered worktrees first (a checked-out branch cannot be deleted) by
        // reconstructing the path from the branch convention guardrails/<rest> → <worktreeRoot>/<rest>.
        foreach (string branch in segmentBranches)
        {
            string[] relParts = branch["guardrails/".Length..].Split('/');
            string worktreePath = Path.Combine([worktreeRoot, .. relParts]);
            if (Directory.Exists(worktreePath))
            {
                try { GitIn(repoPath, "worktree", "remove", "--force", worktreePath); }
                catch (InvalidOperationException) { /* not a registered worktree; pruned below */ }
                // Issue #109: clear any tree git left on disk (Windows read-only loose objects).
                SafeDelete.DeleteDirectory(worktreePath);
            }
        }

        try { GitIn(repoPath, "worktree", "prune"); } catch (InvalidOperationException) { /* best-effort */ }

        foreach (string branch in segmentBranches)
        {
            try { GitIn(repoPath, "branch", "-D", branch); } catch (InvalidOperationException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Issue #274 (part B): fully tear down THIS plan's durable cross-run resume record so a
    /// <c>--fresh</c> run or a full <c>reset</c> genuinely starts over. The plan branch
    /// <c>guardrails/&lt;planName&gt;</c> and its integration worktree carry the <c>Guardrails-Task:</c>
    /// trailers that <see cref="ReconcileFromPlanBranch(IntegrationHandle)"/>'s resume pre-pass reads to
    /// skip an already-succeeded task. <see cref="PruneStaleSegmentBranches"/> DELIBERATELY preserves the
    /// plan branch (a 2-component <c>guardrails/&lt;plan&gt;</c> is not a segment branch) — correct for a
    /// normal resume, but it meant neither <c>--fresh</c> nor <c>reset -y</c> ever cleared it, so a
    /// "fresh" run silently reused the stale trailers and re-skipped edited tasks. This removes the plan
    /// branch and its integration worktree so a fresh reset really is a clean slate:
    /// <list type="number">
    /// <item>remove the integration worktree checked out on the plan branch — located git-authoritatively
    /// via <see cref="WorktreeForBranch"/> (runId-agnostic), then swept off disk with the SAME pattern
    /// <see cref="Discard"/>/<see cref="PruneStaleSegmentBranches"/> use (<c>git worktree remove --force</c>
    /// + issue #109 <see cref="Io.SafeDelete"/> for Windows read-only loose objects);</item>
    /// <item><c>git worktree prune</c>;</item>
    /// <item><c>git branch -D guardrails/&lt;planName&gt;</c>;</item>
    /// <item>a final disk sweep of any <c>_integration</c> directory a crash orphaned under
    /// <paramref name="worktreeRoot"/> WITHOUT a live git registration — the manual <c>rm -rf</c> hazard
    /// #274 was reported on (the root is this plan's own harness-owned tree, safe to sweep).</item>
    /// </list>
    /// Static (so <see cref="State.RunReset"/> can tear down without a run-scoped provider) and every
    /// step best-effort/swallowed, mirroring <see cref="PruneStaleSegmentBranches"/>'s posture exactly: a
    /// missing branch/worktree is a no-op (teardown is idempotent) and a partial/non-git repo never
    /// aborts the reset. MUST be called ONLY on the explicit fresh/full-reset path — a normal resume must
    /// preserve the plan branch and resume against it.
    /// </summary>
    public static void TeardownPlanBranch(string repoPath, string worktreeRoot, string planName)
    {
        // An empty/whitespace plan name would target the invalid branch "guardrails/" — nothing to do
        // (mirrors CreateIntegration's #160 guard, on the teardown side).
        if (string.IsNullOrWhiteSpace(planName)) return;
        string planBranch = $"guardrails/{planName}";

        // Remove the integration worktree checked out on the plan branch first — a checked-out branch
        // cannot be deleted. Located via the git-authoritative listing (not a path reconstruction: the
        // integration path bakes in the runId, unknown here).
        try
        {
            if (WorktreeForBranch(repoPath, planBranch) is { } integWorktree && Directory.Exists(integWorktree))
            {
                try { GitIn(repoPath, "worktree", "remove", "--force", integWorktree); }
                catch (InvalidOperationException) { /* not a registered worktree; pruned below */ }
                // Issue #109: clear any tree git left on disk (Windows read-only loose objects).
                SafeDelete.DeleteDirectory(integWorktree);
            }
        }
        catch (InvalidOperationException)
        {
            // Not a git repo, or git unavailable — nothing to tear down.
            return;
        }

        try { GitIn(repoPath, "worktree", "prune"); } catch (InvalidOperationException) { /* best-effort */ }
        try { GitIn(repoPath, "branch", "-D", planBranch); } catch (InvalidOperationException) { /* best-effort */ }

        // Belt-and-suspenders (issue #274): sweep any _integration directory a crash orphaned under the
        // plan's worktree root without a live git registration — WorktreeForBranch only finds REGISTERED
        // worktrees, so an unregistered leftover would survive step 1. This is the manual `rm -rf` users
        // had to run by hand. Materialize the enumeration before deleting (deleting during a live
        // AllDirectories walk throws); the whole sweep is best-effort.
        if (Directory.Exists(worktreeRoot))
        {
            try
            {
                foreach (string integDir in Directory
                    .EnumerateDirectories(worktreeRoot, "_integration", SearchOption.AllDirectories)
                    .ToList())
                {
                    SafeDelete.DeleteDirectory(integDir);
                }
            }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// W-1 protection (a): delete all <c>guardrails/&lt;runId&gt;/*</c> branches (and their
    /// worktrees) before any resume logic reads trailers, so stale segment refs cannot
    /// be mistaken for integrated work. The integration branch (<c>guardrails/&lt;planName&gt;</c>)
    /// does not match the prefix and is left untouched.
    /// </summary>
    public void PruneStaleRunBranches(string runId, IntegrationHandle integ)
    {
        string branchPrefix = $"guardrails/{runId}/";

        // List ALL local branches without a glob pattern (avoids cross-path-separator ambiguity
        // with git's wildmatch) and filter by the run-scoped prefix in C#.
        string[] staleBranches = Git("for-each-ref", "--format=%(refname:short)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.StartsWith(branchPrefix, StringComparison.Ordinal))
            .ToArray();

        if (staleBranches.Length == 0) return;

        // Remove worktrees by reconstructing the path from the branch name convention:
        // guardrails/<runId>/<rest> → <_worktreeRoot>/<runId>/<rest>
        foreach (string branch in staleBranches)
        {
            string[] relParts = branch[branchPrefix.Length..].Split('/');
            string worktreePath = Path.Combine([_worktreeRoot, runId, .. relParts]);
            if (Directory.Exists(worktreePath))
            {
                try { Git("worktree", "remove", "--force", worktreePath); }
                catch (InvalidOperationException) { /* not a registered worktree; pruned below */ }
                // Issue #109: clear any tree git left on disk (Windows read-only loose objects).
                SafeDelete.DeleteDirectory(worktreePath);
            }
        }

        // Clean up any stale worktree registrations (path removed but git entry lingers).
        Git("worktree", "prune");

        foreach (string branch in staleBranches)
        {
            Git("branch", "-D", branch);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// B1_1/F1 resume reconciliation: walk the plan-branch first-parent history and collect EVERY
    /// task id bearing a <c>Guardrails-Task:</c> trailer reachable from the tip, regardless of which
    /// run committed it. The plan branch is the durable cross-run resume truth (only THIS plan's
    /// integrations land on it), so a task already integrated there must not be re-run after a
    /// journal loss/reset — even though a resumed run carries a fresh runId. Read-only.
    /// </remarks>
    public IReadOnlySet<string> ReconcileFromPlanBranch(IntegrationHandle integ) =>
        CollectTrailerTasks(integ.PlanBranchName, runIdFilter: null);

    /// <summary>
    /// W-1 protection (b): like <see cref="ReconcileFromPlanBranch(IntegrationHandle)"/> but counts
    /// only commits whose <c>Guardrails-Run:</c> matches <paramref name="runId"/> — used where a
    /// single run's own integrations must be isolated from earlier runs' trailers.
    /// </summary>
    public IReadOnlySet<string> ReconcileFromPlanBranch(IntegrationHandle integ, string runId) =>
        CollectTrailerTasks(integ.PlanBranchName, runId);

    /// <summary>
    /// Walk <paramref name="planBranch"/>'s first-parent history and collect task ids from
    /// <c>Guardrails-Task:</c> trailers. When <paramref name="runIdFilter"/> is non-null, only
    /// commits whose <c>Guardrails-Run:</c> matches are counted; when null, every trailer counts.
    /// </summary>
    private IReadOnlySet<string> CollectTrailerTasks(string planBranch, string? runIdFilter)
    {
        string log = Git("log", "--first-parent", "--format=%B", planBranch);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        string? currentTask = null;
        string? currentRun = null;

        void Flush()
        {
            if (currentTask != null && (runIdFilter is null || currentRun == runIdFilter))
                settled.Add(currentTask);
            currentTask = null;
            currentRun = null;
        }

        foreach (string rawLine in log.Split('\n'))
        {
            string line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("Guardrails-Task: ", StringComparison.Ordinal))
                currentTask = line["Guardrails-Task: ".Length..];
            else if (line.StartsWith("Guardrails-Run: ", StringComparison.Ordinal))
                currentRun = line["Guardrails-Run: ".Length..];
        }

        // Flush any trailing block (log output may not end with a blank line).
        Flush();
        return settled;
    }

    /// <summary>
    /// Reset the segment to <c>handle.TaskBase</c> (the upstream's recorded commit sha,
    /// captured at assignment — NOT the plan-branch <c>PlanBranchHead</c>) and clean
    /// untracked non-ignored files. Git-ignored build caches survive (<c>-fd</c> not
    /// <c>-fdx</c> — Decision 7: keeps warm artifacts so retries don't pay cold rebuilds).
    /// </summary>
    public void ResetForRetry(WorktreeHandle handle) =>
        ResetSegment(handle.WorktreePath, handle.TaskBase);

    /// <summary>
    /// Reset a segment worktree to <paramref name="taskBase"/> and clean untracked non-ignored
    /// files (Decision 7: <c>-fd</c>, not <c>-fdx</c>, so warm build caches survive). Static so
    /// the attempt loop in <see cref="TaskExecutor"/> can reset a retry segment without holding a
    /// provider reference (F2) — the same git operations the provider uses.
    /// </summary>
    public static void ResetSegment(string worktreePath, string taskBase)
    {
        GitIn(worktreePath, "reset", "--hard", taskBase);
        GitIn(worktreePath, "clean", "-fd");
    }

    /// <summary>
    /// Retry-salvage (issue #195): commit the segment's CURRENT working-tree state — including
    /// uncommitted writes — to <paramref name="refName"/> (e.g. <c>refs/guardrails/&lt;taskId&gt;/attempt-N</c>),
    /// WITHOUT touching the segment branch/HEAD, so the attempt's work survives the F2 rollback
    /// (<see cref="ResetSegment"/>) that runs immediately after for a <c>max-turns</c>/<c>output-cap</c>
    /// retry. Uses a throwaway index (<c>GIT_INDEX_FILE</c>) so the segment's real staged/unstaged state
    /// is never disturbed — this is purely a side-channel snapshot, not a real commit on the branch.
    /// <c>commit-tree</c> (unlike <c>git commit</c>) always succeeds even when the resulting tree is
    /// byte-identical to the parent's, so the ref always exists once called — "does a salvage ref exist
    /// for this attempt" is a simple ref lookup, never a conditional-commit gotcha. Static, mirroring
    /// <see cref="ResetSegment"/>, so the attempt loop can call it without a provider reference.
    /// </summary>
    public static void PreserveAttemptToRef(string worktreePath, string refName)
    {
        string tempIndex = Path.Combine(Path.GetTempPath(), $"gr-salvage-index-{Guid.NewGuid():N}");
        try
        {
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            GitInWithEnv(worktreePath, env, "add", "-A");
            string treeSha = GitInWithEnv(worktreePath, env, "write-tree").Trim();
            string parentSha = GitIn(worktreePath, "rev-parse", "HEAD").Trim();
            string commitSha = GitIn(
                worktreePath, "commit-tree", treeSha, "-p", parentSha, "-m",
                $"guardrails: salvage snapshot ({refName})").Trim();
            GitIn(worktreePath, "update-ref", refName, commitSha);
        }
        finally
        {
            if (File.Exists(tempIndex))
            {
                try { File.Delete(tempIndex); } catch (IOException) { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// A <c>git diff --stat</c> summary of <paramref name="refName"/> against <paramref name="taskBase"/>
    /// (issue #195) — what a salvaged attempt actually changed, for the next attempt's retry feedback.
    /// Returns an empty string (never throws) when the ref is missing or the diff otherwise fails, so a
    /// best-effort feedback composer degrades gracefully rather than crashing the retry loop.
    /// </summary>
    public static string DiffStatAgainstBase(string worktreePath, string taskBase, string refName)
    {
        try
        {
            return GitIn(worktreePath, "diff", "--stat", taskBase, refName).Trim();
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// Retry-salvage pruning (issue #195, deliverable 6): delete every preserved salvage ref for
    /// <paramref name="taskId"/> (<c>refs/guardrails/&lt;taskId&gt;/attempt-*</c>) — called on task
    /// settle-succeeded and on a full <c>--fresh</c> reset, alongside the existing stale-branch pruning,
    /// so salvage refs never accumulate unbounded in a long-lived repo. Best-effort: an individual
    /// failed delete never aborts the sweep.
    /// </summary>
    public static void PruneSalvageRefs(string repoPath, string taskId)
    {
        // NOTE: `git for-each-ref <pattern>` only matches a pattern that is EITHER a full ref name OR
        // ends at a '/' boundary (a directory prefix) OR carries an explicit '*' glob — a bare
        // "…/attempt-" prefix (no trailing '/' or '*') matches NOTHING, even though
        // "refs/guardrails/<taskId>/attempt-1" is textually prefixed by it. Passing the DIRECTORY
        // prefix "refs/guardrails/<taskId>" (no trailing dash/glob) matches every ref nested under it —
        // exactly the attempt-N refs this task ever preserved — without any glob-escaping concerns for
        // a taskId containing characters git would otherwise wildmatch specially.
        string prefix = $"refs/guardrails/{taskId}";
        string[] refs;
        try
        {
            refs = GitIn(repoPath, "for-each-ref", "--format=%(refname)", prefix)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (string r in refs)
        {
            try { GitIn(repoPath, "update-ref", "-d", r); }
            catch (InvalidOperationException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// Retry-salvage pruning (issue #195): delete EVERY preserved salvage ref in the repo
    /// (<c>refs/guardrails/*/attempt-*</c>, at least 3 path components after <c>refs/guardrails/</c>) —
    /// used by a full <c>--fresh</c> reset, which has no single task in scope. Best-effort, mirroring
    /// <see cref="PruneStaleSegmentBranches"/>'s swallow-everything discipline so a partial repo never
    /// aborts the reset.
    /// </summary>
    public static void PruneAllSalvageRefs(string repoPath)
    {
        string[] refs;
        try
        {
            refs = GitIn(repoPath, "for-each-ref", "--format=%(refname)", "refs/guardrails")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(r => r.StartsWith("refs/guardrails/", StringComparison.Ordinal)
                            && r.Contains("/attempt-", StringComparison.Ordinal))
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        foreach (string r in refs)
        {
            try { GitIn(repoPath, "update-ref", "-d", r); }
            catch (InvalidOperationException) { /* best-effort */ }
        }
    }

    /// <summary>
    /// True when the user's checkout (<c>_repoPath</c>) has uncommitted changes to TRACKED files
    /// — staged or unstaged (<c>git status --porcelain --untracked-files=no</c> non-empty). F4 /
    /// run-start pre-flight. Untracked files are EXCLUDED: the harness writes its own runtime state
    /// (<c>state/</c>, <c>logs/</c>) under the plan folder inside the repo, which would otherwise
    /// flag every real run as dirty; and an untracked file cannot be silently interleaved by a
    /// fast-forward (git errors on an untracked-file collision, handled by the merge path). The F4
    /// hazard is specifically a tracked modification (e.g. an unstaged edit to a committed file)
    /// that an ff-only merge would silently fold in alongside the plan's output.
    /// </summary>
    private bool IsWorkingTreeDirty() =>
        Git("status", "--porcelain", "--untracked-files=no").Trim().Length > 0;

    /// <summary>True when local branch <paramref name="branch"/> already exists (resume idempotency).</summary>
    private bool BranchExists(string branch)
    {
        var (_, exit) = TryGitIn(_repoPath, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        return exit == 0;
    }

    /// <summary>
    /// The absolute path of the worktree that already has <paramref name="branch"/> checked out in the
    /// repo at <paramref name="repoPath"/>, or null when no worktree holds it. Parses
    /// <c>git worktree list --porcelain</c> (its <c>worktree</c> + <c>branch refs/heads/&lt;name&gt;</c>
    /// record pairs). Static and shared so BOTH <see cref="CreateIntegration"/> (adopting an existing
    /// integration worktree on resume) and <see cref="TeardownPlanBranch"/> (removing it on a fresh
    /// reset) locate the plan branch's worktree through ONE parse of the git-authoritative listing —
    /// runId-agnostic, since the integration path bakes in the runId
    /// (<c>&lt;worktreeRoot&gt;/&lt;runId&gt;/_integration</c>), so a path reconstruction from the
    /// worktree root alone could not find it.
    /// </summary>
    private static string? WorktreeForBranch(string repoPath, string branch)
    {
        string listing = GitIn(repoPath, "worktree", "list", "--porcelain");
        string? currentPath = null;
        foreach (string rawLine in listing.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                currentPath = line["worktree ".Length..].Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                string refName = line["branch ".Length..].Trim();
                if (refName == $"refs/heads/{branch}" && currentPath is not null)
                    return currentPath;
            }
        }
        return null;
    }

    /// <summary>Add a fresh integration worktree on <paramref name="planBranch"/> and return its path.</summary>
    private string AddIntegrationWorktree(string runId, string planBranch)
    {
        string integPath = Path.Combine(_worktreeRoot, runId, "_integration");
        Directory.CreateDirectory(Path.GetDirectoryName(integPath)!);
        Git("worktree", "add", integPath, planBranch);
        return integPath;
    }

    private string Git(params string[] args) => GitIn(_repoPath, args);

    private static string GitIn(string workingDir, params string[] args) =>
        GitInWithEnv(workingDir, null, args);

    /// <summary>
    /// Like <see cref="GitIn"/> but overlays <paramref name="extraEnv"/> onto the child process's
    /// environment (issue #195: <c>PreserveAttemptToRef</c> needs <c>GIT_INDEX_FILE</c> to stage into a
    /// throwaway index without disturbing the segment's real index). Null/empty behaves identically to
    /// the plain <see cref="GitIn"/>.
    /// </summary>
    private static string GitInWithEnv(
        string workingDir, IReadOnlyDictionary<string, string>? extraEnv, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (extraEnv is not null)
        {
            foreach (KeyValuePair<string, string> kv in extraEnv)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        return stdout;
    }

    private static (string stdout, int exitCode) TryGitIn(string workingDir, params string[] args)
    {
        var (stdout, _, exitCode) = TryGitInWithStderr(workingDir, args);
        return (stdout, exitCode);
    }

    /// <summary>
    /// Like <see cref="TryGitIn"/> but RETURNS the captured stderr (issue #150): a non-throwing git
    /// call surfacing both the exit code and stderr so a hook rejection's message can be shown to the
    /// user. The plain <see cref="TryGitIn"/> reads and discards stderr; this variant returns it.
    /// </summary>
    private static (string stdout, string stderr, int exitCode) TryGitInWithStderr(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }
}
