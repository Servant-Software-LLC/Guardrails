namespace Guardrails.Core.Execution;

/// <summary>
/// Decides when a PERMISSION WALL — a write/edit the runtime refuses because the path is not granted —
/// should settle a task <c>needs-human</c> EARLY instead of burning the remaining retries on the same
/// unrecoverable wall (issues #86 / #104). Runner-agnostic: it consumes only the list of refused paths
/// the runner reported for each attempt (mined inside the Claude quarantine, never a vendor string),
/// and tracks how many attempts each distinct path has been refused on.
///
/// <para>Two halt rules, each with a distinct rationale:</para>
/// <list type="number">
/// <item><b>Structural path (issue #104).</b> A wall on a <c>.claude/</c> path is structural — the
///   Claude Code sub-agent runtime blocks automated writes to <c>.claude/</c> even under
///   <c>acceptEdits</c>, so NO number of retries can clear it. One refusal is enough to halt with an
///   actionable reason (grant <c>Write(.claude/**)</c>, or have the task write to a staging path the
///   harness moves into place). Detected on the FIRST attempt that hits it — zero retries wasted.</item>
/// <item><b>Repeated same path (issue #86).</b> Any other path refused on TWO OR MORE attempts is a
///   structural blocker the agent cannot fix by retrying or switching tools. Halt on the second
///   attempt that re-hits the SAME path, rather than spending the rest of the budget on the identical
///   wall.</item>
/// </list>
///
/// <para>The tracker is per-task and stateful: <see cref="Observe"/> is called once per attempt with
/// that attempt's refused paths; <see cref="ShouldHalt"/> reports whether — given everything observed
/// so far — the task should settle <c>needs-human</c> now, and which paths are the wall.</para>
/// </summary>
public sealed class PermissionWallTracker
{
    /// <summary>How many attempts each distinct refused path has appeared on (insertion-ordered).</summary>
    private readonly Dictionary<string, int> _attemptsByPath = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>
    /// The number of attempts a NON-structural path must be refused on before it triggers an early
    /// halt (issue #86). Two = "refused again on the very next attempt" — the first repeat.
    /// </summary>
    public const int RepeatThreshold = 2;

    /// <summary>
    /// Record one attempt's refused write paths. Each distinct path increments its attempt count by at
    /// most one per call (a single attempt that refuses the same path many times counts as one attempt
    /// for the repeat rule — the per-attempt repetition is already a wall the agent could not clear,
    /// but the cross-ATTEMPT count is the budget-burn signal #86 targets).
    /// </summary>
    public void Observe(IReadOnlyList<string>? blockedWritePaths)
    {
        if (blockedWritePaths is null || blockedWritePaths.Count == 0)
        {
            return;
        }

        foreach (string raw in blockedWritePaths)
        {
            string path = Normalize(raw);
            if (path.Length == 0)
            {
                continue;
            }

            if (_attemptsByPath.TryGetValue(path, out int count))
            {
                _attemptsByPath[path] = count + 1;
            }
            else
            {
                _attemptsByPath[path] = 1;
                _order.Add(path);
            }
        }
    }

    /// <summary>
    /// Whether the task should settle <c>needs-human</c> now because a permission wall is structural
    /// (a <c>.claude/</c> path, seen at least once) OR has repeated across <see cref="RepeatThreshold"/>
    /// attempts. Returns the offending paths (structural ones first, then repeated ones, each in
    /// first-seen order) so the feedback names the exact wall; empty when no halt is warranted.
    /// </summary>
    public PermissionWallDecision ShouldHalt()
    {
        var structural = new List<string>();
        var repeated = new List<string>();

        foreach (string path in _order)
        {
            if (IsClaudeDir(path))
            {
                structural.Add(path);
            }
            else if (_attemptsByPath[path] >= RepeatThreshold)
            {
                repeated.Add(path);
            }
        }

        bool halt = structural.Count > 0 || repeated.Count > 0;
        return new PermissionWallDecision(halt, structural, repeated);
    }

    /// <summary>
    /// True when <paramref name="path"/> targets the <c>.claude/</c> tree — the structurally-blocked
    /// destination of issue #104. Matches a leading <c>.claude/</c>, any <c>/.claude/</c> or
    /// <c>\.claude\</c> segment, so an absolute, repo-relative, or workspace-relative path all hit.
    /// </summary>
    public static bool IsClaudeDir(string path)
    {
        string p = path.Replace('\\', '/');
        return p.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("/.claude/", StringComparison.OrdinalIgnoreCase) ||
               p.Equals(".claude", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith("/.claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trim and strip surrounding quotes so the same path compares equal across attempts.</summary>
    private static string Normalize(string path) => path.Trim().Trim('"', '\'', '`');
}

/// <summary>
/// The tracker's verdict (<see cref="PermissionWallTracker.ShouldHalt"/>): whether to halt now, the
/// structurally-blocked <c>.claude/</c> paths (issue #104), and the non-structural paths refused on
/// repeated attempts (issue #86). <see cref="AllPaths"/> is the de-duplicated union, structural first.
/// </summary>
public sealed record PermissionWallDecision(
    bool Halt,
    IReadOnlyList<string> StructuralPaths,
    IReadOnlyList<string> RepeatedPaths)
{
    /// <summary>Every offending path, structural ones first, each in first-seen order, de-duplicated.</summary>
    public IReadOnlyList<string> AllPaths => StructuralPaths.Concat(RepeatedPaths).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>True when at least one structural <c>.claude/</c> wall is present (issue #104).</summary>
    public bool HasStructural => StructuralPaths.Count > 0;
}
