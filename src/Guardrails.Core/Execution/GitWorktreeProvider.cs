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
    private readonly string _realRoot;
    private IntegrationHandle? _integration;

    // Saved before a non-FF --no-commit merge so RollbackMerge can reset --hard to this sha.
    // Safe as a field because Integrate is serialized by the Scheduler's _integrationLock.
    private string _preMergeIntegHead = "";

    /// <param name="repoPath">The user's workspace repo (git top-level).</param>
    /// <param name="worktreeRoot">The EFFECTIVE root segments/integration are built under — the short Windows junction when one is in effect (issue #383), else the real root.</param>
    /// <param name="realRoot">
    /// The REAL (git-canonical) worktree root (issue #419). git canonicalizes the junction away in its own
    /// registrations, so <see cref="WorktreeForBranch"/> returns the real path even when this run launches
    /// under the junction. On RESUME the reused integration worktree is adopted via that real path — so it
    /// must be RE-ALIASED onto <paramref name="worktreeRoot"/> (<see cref="AliasForLaunch"/>) to keep the
    /// terminal-gate / plan-guardrail / union-reverify cwd short. Null (or equal to
    /// <paramref name="worktreeRoot"/>) ⇒ no junction ⇒ aliasing is the identity.
    /// </param>
    public GitWorktreeProvider(string repoPath, string worktreeRoot, string? realRoot = null)
    {
        _repoPath = repoPath;
        _worktreeRoot = worktreeRoot;
        _realRoot = string.IsNullOrWhiteSpace(realRoot) ? worktreeRoot : realRoot;
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
        // resume must adopt the existing checkout rather than add a second one. git canonicalized the
        // junction away when the ORIGINAL run created it, so WorktreeForBranch returns the REAL (long) path
        // — RE-ALIAS it onto this run's junction (issue #419) so the terminal-gate / union-reverify cwd
        // stays short on resume. A fresh AddIntegrationWorktree already builds under _worktreeRoot (the
        // junction), so it needs no aliasing.
        string integPath = WorktreeForBranch(_repoPath, planBranch) is { } existing
            ? AliasForLaunch(existing)
            : AddIntegrationWorktree(runId, planBranch);

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
        // Staging EXCLUDES the reconstructable dep/build set (SSOT §5.3(D), issue #280): a guardrail's
        // `npm ci` node_modules (at any depth) and the harness's own .guardrails-staging/ /
        // .guardrails-agent-io/ scaffolding can NEVER be captured into the segment commit, regardless
        // of .gitignore timing or whether the task declared a writeScope. Stage-exclusion only — the
        // dirs stay on disk (warm-cache #255).
        // --no-verify (issue #149): this is an INTERNAL plumbing commit in a throwaway segment
        // worktree — machine bookkeeping, never the user's deliverable. A global user git hook
        // (e.g. GitGuardian's pre-commit, which fired offline and crashed the run in the incident)
        // must NOT gate it. User hooks run only on the user-facing merge (MergePlanBranchIntoUserBranch).
        SegmentStaging.StageAll(segment.WorktreePath);
        string commitMsg = TrailerMessage(taskId, integ.RunId, segment.DefinitionHash);
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
    /// Issue #451: <c>git diff --name-only --diff-filter=U</c> in the integration worktree. Read-only
    /// and non-throwing — a git failure here reports "no unmerged paths" rather than manufacturing a
    /// second fault on a path whose whole purpose is to keep a known state out of the fault handler.
    /// </remarks>
    public IReadOnlyList<string> UnmergedPaths(IntegrationHandle integ)
    {
        var (stdout, exitCode) = TryGitIn(
            integ.IntegrationWorktreePath, "diff", "--name-only", "--diff-filter=U");
        if (exitCode != 0)
        {
            return [];
        }

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// B2: commit the staged (<c>merge --no-commit</c>) union in the integration worktree as a
    /// merge commit carrying the <c>Guardrails-Task:</c>/<c>Guardrails-Run:</c> trailers, so the
    /// settled task leaves a first-parent trailer on the plan branch exactly like the FF path.
    /// Called by the Scheduler only after the union re-verify passes. Clears the rollback anchor.
    /// </remarks>
    public string CommitStagedMerge(
        IntegrationHandle integ, string taskId, CancellationToken ct, string? definitionHash = null)
    {
        string commitMsg = TrailerMessage(taskId, integ.RunId, definitionHash);
        // --no-verify (issue #149): the union merge commit in the harness-owned integration worktree
        // is an INTERNAL plumbing commit (machine bookkeeping on the plan branch), not the user's
        // deliverable — user git hooks must NOT gate it. They run only on the user-facing merge.
        GitIn(integ.IntegrationWorktreePath, "commit", "--no-verify", "-m", commitMsg);
        _preMergeIntegHead = "";
        return GitIn(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Decision E (SSOT §14.5): commit an EMPTY marker in the integration worktree on the plan branch,
    /// carrying the <c>Guardrails-Wave:</c> / <c>Guardrails-Wave-Hash:</c> / <c>Guardrails-Run:</c>
    /// trailer triple. <c>--no-verify</c> for the same reason the task integration commit uses it (issue
    /// #149): an INTERNAL plumbing commit on the harness-owned plan branch, never gated by a user git hook.
    /// <c>--allow-empty</c> because the marker adds no tree change — it is a durable anchor, not work.
    /// </remarks>
    public string CommitWaveMarker(IntegrationHandle integ, string waveDir, string waveHash, CancellationToken ct)
    {
        string msg = $"Guardrails-Wave: {waveDir}\nGuardrails-Wave-Hash: {waveHash}\nGuardrails-Run: {integ.RunId}";
        GitIn(integ.IntegrationWorktreePath, "commit", "--no-verify", "--allow-empty", "-m", msg);
        return GitIn(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PlanBranchWaveRecord> ReconcileWavesFromPlanBranch(IntegrationHandle integ) =>
        ParseWaveMarkers(Git("log", "--first-parent", TrailerLogFormat, integ.PlanBranchName));

    /// <summary>
    /// Read-only query of the plan branch's <c>Guardrails-Wave:</c> marker commits (SSOT §14.5) WITHOUT
    /// creating an integration worktree — for the wave-scoped reset / dry-run. Degrades to the EMPTY map
    /// (never throws) when the workspace is not a git repo, git is unavailable, or the plan branch is
    /// absent. Static so the CLI needs no run-scoped provider.
    /// </summary>
    public static IReadOnlyDictionary<string, PlanBranchWaveRecord> ReadPlanBranchWaveMarkers(
        string repoPath, string planName)
    {
        var empty = new Dictionary<string, PlanBranchWaveRecord>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(planName))
        {
            return empty;
        }

        try
        {
            var (log, exit) = TryGitIn(repoPath, "log", "--first-parent", TrailerLogFormat, $"guardrails/{planName}");
            return exit == 0 ? ParseWaveMarkers(log) : empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return empty;
        }
    }

    /// <summary>
    /// Parse a <see cref="TrailerLogFormat"/> first-parent log into per-wave <see cref="PlanBranchWaveRecord"/>s
    /// (marker sha + <c>Guardrails-Wave-Hash:</c>). Newest-first, so the FIRST marker seen for a wave dir
    /// wins (its most recent completion). Shares the record/unit separators + last-trailer-block discipline
    /// with the task-trailer parser so a body's own blank lines can never split a record.
    /// </summary>
    private static IReadOnlyDictionary<string, PlanBranchWaveRecord> ParseWaveMarkers(string log)
    {
        char recordSep = Hashing.HashText.RecordSeparator;
        char unitSep = Hashing.HashText.UnitSeparator;
        var waves = new Dictionary<string, PlanBranchWaveRecord>(StringComparer.Ordinal);

        foreach (string record in log.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = record.IndexOf(unitSep);
            if (split < 0)
            {
                continue;
            }

            string commitSha = record[..split].Trim();
            string body = record[(split + 1)..];

            string? waveDir = null;
            string? waveHash = null;
            foreach (string line in LastTrailerBlockLines(body))
            {
                if (line.StartsWith("Guardrails-Wave: ", StringComparison.Ordinal))
                    waveDir = line["Guardrails-Wave: ".Length..];
                else if (line.StartsWith("Guardrails-Wave-Hash: ", StringComparison.Ordinal))
                    waveHash = line["Guardrails-Wave-Hash: ".Length..];
            }

            if (waveDir is null)
            {
                continue;
            }

            if (!waves.ContainsKey(waveDir))
            {
                waves[waveDir] = new PlanBranchWaveRecord(commitSha, waveHash);
            }
        }

        return waves;
    }

    /// <summary>
    /// The integration commit message carrying the parseable resume trailers
    /// (<c>Guardrails-Task:</c> / <c>Guardrails-Run:</c>), plus the OPTIONAL third
    /// <c>Guardrails-Task-Hash:</c> line (issue #274 Part A, §7.2) when
    /// <paramref name="definitionHash"/> is available — written identically on the FF'd segment commit
    /// and the non-FF merge commit, omitted when null so old commits and fake providers stay
    /// backward-compatible.
    /// </summary>
    private static string TrailerMessage(string taskId, string runId, string? definitionHash)
    {
        string msg = $"Guardrails-Task: {taskId}\nGuardrails-Run: {runId}";
        if (!string.IsNullOrEmpty(definitionHash))
        {
            msg += $"\nGuardrails-Task-Hash: {definitionHash}";
        }

        return msg;
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

        // F4, NARROWED TO A REAL INTERSECTION (issue #448): never run git over uncommitted user work
        // THIS MERGE WOULD OVERWRITE. The pre-#448 gate refused on ANY tracked modification anywhere
        // in the repo, which is strictly coarser than git's own rule — git refuses a merge only when
        // it must update a locally-modified file. The incident: a wholly-green WAVED run regenerated
        // its own tracked per-wave `diagram.md`/`diagram.html` mid-run, then refused its own delivery
        // on that self-inflicted dirt even though the merge touched only `src/`, `tests/` and one doc
        // (`git merge-tree` predicted zero conflicts; the manual merge had none).
        if (BlockingDirtyPaths(integ.PlanBranchName) is { } blocking)
        {
            // Part B of #448: NAME the offending paths so the user is not sent to `git status` to
            // discover what blocked a green run's delivery. Null when nothing could be enumerated
            // (git itself failed) — the CLI then falls back to the generic wording.
            LastMergeOnSuccessDetail = blocking.Count > 0 ? string.Join("\n", blocking) : null;
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
    /// Issue #407 (junction lifecycle): remove EVERY git worktree registered in <paramref name="repoPath"/>
    /// whose path resolves UNDER <paramref name="worktreeRoot"/>, prune the dangling registrations (only
    /// when something WAS unregistered — issue #450: a prune has nothing to do otherwise), then delete the
    /// root directory itself. Unlike <see cref="TeardownPlanBranch"/> it NEVER deletes a branch
    /// (plan OR segment): a green-delivered run's plan branch must survive on the user's repo, and a foreign
    /// leak's branches are none of our business — this reclaims the on-disk worktree TREE + its git
    /// registrations ONLY. Used by BOTH the terminal-completion cleanup (A) and the startup GC (B) via
    /// <see cref="WorktreeReclaim"/>. Static (no run-scoped provider needed) and best-effort — every git
    /// step is swallowed so a partial/non-git/foreign repo never aborts the run. A worktree whose
    /// registration lives in a DIFFERENT repo is not found by the list here, so only its DIRECTORY is swept;
    /// that repo's own <c>git worktree prune</c> later clears the harmless dangling entry.
    /// </summary>
    public static void RemoveWorktreeRoot(string repoPath, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot)) return;

        if (RemoveWorktreeRoot(repoPath, worktreeRoot, RegisteredWorktreePaths(repoPath)))
        {
            PruneWorktrees(repoPath);
        }
    }

    /// <summary>
    /// The bulk-sweep form of <see cref="RemoveWorktreeRoot(string, string)"/> (issue #450): identical
    /// removal, but the caller supplies a listing of <paramref name="repoPath"/>'s registered worktrees
    /// fetched ONCE for the whole sweep, and the caller prunes ONCE at the end (this returns whether a
    /// prune is warranted). Sweeping a backlog through the single-root form spawned three git processes
    /// PER ROOT — <c>worktree list</c>, <c>worktree remove</c>, <c>worktree prune</c> — which is what
    /// made a ~1000-root sweep take minutes even though the roots were foreign fixture debris this repo
    /// had never registered. Here a root with nothing registered under it costs ZERO git processes: just
    /// the directory delete.
    /// </summary>
    /// <param name="registeredWorktrees">
    /// The repo's registered worktree paths, from <see cref="RegisteredWorktreePaths"/>. Pre-fetched, so
    /// it can be moments stale — harmless: a worktree registered since the fetch belongs to a run that
    /// started since the sweep began, and such a root is fresh/live-locked and never reaches this call.
    /// </param>
    /// <returns>
    /// <c>true</c> when at least one registered worktree was unregistered, so the caller should call
    /// <see cref="PruneWorktrees"/> once when the sweep finishes. <c>false</c> when the root held no
    /// registration of this repo (nothing for a prune to do — the #450 cheap path).
    /// </returns>
    public static bool RemoveWorktreeRoot(
        string repoPath, string worktreeRoot, IReadOnlyList<string> registeredWorktrees)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot)) return false;

        // Unregister any worktrees under the root FIRST — a checked-out worktree cannot simply be rm-rf'd
        // without leaving git's bookkeeping inconsistent. Located git-authoritatively (git stores the REAL
        // path even when a #383 junction aliased it during the run), so this keys on the real root.
        //
        // The comparison must be SYMLINK-RESOLVED (issue #452), not merely full-path: `p` is what git
        // printed and git stores the resolved real path, while `worktreeRoot` descends from
        // Path.GetTempPath() — which on macOS is the UNRESOLVED /var/folders/… of a /var → /private/var
        // symlink. Lexically the two never matched there, so NOTHING was ever unregistered on macOS,
        // `unregisteredAny` stayed false, the caller's single batched prune never ran, and every sweep
        // deleted the tree while leaving git's record of it behind — an accumulating stale registration.
        // (The pre-#450 unconditional prune swept that record away by accident, which is what masked it.)
        bool unregisteredAny = false;
        try
        {
            foreach (string wt in registeredWorktrees.Where(p => RealPath.IsUnder(p, worktreeRoot)))
            {
                try { GitIn(repoPath, "worktree", "remove", "--force", wt); }
                catch (InvalidOperationException) { /* not removable / already gone; pruned by the caller */ }
                // Issue #109: clear any tree git left on disk (Windows read-only loose objects).
                SafeDelete.DeleteDirectory(wt);
                unregisteredAny = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not a git repo, or git unavailable / off PATH — still sweep the directory below (harness-owned).
        }

        SafeDelete.DeleteDirectory(worktreeRoot);
        return unregisteredAny;
    }

    /// <summary>
    /// <c>git worktree prune</c> in <paramref name="repoPath"/> — drop the admin records of worktrees whose
    /// directories are gone. Best-effort (a non-repo / absent git is swallowed). Issue #450: hoisted out of
    /// the per-root removal so a bulk sweep prunes ONCE instead of once per reclaimed root.
    /// </summary>
    public static void PruneWorktrees(string repoPath)
    {
        try { GitIn(repoPath, "worktree", "prune"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort — a stale admin record is harmless and the next prune clears it.
        }
    }

    /// <summary>
    /// Every git worktree path registered in <paramref name="repoPath"/> (issue #450), from ONE
    /// <c>git worktree list --porcelain</c>. Includes the main worktree (the repo itself), which is
    /// naturally excluded by the under-the-root filter in
    /// <see cref="RemoveWorktreeRoot(string, string, IReadOnlyList{string})"/>. Empty on any git failure
    /// (best-effort) — the caller then falls back to a directory-only sweep, exactly as before.
    /// </summary>
    public static IReadOnlyList<string> RegisteredWorktreePaths(string repoPath)
    {
        string listing;
        try { listing = GitIn(repoPath, "worktree", "list", "--porcelain"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (string rawLine in listing.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
            paths.Add(line["worktree ".Length..].Trim());
        }

        return paths;
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
    public IReadOnlyDictionary<string, PlanBranchTaskRecord> ReconcileFromPlanBranch(IntegrationHandle integ) =>
        CollectTrailerTasks(integ.PlanBranchName, runIdFilter: null);

    /// <summary>
    /// W-1 protection (b): like <see cref="ReconcileFromPlanBranch(IntegrationHandle)"/> but counts
    /// only commits whose <c>Guardrails-Run:</c> matches <paramref name="runId"/> — used where a
    /// single run's own integrations must be isolated from earlier runs' trailers. Returns the task-id
    /// SET (the drift-report enrichment is irrelevant here).
    /// </summary>
    public IReadOnlySet<string> ReconcileFromPlanBranch(IntegrationHandle integ, string runId) =>
        CollectTrailerTasks(integ.PlanBranchName, runId).Keys.ToHashSet(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// Recover the file's bytes at <paramref name="commitSha"/> via <c>git show &lt;sha&gt;:&lt;rel&gt;</c>
    /// (issue #274 Part A). Returns null when the path is not tracked at that commit (a new file, or the
    /// plan folder was not committed there) or the commit is unresolvable — the drift report's Tier-2
    /// enrichment then degrades gracefully for that file while Tier 1 (the aggregate hash) stands.
    /// </remarks>
    public string? ReadFileAtCommit(string commitSha, string absolutePath)
    {
        if (string.IsNullOrEmpty(commitSha))
        {
            return null;
        }

        if (RepoRelativePath(absolutePath) is not { } rel)
        {
            return null;
        }

        var (stdout, exit) = TryGitIn(_repoPath, "show", $"{commitSha}:{rel}");
        return exit == 0 ? stdout : null;
    }

    /// <inheritdoc />
    public string? RepoRelativePath(string absolutePath)
    {
        string rel = Path.GetRelativePath(_repoPath, absolutePath).Replace('\\', '/');
        // A path outside the repo root ("../…") or an unrelated rooted path cannot be addressed as
        // <commit>:<rel>; treat it as unrecoverable rather than handing git a nonsense spec.
        return rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>git ls-tree -r --name-only &lt;commit&gt; -- &lt;relDir&gt;</c> lists the files tracked under the
    /// task folder at that commit; converting each repo-relative result back to an absolute path lets the
    /// drift reporter fold them into the union with the current on-disk set, so a REMOVED file is still
    /// named (issue #274 Part A).
    /// </remarks>
    public IReadOnlyList<string> ListFilesAtCommit(string commitSha, string absoluteDir)
    {
        if (string.IsNullOrEmpty(commitSha) || RepoRelativePath(absoluteDir) is not { } relDir)
        {
            return [];
        }

        var (stdout, exit) = TryGitIn(_repoPath, "ls-tree", "-r", "--name-only", commitSha, "--", relDir);
        if (exit != 0)
        {
            return [];
        }

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(rel => Path.GetFullPath(Path.Combine(_repoPath, rel)))
            .ToList();
    }

    /// <summary>
    /// Walk <paramref name="planBranch"/>'s first-parent history and collect, per task id, its
    /// <c>Guardrails-Task:</c> trailer commit sha and <c>Guardrails-Task-Hash:</c> (issue #274 Part A).
    /// The MOST RECENT integration per task wins (git log is newest-first, so the first occurrence is
    /// kept). When <paramref name="runIdFilter"/> is non-null, only commits whose <c>Guardrails-Run:</c>
    /// matches are counted; when null, every trailer counts. Each commit record is framed by a record
    /// separator with its sha, then its raw body — control chars git never emits in a commit message —
    /// so a body's own blank lines can't split one commit across two records.
    /// </summary>
    private IReadOnlyDictionary<string, PlanBranchTaskRecord> CollectTrailerTasks(
        string planBranch, string? runIdFilter) =>
        ParseTrailerRecords(Git("log", "--first-parent", TrailerLogFormat, planBranch), runIdFilter);

    /// <summary>
    /// The <c>git log</c> pretty-format that frames each commit record with the shared HashText
    /// separators (control chars git never emits in a commit message): a record separator, the commit
    /// sha, a unit separator, then the raw body — so a body's own blank lines can't split one commit's
    /// trailers across two records the way the prior blank-line-delimited <c>%B</c> parse could.
    /// </summary>
    private static string TrailerLogFormat =>
        $"--format={Hashing.HashText.RecordSeparator}%H{Hashing.HashText.UnitSeparator}%B";

    /// <summary>
    /// Parse the <see cref="TrailerLogFormat"/> log into per-task <see cref="PlanBranchTaskRecord"/>s
    /// (commit sha + <c>Guardrails-Task-Hash:</c>). The MOST RECENT integration per task wins (git log is
    /// newest-first, so the first occurrence is kept). When <paramref name="runIdFilter"/> is non-null,
    /// only commits whose <c>Guardrails-Run:</c> matches are counted. Pure (no IO), shared by the instance
    /// resume reconcile and the static <see cref="ReadPlanBranchTaskHashes"/> dry-run query.
    /// </summary>
    private static IReadOnlyDictionary<string, PlanBranchTaskRecord> ParseTrailerRecords(
        string log, string? runIdFilter)
    {
        char recordSep = Hashing.HashText.RecordSeparator;
        char unitSep = Hashing.HashText.UnitSeparator;
        var settled = new Dictionary<string, PlanBranchTaskRecord>(StringComparer.Ordinal);

        foreach (string record in log.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = record.IndexOf(unitSep);
            if (split < 0)
            {
                continue;
            }

            string commitSha = record[..split].Trim();
            string body = record[(split + 1)..];

            string? currentTask = null;
            string? currentRun = null;
            string? currentHash = null;

            // Only the LAST trailer block attributes (git interpret-trailers semantics) — a stray
            // Guardrails-Task: line in a human hand-fix commit's prose must NOT be read as attribution
            // (issue #274 Part C NIT). A genuine hand-fix stays un-attributed → the rewind refuses it.
            foreach (string line in LastTrailerBlockLines(body))
            {
                if (line.StartsWith("Guardrails-Task: ", StringComparison.Ordinal))
                    currentTask = line["Guardrails-Task: ".Length..];
                else if (line.StartsWith("Guardrails-Run: ", StringComparison.Ordinal))
                    currentRun = line["Guardrails-Run: ".Length..];
                else if (line.StartsWith("Guardrails-Task-Hash: ", StringComparison.Ordinal))
                    currentHash = line["Guardrails-Task-Hash: ".Length..];
            }

            if (currentTask is null || (runIdFilter is not null && currentRun != runIdFilter))
            {
                continue;
            }

            // Newest-first: keep the FIRST record seen for a task id (its most recent integration).
            if (!settled.ContainsKey(currentTask))
            {
                settled[currentTask] = new PlanBranchTaskRecord(commitSha, currentHash);
            }
        }

        return settled;
    }

    /// <summary>
    /// Read-only query of the plan branch's per-task <c>Guardrails-Task-Hash:</c> trailers (issue #274
    /// Part A) WITHOUT creating an integration worktree — for the <c>--dry-run</c> drift preview, which
    /// must touch nothing. Runs <c>git log</c> (read-only) in <paramref name="repoPath"/> against
    /// <c>guardrails/&lt;planName&gt;</c>; degrades to the EMPTY map (never throws) when the workspace is
    /// not a git repo, git is unavailable, or the plan branch does not exist — so a dry run in a non-git
    /// plan folder behaves exactly as before (journal-only). Static so the CLI needs no run-scoped
    /// provider or integration handle.
    /// </summary>
    public static IReadOnlyDictionary<string, PlanBranchTaskRecord> ReadPlanBranchTaskHashes(
        string repoPath, string planName)
    {
        var empty = new Dictionary<string, PlanBranchTaskRecord>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(planName))
        {
            return empty;
        }

        try
        {
            var (log, exit) = TryGitIn(repoPath, "log", "--first-parent", TrailerLogFormat, $"guardrails/{planName}");
            return exit == 0 ? ParseTrailerRecords(log, runIdFilter: null) : empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git not installed, or the working dir is not a repo — the dry-run preview simply falls back
            // to journal-only, never a crash.
            return empty;
        }
    }

    /// <inheritdoc />
    public bool TracksPlanBranchTrailers => true;

    /// <inheritdoc />
    public string CurrentPlanBranchTip(IntegrationHandle integ) =>
        CurrentPlanBranchTip(_repoPath, integ.PlanBranchName);

    /// <summary>
    /// The current tip sha of <paramref name="planBranch"/> (issue #274 Part C compare-and-swap). Static so
    /// the manual scoped reset can read it without a run-scoped provider. Degrades to the empty string when
    /// the branch does not exist / git is unavailable / the workspace directory does not exist — a non-git
    /// plan folder has no branch to CAS against. The <c>Win32Exception</c> catch covers a workspace path that
    /// is not a real directory (git cannot even be spawned there), so a serial / synthetic-workspace caller
    /// (#311 <c>RunReset.Task</c> worktree probe) never crashes — it just reads "no branch".
    /// </summary>
    public static string CurrentPlanBranchTip(string repoPath, string planBranch)
    {
        try
        {
            var (stdout, exit) = TryGitIn(repoPath, "rev-parse", planBranch);
            return exit == 0 ? stdout.Trim() : "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }

    /// <inheritdoc />
    public SafeSuffixDecision EvaluateSafeSuffix(
        IntegrationHandle integ, IReadOnlySet<string> safeSet,
        IReadOnlyDictionary<string, string> recognizedSettleHashes) =>
        EvaluateSafeSuffix(_repoPath, integ.PlanBranchName, safeSet, recognizedSettleHashes);

    /// <inheritdoc />
    /// <remarks>
    /// Reset WITHIN the integration worktree so its working tree (not just the branch ref) matches the
    /// rewound tip — the integration worktree is checked out on the plan branch and later FFs segments
    /// onto it, so its tree must agree with the branch. <c>git reset --hard</c> keeps the discarded
    /// commits in the branch reflog (recoverable), so the rewind is destructive but not unrecoverable.
    /// </remarks>
    public void RewindPlanBranchTo(IntegrationHandle integ, string resetTarget) =>
        GitIn(integ.IntegrationWorktreePath, "reset", "--hard", resetTarget);

    /// <summary>
    /// Part C (issue #274, SSOT §7.2): build the plan branch's <c>--first-parent</c> trailer history as
    /// the pure <see cref="SafeSuffixEvaluator"/>'s <see cref="TrailerCommit"/> model and run the check
    /// for <paramref name="safeSet"/>. Read-only. Static so BOTH the run-time pre-DAG gate (via the
    /// instance method above) and the manual scoped reset (which has no run-scoped provider) share ONE
    /// decision path. Degrades to <see cref="SafeSuffixDecision.Nothing"/> when the plan branch does not
    /// exist / git is unavailable (a non-git plan folder), matching the read-only dry-run posture.
    /// <paramref name="recognizedSettleHashes"/> (issue #322) is <c>task id → the journal-recorded settle
    /// hash</c>; a commit in the removed range whose <c>Guardrails-Task-Hash:</c> is not one of these is a
    /// copied-trailer hand-fix and is REFUSED (see <see cref="SafeSuffixEvaluator.Evaluate"/>).
    /// </summary>
    public static SafeSuffixDecision EvaluateSafeSuffix(
        string repoPath, string planBranch, IReadOnlySet<string> safeSet,
        IReadOnlyDictionary<string, string> recognizedSettleHashes)
    {
        IReadOnlyList<TrailerCommit> history;
        try
        {
            history = GatherFirstParentHistory(repoPath, planBranch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return SafeSuffixDecision.Nothing();
        }

        return SafeSuffixEvaluator.Evaluate(history, safeSet, recognizedSettleHashes);
    }

    /// <summary>
    /// Part C (issue #274): DESTRUCTIVELY rewind <paramref name="planBranch"/> to
    /// <paramref name="resetTarget"/>. Static so the manual scoped reset can rewind without a run-scoped
    /// provider. Resets INSIDE the plan branch's checked-out worktree when one exists (so its tree tracks
    /// the branch); otherwise force-moves the branch ref. Both keep the discarded commits in the reflog.
    /// </summary>
    public static void RewindPlanBranch(string repoPath, string planBranch, string resetTarget)
    {
        if (WorktreeForBranch(repoPath, planBranch) is { } wt && Directory.Exists(wt))
        {
            GitIn(wt, "reset", "--hard", resetTarget);
        }
        else
        {
            // No checked-out worktree holds the branch — force-move the ref (also reflog-logged).
            GitIn(repoPath, "branch", "-f", planBranch, resetTarget);
        }
    }

    /// <summary>
    /// Build the plan branch's <c>--first-parent</c> history (newest-first) as <see cref="TrailerCommit"/>
    /// records for the pure safe-suffix check (issue #274 Part C). Each first-parent commit carries its
    /// own <c>Guardrails-Task:</c> trailer (or null), its first-parent sha (the rewind target when it is
    /// the oldest removed), and — for a merge/union — the tasks reachable via its NON-first-parent
    /// lineage(s) relative to its own first parent (<c>git rev-list &lt;sha&gt; --not &lt;firstParent&gt;</c>,
    /// the merge-tip caveat input). The union of the first-parent commits and every merge's lineage set
    /// is exactly what <c>git reset --hard c_j^</c> discards, so the pure check over this model is sound.
    /// </summary>
    public static IReadOnlyList<TrailerCommit> GatherFirstParentHistory(string repoPath, string planBranch)
    {
        // A sha → Guardrails-Task: trailer map over EVERY reachable commit (all parents), so a merge's
        // non-first-parent lineage commits (never on the first-parent chain) can be attributed too. The
        // SAME log also yields the harness Guardrails-Wave: marker shas (#254 M2b) so the safe-suffix check
        // can EXEMPT them from its trailer-less REFUSE (they are known bookkeeping, not human hand-fixes).
        string trailerLog = GitIn(repoPath, "log", TrailerShaBodyFormat, planBranch);
        IReadOnlyDictionary<string, string?> taskBySha = ParseShaToTask(trailerLog);
        IReadOnlyDictionary<string, string?> hashBySha = ParseShaToHash(trailerLog);
        IReadOnlySet<string> waveMarkerShas = ParseWaveMarkerShas(trailerLog);

        // The first-parent spine: sha + its parent shas (space-separated; first is the first parent).
        string fpLog = GitIn(repoPath, "log", "--first-parent", $"--format=%H{Hashing.HashText.UnitSeparator}%P", planBranch);

        var commits = new List<TrailerCommit>();
        foreach (string rawLine in fpLog.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int split = line.IndexOf(Hashing.HashText.UnitSeparator);
            if (split < 0)
            {
                continue;
            }

            string sha = line[..split].Trim();
            string[] parents = line[(split + 1)..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string firstParent = parents.Length > 0 ? parents[0] : "";

            IReadOnlyList<string?> mergedIn = parents.Length > 1
                ? MergedLineageTasks(repoPath, sha, firstParent, taskBySha)
                : [];

            commits.Add(new TrailerCommit
            {
                Sha = sha,
                Task = taskBySha.GetValueOrDefault(sha),
                // #322: the commit's OWN Guardrails-Task-Hash: trailer (null on a pre-#274 commit) — the
                // uncorroborated-trailer REFUSE compares it against the harness's journal-recorded hash.
                DefinitionHash = hashBySha.GetValueOrDefault(sha),
                ParentSha = firstParent,
                MergedInTasks = mergedIn,
                // A GENUINE Guardrails-Wave: marker is EMPTY (CommitWaveMarker always commits --allow-empty
                // against a clean integration worktree). Gate the marker exemption on BOTH the trailer AND an
                // empty tree delta vs its first parent (#311 WEAK-1): a human hand-fix that carries a
                // Guardrails-Wave: trailer (a `git commit --amend` onto a marker tip, a copy-pasted trailer)
                // ALWAYS changes files, so it fails the empty-tree gate → NOT classified a marker → falls
                // through to the trailer-less REFUSE and is preserved, never silently discarded.
                IsWaveMarker = waveMarkerShas.Contains(sha)
                    && !string.IsNullOrEmpty(firstParent)
                    && HasEmptyTreeDelta(repoPath, firstParent, sha)
            });
        }

        return commits;
    }

    /// <summary>
    /// The tasks reachable from merge commit <paramref name="sha"/> via its NON-first-parent lineage(s),
    /// back to the merge-base with the retained mainline: <c>git rev-list &lt;sha&gt; --not
    /// &lt;firstParent&gt;</c> minus <paramref name="sha"/> itself, each mapped to its
    /// <c>Guardrails-Task:</c> trailer (null when the commit carries none — a trailer-less merged commit,
    /// which forces a REFUSE). Excluding everything reachable from the first parent (an ancestor of the
    /// eventual reset target) drops the retained-mainline region, so this is exactly the extra work
    /// <c>reset --hard</c> would un-integrate through this merge.
    /// </summary>
    private static IReadOnlyList<string?> MergedLineageTasks(
        string repoPath, string sha, string firstParent, IReadOnlyDictionary<string, string?> taskBySha)
    {
        if (string.IsNullOrEmpty(firstParent))
        {
            return [];
        }

        var (stdout, exit) = TryGitIn(repoPath, "rev-list", sha, "--not", firstParent);
        if (exit != 0)
        {
            // Could not enumerate the lineage — refuse safely by surfacing an un-attributable entry.
            return [null];
        }

        var merged = new List<string?>();
        foreach (string rawSha in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string mergedSha = rawSha.Trim();
            if (mergedSha.Length == 0 || string.Equals(mergedSha, sha, StringComparison.Ordinal))
            {
                continue; // the merge commit itself is scored by the first-parent walk, not here.
            }

            merged.Add(taskBySha.GetValueOrDefault(mergedSha));
        }

        return merged;
    }

    /// <summary>The <c>git log</c> pretty-format framing each commit as sha + unit-separator + raw body.</summary>
    private static string TrailerShaBodyFormat =>
        $"--format={Hashing.HashText.RecordSeparator}%H{Hashing.HashText.UnitSeparator}%B";

    /// <summary>
    /// Parse a <see cref="TrailerShaBodyFormat"/> log into a sha → <c>Guardrails-Task:</c> trailer map
    /// (value null when a commit carries no such trailer). Shares the record/unit separators with
    /// <see cref="ParseTrailerRecords"/> so a body's own blank lines can never split one commit's record.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ParseShaToTask(string log)
    {
        char recordSep = Hashing.HashText.RecordSeparator;
        char unitSep = Hashing.HashText.UnitSeparator;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (string record in log.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = record.IndexOf(unitSep);
            if (split < 0)
            {
                continue;
            }

            string sha = record[..split].Trim();
            string body = record[(split + 1)..];

            // Only the LAST trailer block attributes (see ParseTrailerRecords) — a stray Guardrails-Task:
            // in a human hand-fix's prose stays un-attributed (null) so the rewind refuses it (#274 Part C).
            string? task = null;
            foreach (string line in LastTrailerBlockLines(body))
            {
                if (line.StartsWith("Guardrails-Task: ", StringComparison.Ordinal))
                {
                    task = line["Guardrails-Task: ".Length..];
                }
            }

            map[sha] = task;
        }

        return map;
    }

    /// <summary>
    /// Parse a <see cref="TrailerShaBodyFormat"/> log into a sha → <c>Guardrails-Task-Hash:</c> trailer map
    /// (value null when a commit carries no such trailer). The hash half of <see cref="ParseShaToTask"/>,
    /// sharing the same record/unit separators and the same LAST-trailer-block discipline so a body's own
    /// blank lines never split a record and a stray hash mention in prose is not read as a trailer (issue
    /// #322). Feeds <see cref="TrailerCommit.DefinitionHash"/> for the uncorroborated-trailer REFUSE.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> ParseShaToHash(string log)
    {
        char recordSep = Hashing.HashText.RecordSeparator;
        char unitSep = Hashing.HashText.UnitSeparator;
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (string record in log.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = record.IndexOf(unitSep);
            if (split < 0)
            {
                continue;
            }

            string sha = record[..split].Trim();
            string body = record[(split + 1)..];

            string? hash = null;
            foreach (string line in LastTrailerBlockLines(body))
            {
                if (line.StartsWith("Guardrails-Task-Hash: ", StringComparison.Ordinal))
                {
                    hash = line["Guardrails-Task-Hash: ".Length..];
                }
            }

            map[sha] = hash;
        }

        return map;
    }

    /// <summary>
    /// True when <paramref name="sha"/> has an EMPTY tree delta vs <paramref name="parent"/> (#311 WEAK-1):
    /// <c>git diff --quiet &lt;parent&gt; &lt;sha&gt;</c> exits 0 iff the two trees are identical. A genuine
    /// <c>Guardrails-Wave:</c> marker is always empty (committed <c>--allow-empty</c> against a clean tree);
    /// a human hand-fix changes files. FAIL-SAFE: any git error (exit ≠ 0/1, or a spawn failure) reads as
    /// "not empty" → the commit is NOT treated as a marker → it falls through to the trailer-less REFUSE.
    /// </summary>
    private static bool HasEmptyTreeDelta(string repoPath, string parent, string sha)
    {
        try
        {
            var (_, exit) = TryGitIn(repoPath, "diff", "--quiet", parent, sha);
            return exit == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The set of commit shas whose LAST trailer block carries a <c>Guardrails-Wave:</c> trailer (#254 M2b) —
    /// the CANDIDATE harness wave-marker commits. Parses the SAME <see cref="TrailerShaBodyFormat"/> log
    /// <see cref="ParseShaToTask"/> reads, with the identical last-trailer-block discipline, so a body's own
    /// blank lines can never split a record and a stray mention in prose never counts. The empty-tree gate
    /// (<see cref="HasEmptyTreeDelta"/>) is applied by <see cref="GatherFirstParentHistory"/> on top of this
    /// (#311 WEAK-1) so a hand-fix carrying the trailer but changing files is NOT exempted.
    /// </summary>
    private static IReadOnlySet<string> ParseWaveMarkerShas(string log)
    {
        char recordSep = Hashing.HashText.RecordSeparator;
        char unitSep = Hashing.HashText.UnitSeparator;
        var markers = new HashSet<string>(StringComparer.Ordinal);

        foreach (string record in log.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = record.IndexOf(unitSep);
            if (split < 0)
            {
                continue;
            }

            string sha = record[..split].Trim();
            string body = record[(split + 1)..];
            foreach (string line in LastTrailerBlockLines(body))
            {
                if (line.StartsWith("Guardrails-Wave: ", StringComparison.Ordinal))
                {
                    markers.Add(sha);
                    break;
                }
            }
        }

        return markers;
    }

    /// <summary>
    /// The lines of a commit body's LAST trailer block (approximating <c>git interpret-trailers</c>): the
    /// final paragraph, returned ONLY when every one of its non-blank lines is trailer-shaped
    /// (<c>Key: value</c>, key = letters/digits/hyphen). This is what makes a <c>Guardrails-Task:</c>
    /// occurrence in ordinary prose NOT count as attribution (issue #274 Part C NIT) — a human hand-fix
    /// commit that merely mentions a trailer keeps its <c>null</c> attribution and is refused by the safe
    /// rewind. Returns an empty list when the final paragraph is prose (not a trailer block).
    /// </summary>
    private static IReadOnlyList<string> LastTrailerBlockLines(string body)
    {
        string[] lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int end = lines.Length - 1;
        while (end >= 0 && lines[end].Trim().Length == 0) end--; // skip trailing blanks
        if (end < 0) return [];

        int start = end;
        while (start > 0 && lines[start - 1].Trim().Length > 0) start--; // to the top of the final paragraph

        var block = new List<string>(end - start + 1);
        for (int i = start; i <= end; i++)
        {
            string line = lines[i].Trim();
            if (!IsTrailerLine(line))
            {
                return []; // the final paragraph is prose, not a trailer block → no attribution
            }

            block.Add(line);
        }

        return block;
    }

    /// <summary>True when <paramref name="trimmed"/> is a trailer line <c>Key: value</c> (key = letters/digits/hyphen, then <c>": "</c>, then a non-empty value).</summary>
    private static bool IsTrailerLine(string trimmed)
    {
        int colon = trimmed.IndexOf(':');
        if (colon <= 0 || colon + 1 >= trimmed.Length || trimmed[colon + 1] != ' ')
        {
            return false;
        }

        for (int i = 0; i < colon; i++)
        {
            char ch = trimmed[i];
            if (!char.IsLetterOrDigit(ch) && ch != '-')
            {
                return false;
            }
        }

        return trimmed.Length > colon + 2; // a non-empty value after ": "
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
            // #306 review NIT-2: stage through the SAME reconstructable-exclusion pathspec set as the
            // segment commit (#280) — but into the THROWAWAY index (GIT_INDEX_FILE), so node_modules and
            // the harness's own .guardrails-* scaffolding never bloat the agent-applyable salvage patch,
            // while the segment's real staged/unstaged state stays untouched.
            GitInWithEnv(worktreePath, env, SegmentStaging.StageAllArguments().ToArray());
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // "never throws" per the summary: a git-spawn failure (git off PATH, bad dir) surfaces as
            // Win32Exception, not InvalidOperationException — catch the full best-effort set (#306 WEAK-2).
            return "";
        }
    }

    /// <summary>
    /// A full, applyable unified-diff patch of <paramref name="refName"/> against
    /// <paramref name="taskBase"/> (issue #306) — the exact bytes a retry agent can <c>git apply</c> from
    /// the clean segment (which the F2 reset restores to <paramref name="taskBase"/>) to recover ALL of a
    /// rolled-back attempt's work, or read to cherry-pick by hand. Uses <c>--binary</c> so a binary change
    /// is still applyable, and <c>--no-color</c> so the patch is never polluted by a user's
    /// <c>color.diff=always</c> git config. Returns an empty string (never throws) when the ref is missing
    /// or the diff otherwise fails, so a best-effort feedback composer degrades gracefully.
    /// </summary>
    public static string DiffAgainstBase(string worktreePath, string taskBase, string refName)
    {
        try
        {
            return GitIn(worktreePath, "diff", "--binary", "--no-color", taskBase, refName);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // "never throws" per the summary — full best-effort catch set, incl. a git-spawn Win32Exception (#306 WEAK-2).
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
    /// The F4 dirty-working-tree gate for the end-of-run delivery, narrowed to a REAL intersection
    /// (issue #448). Returns <c>null</c> when the merge may proceed — the user's checkout is clean, or
    /// its dirty paths are DISJOINT from the set of paths this merge would update and git would
    /// therefore merge cleanly while preserving the user's WIP. Otherwise returns the blocking paths,
    /// ordinal-sorted, for <see cref="LastMergeOnSuccessDetail"/> to name.
    /// <para>
    /// Untracked files stay EXCLUDED (<c>--untracked-files=no</c>): the harness writes its own runtime
    /// state (<c>state/</c>, <c>logs/</c>) under the plan folder inside the repo, which would otherwise
    /// flag every real run as dirty; and an untracked file cannot be silently interleaved by a
    /// fast-forward (git errors on an untracked-file collision, handled by the merge path).
    /// </para>
    /// <para>
    /// <b>Disjoint dirt is not uniformly safe — it depends on the merge SHAPE</b> (measured, not assumed):
    /// a FAST-FORWARD tolerates every flavour of disjoint dirt (unstaged, staged, staged rename), because
    /// git rewrites only the paths it updates. A REAL (non-FF) merge additionally demands a CLEAN INDEX —
    /// it refuses outright when anything is STAGED, even on a path it never touches. So a disjoint-but-staged
    /// change is still blocking on the non-FF path, and refusing it HERE keeps it an honest
    /// <see cref="MergeOnSuccessResult.DirtyWorkingTree"/> instead of letting <c>git merge</c> fail below
    /// and be misreported as a <see cref="MergeOnSuccessResult.Conflict"/>.
    /// </para>
    /// <para>
    /// <b>Fail CLOSED on any git failure.</b> If the touched-path set cannot be computed (git unavailable,
    /// unrelated histories with no merge base, unparseable porcelain) this falls back to the pre-#448
    /// refuse-on-ANY-tracked-dirt behaviour rather than proceeding; likewise an indeterminate merge shape
    /// is treated as non-FF. A fail-OPEN here — running a merge over user work we could not prove safe —
    /// would be strictly worse than the bug this narrowing fixes.
    /// </para>
    /// </summary>
    private IReadOnlyList<string>? BlockingDirtyPaths(string planBranch)
    {
        string porcelain;
        try
        {
            // -z: NUL-separated and UNQUOTED, so a path with a space/non-ASCII byte parses exactly.
            porcelain = Git("status", "--porcelain", "--untracked-files=no", "-z");
        }
        catch (InvalidOperationException)
        {
            // git status itself failed — we cannot prove the tree is clean. Refuse, unnamed.
            return [];
        }

        IReadOnlyList<DirtyEntry>? dirty = ParseDirtyTrackedPaths(porcelain);
        if (dirty is null)
        {
            // Unparseable porcelain — refuse rather than guess at the user's WIP.
            return [];
        }

        if (dirty.Count == 0)
        {
            return null; // clean tree — by far the common case
        }

        IReadOnlyList<string>? touched = MergeTouchedPaths(planBranch);
        if (touched is null)
        {
            // Touched-path set unknown ⇒ conservative fallback: refuse on ANY tracked dirt, naming it.
            return Sorted(dirty.Select(d => d.Path));
        }

        var touchedSet = new HashSet<string>(touched, StringComparer.Ordinal);
        List<string> intersecting = dirty.Where(d => touchedSet.Contains(d.Path)).Select(d => d.Path).ToList();
        if (intersecting.Count > 0)
        {
            return Sorted(intersecting);
        }

        // Disjoint. Safe on a fast-forward; on a real merge only if nothing is staged (see the remarks).
        if (MergeWouldFastForward(planBranch))
        {
            return null;
        }

        List<string> staged = dirty.Where(d => d.Staged).Select(d => d.Path).ToList();
        return staged.Count == 0 ? null : Sorted(staged);
    }

    /// <summary>
    /// True when the pending delivery can fast-forward — i.e. <c>HEAD</c> is an ancestor of
    /// <paramref name="planBranch"/> (which includes the already-delivered "Already up to date" resume
    /// case). Any non-zero exit — "not an ancestor" (1) AND a git error (128) — reports false, so an
    /// indeterminate shape is treated as the stricter non-FF case.
    /// </summary>
    private bool MergeWouldFastForward(string planBranch)
    {
        var (_, exit) = TryGitIn(_repoPath, "merge-base", "--is-ancestor", "HEAD", planBranch);
        return exit == 0;
    }

    /// <summary>
    /// The paths the pending delivery would UPDATE in the user's checkout: everything
    /// <paramref name="planBranch"/> changed since it diverged from <c>HEAD</c>
    /// (<c>git diff --name-only HEAD...&lt;planBranch&gt;</c>, i.e. merge-base→plan-branch). This is a
    /// deliberate SUPERSET of the files a merge actually rewrites — a path both sides changed
    /// identically appears here yet leaves the working tree untouched — which keeps the gate on the
    /// conservative side. For a fast-forward the merge base IS <c>HEAD</c>, so the set is exact.
    /// Null when git could not compute it (see the fail-closed note on <see cref="BlockingDirtyPaths"/>).
    /// </summary>
    private IReadOnlyList<string>? MergeTouchedPaths(string planBranch)
    {
        try
        {
            // Trailing "--" disambiguates the revision range from a pathspec.
            return SplitNulSeparated(Git("diff", "--name-only", "-z", $"HEAD...{planBranch}", "--"));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>One TRACKED path with uncommitted changes; <paramref name="Staged"/> = the index differs from HEAD for it.</summary>
    private readonly record struct DirtyEntry(string Path, bool Staged);

    /// <summary>
    /// Parse <c>git status --porcelain --untracked-files=no -z</c> into the TRACKED paths carrying
    /// uncommitted changes. Each record is <c>XY&lt;space&gt;&lt;path&gt;NUL</c> where <c>X</c> is the
    /// INDEX status (anything but a space ⇒ staged) and <c>Y</c> the worktree status; a rename/copy
    /// (<c>X</c> or <c>Y</c> in <c>{R, C}</c>) appends a second <c>&lt;origPath&gt;NUL</c> field and BOTH
    /// endpoints are reported dirty — a staged rename has moved content away from the old path and into
    /// the new one, so either endpoint colliding with the merge is blocking. Returns null on a malformed
    /// record so the caller can fail closed instead of silently under-reporting the user's WIP.
    /// </summary>
    private static IReadOnlyList<DirtyEntry>? ParseDirtyTrackedPaths(string porcelainZ)
    {
        var entries = new List<DirtyEntry>();
        int i = 0;
        while (i < porcelainZ.Length)
        {
            int end = porcelainZ.IndexOf('\0', i);
            if (end < 0) return null; // truncated record
            string record = porcelainZ[i..end];
            i = end + 1;

            // "XY " + at least one path character.
            if (record.Length < 4 || record[2] != ' ') return null;
            bool staged = record[0] != ' ';
            entries.Add(new DirtyEntry(record[3..], staged));

            if (record[0] is 'R' or 'C' || record[1] is 'R' or 'C')
            {
                int origEnd = porcelainZ.IndexOf('\0', i);
                if (origEnd < 0) return null; // rename record missing its source field
                entries.Add(new DirtyEntry(porcelainZ[i..origEnd], staged));
                i = origEnd + 1;
            }
        }

        return entries;
    }

    private static string[] SplitNulSeparated(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Ordinal sort — the repo-wide ordering convention, so the named paths are locale-stable.</summary>
    private static IReadOnlyList<string> Sorted(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();

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

    /// <summary>
    /// Re-alias a git-canonical (REAL-root) path onto this run's launch root (issue #419): map
    /// <c>&lt;realRoot&gt;/…</c> → <c>&lt;worktreeRoot&gt;/…</c> so the reused integration worktree's cwd is the
    /// SHORT junction on resume, matching a fresh run's. The IDENTITY when no junction is in effect
    /// (<c>_realRoot == _worktreeRoot</c> — non-Windows or a lazy-skip / fallback run) or when
    /// <paramref name="path"/> is not under the real root. Both roots are the harness's own worktree roots
    /// (no user metacharacters); the comparison is case-insensitive, matching git's letter-agnostic
    /// canonicalization on Windows.
    /// <para>
    /// The comparison is taken over the SYMLINK-RESOLVED spelling of both sides (issue #452): the same
    /// shape as the sweep's containment test — <paramref name="path"/> is git-reported (git stores the
    /// resolved real path) while <c>_realRoot</c> descends from <see cref="Path.GetTempPath"/>, which does
    /// not resolve symlinks. A junctioned/symlinked temp dir would otherwise make a genuinely-under-the-root
    /// integration worktree look foreign, silently skipping the re-aliasing and leaving the resume on the
    /// long path. The <c>_realRoot</c>/<c>_worktreeRoot</c> gate above stays LEXICAL on purpose — the #383
    /// junction and its target ARE the same directory, and telling those two spellings apart is exactly
    /// what this method exists to do.
    /// </para>
    /// </summary>
    private string AliasForLaunch(string path)
    {
        if (WorktreeJunction.SamePath(_realRoot, _worktreeRoot))
        {
            return path; // no junction in effect — nothing to re-alias
        }

        string real = RealPath.Resolve(_realRoot);
        string full = RealPath.Resolve(path);

        if (string.Equals(full, real, StringComparison.OrdinalIgnoreCase))
        {
            return _worktreeRoot;
        }

        string prefix = real + Path.DirectorySeparatorChar;
        if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(_worktreeRoot, full[prefix.Length..]);
        }

        return path; // not under the real root — leave as-is
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
            UseShellExecute = false,
            // Issue #457: git speaks UTF-8; an unpinned stream is decoded with the host console code page.
            // This runner captures commit trailers, porcelain paths and hook stderr — all of which can
            // carry non-ASCII and all of which are re-surfaced (to the user, or into a commit message).
            StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
            StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
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
            UseShellExecute = false,
            // Issue #457 — the hook-rejection stderr surfaced verbatim to the user (#149/#150) and the
            // dirty-path list (#448) both flow through here; pin UTF-8 so neither is mojibake.
            StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
            StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }
}
