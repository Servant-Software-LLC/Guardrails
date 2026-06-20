using System.Threading.Channels;
using Guardrails.Core.Graph;
using Guardrails.Core.Model;
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

    private readonly object _gate = new();

    // First unexpected (non-cancellation) executor fault wins; surfaced after WhenAll so the
    // run terminates deterministically with a harness error instead of hanging (see WorkerLoopAsync).
    private Exception? _fault;

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        ISchedulerJournal journal,
        IWorktreeProvider? worktreeProvider = null,
        IRunObserver? observer = null,
        int? maxParallelism = null)
    {
        _plan = plan;
        _executor = executor;
        _journal = journal;
        _worktreeProvider = worktreeProvider;
        _observer = observer ?? IRunObserver.Null;
        _maxParallelism = Math.Max(1, maxParallelism ?? plan.Config.MaxParallelism);
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

        // Create the integration handle once for this run (M1 seam; M2 will do real git).
        IntegrationHandle? integ = _worktreeProvider?.CreateIntegration(
            planName: Path.GetFileName(plan.PlanDirectory),
            runId: Guid.NewGuid().ToString("N")[..8],
            cancellationToken);

        // --- resume pre-pass: journaled successes are green without re-running ----------
        var preSettledGreen = new HashSet<string>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (_journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded)
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

        // Pre-create worktree handles for every task that will run. Handles are built upfront
        // (single-threaded, before workers start) so OnSettled can enqueue them without needing
        // a CancellationToken or taking extra locks.
        var handles = new Dictionary<string, WorktreeHandle>(StringComparer.Ordinal);
        foreach (TaskNode task in plan.Tasks)
        {
            if (!preSettledGreen.Contains(task.Id))
            {
                handles[task.Id] = _worktreeProvider != null && integ != null
                    ? _worktreeProvider.CreateSegment(task.Id, attempt: 1, integ, cancellationToken)
                    : new WorktreeHandle();
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
        // A run-scoped CTS linked to the caller's token: the workers honor the caller's
        // cancellation AND an unexpected executor fault cancels it internally, so sibling
        // workers drain instead of blocking forever on a channel that would never complete.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new RunContext(graph, byId, settled, pendingDeps, channel, remaining, handles, integ);
        int workerCount = Math.Min(_maxParallelism, remaining);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(context, runCts), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        // An unexpected (non-cancellation) executor throw is a harness fault, not an actionable
        // task verdict: surface it so the run terminates with a non-zero (harness-error) exit
        // (SSOT §7) rather than hanging or silently degrading the report.
        if (_fault is { } fault)
        {
            throw new InvalidOperationException(
                $"A task executor threw an unexpected exception; the run was aborted: {fault.Message}",
                fault);
        }

        return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested);
    }

    private async Task WorkerLoopAsync(RunContext context, CancellationTokenSource runCts)
    {
        CancellationToken cancellationToken = runCts.Token;
        try
        {
            await foreach (TaskEnvelope envelope in context.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                TaskNode task = envelope.Task;
                WorktreeHandle handle = envelope.Handle;

                // Per-run cost cap (SSOT §2 / plan 04): if the journal's cumulative cost has reached
                // the configured cap, do NOT launch this attempt. Settle the task needs-human and let
                // OnSettled block its transitive dependents via the existing halt path. The check is
                // against cumulative journaled cost, so resumes account for prior spend; an attempt
                // already in flight on another worker is never interrupted — the cap only gates new
                // launches.
                if (CostCapHaltFor(task) is { } capped)
                {
                    OnSettled(context, task, capped, handle);
                    continue;
                }

                TaskResult result = await _executor.ExecuteAsync(task, handle, cancellationToken).ConfigureAwait(false);

                OnSettled(context, task, result, handle);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled drain: in-flight attempts were journaled by the executor; the
            // report marks the run cancelled. (Also reached when an executor fault on a
            // sibling worker cancels runCts — the fault itself is recorded below.)
        }
        catch (Exception ex)
        {
            // Unexpected executor fault: record the first one (first-wins), then cancel the
            // run-scoped token and complete the channel so every sibling worker breaks out of
            // ReadAllAsync and drains. Without this the channel would never complete and
            // Task.WhenAll — hence the whole run — would hang.
            lock (_gate)
            {
                _fault ??= ex;
            }

            context.Channel.Writer.TryComplete();
            runCts.Cancel();
        }
    }

    /// <summary>
    /// If the per-run cost cap (<see cref="RunConfig.MaxCostUsd"/>) is set and the journal's
    /// cumulative cost has reached it, return the <c>needs-human</c> result that settles
    /// <paramref name="task"/> without launching its attempt; otherwise null (launch normally).
    /// </summary>
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

    private void OnSettled(RunContext context, TaskNode task, TaskResult result, WorktreeHandle handle)
    {
        var newlyReady = new List<TaskNode>();
        var newlyBlocked = new List<TaskResult>();

        lock (_gate)
        {
            // Integrate under the gate so the provider's counter is serialized across workers.
            if (result.IsGreen && _worktreeProvider is { } provider && context.Integ is { } integ)
            {
                provider.Integrate(handle, integ, CancellationToken.None);
            }

            context.Settled[task.Id] = result;
            context.Remaining--;

            if (result.IsGreen)
            {
                foreach (string dependent in context.Graph.DependentsOf(task.Id))
                {
                    if (!context.Settled.ContainsKey(dependent) && --context.PendingDeps[dependent] == 0)
                    {
                        newlyReady.Add(context.ById[dependent]);
                    }
                }
            }
            else if (result.Outcome != TaskOutcome.Cancelled)
            {
                // Block the transitive closure now: those tasks can never become ready,
                // and counting them settled lets independent branches finish the run.
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

        foreach (TaskNode ready in newlyReady)
        {
            WorktreeHandle readyHandle = context.Handles.GetValueOrDefault(ready.Id) ?? new WorktreeHandle();
            context.Channel.Writer.TryWrite(new TaskEnvelope(ready, readyHandle));
        }
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

    /// <summary>Per-task channel item pairing a task with its assigned worktree handle.</summary>
    private readonly record struct TaskEnvelope(TaskNode Task, WorktreeHandle Handle);

    /// <summary>Mutable shared state of one run, guarded by the scheduler's gate.</summary>
    private sealed class RunContext(
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> byId,
        Dictionary<string, TaskResult> settled,
        Dictionary<string, int> pendingDeps,
        Channel<TaskEnvelope> channel,
        int remaining,
        IReadOnlyDictionary<string, WorktreeHandle> handles,
        IntegrationHandle? integ)
    {
        public DependencyGraph Graph { get; } = graph;
        public IReadOnlyDictionary<string, TaskNode> ById { get; } = byId;
        public Dictionary<string, TaskResult> Settled { get; } = settled;
        public Dictionary<string, int> PendingDeps { get; } = pendingDeps;
        public Channel<TaskEnvelope> Channel { get; } = channel;
        public int Remaining { get; set; } = remaining;
        public IReadOnlyDictionary<string, WorktreeHandle> Handles { get; } = handles;
        public IntegrationHandle? Integ { get; } = integ;
    }
}
