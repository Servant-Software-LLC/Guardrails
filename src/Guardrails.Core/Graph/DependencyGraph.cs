using Guardrails.Core.Model;

namespace Guardrails.Core.Graph;

/// <summary>
/// The plan's dependency DAG: forward edges (dependency → dependent) computed once from
/// <c>dependsOn</c>, cycle detection with a printable cycle path, execution waves for
/// <c>guardrails plan</c>, and the transitive-dependent closure used to block downstream
/// tasks when one halts. Immutable after construction; safe to share across workers.
/// </summary>
public sealed class DependencyGraph
{
    private readonly IReadOnlyDictionary<string, TaskNode> _tasks;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _dependents;

    public DependencyGraph(IReadOnlyList<TaskNode> tasks)
    {
        _tasks = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

        var dependents = tasks.ToDictionary(t => t.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (TaskNode task in tasks)
        {
            foreach (string dependency in task.DependsOn)
            {
                // Unknown dependencies are a validation error (GR2001) caught before any
                // graph is built; tolerate them here so the graph can serve diagnostics.
                if (dependents.TryGetValue(dependency, out List<string>? list))
                {
                    list.Add(task.Id);
                }
            }
        }

        _dependents = dependents.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.Ordinal);
    }

    /// <summary>The task ids that directly depend on <paramref name="taskId"/>.</summary>
    public IReadOnlyList<string> DependentsOf(string taskId) => _dependents[taskId];

    /// <summary>
    /// Every task <paramref name="taskId"/> depends on, transitively — the ancestor closure
    /// reached by following <c>dependsOn</c> edges. This is the set whose work a task builds
    /// on; used to give the task descriptive context about its ancestors (issue #26 Gap 4).
    /// Unknown ids in <c>dependsOn</c> (a validation error caught earlier) are skipped.
    /// </summary>
    public IReadOnlySet<string> TransitiveDependenciesOf(string taskId)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        if (!_tasks.TryGetValue(taskId, out TaskNode? start))
        {
            return closure;
        }

        var queue = new Queue<string>(start.DependsOn.Where(_tasks.ContainsKey));
        while (queue.TryDequeue(out string? current))
        {
            if (closure.Add(current))
            {
                foreach (string next in _tasks[current].DependsOn.Where(_tasks.ContainsKey))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return closure;
    }

    /// <summary>
    /// Every task reachable from <paramref name="taskId"/> by following dependent edges —
    /// the set that must be blocked when <paramref name="taskId"/> cannot succeed.
    /// </summary>
    public IReadOnlySet<string> TransitiveDependentsOf(string taskId)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(_dependents[taskId]);
        while (queue.TryDequeue(out string? current))
        {
            if (closure.Add(current))
            {
                foreach (string next in _dependents[current])
                {
                    queue.Enqueue(next);
                }
            }
        }

        return closure;
    }

    /// <summary>
    /// Finds a dependency cycle if one exists, returned as a closed path
    /// (<c>a → b → c → a</c>); null when the graph is acyclic.
    /// </summary>
    public IReadOnlyList<string>? FindCycle()
    {
        var state = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (string id in _tasks.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            IReadOnlyList<string>? cycle = Visit(id, state, stack);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    /// <summary>
    /// Execution waves: wave 0 contains tasks with no dependencies; wave N contains tasks
    /// whose deepest dependency sits in wave N−1. Tasks within one wave may run in
    /// parallel (subject to <c>maxParallelism</c>). Throws when the
    /// graph has a cycle — call <see cref="FindCycle"/> first.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TaskNode>> Waves()
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(string id)
        {
            if (depth.TryGetValue(id, out int known))
            {
                return known >= 0
                    ? known
                    : throw new InvalidOperationException($"Dependency cycle involving '{id}'.");
            }

            depth[id] = -1; // in-progress marker
            TaskNode task = _tasks[id];
            int value = task.DependsOn.Count == 0
                ? 0
                : task.DependsOn.Where(_tasks.ContainsKey).Select(DepthOf).DefaultIfEmpty(-1).Max() + 1;
            depth[id] = value;
            return value;
        }

        foreach (string id in _tasks.Keys)
        {
            DepthOf(id);
        }

        return _tasks.Values
            .GroupBy(t => depth[t.Id])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<TaskNode>)g.OrderBy(t => t.Id, StringComparer.Ordinal).ToList())
            .ToList();
    }

    private enum VisitState { Visiting, Done }

    private IReadOnlyList<string>? Visit(string id, Dictionary<string, VisitState> state, List<string> stack)
    {
        if (state.TryGetValue(id, out VisitState seen))
        {
            return seen == VisitState.Visiting ? CloseCycle(stack, id) : null;
        }

        state[id] = VisitState.Visiting;
        stack.Add(id);

        foreach (string dependency in _tasks[id].DependsOn.Where(_tasks.ContainsKey)
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            IReadOnlyList<string>? cycle = Visit(dependency, state, stack);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[id] = VisitState.Done;
        return null;
    }

    private static IReadOnlyList<string> CloseCycle(List<string> stack, string repeated)
    {
        int start = stack.IndexOf(repeated);
        var cycle = stack.Skip(start).ToList();
        cycle.Add(repeated); // close the loop for display: a → b → a
        return cycle;
    }
}
