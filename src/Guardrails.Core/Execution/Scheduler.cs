using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M4 DAG scheduler. Kahn-style readiness (a task becomes ready when every
/// dependency is green) feeding an unbounded <see cref="Channel{T}"/> consumed by
/// <c>maxParallelism</c> workers. A task that ends <c>needs-human</c> (or otherwise
/// non-green) blocks its TRANSITIVE dependents immediately while independent branches
/// keep running — every completed task is durable progress in the journal, and one run
/// surfaces every needs-human halt instead of one per run.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly ISchedulerJournal _journal;
    private readonly IWorktreeProvider? _worktreeProvider;
    private readonly IRunObserver _observer;
    private readonly int _maxParallelism;
    private readonly IReVerifier? _reVerifier;

    private readonly object _gate = new();

    // Serialize-merges lock (plan 08 §3): one integration-settle at a time so that
    // non-FF union re-verify and B1 rollback are atomic w.r.t. other settling workers.
    private readonly SemaphoreSlim _integrationLock = new(1, 1);

    // First unexpected (non-cancellation) executor fault wins; surfaced after WhenAll so the
    // run terminates deterministically with a harness error instead of hanging (see WorkerLoopAsync).
    private Exception? _fault;

    private readonly IAiMergeWorker? _aiMergeWorker;

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        ISchedulerJournal journal,
        IWorktreeProvider? worktreeProvider = null,
        IRunObserver? observer = null,
        int? maxParallelism = null,
        IReVerifier? reVerifier = null,
        IAiMergeWorker? aiMergeWorker = null)
    {
        _plan = plan;
        _executor = executor;
        _journal = journal;
        _worktreeProvider = worktreeProvider;
        _observer = observer ?? IRunObserver.Null;
        _reVerifier = reVerifier;
        _aiMergeWorker = aiMergeWorker;

        int requested = Math.Max(1, maxParallelism ?? plan.Config.MaxParallelism);

        // F7 HARD GUARD: worktree mode (parallelism > 1) requires a worktree provider for
        // per-task isolation. With no provider, parallel workers would share the single
        // workspace and race undetectably (the rejected shared-workspace corruption class).
        // CLAMP to 1 (serial shared-workspace, the pre-plan-08 model) rather than running
        // an unsafe parallel run — and tell the observer so the demotion is not silent.
        if (requested > 1 && _worktreeProvider is null)
        {
            _observer.ParallelismClampedNoProvider(requested);
            requested = 1;
        }

        _maxParallelism = requested;
    }

    /// <summary>
    /// Run the plan to quiescence: every task green, blocked, or needs-human — or the
    /// token cancelled (in-flight attempts are journaled back to pending by the
    /// executor; unstarted tasks are reported <see cref="TaskOutcome.Cancelled"/>).
    /// </summary>
    public async Task<RunReport> RunAsync(PlanDefinition plan, CancellationToken cancellationToken = default)
    {
        var graph = new DependencyGraph(plan.Tasks);
        if (graph.FindCycle() is { } cycle)
        {
            // Validation (GR2007) catches this before a run; this guard keeps the
            // scheduler safe when embedded directly.
            throw new InvalidOperationException($"Dependency cycle: {string.Join(" -> ", cycle)}");
        }

        var byId = plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var settled = new Dictionary<string, TaskResult>(StringComparer.Ordinal);
        var pendingDeps = new Dictionary<string, int>(StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<TaskEnvelope>();

        // Create the integration handle once for this run (worktree mode only).
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle? integ = _worktreeProvider?.CreateIntegration(
            planName: Path.GetFileName(plan.PlanDirectory),
            runId: runId,
            cancellationToken);

        // B1_1/F1 resume pre-pass: a task is already green if the JOURNAL says Succeeded OR it is
        // already integrated on the PLAN BRANCH (a Guardrails-Task: trailer reachable from the tip).
        // Git is the durable resume truth: a kill after the FF/merge commit but before the journal
        // write leaves the task on the plan branch with no journal record — the union below stops it
        // being re-run. Prune this run's stale segment refs first so they can't be mistaken for work.
        var planBranchSettled = new HashSet<string>(StringComparer.Ordinal);
        if (_worktreeProvider is { } wp && integ is { } activeInteg)
        {
            wp.PruneStaleRunBranches(runId, activeInteg);
            planBranchSettled.UnionWith(wp.ReconcileFromPlanBranch(activeInteg));
        }

        // --- resume pre-pass: journaled successes + plan-branch trailers are green ----------
        var preSettledGreen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (_journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded
                || planBranchSettled.Contains(task.Id))
            {
                preSettledGreen.Add(task.Id);
                var skipped = new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.Skipped,
                    Summary = "already succeeded (resumed) — skipped"
                };
                settled[task.Id] = skipped;
                _observer.TaskFinished(skipped);
            }
        }

        int remaining = 0;
        foreach (TaskNode task in plan.Tasks)
        {
            if (preSettledGreen.Contains(task.Id))
            {
                continue;
            }

            remaining++;
            pendingDeps[task.Id] = task.DependsOn.Count(d => !preSettledGreen.Contains(d));
        }

        if (remaining == 0)
        {
            return BuildReport(plan, settled, cancelled: false);
        }

        // Pre-create worktree handles only for initially-ready tasks (no pending deps).
        // Dependent tasks get their handles created LAZILY in OnSettledAsync AFTER the upstream
        // integration commits advance the plan branch — this is what makes FF possible for
        // linear chains: task B's segment is forked from task A's integrated HEAD, not from the
        // original plan-branch HEAD before A ran.
        var handles = new Dictionary<string, WorktreeHandle>(StringComparer.Ordinal);
        // DirectoryOwner (plan 08 topology-wiring M0): worktree path → current owning task id.
        // The single source of truth for "is this directory free to Discard / reuse?". Populated
        // on every CreateSegment/ForkFromTip; ownership transfers on ReuseSegment (M1). In M0 every
        // task still owns its own fresh directory, so this map mirrors the fresh-per-task baseline.
        var directoryOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                WorktreeHandle handle = _worktreeProvider != null && integ != null
                    ? _worktreeProvider.CreateSegment(task.Id, attempt: 1, integ, cancellationToken)
                    : new WorktreeHandle();
                handles[task.Id] = handle;
                if (!string.IsNullOrEmpty(handle.WorktreePath))
                {
                    directoryOwner[handle.WorktreePath] = task.Id;
                }
            }
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                channel.Writer.TryWrite(new TaskEnvelope(task, handles[task.Id]));
            }
        }

        // --- workers ---------------------------------------------------------------------
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new RunContext(graph, byId, settled, pendingDeps, channel, remaining, handles, directoryOwner, integ);
        int workerCount = Math.Min(_maxParallelism, remaining);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(context, runCts), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (_fault is { } fault)
        {
            throw new InvalidOperationException(
                $"A task executor threw an unexpected exception; the run was aborted: {fault.Message}",
                fault);
        }

        RunReport report = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested);

        // --- C1 terminal whole-repo integration gate (§3.3/§4a) --------------------------
        // After every task settles green, re-run the run's integration-guardrail set on the
        // FINAL plan-branch HEAD (via the integration worktree) — the union's whole-repo
        // soundness boundary that backstops the per-hop FF-integrations a linear chain skipped.
        // A failing gate flips the run off-green (the sink task, or first task, to needs-human)
        // so mergeOnSuccess is refused and the report is not certified.
        if (report.AllSucceeded && _reVerifier != null && integ != null)
        {
            IReadOnlyList<GuardrailDefinition> integrationSet =
                GuardrailScopeFilter.IntegrationSet(plan.Tasks.SelectMany(t => t.Guardrails));

            if (integrationSet.Count > 0)
            {
                ReVerifyResult gate = await _reVerifier
                    .ReVerifyAsync(integ.IntegrationWorktreePath, integrationSet, cancellationToken)
                    .ConfigureAwait(false);

                if (!gate.Passed)
                {
                    report = WithTerminalGateFailure(plan, report, gate);
                }
            }
        }

        // Deliver the completed plan branch to the user's branch when every task succeeded
        // and mergeOnSuccess is enabled. AI-merge is withheld: a conflict halts to needs-human
        // with the plan branch intact (SSOT §5.3).
        MergeOnSuccessResult? mergeOutcome = null;
        if (report.AllSucceeded && plan.Config.MergeOnSuccess && _worktreeProvider != null && integ != null)
        {
            mergeOutcome = _worktreeProvider.MergePlanBranchIntoUserBranch(integ, cancellationToken);
        }

        return report with { MergeOnSuccessOutcome = mergeOutcome };
    }

    private async Task WorkerLoopAsync(RunContext context, CancellationTokenSource runCts)
    {
        CancellationToken cancellationToken = runCts.Token;
        try
        {
            await foreach (TaskEnvelope envelope in context.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                TaskNode task = envelope.Task;
                WorktreeHandle handle = MaterializeForkIfDeferred(context, envelope);

                if (CostCapHaltFor(task) is { } capped)
                {
                    await OnSettledAsync(context, task, capped, handle, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TaskResult result = await _executor.ExecuteAsync(task, handle, cancellationToken).ConfigureAwait(false);

                await OnSettledAsync(context, task, result, handle, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled drain.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault ??= ex;
            }

            context.Channel.Writer.TryComplete();
            runCts.Cancel();
        }
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §B: materialize a deferred fork-the-rest sibling's worktree at
    /// dequeue — the actual <c>git worktree add</c> runs HERE, OFF the <see cref="_gate"/> every
    /// settling worker contends for. The fork roots off the producer's RECORDED sha (captured in
    /// the request under <c>_gate</c> at assignment, W-2), never a live rev-parse of the segment
    /// branch the inheritor may have advanced. Returns the envelope's existing handle unchanged
    /// when there is no deferred fork.
    /// </summary>
    private WorktreeHandle MaterializeForkIfDeferred(RunContext context, TaskEnvelope envelope)
    {
        if (envelope.Fork is not { } fork || _worktreeProvider is not { } provider)
        {
            return envelope.Handle;
        }

        // git I/O off the gate.
        WorktreeHandle handle = provider.ForkFromTip(fork.ProducerRecordedSha, envelope.Task.Id, attempt: 1);

        // Bookkeeping under the gate: record the assigned handle + directory ownership.
        lock (_gate)
        {
            context.Handles[envelope.Task.Id] = handle;
            if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                context.DirectoryOwner[handle.WorktreePath] = envelope.Task.Id;
            }
        }

        return handle;
    }

    private TaskResult? CostCapHaltFor(TaskNode task)
    {
        if (_plan.Config.MaxCostUsd is not { } cap || _journal.CurrentCostUsd() < cap)
        {
            return null;
        }

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"cost cap reached: cumulative journaled cost has reached the configured " +
                      $"maxCostUsd (${cap}); task not launched."
        };
    }

    /// <summary>
    /// Called after a task finishes (executor or cost-cap halt). For worktree-mode green results,
    /// performs the B1 deferred settle (fragment merge → git integration commit → journal settle)
    /// under <see cref="_integrationLock"/> BEFORE updating the shared run context under
    /// <see cref="_gate"/>. This ordering ensures dependents only become ready after the upstream
    /// integration has advanced the plan branch, making lazy handle creation FF-compatible.
    /// </summary>
    private async Task OnSettledAsync(
        RunContext context, TaskNode task, TaskResult result, WorktreeHandle handle, CancellationToken ct)
    {
        // B1 deferred settle (worktree mode, real segment): ValidateFragmentForSettle sets
        // DeferredSettle=true, meaning the Scheduler owns the fragment merge → git commit →
        // journal settle sequence under the integration lock.
        //
        // Old path (serial mode or fake provider): the executor already merged + journaled;
        // just call provider.Integrate directly so IWorktreeProvider.IntegrateCallCount tests pass.
        if (result.IsGreen && _worktreeProvider is { } provider && context.Integ is { } integ)
        {
            if (result.DeferredSettle)
            {
                await _integrationLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    result = await SettleAsync(task, result, handle, provider, integ, ct).ConfigureAwait(false);
                }
                finally
                {
                    _integrationLock.Release();
                }
            }
            else if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                // Non-deferred: executor already handled journal; just integrate the segment.
                provider.Integrate(handle, integ, CancellationToken.None);
            }
        }

        var newlyReady = new List<TaskEnvelope>();
        var newlyBlocked = new List<TaskResult>();

        lock (_gate)
        {
            context.Settled[task.Id] = result;
            context.Remaining--;

            if (result.IsGreen)
            {
                // Which dependents of this producer just had their LAST pending dep cleared?
                // (A multi-producer dependent here is a fan-in: it has other, already-green
                // producers, and reaches the merged plan tip — never reused, M1 §A1.)
                var justReady = new List<string>();
                foreach (string dependent in context.Graph.DependentsOf(task.Id))
                {
                    if (!context.Settled.ContainsKey(dependent) && --context.PendingDeps[dependent] == 0)
                    {
                        justReady.Add(dependent);
                    }
                }

                AssignDependentHandles(context, task, justReady, newlyReady);
            }
            else if (result.Outcome != TaskOutcome.Cancelled)
            {
                foreach (string dependent in context.Graph.TransitiveDependentsOf(task.Id)
                             .OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (context.Settled.ContainsKey(dependent))
                    {
                        continue;
                    }

                    var blocked = new TaskResult
                    {
                        TaskId = dependent,
                        Outcome = TaskOutcome.Blocked,
                        Summary = $"blocked: dependency '{task.Id}' did not succeed"
                    };
                    context.Settled[dependent] = blocked;
                    context.Remaining--;
                    _journal.MarkBlocked(dependent);
                    newlyBlocked.Add(blocked);
                }
            }

            if (context.Remaining == 0)
            {
                context.Channel.Writer.TryComplete();
            }
        }

        _observer.TaskFinished(result);
        foreach (TaskResult blocked in newlyBlocked)
        {
            _observer.TaskFinished(blocked);
        }

        // Each envelope already carries its assigned handle (fresh segment / reused directory) OR a
        // deferred fork request the worker materializes off-gate at dequeue (M1 §B).
        foreach (TaskEnvelope ready in newlyReady)
        {
            context.Channel.Writer.TryWrite(ready);
        }
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §A/§B: assign worktree handles to the dependents of a just-settled
    /// green producer <paramref name="producer"/>, choosing reuse vs fork vs fresh-segment.
    /// <list type="bullet">
    ///   <item><b>Multi-producer dependents (fan-in)</b> get a fresh <see cref="IWorktreeProvider.CreateSegment"/>
    ///     off the plan-branch tip, which already contains every producer's integrated work — never
    ///     reused (§A1).</item>
    ///   <item><b>Single-producer dependents</b> are the inherit-one/fork-rest fan-out. The inheritor
    ///     (longest downstream chain via <see cref="DependencyGraph.TransitiveDependentsOf"/>, ordinal-id
    ///     tiebreak) reuses the producer's segment directory via the pure-handle
    ///     <see cref="IWorktreeProvider.ReuseSegment"/> (safe under <see cref="_gate"/>; ownership
    ///     transfers to the inheritor). The rest fork off the producer's RECORDED sha — a DEFERRED
    ///     request the worker materializes off-gate (§B, W-2).</item>
    /// </list>
    /// Runs under <see cref="_gate"/>. All assignment + bookkeeping is here; only the fork's
    /// <c>git worktree add</c> is deferred off-gate.
    /// </summary>
    private void AssignDependentHandles(
        RunContext context, TaskNode producer, List<string> justReady, List<TaskEnvelope> newlyReady)
    {
        // Fan-in (multi-producer) dependents reach the merged plan tip with a fresh segment.
        var singleProducer = new List<string>();
        foreach (string dependent in justReady)
        {
            if (context.ById[dependent].DependsOn.Count > 1)
            {
                WorktreeHandle fanInHandle = CreateFreshSegment(context, dependent);
                context.Handles[dependent] = fanInHandle;
                RecordOwnership(context, fanInHandle, dependent);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], fanInHandle));
            }
            else
            {
                singleProducer.Add(dependent);
            }
        }

        if (singleProducer.Count == 0)
        {
            return;
        }

        // Inherit-one: the single-producer dependent with the longest downstream chain reuses the
        // producer's directory; ordinal-id tiebreak. The producer's handle carries the RecordedSha
        // that Integrate captured during this settle (strict happens-before).
        string inheritor = singleProducer
            .OrderByDescending(d => context.Graph.TransitiveDependentsOf(d).Count)
            .ThenBy(d => d, StringComparer.Ordinal)
            .First();

        WorktreeHandle? producerHandle =
            _worktreeProvider != null ? context.Handles.GetValueOrDefault(producer.Id) : null;

        foreach (string dependent in singleProducer)
        {
            if (dependent == inheritor && _worktreeProvider is { } reuseProvider && producerHandle is { } ph)
            {
                // Pure handle rewrite — no git, safe under _gate. Ownership of the producer's
                // directory transfers to the inheritor.
                WorktreeHandle reused = reuseProvider.ReuseSegment(ph, dependent, attempt: 1);
                context.Handles[dependent] = reused;
                if (!string.IsNullOrEmpty(reused.WorktreePath))
                {
                    context.DirectoryOwner[reused.WorktreePath] = dependent;
                }
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], reused));
            }
            else if (_worktreeProvider is not null && producerHandle is { } pf)
            {
                // Fork-the-rest: defer the git worktree add to the worker (off-gate). Root off the
                // producer's RECORDED sha — never the live segment-branch tip the inheritor advances.
                var fork = new ForkRequest(pf.RecordedCommitSha);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], new WorktreeHandle(), fork));
            }
            else
            {
                // No provider (serial/fake-less mode): an empty placeholder handle, as before.
                var placeholder = new WorktreeHandle();
                context.Handles[dependent] = placeholder;
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], placeholder));
            }
        }
    }

    /// <summary>Create a fresh segment off the plan-branch tip (or an empty handle without a provider).</summary>
    private WorktreeHandle CreateFreshSegment(RunContext context, string taskId) =>
        _worktreeProvider != null && context.Integ != null
            ? _worktreeProvider.CreateSegment(taskId, attempt: 1, context.Integ, CancellationToken.None)
            : new WorktreeHandle();

    /// <summary>Record directory ownership for a non-empty handle path (M0 bookkeeping; under <see cref="_gate"/>).</summary>
    private static void RecordOwnership(RunContext context, WorktreeHandle handle, string taskId)
    {
        if (!string.IsNullOrEmpty(handle.WorktreePath))
        {
            context.DirectoryOwner[handle.WorktreePath] = taskId;
        }
    }

    /// <summary>
    /// B1 fixed-order settle under <see cref="_integrationLock"/>:
    /// (1) deep-merge fragment into state.json,
    /// (2) git integration commit (FF or non-FF merge),
    /// (3) reserve mergeSequence + journal RecordSettle.
    /// On non-FF failure: restore state.json, reset integration worktree, journal NeedsHuman.
    /// </summary>
    private async Task<TaskResult> SettleAsync(
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        IWorktreeProvider provider,
        IntegrationHandle integ,
        CancellationToken ct)
    {
        string statePath = Path.Combine(_plan.PlanDirectory, "state", "state.json");
        string preMergeState = File.Exists(statePath) ? File.ReadAllText(statePath) : "{}";

        // B1 step 1: merge fragment into state.json BEFORE the git commit.
        if (result.FragmentPath is { } fp && File.Exists(fp))
        {
            MergeFragmentIntoState(statePath, preMergeState, fp);
        }

        // B1 step 2: git integration commit (FF or non-FF union).
        IntegrationResult integResult = provider.Integrate(handle, integ, ct);

        if (integResult == IntegrationResult.FastForward)
        {
            // FF is free — no re-verify needed. Consume one merge sequence.
            long seq = _journal.ReserveMergeSequence();
            _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, seq);
            return result;
        }

        if (integResult == IntegrationResult.Conflict)
        {
            // AI merge worker resolves the conflict (§9.1). If no worker is wired or all
            // attempts fail, escalate to needs-human with a full B1 rollback.
            bool aiResolved = _aiMergeWorker != null
                && await _aiMergeWorker.TryResolveAsync(
                    integ.IntegrationWorktreePath,
                    handle.SegmentBranchName,
                    _plan.PlanDirectory,
                    ct).ConfigureAwait(false);

            if (!aiResolved)
            {
                AtomicFile.WriteAllText(statePath, preMergeState);
                provider.RollbackMerge(integ, ct);
                _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
                return new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.NeedsHuman,
                    ActionExitCode = result.ActionExitCode,
                    Guardrails = result.Guardrails,
                    Summary = "merge conflict could not be AI-resolved; needs human"
                };
            }

            // AI merge succeeded: re-verify using the FULL guardrail set of all colliding
            // siblings unconditionally (B-3: AI may silently drop a sibling's hunk).
            IReadOnlyList<GuardrailDefinition> allGuardrails =
                _plan.Tasks.SelectMany(t => t.Guardrails).ToList();

            ReVerifyResult aiReVerify = _reVerifier != null
                ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, allGuardrails, ct).ConfigureAwait(false)
                : new ReVerifyResult { Passed = true };

            if (aiReVerify.Passed)
            {
                // B2: commit the AI-resolved staged merge with the task trailer BEFORE journaling.
                provider.CommitStagedMerge(integ, task.Id, ct);
                long seq = _journal.ReserveMergeSequence();
                _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, seq);
                return result;
            }

            // Re-verify failed after AI merge: B1 four-effect rollback.
            AtomicFile.WriteAllText(statePath, preMergeState);
            provider.RollbackMerge(integ, ct);
            _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.NeedsHuman,
                ActionExitCode = result.ActionExitCode,
                Guardrails = result.Guardrails,
                Summary = "AI-merge resolution failed re-verify (B-3); needs human"
            };
        }

        // Non-FF union: re-verify the merged bytes in the integration worktree.
        IReadOnlyList<GuardrailDefinition> integGuardrails =
            GuardrailScopeFilter.IntegrationSet(_plan.Tasks.SelectMany(t => t.Guardrails));

        ReVerifyResult reVerify = _reVerifier != null
            ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, integGuardrails, ct).ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        if (reVerify.Passed)
        {
            // B2 step 2: commit the staged non-FF union with the task trailer BEFORE journaling,
            // so the plan branch carries this task's Guardrails-Task: trailer (the FF path commits
            // implicitly via the FF move; the non-FF path must commit the staged merge explicitly).
            provider.CommitStagedMerge(integ, task.Id, ct);
            long seq = _journal.ReserveMergeSequence();
            _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, seq);
            return result;
        }

        // B1 four-effect rollback:
        // 1. Restore state.json (undo fragment merge).
        AtomicFile.WriteAllText(statePath, preMergeState);
        // 2. Reset integration worktree to pre-merge HEAD.
        provider.RollbackMerge(integ, ct);
        // 3. Journal NeedsHuman — mergeSequence NOT consumed.
        _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = result.ActionExitCode,
            Guardrails = result.Guardrails,
            Summary = "non-FF union re-verify failed; rolled back (B1 four-effect)"
        };
    }

    /// <summary>
    /// Shallow-merge <paramref name="fragmentPath"/> into state.json at <paramref name="statePath"/>.
    /// The fragment was already validated (valid JSON object, no foreign keys). Uses atomic write.
    /// </summary>
    private static void MergeFragmentIntoState(string statePath, string preMergeState, string fragmentPath)
    {
        var stateObj = (JsonNode.Parse(preMergeState) as JsonObject) ?? new JsonObject();
        string rawFrag = File.ReadAllText(fragmentPath);
        var fragObj = (JsonNode.Parse(rawFrag) as JsonObject) ?? new JsonObject();

        foreach (var (key, value) in fragObj)
        {
            stateObj[key] = value?.DeepClone();
        }

        AtomicFile.WriteAllText(statePath, stateObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Convert an all-green report into a needs-human one when the terminal integration gate
    /// (§3.3) failed on the final plan-branch HEAD. The failure is attributed to the
    /// <c>integrationGate:true</c> sink task (or, absent one, the last task in plan order) so the
    /// run is not certified and mergeOnSuccess is refused.
    /// </summary>
    private static RunReport WithTerminalGateFailure(
        PlanDefinition plan, RunReport report, ReVerifyResult gate)
    {
        string gateTaskId =
            plan.Tasks.LastOrDefault(t => t.IntegrationGate)?.Id
            ?? plan.Tasks[^1].Id;

        string failed = string.Join(", ", gate.FailedGuardrails.Select(g => g.Name));
        var rewritten = report.Tasks.Select(t => t.TaskId == gateTaskId
            ? t with
            {
                Outcome = TaskOutcome.NeedsHuman,
                Guardrails = gate.FailedGuardrails,
                Summary = $"terminal integration gate failed on final HEAD: {failed}"
            }
            : t).ToList();

        return report with { Tasks = rewritten };
    }

    private static RunReport BuildReport(
        PlanDefinition plan,
        IReadOnlyDictionary<string, TaskResult> settled,
        bool cancelled)
    {
        var results = new List<TaskResult>(plan.Tasks.Count);
        foreach (TaskNode task in plan.Tasks)
        {
            results.Add(settled.TryGetValue(task.Id, out TaskResult? result)
                ? result
                : new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.Cancelled,
                    Summary = "not started (run cancelled)"
                });
        }

        return new RunReport { Tasks = results, Cancelled = cancelled };
    }

    /// <summary>
    /// Per-task channel item pairing a task with its assigned worktree handle. When
    /// <see cref="Fork"/> is non-null the handle is a placeholder and the worker materializes the
    /// real fork worktree off-gate at dequeue (M1 §B); otherwise <see cref="Handle"/> is the final
    /// assigned segment/reused directory.
    /// </summary>
    private readonly record struct TaskEnvelope(TaskNode Task, WorktreeHandle Handle, ForkRequest? Fork = null);

    /// <summary>
    /// A deferred fork-the-rest request (M1 §B): the producer's RECORDED commit sha to fork off
    /// (W-2 — never a live rev-parse of the inheritor-advanced segment branch). Materialized by the
    /// worker via <see cref="IWorktreeProvider.ForkFromTip"/> before the task's action runs.
    /// </summary>
    private readonly record struct ForkRequest(string ProducerRecordedSha);

    /// <summary>Mutable shared state of one run, guarded by the scheduler's gate.</summary>
    private sealed class RunContext(
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> byId,
        Dictionary<string, TaskResult> settled,
        Dictionary<string, int> pendingDeps,
        Channel<TaskEnvelope> channel,
        int remaining,
        Dictionary<string, WorktreeHandle> handles,
        Dictionary<string, string> directoryOwner,
        IntegrationHandle? integ)
    {
        public DependencyGraph Graph { get; } = graph;
        public IReadOnlyDictionary<string, TaskNode> ById { get; } = byId;
        public Dictionary<string, TaskResult> Settled { get; } = settled;
        public Dictionary<string, int> PendingDeps { get; } = pendingDeps;
        public Channel<TaskEnvelope> Channel { get; } = channel;
        public int Remaining { get; set; } = remaining;
        public Dictionary<string, WorktreeHandle> Handles { get; } = handles;

        /// <summary>
        /// plan 08 topology-wiring M0 bookkeeping: worktree path → current owning task id. Written
        /// under <see cref="_gate"/> only (CreateSegment/ForkFromTip set it; ReuseSegment transfers
        /// ownership to the inheritor; Discard removes the entry). The single source of truth for
        /// "is this directory free to Discard / reuse?".
        /// </summary>
        public Dictionary<string, string> DirectoryOwner { get; } = directoryOwner;
        public IntegrationHandle? Integ { get; } = integ;
    }
}
