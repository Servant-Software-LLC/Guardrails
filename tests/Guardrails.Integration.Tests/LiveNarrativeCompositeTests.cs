using System.Text.RegularExpressions;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Design 37 §7.4 — the #372 invariant, ASSERTED rather than eyeballed.
///
/// <para>Before design 37, <see cref="LiveRunObserver"/> wrote twelve kinds of line with
/// <c>AnsiConsole.MarkupLine</c> from INSIDE the Spectre Live region. Spectre's <c>LiveRenderable</c>
/// remembers the height it drew and moves the cursor up by that amount on the next refresh; a raw write
/// advances the cursor without updating that bookkeeping, so the next repaint lands one row low and stamps
/// the table's <c>TableBorder.Rounded</c> glyphs THROUGH the line just written.</para>
///
/// <para><b>What this harness can and cannot prove — read this before adding an assertion here.</b>
/// <see cref="TestConsole"/> is a plain writer with no cursor control, so it cannot REPRODUCE the overprint;
/// no harness without a cursor can, and any assertion phrased as "no rendered line carries both narrative
/// text and a border glyph" is therefore incapable of failing for ANY input. What this harness CAN do is
/// separate the two mechanisms that produce the overprint, and that separation is exact:</para>
/// <list type="bullet">
///   <item>a line inside the Live TARGET is part of the renderable Spectre repaints, so it is re-emitted in
///     EVERY frame from the moment it is appended until the budget evicts it; while</item>
///   <item>a raw write reaches the writer exactly ONCE, at the point of emission, and never appears in any
///     later frame.</item>
/// </list>
///
/// <para>So the discriminator these tests use is <see cref="AssertRenderedInsideTheLiveRegion"/>: present in
/// the frame where it was emitted AND in every frame after it, the LAST frame included. Asserting merely
/// "present somewhere in <c>console.Output</c>" does NOT discriminate — a raw write satisfies that too, which
/// is how #372's own defect, re-introduced through the injected <c>_console</c> field rather than the global
/// <see cref="AnsiConsole"/>, passed an earlier revision of this whole class.</para>
///
/// <para>The residual — that a raw write CORRUPTS rather than merely escapes, on a real cursor-bearing
/// terminal — stays eyeball-only by construction, and design 37 §7.4 says so in those words.</para>
///
/// <para><b>On <c>LiveDisplayCollection</c>:</b> these tests deliberately do NOT join it, and
/// <see cref="TestConsole_OwnsItsExclusivityMode_WhichIsWhyThisClassNeedNotJoinLiveDisplayCollection"/>
/// is the assertion that keeps that safe. Spectre's exclusivity lock is per-<c>IAnsiConsole</c>, not
/// per-process; it only LOOKS process-wide because every other live test drives the one shared
/// <see cref="AnsiConsole.Console"/> instance. If that ever stops being true, that test goes red here
/// rather than as a misattributed teardown failure in some other class — which is the failure mode
/// <see cref="LiveDisplayCollection"/> documents.</para>
/// </summary>
public sealed class LiveNarrativeCompositeTests
{
    private const char TopLeft = '╭';
    private const char BottomRight = '╯';
    private static readonly char[] BorderGlyphs = ['╭', '╮', '╰', '╯', '│', '─', '├', '┤', '┬', '┴', '┼'];

    private static TestConsole Console(int width = 100) =>
        new TestConsole().Width(width).Interactive();

    private static ActionDefinition Action(string dir) => new() { Path = $"{dir}/action.sh", Kind = ActionKind.Script };

    private static GuardrailDefinition Guardrail(string dir) =>
        new() { Name = "01-check", Path = $"{dir}/guardrails/01-check.sh", Kind = ActionKind.Script };

    private static TaskNode Task(string? waveDir, string folder)
    {
        string dir = waveDir is null ? $"/fake/plan/tasks/{folder}" : $"/fake/plan/{waveDir}/tasks/{folder}";
        return new TaskNode
        {
            Id = waveDir is null ? folder : $"{waveDir}/{folder}",
            WaveDir = waveDir,
            Directory = dir,
            Description = $"fixture — {folder}",
            Action = Action(dir),
            Guardrails = [Guardrail(dir)]
        };
    }

    private static WaveNode Wave(string dir, int number, params TaskNode[] tasks) => new()
    {
        Dir = dir,
        Number = number,
        Slug = dir.Split('-', 3)[2],
        Directory = $"/fake/plan/{dir}",
        Tasks = tasks
    };

    /// <summary>A throwaway tree — the breakdown phase probe stats real paths.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-372-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    private static DecisionEntry Decision(string boundary, string subject, string headline) => new()
    {
        Boundary = boundary,
        Policy = "auto",
        Decision = "auto-applied",
        Subject = subject,
        Headline = headline
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Frames — the anchor every behavioural assertion below hangs off.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rendered lines, trailing padding removed and blanks dropped.
    ///
    /// <para><b>Why the extra split on <c>╯</c>.</b> A real terminal repositions the cursor between frames;
    /// <see cref="TestConsole"/> is a plain writer with no cursor control, so it emits each frame back to back
    /// and frame N's closing bottom-right corner runs into frame N+1's first character with no newline
    /// between them. That is an artifact of the harness, not of the renderer, so the corner is treated as the
    /// frame terminator it is.</para>
    /// </summary>
    private static IReadOnlyList<string> RenderedLines(TestConsole console) =>
    [
        .. console.Output
            .Replace("╯", "╯\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd('\r', ' '))
            .Where(l => l.Length > 0)
    ];

    /// <summary>
    /// Every frame the Live region painted, in order — each one the WHOLE composite
    /// (<c>Rows([…narrative…, table])</c>, or the bare table while the pane is empty), terminated by the
    /// table's bottom-right corner.
    ///
    /// <para>Any text left over after the final frame was written OUTSIDE the Live region — the #372 shape,
    /// in the special case where the offending emitter fired last — so it fails here rather than being
    /// silently discarded as an incomplete tail.</para>
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> Frames(TestConsole console)
    {
        var frames = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (string line in RenderedLines(console))
        {
            current.Add(line);
            if (line.Contains(BottomRight, StringComparison.Ordinal))
            {
                frames.Add(current);
                current = [];
            }
        }

        Assert.True(
            current.Count == 0,
            "text reached the console AFTER the Live region painted its final frame, so it was never part of "
            + "any frame — an out-of-band write (#372):\n  " + string.Join("\n  ", current));

        Assert.NotEmpty(frames);
        return frames;
    }

    /// <summary>The last frame — what is on the operator's screen when the run ends.</summary>
    private static IReadOnlyList<string> LastFrame(TestConsole console) => Frames(console)[^1];

    /// <summary>
    /// <b>The #372 discriminator.</b> <paramref name="text"/> must be part of the Live region's TARGET, not
    /// merely present somewhere in the byte stream: it is asserted present in the frame where it first
    /// appears and in every frame after that one, the LAST frame included.
    ///
    /// <para>A raw write — <c>AnsiConsole.MarkupLine</c>, or the same call reached through the injected
    /// <c>_console</c> field — reaches the writer once and is absent from every subsequent repaint, so it
    /// fails here. "Contained in <c>console.Output</c>" does not distinguish the two and must never be the
    /// assertion.</para>
    ///
    /// <para>At least one frame must follow the first appearance, or the property is vacuous (a line emitted
    /// after the final repaint would trivially satisfy "in the last frame"). Disposal always repaints, so
    /// every test here clears that bar; the assertion exists so a future test that stops driving events
    /// cannot quietly go blind.</para>
    /// </summary>
    private static void AssertRenderedInsideTheLiveRegion(TestConsole console, string text)
    {
        IReadOnlyList<IReadOnlyList<string>> frames = Frames(console);
        int first = -1;
        for (int i = 0; i < frames.Count && first < 0; i++)
        {
            if (Holds(frames[i], text))
            {
                first = i;
            }
        }

        Assert.True(
            first >= 0,
            $"'{text}' was never rendered inside the Live region at all (searched {frames.Count} frames).");

        Assert.True(
            first < frames.Count - 1,
            $"'{text}' appears ONLY in the final frame, so nothing repainted after it and this assertion "
            + "proves nothing about where it came from — drive one more event before asserting.");

        for (int i = first + 1; i < frames.Count; i++)
        {
            Assert.True(
                Holds(frames[i], text),
                $"'{text}' was in frame {first} and is GONE from frame {i} of {frames.Count - 1}. That is the "
                + "signature of a RAW WRITE (#372): emitted once beside the Live region instead of being part "
                + "of the target Spectre repaints, so it never appears in a later frame.");
        }

        static bool Holds(IReadOnlyList<string> frame, string text) =>
            frame.Any(l => l.Contains(text, StringComparison.Ordinal));
    }

    /// <summary>
    /// <paramref name="text"/> renders in the PANE of the last frame: on a line above the table's top border
    /// (the composite is <c>Rows([…narrative…, table])</c>) that carries no border glyph of its own, i.e. it
    /// is a narrative entry rather than the contents of a table cell.
    ///
    /// <para>This is a PLACEMENT assertion, not the #372 one — it distinguishes "rendered in the pane" from
    /// "rendered inside a cell", and cannot detect an out-of-band write.
    /// <see cref="AssertRenderedInsideTheLiveRegion"/> is the assertion that does that.</para>
    /// </summary>
    private static void AssertInThePaneOfTheLastFrame(TestConsole console, string text)
    {
        IReadOnlyList<string> frame = LastFrame(console);
        int at = -1;
        int table = -1;
        for (int i = 0; i < frame.Count; i++)
        {
            if (at < 0 && frame[i].Contains(text, StringComparison.Ordinal))
            {
                at = i;
            }

            if (table < 0 && frame[i].Contains(TopLeft, StringComparison.Ordinal))
            {
                table = i;
            }
        }

        Assert.True(at >= 0, $"'{text}' is not in the last rendered frame:\n{string.Join("\n", frame)}");
        Assert.True(table > at, $"'{text}' rendered at or below the table's top border, not in the pane above it.");
        Assert.False(
            HasBorderGlyph(frame[at]),
            $"'{text}' rendered INSIDE a table cell rather than as a pane entry: '{frame[at]}'");
    }

    private static bool HasBorderGlyph(string line) => line.IndexOfAny(BorderGlyphs) >= 0;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The #372 invariant in its MECHANICAL form — the source census.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The members of an <see cref="IAnsiConsole"/> this file may touch. <c>Live</c> creates the region that
    /// OWNS the cursor; <c>Profile</c> is a read (the width the §4.3 budget keys off). Every other member of
    /// that interface and of its extension surface either writes or moves the cursor, and doing either from
    /// inside the region is exactly #372.
    /// </summary>
    private static readonly string[] NonWritingConsoleMembers = ["Live", "Profile"];

    /// <summary>
    /// Design 37 §4.1: "Zero <c>AnsiConsole.MarkupLine</c> calls remain in <see cref="LiveRunObserver"/>.
    /// That is the #372 invariant, and it is mechanically checkable."
    ///
    /// <para>The behavioural tests below prove that the ten emitters they can DRIVE go through the composite.
    /// This one covers the file — including the two ceiling-notice lines, which fire from the 1 Hz ticker only
    /// once 25 minutes of a breakdown have elapsed and are therefore not reachable from a test without a clock
    /// seam none of this design introduces. A source census is the honest instrument for those two: it cannot
    /// prove they RENDER correctly, but it can prove they are not raw writes.</para>
    ///
    /// <para><b>The census is by TYPE and by ALLOWLIST, deliberately, because a denylist of spellings does not
    /// work here.</b> An earlier revision matched five literals — <c>AnsiConsole.MarkupLine</c>,
    /// <c>AnsiConsole.Write</c>, <c>AnsiConsole.Markup(</c>, <c>Console.WriteLine</c>, <c>Console.Write(</c> —
    /// and the fix's own injected field defeated all five: <c>_console.MarkupLine(…)</c> matches none of them
    /// (lowercase <c>_console</c> misses the two case-sensitive <c>Console.Write*</c> literals), so #372
    /// verbatim, reached through the field the fix introduced, passed the census. Adding a sixth literal would
    /// have left <c>_console.Write</c> open. So: find every identifier this file declares AS an
    /// <see cref="IAnsiConsole"/>, and permit only <see cref="NonWritingConsoleMembers"/> on it — a new console
    /// field under any name is covered the day it is declared, and any write verb fails whatever it is called.
    /// </para>
    /// </summary>
    [Fact]
    public void LiveRunObserver_ContainsNoOutOfBandConsoleWrite_OnlyTheInjectionDefault()
    {
        string[] code =
        [
            .. File.ReadAllLines(LiveRunObserverSource())
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
        ];

        // Pass 1 — every identifier DECLARED as an IAnsiConsole (the field, and the ctor parameter), plus
        // where those declarations sit, so pass 2 does not read a declaration as a use.
        var consoles = new HashSet<string>(StringComparer.Ordinal);
        var declaredAt = new Dictionary<int, HashSet<int>>();
        for (int i = 0; i < code.Length; i++)
        {
            foreach (Match decl in Regex.Matches(code[i], @"IAnsiConsole\??\s+([A-Za-z_][A-Za-z0-9_]*)"))
            {
                consoles.Add(decl.Groups[1].Value);
                if (!declaredAt.TryGetValue(i, out HashSet<int>? at))
                {
                    declaredAt[i] = at = [];
                }

                at.Add(decl.Groups[1].Index);
            }
        }

        // The census is rooted, not vacuous: if the field is renamed, its type inferred, or the injection
        // removed, this fails LOUDLY instead of quietly scanning for an identifier that no longer exists and
        // reporting no offenders. Rename the field here too — do not delete this line to get green.
        Assert.True(
            consoles.Contains("_console"),
            "no `IAnsiConsole _console` is declared in LiveRunObserver.cs, so this census has nothing to scan "
            + "and would pass vacuously. Found these IAnsiConsole-typed identifiers: ["
            + string.Join(", ", consoles) + "]");

        // Pass 2 — every USE of one of those identifiers. A member access must name a non-writing member; a
        // bare use (assignment, or the identifier handed to something else that could write through it) is
        // legal ONLY on §7.4's injection-default line.
        var offenders = new List<string>();
        for (int i = 0; i < code.Length; i++)
        {
            foreach (string name in consoles)
            {
                string pattern =
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])\s*(?:\.\s*(?<member>[A-Za-z_][A-Za-z0-9_]*))?";
                foreach (Match use in Regex.Matches(code[i], pattern))
                {
                    if (declaredAt.TryGetValue(i, out HashSet<int>? at) && at.Contains(use.Index))
                    {
                        continue;
                    }

                    if (use.Groups["member"].Success)
                    {
                        string member = use.Groups["member"].Value;
                        if (!NonWritingConsoleMembers.Contains(member, StringComparer.Ordinal))
                        {
                            offenders.Add($"{name}.{member}  →  {code[i].Trim()}");
                        }

                        continue;
                    }

                    if (!code[i].Contains("?? AnsiConsole.Console", StringComparison.Ordinal))
                    {
                        offenders.Add($"{name} (not a member access)  →  {code[i].Trim()}");
                    }
                }
            }
        }

        // The process-global console, under either spelling. `AnsiConsole.Console` is excluded by the
        // lookbehind — that ONE reference is the injection default, checked against its own rule below.
        foreach (string line in code)
        {
            foreach (Match use in Regex.Matches(line, @"(?<![A-Za-z0-9_.])Console\s*\.\s*[A-Za-z_][A-Za-z0-9_]*"))
            {
                offenders.Add($"the process-global console  →  {line.Trim()} (at '{use.Value}')");
            }

            if (line.Contains("System.Console", StringComparison.Ordinal))
            {
                offenders.Add($"the process-global console  →  {line.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "LiveRunObserver must never write outside the Live region (#372). Only "
            + string.Join('/', NonWritingConsoleMembers) + " may be touched on an IAnsiConsole here; found:\n  "
            + string.Join("\n  ", offenders));

        // Exactly ONE `AnsiConsole.` reference is permitted, and it is named: the constructor's injection
        // default (§7.4), which RESOLVES a console rather than writing to one.
        string[] references = [.. code.Where(l => l.Contains("AnsiConsole.", StringComparison.Ordinal))];
        string only = Assert.Single(references);
        Assert.Contains("AnsiConsole.Console", only, StringComparison.Ordinal);
        Assert.Contains("console ??", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// The observer's source file, located from THIS file's compile-time path — the same
    /// <see cref="System.Runtime.CompilerServices.CallerFilePathAttribute"/> mechanism
    /// <c>Guardrails.Core.Tests.TestPaths</c> uses, so the census cannot mis-root against a build layout.
    /// </summary>
    private static string LiveRunObserverSource(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        string source = Path.Combine(repoRoot, "src", "Guardrails.Cli", "Ui", "LiveRunObserver.cs");
        Assert.True(File.Exists(source), $"the census mis-rooted: no LiveRunObserver.cs at {source}");
        return source;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The seam itself.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestConsole_OwnsItsExclusivityMode_WhichIsWhyThisClassNeedNotJoinLiveDisplayCollection()
    {
        using var a = new TestConsole();
        using var b = new TestConsole();

        Assert.NotSame(AnsiConsole.Console.ExclusivityMode, a.ExclusivityMode);
        Assert.NotSame(a.ExclusivityMode, b.ExclusivityMode);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §4.1 — the empty-narrative case renders the BARE table.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlatRunWithNoAdvisories_EmitsNoNarrativeAtAll_AndTheTargetStaysTheBareTable()
    {
        // §4.4's net effect on the dominant plan shape: ~60 out-of-band lines → 0. A flat plan emits no
        // wave lines, and AttemptFinished/AttemptModelResolved no longer speak in the agreeing cases.
        TestConsole console = Console();
        TaskNode a = Task(null, "01-a");
        TaskNode b = Task(null, "02-b");

        await using (var observer = new LiveRunObserver([a, b], console: console))
        {
            observer.TaskStarting(a);
            observer.AttemptStarting(a, 1, 3);
            observer.AttemptFinished(a, Attempt(1, Core.Journal.AttemptOutcome.Succeeded));
            observer.AttemptModelResolved(a, 1, "claude-sonnet-4-5", requestedModel: null);
            observer.TaskFinished(new TaskResult { TaskId = a.Id, Outcome = TaskOutcome.Succeeded, Summary = "ok" });
        }

        string output = console.Output;
        Assert.DoesNotContain("attempt 1: Succeeded", output, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-sonnet-4-5", output, StringComparison.Ordinal);

        // Every rendered line of every frame belongs to the table — there is no pane above it, and nothing
        // was written beside it. On a bare-table run this DOES catch a raw write, because any such line
        // would be a non-table line in a stream that is otherwise nothing but table.
        foreach (IReadOnlyList<string> frame in Frames(console))
        {
            Assert.All(frame, line => Assert.True(
                HasBorderGlyph(line), $"a bare-table frame emitted a non-table line: '{line}'"));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §5.3 — resume into wave 4 of 6: #372's worst artifact, and the invariant that names it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeIntoWaveFour_KeepsEveryLineInEveryLaterFrame_WhichARawWriteCannotDo()
    {
        TestConsole console = Console();
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode w2 = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-a"));
        WaveNode w3 = Wave("wave-03-integration", 3, Task("wave-03-integration", "01-a"));
        WaveNode w4 = Wave("wave-04-delivery", 4, Task("wave-04-delivery", "01-author-tests"));
        IReadOnlyList<WaveNode> waves = [w1, w2, w3, w4];
        IReadOnlyList<TaskNode> tasks = [.. waves.SelectMany(w => w.Tasks)];

        await using (var observer = new LiveRunObserver(tasks, waves: waves, console: console))
        {
            observer.WaveFinished(w1, Core.Journal.WaveStatus.Completed, skipped: true);
            observer.WaveFinished(w2, Core.Journal.WaveStatus.Completed, skipped: true);
            observer.WaveFinished(w3, Core.Journal.WaveStatus.Completed, skipped: true);
            observer.WaveStarting(w4, 4, 4);
            observer.TaskStarting(w4.Tasks[0]);
        }

        string[] expected =
        [
            "Wave wave-01-foundation: already complete — skipped (resume)",
            "Wave wave-02-consumers: already complete — skipped (resume)",
            "Wave wave-03-integration: already complete — skipped (resume)",
            "Wave 4/4: wave-04-delivery — 1 task(s)"
        ];

        foreach (string text in expected)
        {
            // The #372 property: part of the Live TARGET, so still there four frames later.
            AssertRenderedInsideTheLiveRegion(console, text);

            // …and in the pane above the table rather than inside a cell.
            AssertInThePaneOfTheLastFrame(console, text);
        }

        // §5.3's rendered block, in order: the four entries, then the table, in ONE frame.
        IReadOnlyList<string> last = LastFrame(console);
        Assert.Equal(expected.Length, last.TakeWhile(l => !l.Contains(TopLeft, StringComparison.Ordinal)).Count());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §4.5 — the advisory burst that motivates the whole budget.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwentyFiveVerifierAdvisories_CoalesceToOneCountedEntry_AndDoNotEvictTheWaveLines()
    {
        TestConsole console = Console();
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        IReadOnlyList<TaskNode> tasks = [.. w1.Tasks];

        await using (var observer = new LiveRunObserver(tasks, waves: [w1], console: console))
        {
            observer.WaveStarting(w1, 1, 1);
            for (int i = 1; i <= 25; i++)
            {
                observer.VerifierAdvisoryFound($"task-{i:00}", "judge 'meets-spec' has no verifier condition");
            }
        }

        // The FOLDED line only ever exists at its final count, so its first frame is the 25th append's —
        // and the frames after it (teardown's repaints) are what prove it is in the target, not beside it.
        AssertRenderedInsideTheLiveRegion(
            console, "verifier advisory — 25 task(s), latest task-25: judge 'meets-spec' has no verifier condition");

        // The wave line survived a 25-strong burst: that is the coalescing dependency §4.3 names. It has been
        // in every frame since the first, which is the strongest form of "not evicted".
        AssertRenderedInsideTheLiveRegion(console, "Wave 1/1: wave-01-foundation — 1 task(s)");

        // …and no earlier-lines elision was ever needed, because the burst folded rather than pushed.
        Assert.DoesNotContain(
            "earlier line", string.Join('\n', LastFrame(console)), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §5.4 — the elision line, and §4.4 #12's decision prefix.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PastTheBudget_ThePaneLeadsWithTheAttachReplayPointer()
    {
        TestConsole console = Console();
        TaskNode a = Task(null, "01-a");

        await using (var observer = new LiveRunObserver(
            [a], planDirectory: "docs/plans/model-tiering-stage-2", runId: "run-1", console: console))
        {
            for (int i = 1; i <= 14; i++)
            {
                observer.DecisionRecorded(Decision("drift", $"unit-{i:00}", $"Definition drift auto-resolved #{i}"));
            }
        }

        AssertRenderedInsideTheLiveRegion(
            console, "… 6 earlier lines — replay with: guardrails attach docs/plans/model-tiering-stage-2");
        AssertInThePaneOfTheLastFrame(
            console, "… 6 earlier lines — replay with: guardrails attach docs/plans/model-tiering-stage-2");

        // The 8 kept entries are the most recent 8: #7 is the oldest survivor, #6 was evicted, #14 is newest.
        string last = string.Join('\n', LastFrame(console));
        Assert.Contains("Definition drift auto-resolved #14:", last, StringComparison.Ordinal);
        Assert.Contains("Definition drift auto-resolved #7:", last, StringComparison.Ordinal);
        Assert.DoesNotContain("Definition drift auto-resolved #6:", last, StringComparison.Ordinal);
    }

    /// <summary>
    /// Design 37 §4.3 — the budget is read from the CONSOLE the observer was handed, not baked in. Nothing
    /// else in the repo constructs a <see cref="LiveRunObserver"/> below 60 columns, so without this test
    /// deleting the width check and hardcoding <see cref="LiveNarrative.DefaultBudget"/> passes everything:
    /// a 56-column terminal would then get an 8-entry pane that wraps to 16 rows and eats the table, which
    /// is the exact outcome §4.3's halving exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(56, 4, 2)]  // §5.5's worked narrow example — NarrowBudget, two entries elided
    [InlineData(60, 6, 0)]  // the boundary is inclusive on the wide side — all six kept
    public async Task ThePaneBudgetFollowsTheConsoleWidth(int width, int keptEntries, int elidedEntries)
    {
        TestConsole console = Console(width);
        TaskNode a = Task(null, "01-a");

        // Deliberately terse wording: a 56-column pane wraps a normal decision line across two rendered rows,
        // and this test is about HOW MANY entries survive, not about wrapping.
        await using (var observer = new LiveRunObserver([a], console: console))
        {
            for (int i = 1; i <= 6; i++)
            {
                observer.DecisionRecorded(Decision("drift", $"u{i}", $"d#{i}"));
            }
        }

        IReadOnlyList<string> last = LastFrame(console);
        Assert.Equal(keptEntries, last.Count(l => l.Contains("decision:drift", StringComparison.Ordinal)));

        // The survivors are the most recent ones, and the elision line accounts for the rest.
        for (int i = 6; i > 6 - keptEntries; i--)
        {
            Assert.Contains(last, l => l.Contains($"d#{i}: u{i}", StringComparison.Ordinal));
        }

        for (int i = 6 - keptEntries; i >= 1; i--)
        {
            Assert.DoesNotContain(last, l => l.Contains($"d#{i}: u{i}", StringComparison.Ordinal));
        }

        string joined = string.Join('\n', last);
        if (elidedEntries == 0)
        {
            Assert.DoesNotContain("earlier line", joined, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"… {elidedEntries} earlier lines", joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DecisionRecorded_CarriesTheBoundaryToken_SoTheLineIsLegibleUnderNoColor()
    {
        TestConsole console = Console(140);
        TaskNode a = Task(null, "01-a");

        await using (var observer = new LiveRunObserver([a], console: console))
        {
            observer.DecisionRecorded(Decision(
                "wave", "wave-02-consumers", "Wave 'wave-02-consumers' ran UNREVIEWED (5 task(s))"));
        }

        const string Line = "decision:wave  Wave 'wave-02-consumers' ran UNREVIEWED (5 task(s)): wave-02-consumers";
        AssertRenderedInsideTheLiveRegion(console, Line);
        AssertInThePaneOfTheLastFrame(console, Line);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §4.4 #1/#2 — what STOPS being said.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedAttempt_SpeaksInTheDetailCell_NotInThePane()
    {
        TestConsole console = Console();
        TaskNode a = Task(null, "01-a");

        await using (var observer = new LiveRunObserver([a], console: console))
        {
            observer.TaskStarting(a);
            observer.AttemptStarting(a, 1, 3);
            observer.AttemptFinished(a, Attempt(1, Core.Journal.AttemptOutcome.GuardrailFailed));
            observer.AttemptStarting(a, 2, 3);
        }

        // The outcome is a CELL: it persists across every later repaint (a raw line would not), and its line
        // carries the table's own border glyphs.
        AssertRenderedInsideTheLiveRegion(console, "attempt 1 GuardrailFailed");
        AssertRenderedInsideTheLiveRegion(console, "retry 2/3");
        Assert.Contains(
            LastFrame(console),
            l => l.Contains("attempt 1 GuardrailFailed", StringComparison.Ordinal) && HasBorderGlyph(l));

        // …and the vaguer sentence AttemptStarting used to overwrite it with is gone.
        Assert.DoesNotContain("previous attempt failed", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgreeingModelResolution_IsCellOnly_AMismatchKeepsItsCompanionLine()
    {
        TestConsole console = Console(140);
        TaskNode a = Task(null, "01-a");

        await using (var observer = new LiveRunObserver([a], console: console))
        {
            observer.TaskStarting(a);
            observer.AttemptModelResolved(a, 1, "claude-sonnet-4-5", requestedModel: null);
            Assert.DoesNotContain("claude-sonnet-4-5", console.Output, StringComparison.Ordinal);

            observer.AttemptModelResolved(a, 2, "claude-sonnet-4-5", requestedModel: "claude-opus-4-1");
        }

        const string Line = "model 01-a attempt 2: claude-sonnet-4-5 — MISMATCH: the route requested claude-opus-4-1";
        AssertRenderedInsideTheLiveRegion(console, Line);
        AssertInThePaneOfTheLastFrame(console, Line);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The remaining §4.4 narrative sites, driven through the injected console in one sweep.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryDrivableNarrativeEmitter_RendersInsideTheCompositeAndNeverBesideIt()
    {
        using var tree = new TempTree();
        TestConsole console = Console(140);
        TaskNode a = Task(null, "01-a");
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode stub = Wave("wave-02-consumers", 2);

        var context = new WaveBreakdownContext
        {
            WaveDir = "wave-02-consumers",
            Index = 2,
            Total = 2,
            Ceiling = TimeSpan.FromMinutes(30),
            TasksDirectory = Path.Combine(tree.Root, "wave-02-consumers", "tasks"),
            StreamLogPath = Path.Combine(tree.Root, "breakdown", "stream.jsonl"),
            IntentManifestPath = null,
            BreakdownLogDir = Path.Combine(tree.Root, "breakdown"),
            ComposedPromptBytes = 2048
        };

        await using (var observer = new LiveRunObserver(
            [a, .. w1.Tasks], waves: [w1, stub], console: console))
        {
            observer.PlanHashMismatch("sha256:9f2a");                                   // §4.4 #11
            observer.OverwatchNoVerdict("01-a", "model returned no JSON block");         // §4.4 #4
            observer.WaveStarting(w1, 1, 2);                                             // §4.4 #5
            observer.WaveBreakdownStarting(context);                                     // §4.4 #7-8
            observer.WaveFinished(w1, Core.Journal.WaveStatus.Completed, skipped: false); // §4.4 #6
            observer.DecisionRecorded(Decision("drift", "01-a", "Definition drift auto-resolved")); // §4.4 #12
        }

        string[] expected =
        [
            "plan manifests changed since the last run",
            "overwatch: no verdict 01-a — model returned no JSON block",
            "Wave 1/2: wave-01-foundation — 1 task(s)",
            "authoring tasks (JIT breakdown). Ceiling 30m00s.",
            "Breakdown log: " + context.BreakdownLogDir,
            "Wave wave-01-foundation: completed",
            "decision:drift  Definition drift auto-resolved: 01-a"
        ];

        foreach (string text in expected)
        {
            // THE assertion. Each of these is part of the Live region's TARGET, so it is repainted in every
            // frame from the one it was appended in through the last — which a write to the same console from
            // beside the region can never be, whatever verb or spelling that write uses.
            AssertRenderedInsideTheLiveRegion(console, text);

            // …in the pane, above the table, and never inside a cell.
            AssertInThePaneOfTheLastFrame(console, text);
        }

        // Seven entries under a budget of eight: nothing was elided, so the last frame IS the whole narrative.
        // (Asserted as the absence of the elision line rather than as an exact pane-line count, because one of
        // these entries carries a temp path whose length varies by machine and could wrap at 140 columns.)
        Assert.DoesNotContain(
            "earlier line", string.Join('\n', LastFrame(console)), StringComparison.Ordinal);
    }

    private static Core.Journal.AttemptRecord Attempt(int attempt, Core.Journal.AttemptOutcome outcome) => new()
    {
        Attempt = attempt,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        Outcome = outcome,
        LogDir = $"01-a/attempt-{attempt}"
    };
}
