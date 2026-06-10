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
/// surfaces every needs-human halt instead of one per run. Tasks with
/// <c>exclusive: true</c> (the default for prompt actions) hold the
/// <see cref="WorkspaceLock"/> exclusively and run alone.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly ISchedulerJournal _journal;
    private readonly IRunObserver _observer;
    private readonly int _maxParallelism;

    private readonly object _gate = new();
    private readonly WorkspaceLock _workspaceLock = new();

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        ISchedulerJournal journal,
        IRunObserver? observer = null,
        int? maxParallelism = null)
    {
        _plan = plan;
        _executor = executor;
        _journal = journal;
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
        var channel = Channel.CreateUnbounded<TaskNode>();

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

        foreach (TaskNode task in plan.Tasks)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                channel.Writer.TryWrite(task);
            }
        }

        // --- workers ---------------------------------------------------------------------
        var context = new RunContext(graph, byId, settled, pendingDeps, channel, remaining);
        int workerCount = Math.Min(_maxParallelism, remaining);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(context, cancellationToken), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested);
    }

    private async Task WorkerLoopAsync(RunContext context, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TaskNode task in context.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                bool exclusive = task.Exclusive ?? task.Action.Kind == ActionKind.Prompt;
                await _workspaceLock.AcquireAsync(exclusive, cancellationToken).ConfigureAwait(false);

                TaskResult result;
                try
                {
                    result = await _executor.ExecuteAsync(task, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _workspaceLock.Release(exclusive);
                }

                OnSettled(context, task, result);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled drain: in-flight attempts were journaled by the executor; the
            // report marks the run cancelled.
        }
    }

    private void OnSettled(RunContext context, TaskNode task, TaskResult result)
    {
        var newlyReady = new List<TaskNode>();
        var newlyBlocked = new List<TaskResult>();

        lock (_gate)
        {
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
            context.Channel.Writer.TryWrite(ready);
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

    /// <summary>Mutable shared state of one run, guarded by the scheduler's gate.</summary>
    private sealed class RunContext(
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> byId,
        Dictionary<string, TaskResult> settled,
        Dictionary<string, int> pendingDeps,
        Channel<TaskNode> channel,
        int remaining)
    {
        public DependencyGraph Graph { get; } = graph;
        public IReadOnlyDictionary<string, TaskNode> ById { get; } = byId;
        public Dictionary<string, TaskResult> Settled { get; } = settled;
        public Dictionary<string, int> PendingDeps { get; } = pendingDeps;
        public Channel<TaskNode> Channel { get; } = channel;
        public int Remaining { get; set; } = remaining;
    }
}
