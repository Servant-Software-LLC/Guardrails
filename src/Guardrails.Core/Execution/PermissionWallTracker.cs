namespace Guardrails.Core.Execution;

/// <summary>
/// Decides when a PERMISSION WALL — a call the runtime refuses because its target is not granted —
/// should settle a task <c>needs-human</c> EARLY instead of burning the remaining retries on the same
/// unrecoverable wall (issues #86 / #104). Runner-agnostic: it consumes only the refused targets the runner
/// reported for each attempt (mined inside the Claude quarantine, never a vendor string) — write paths, and
/// which of them are refused COMMANDS (#708) — and tracks how many attempts each distinct target was refused on.
///
/// <para>Two halt rules, each with a distinct rationale:</para>
/// <list type="number">
/// <item><b>Structural path (issue #104).</b> A wall on a <c>.claude/</c> path is structural — the
///   Claude Code sub-agent runtime blocks automated writes to <c>.claude/</c> even under
///   <c>acceptEdits</c>, so NO number of retries can clear it. One refusal is enough to halt with an
///   actionable reason (grant <c>Write(.claude/**)</c>, or have the task write to a staging path the
///   harness moves into place). Detected on the FIRST attempt that hits it — zero retries wasted. A refused
///   COMMAND is never this wall, even one that names a <c>.claude/</c> path (#708): it is not a write.</item>
/// <item><b>Repeated same target (issue #86).</b> Any other path, or any command, refused on TWO OR MORE
///   attempts is a blocker the agent cannot fix by retrying or switching tools. Halt on the second
///   attempt that re-hits the SAME target, rather than spending the rest of the budget on the identical
///   wall.</item>
/// </list>
///
/// <para>The tracker is per-task and stateful: <see cref="Observe"/> is called once per attempt with
/// that attempt's refused targets; <see cref="ShouldHalt"/> reports whether — given everything observed
/// so far — a wall stands, and which targets are the wall. Which attempts a wall may SETTLE is the
/// executor's decision: a structural wall or a repeated path only one that did not converge (#325 / #708),
/// and a repeated command only one whose action failed (#708).</para>
/// </summary>
public sealed class PermissionWallTracker
{
    /// <summary>How many attempts each distinct refused target has appeared on (insertion-ordered).</summary>
    private readonly Dictionary<string, int> _attemptsByTarget = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>
    /// The targets the runner reported as refused COMMANDS rather than paths (#708). The runner's list carries both,
    /// and reading every target as a path is what made a refused <c>grep</c> over a <c>.claude/</c> directory the
    /// structural write wall, and what listed a refused <c>echo</c> as a "repeatedly-refused path".
    /// </summary>
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);

    /// <summary>
    /// Did the MOST RECENTLY observed attempt refuse anything at all? (#534)
    ///
    /// <para>
    /// Both halt rules below are about a wall the agent <i>cannot get past</i>. Neither is about a wall it
    /// has already got past — and the tracker had no way to tell the difference, because it only ever
    /// accumulated history. Measured on run <c>2026-08-29T16-37-39Z-fc5d</c>, task
    /// <c>08-record-visibility-surfaces-in-ssot</c>:
    /// </para>
    /// <code>
    /// attempt 1  15 Bash calls,  0 refused   guardrail-failed (anchor mismatch)
    /// attempt 2  12 Bash calls,  5 refused   guardrail-failed (anchor mismatch)
    /// attempt 3   0 Bash calls,  0 refused   PERMISSION-DENIED
    /// </code>
    /// <para>
    /// Attempt 3 used <c>Read</c> ×6, <c>Grep</c> ×2, <c>Write</c> ×1 — it had stopped reaching for the
    /// refused command, re-read both targets, and emitted a well-formed root-level
    /// <c>needsHarnessWrite</c> with corrected anchors. The wall fired on attempt 2's HISTORY, settled the
    /// task <c>needs-human</c> with "no further retries", and the corrected fragment was never applied. The
    /// mechanism designed to stop an agent burning attempts on an unclearable wall threw away the attempt
    /// that had cleared it. Cost: $1.27 and a plan stalled at task 8 of 8.
    /// </para>
    /// <para>
    /// So an attempt that refused NOTHING settles on its own merits. This does not weaken either rule: a
    /// structural <c>.claude/</c> wall is detected on the attempt that hits it, and a repeated path halts
    /// on the attempt that re-hits it — in both cases the current attempt is, by construction, not clean.
    /// </para>
    /// </summary>
    private bool _lastAttemptRefusedSomething;

    /// <summary>
    /// The number of attempts a NON-structural path must be refused on before it triggers an early
    /// halt (issue #86). Two = "refused again on the very next attempt" — the first repeat.
    /// </summary>
    public const int RepeatThreshold = 2;

    /// <summary>
    /// Record one attempt's refused targets. Each distinct target increments its attempt count by at
    /// most one per call (a single attempt that refuses the same target many times counts as one attempt
    /// for the repeat rule — the per-attempt repetition is already a wall the agent could not clear,
    /// but the cross-ATTEMPT count is the budget-burn signal #86 targets).
    /// </summary>
    /// <param name="blockedWritePaths">Every target the runner reported refused this attempt, paths and commands alike.</param>
    /// <param name="refusedCommands">
    /// The entries of <paramref name="blockedWritePaths"/> the runner attributed to a refused COMMAND (#708). An entry
    /// named here is never a structural <c>.claude/</c> path, and is kept verbatim rather than quote-trimmed.
    /// </param>
    public void Observe(IReadOnlyList<string>? blockedWritePaths, IReadOnlyList<string>? refusedCommands = null)
    {
        // Set BEFORE the early return: "this attempt refused nothing" is the fact #534 turns on, and it is
        // exactly the case the early return used to discard.
        _lastAttemptRefusedSomething = false;

        if (blockedWritePaths is null || blockedWritePaths.Count == 0)
        {
            return;
        }

        foreach (string raw in blockedWritePaths)
        {
            bool isCommand = refusedCommands is not null && refusedCommands.Contains(raw, StringComparer.Ordinal);
            // #708: a command keeps its quotes. Trimming them the way a path is normalized is what turned plan 40's
            // refused `echo "EXIT:$?"` into `echo "EXIT:$?`.
            string target = isCommand ? raw.Trim() : Normalize(raw);
            if (target.Length == 0)
            {
                continue;
            }

            _lastAttemptRefusedSomething = true;

            if (isCommand)
            {
                _commands.Add(target);
            }

            if (_attemptsByTarget.TryGetValue(target, out int count))
            {
                _attemptsByTarget[target] = count + 1;
            }
            else
            {
                _attemptsByTarget[target] = 1;
                _order.Add(target);
            }
        }
    }

    /// <summary>
    /// Whether a permission wall stands because a path is structural (a <c>.claude/</c> path, seen at least
    /// once) OR a target has repeated across <see cref="RepeatThreshold"/> attempts. Returns the offending
    /// targets (structural paths, repeated paths and repeated commands, each in first-seen order) so the
    /// feedback names the exact wall; empty when no wall stands.
    /// </summary>
    public PermissionWallDecision ShouldHalt()
    {
        // #534: an attempt that refused NOTHING is not standing at a wall, whatever its predecessors hit.
        // Evaluating the accumulated history against a clean attempt is what killed a task that had already
        // recovered — and discarded the deliverable it recovered with.
        if (!_lastAttemptRefusedSomething)
        {
            return new PermissionWallDecision(Halt: false, StructuralPaths: [], RepeatedPaths: [], RepeatedCommands: []);
        }

        var structural = new List<string>();
        var repeatedPaths = new List<string>();
        var repeatedCommands = new List<string>();

        foreach (string target in _order)
        {
            bool repeated = _attemptsByTarget[target] >= RepeatThreshold;
            if (_commands.Contains(target))
            {
                // #708: a refused command that names a .claude/ path is still a command, not the structural write wall.
                if (repeated)
                {
                    repeatedCommands.Add(target);
                }
            }
            else if (IsClaudeDir(target))
            {
                structural.Add(target);
            }
            else if (repeated)
            {
                repeatedPaths.Add(target);
            }
        }

        bool halt = structural.Count > 0 || repeatedPaths.Count > 0 || repeatedCommands.Count > 0;
        return new PermissionWallDecision(halt, structural, repeatedPaths, repeatedCommands);
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
/// The tracker's verdict (<see cref="PermissionWallTracker.ShouldHalt"/>): whether a wall stands, the
/// structurally-blocked <c>.claude/</c> paths (issue #104), the non-structural paths refused on repeated
/// attempts (issue #86), and the commands refused on repeated attempts, kept apart from the paths so no
/// message calls a command a path (#708). <see cref="AllPaths"/> is the de-duplicated union of the paths,
/// structural first.
/// </summary>
public sealed record PermissionWallDecision(
    bool Halt,
    IReadOnlyList<string> StructuralPaths,
    IReadOnlyList<string> RepeatedPaths,
    IReadOnlyList<string> RepeatedCommands)
{
    /// <summary>Every offending path, structural ones first, each in first-seen order, de-duplicated. Commands are not paths.</summary>
    public IReadOnlyList<string> AllPaths => StructuralPaths.Concat(RepeatedPaths).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>True when at least one structural <c>.claude/</c> wall is present (issue #104).</summary>
    public bool HasStructural => StructuralPaths.Count > 0;

    /// <summary>True when a path or a command was refused on repeated attempts (issue #86).</summary>
    public bool HasRepeated => RepeatedPaths.Count > 0 || RepeatedCommands.Count > 0;
}
