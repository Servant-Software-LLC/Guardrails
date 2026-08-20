using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Cli.Ui;

/// <summary>
/// The between-wave JIT breakdown's progress signals and the ONE formatter every surface renders them
/// through (design 23 §10.3, issue #469). The live table's phase row and the <c>--no-ui</c> heartbeat both
/// build their text from the same <see cref="Snapshot"/> through the same fragments, so the two cannot drift
/// into telling an operator different numbers about the same session.
///
/// <para><b>Never invent progress.</b> The eventual task count is NOT knowable at invocation time (design 20
/// §3.2 measured <c>brief.md</c>'s work-item count under-declaring by 3–5×; the count is a RESULT of the
/// session). So nothing here renders a bar, a percentage, or an inferred denominator. It renders elapsed
/// against the one denominator that IS known — the 30-minute BUDGET — plus two independently observed
/// liveness facts, and lets the operator judge:</para>
/// <list type="bullet">
///   <item><b>forward progress</b> — task folders on disk, monotonic, over-counting by at most the in-flight
///     folder;</item>
///   <item><b>stream freshness</b> — whether the runner's teed JSONL grew recently. This is the only signal
///     anywhere that separates "the agent is authoring" from "the agent has emitted nothing for six
///     minutes", the distinction the maintainer had to recover by hand from file mtimes.</item>
/// </list>
///
/// <para><b>Why both.</b> Neither works alone: <c>0 task folders</c> beside <c>stream ok</c> reads correctly
/// as "alive, not yet producing", which is normal for the first ten minutes while the agent reads the
/// materialized worktree. And the freshness fragment becomes a NUMBER only once it carries information
/// (at/above <see cref="StreamFreshSeconds"/>) — below that it is the flat word <c>stream ok</c>, so exactly
/// one digit-string on the row moves per second.</para>
///
/// <para>Everything here is pure except <see cref="Probe"/>, which is the only IO and swallows every fault
/// into "unknown" — it runs on a <see cref="System.Threading.Timer"/> thread, where an unobserved throw
/// would take the process down.</para>
/// </summary>
public static class BreakdownProgress
{
    /// <summary>Disk-probe cadence. The CLOCK still ticks at 1s; only the filesystem stat is throttled.</summary>
    public const int ProbeIntervalSeconds = 2;

    /// <summary>
    /// The <c>--no-ui</c> line cadence. Deliberately NOT <c>GuardrailHeartbeat.IntervalSeconds</c> (15s):
    /// that serves guardrails typically running 1–15 minutes, and this phase runs at twice that scale. 30s
    /// yields ~60 lines over a full ceiling — dense enough that a <c>tail -f</c> reader sees motion every
    /// half-minute, sparse enough that the breakdown does not dominate a CI log.
    /// </summary>
    public const int HeartbeatIntervalSeconds = 30;

    /// <summary>Below this many seconds since the stream last grew ⇒ <c>stream ok</c>; at/above ⇒ <c>stream idle Xs</c>.</summary>
    public const int StreamFreshSeconds = 60;

    /// <summary>Minutes into the ceiling at which the one-shot pre-announcement fires (once, never a countdown).</summary>
    public const int CeilingNoticeMinutes = 25;

    /// <summary>The status word while the session runs — the only non-terminal phase word.</summary>
    public const string AuthoringPhase = "authoring";

    /// <summary>The <c>[tag]</c> this file's plain lines carry, matching <see cref="ConsoleRunObserver"/>'s idiom.</summary>
    public const string PlainTag = "breakdown";

    /// <summary><see cref="Snapshot.TaskFolders"/> sentinel for "the tasks/ directory could not be read" — the fragment is then omitted.</summary>
    public const int UnknownTaskFolders = -1;

    /// <summary>
    /// The observed state of one in-flight breakdown. A value struct so the probe allocates nothing per tick.
    /// </summary>
    /// <param name="TaskFolders">
    /// Task folders on disk carrying a <c>task.json</c>, or <see cref="UnknownTaskFolders"/> when the
    /// directory could not be read (the fragment is then omitted rather than reported as zero, which would
    /// be a fabricated alarm).
    /// </param>
    /// <param name="DeclaredTotal">
    /// The total the SESSION declared in its <c>state/breakdown-intent.json</c> (SSOT §14.11) — the only
    /// honest denominator, because nothing here inferred it. Null when there is no usable manifest, and a
    /// null must NEVER be replaced by a synthesised total.
    /// </param>
    /// <param name="StreamIdle">How long since the teed stream last grew; null when it has never been seen.</param>
    /// <param name="StreamSeen">
    /// False when the stream file has never existed since the phase began (a stub runner, or a runner that
    /// does not tee). The fragment is then omitted ENTIRELY — never rendered as <c>idle 12m</c>, which would
    /// be a fabricated alarm about a file nobody promised to write.
    /// </param>
    public readonly record struct Snapshot(
        int TaskFolders, int? DeclaredTotal, TimeSpan? StreamIdle, bool StreamSeen);

    /// <summary>
    /// The ONLY IO on this type: stat the wave's <c>tasks/</c> folder, the teed stream, and the declared
    /// intent manifest. Swallows every IO fault into "unknown" (see <see cref="Snapshot"/>) because this runs
    /// on a timer thread. Tested against a temp directory, never a running breakdown.
    /// </summary>
    /// <param name="tasksDirectory">The wave's <c>tasks/</c> directory.</param>
    /// <param name="streamLogPath">The runner's teed <c>claude-stream[-segment-N].jsonl</c>.</param>
    /// <param name="intentManifestPath">
    /// The wave's <c>state/breakdown-intent.json</c>, or null when the harness saw none. The wave directory
    /// is recovered from it as the manifest's grandparent — the inverse of
    /// <see cref="BreakdownIntent.PathFor"/>, which is the one place that layout is defined.
    /// </param>
    /// <param name="now">The clock, injected so the freshness arithmetic is testable without waiting.</param>
    public static Snapshot Probe(
        string tasksDirectory, string streamLogPath, string? intentManifestPath, DateTimeOffset now)
    {
        int folders = CountTaskFolders(tasksDirectory);
        (bool seen, TimeSpan? idle) = StreamFreshness(streamLogPath, now);
        return new Snapshot(folders, DeclaredTotal(intentManifestPath), idle, seen);
    }

    /// <summary>
    /// The RUNNING status cell: <c>authoring 7:12 / 30:00</c>. The denominator is the BUDGET, not the work —
    /// it says how much time is left, which is exactly the question an operator deciding whether to keep
    /// waiting is asking, and is never a completion estimate. Both halves use the live table's shipped
    /// stopwatch format so the phase row reads like every other running row.
    /// </summary>
    public static string StatusMarkup(TimeSpan elapsed, TimeSpan ceiling, string phase) =>
        $"{phase} {FormatElapsed(elapsed)} / {FormatElapsed(ceiling)}";

    /// <summary>
    /// A SETTLED phase's status cell: the outcome word plus the session's final clock (<c>authored 18:42</c>,
    /// <c>cut off 30:00</c>). No ceiling — a finished session's remaining budget is not a fact anyone needs.
    /// </summary>
    public static string TerminalStatus(string word, TimeSpan elapsed) => $"{word} {FormatElapsed(elapsed)}";

    /// <summary>
    /// The settled phase's status WORD, keyed on the reason it did not end cleanly (null ⇒ it did). Every
    /// state is distinguished by the WORD, never by colour alone, so a no-colour terminal loses nothing:
    /// <c>authored</c> · <c>cut off</c> · <c>incomplete</c> · <c>invalid</c> · <c>faulted</c>.
    /// </summary>
    public static string TerminalWord(string? failureKind) => failureKind switch
    {
        null => "authored",
        BreakdownFailureTokens.Incomplete => "incomplete",
        BreakdownFailureTokens.Invalid => "invalid",
        BreakdownFailureTokens.Error => "faulted",
        _ => "cut off"
    };

    /// <summary>
    /// The settled phase's detail text. The bound that was hit is NAMED — <c>timeout</c> and
    /// <c>max-turns</c> are two different remedies and only one of them is a budget (design 20 milestone 1)
    /// — and the count is stated wherever it is meaningful. The full accounting is in the halt that follows;
    /// this is the one line that stays on the table.
    /// </summary>
    public static string TerminalDetail(string? failureKind, Snapshot s)
    {
        string count = CountFragment(s) ?? "task count unavailable";
        return failureKind switch
        {
            null => count,
            BreakdownFailureTokens.Incomplete => $"{count} — prefix kept",
            BreakdownFailureTokens.Invalid => "the authored wave failed 'guardrails validate' — see the halt below",
            BreakdownFailureTokens.Error => "runner fault — see the halt below",
            _ => $"{failureKind} after {count}"
        };
    }

    /// <summary>
    /// The <c>--no-ui</c> settlement line. Same word and same detail as the live row's terminal cell, so the
    /// two surfaces cannot disagree about how the session ended.
    /// </summary>
    public static string PlainFinishLine(string waveDir, string? failureKind, TimeSpan elapsed, Snapshot s) =>
        $"[{PlainTag}] {waveDir}: {TerminalWord(failureKind).ToUpperInvariant()} after "
        + $"{FormatClock(elapsed)} — {TerminalDetail(failureKind, s)}";

    /// <summary>
    /// The RUNNING detail cell: the observed fragments, <c>·</c>-joined in the order
    /// <b>count, then stream</b>. That order is deliberate — the Detail cell is the elastic column, so on an
    /// 80-column terminal the two decision-critical facts survive the wrap and only the caller's trailing
    /// link is lost. Emits no Spectre markup tags (there is nothing here to colour, and the same string is
    /// reused verbatim by the plain-text surface).
    /// </summary>
    public static string DetailMarkup(Snapshot s) => string.Join(" · ", Fragments(s));

    /// <summary>
    /// The <c>--no-ui</c> heartbeat line, built from the SAME fragments as <see cref="DetailMarkup"/> so the
    /// two surfaces cannot report different counts for one <see cref="Snapshot"/>.
    /// </summary>
    public static string PlainLine(string waveDir, TimeSpan elapsed, TimeSpan ceiling, Snapshot s)
    {
        string head = $"[{PlainTag}] {waveDir}: {FormatClock(elapsed)} / {FormatClock(ceiling)}";
        IReadOnlyList<string> fragments = Fragments(s);
        return fragments.Count == 0 ? head : $"{head} — {string.Join(", ", fragments)}";
    }

    /// <summary>
    /// The observed fragments for one snapshot, in render order. The SINGLE source both surfaces read, so a
    /// change to what is counted lands on both at once.
    /// </summary>
    public static IReadOnlyList<string> Fragments(Snapshot s)
    {
        var fragments = new List<string>(2);
        if (CountFragment(s) is { } count)
        {
            fragments.Add(count);
        }

        if (StreamFragment(s) is { } stream)
        {
            fragments.Add(stream);
        }

        return fragments;
    }

    /// <summary>
    /// The forward-progress fragment: <c>9/14 declared</c> when the session declared a total,
    /// <c>5 task folders</c> when it did not, and NOTHING when the directory could not be read. There is no
    /// third form: a missing manifest never yields a synthesised denominator (design 23 §6.3).
    /// </summary>
    public static string? CountFragment(Snapshot s)
    {
        if (s.TaskFolders < 0)
        {
            return null;
        }

        return s.DeclaredTotal is { } declared
            ? $"{s.TaskFolders}/{declared} declared"
            : $"{s.TaskFolders} task folder{(s.TaskFolders == 1 ? "" : "s")}";
    }

    /// <summary>
    /// The liveness fragment: <c>stream ok</c> below <see cref="StreamFreshSeconds"/>, <c>stream idle
    /// 4m18s</c> at or above it, and NOTHING when the stream has never been seen. Silence over a lie: if a
    /// runner never tees, an <c>idle 12m</c> here would be an alarm about a file nobody promised to write.
    /// </summary>
    public static string? StreamFragment(Snapshot s)
    {
        if (!s.StreamSeen || s.StreamIdle is not { } idle)
        {
            return null;
        }

        return idle.TotalSeconds >= StreamFreshSeconds ? $"stream idle {FormatClock(idle)}" : "stream ok";
    }

    /// <summary>
    /// Stopwatch-style elapsed: <c>0:42</c>, <c>12:05</c>, <c>1:03:20</c>. The live table's shipped format,
    /// owned here so the phase row and the task rows cannot diverge.
    /// </summary>
    public static string FormatElapsed(TimeSpan e)
    {
        if (e < TimeSpan.Zero)
        {
            e = TimeSpan.Zero;
        }

        return e.TotalHours >= 1
            ? $"{(int)e.TotalHours}:{e.Minutes:D2}:{e.Seconds:D2}"
            : $"{e.Minutes}:{e.Seconds:D2}";
    }

    /// <summary>
    /// Compact stopwatch text for PLAIN lines: <c>45s</c>, <c>4m32s</c>, <c>1h04m</c> — the idiom
    /// <see cref="GuardrailHeartbeat"/> and <c>TaskExecutor.FormatDuration</c> already print, so a
    /// <c>--no-ui</c> log carries one duration format, not two.
    /// </summary>
    public static string FormatClock(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}h{d.Minutes:D2}m";
        }

        return d.TotalMinutes >= 1
            ? $"{(int)d.TotalMinutes}m{d.Seconds:D2}s"
            : $"{(int)d.TotalSeconds}s";
    }

    // --- probes -----------------------------------------------------------------------------

    private static int CountTaskFolders(string tasksDirectory)
    {
        try
        {
            if (!Directory.Exists(tasksDirectory))
            {
                return 0; // the stub's tasks/ may not exist yet — that is honestly zero, not unknown
            }

            int count = 0;
            foreach (string dir in Directory.EnumerateDirectories(tasksDirectory))
            {
                if (File.Exists(Path.Combine(dir, "task.json")))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnknownTaskFolders;
        }
    }

    private static (bool Seen, TimeSpan? Idle) StreamFreshness(string streamLogPath, DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(streamLogPath);
            if (!info.Exists)
            {
                return (false, null);
            }

            TimeSpan idle = now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return (true, idle < TimeSpan.Zero ? TimeSpan.Zero : idle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, null);
        }
    }

    private static int? DeclaredTotal(string? intentManifestPath)
    {
        if (string.IsNullOrEmpty(intentManifestPath))
        {
            return null;
        }

        try
        {
            // The manifest lives at <wave>/state/breakdown-intent.json (BreakdownIntent.PathFor), so the
            // wave directory is its grandparent. Reading through BreakdownIntent keeps the "what counts as
            // a declared folder" rule in its one owner rather than re-parsing the file here.
            string? stateDir = Path.GetDirectoryName(intentManifestPath);
            string? waveDir = stateDir is null ? null : Path.GetDirectoryName(stateDir);
            if (waveDir is null)
            {
                return null;
            }

            int declared = BreakdownIntent.TryRead(waveDir)?.DeclaredFolders().Count ?? 0;
            return declared > 0 ? declared : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
