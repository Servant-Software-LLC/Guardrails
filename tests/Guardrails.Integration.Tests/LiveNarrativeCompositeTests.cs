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
/// the table's <c>TableBorder.Rounded</c> glyphs THROUGH the line just written. These tests drive a whole
/// simulated run through an injected <see cref="TestConsole"/> and assert the two halves of the fix:</para>
/// <list type="number">
///   <item>every narrative line is PRESENT in the Live region's own rendered output (it went through the
///     composite, not around it — before the fix these lines went to the global console and would be
///     absent from this console entirely); and</item>
///   <item>NO rendered line carries both narrative text and a table border glyph, and every frame's
///     <c>╭</c> is closed by a <c>╮</c> on the same line — the corruption itself, named and excluded.</item>
/// </list>
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
    private const char TopRight = '╮';
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

    private static DecisionEntry Decision(string boundary, string subject, string headline) => new()
    {
        Boundary = boundary,
        Policy = "auto",
        Decision = "auto-applied",
        Subject = subject,
        Headline = headline
    };

    /// <summary>
    /// The rendered lines, trailing padding removed and blanks dropped.
    ///
    /// <para><b>Why the extra split on <c>╯</c>.</b> A real terminal repositions the cursor between frames;
    /// <see cref="TestConsole"/> is a plain writer with no cursor control, so it emits each frame back to back
    /// and frame N's closing bottom-right corner runs into frame N+1's first character with no newline
    /// between them. That is an artifact of the harness, not of the renderer, so the corner is treated as the
    /// frame terminator it is. Everything asserted below is therefore WITHIN one frame — which is exactly the
    /// scope #372's corruption lived in.</para>
    /// </summary>
    private static IReadOnlyList<string> RenderedLines(TestConsole console) =>
    [
        .. console.Output
            .Replace("╯", "╯\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd('\r', ' '))
            .Where(l => l.Length > 0)
    ];

    private static bool HasBorderGlyph(string line) => line.IndexOfAny(BorderGlyphs) >= 0;

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

        // Every non-blank rendered line belongs to the table — there is no pane above it.
        Assert.All(RenderedLines(console), line => Assert.True(
            HasBorderGlyph(line), $"a bare-table frame emitted a non-table line: '{line}'"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §5.3 — resume into wave 4 of 6: #372's worst artifact, and the invariant that names it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeIntoWaveFour_PutsEveryLineInThePane_AndNoLineCarriesBothTextAndABorder()
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

        IReadOnlyList<string> lines = RenderedLines(console);

        // (1) Every narrative line reached THIS console — i.e. it went through the Live target. Before the
        //     fix these were AnsiConsole.MarkupLine calls against the global console and would be absent.
        foreach (string wave in new[] { "wave-01-foundation", "wave-02-consumers", "wave-03-integration" })
        {
            Assert.Contains(
                lines, l => l.Contains($"Wave {wave}: already complete — skipped (resume)", StringComparison.Ordinal));
        }

        Assert.Contains(lines, l => l.Contains("Wave 4/4: wave-04-delivery — 1 task(s)", StringComparison.Ordinal));

        // (2) THE #372 DEFECT, excluded directly: no line mixes narrative text with a table border glyph.
        foreach (string line in lines)
        {
            if (line.Contains("already complete — skipped (resume)", StringComparison.Ordinal)
                || line.Contains("wave-04-delivery — 1 task(s)", StringComparison.Ordinal))
            {
                Assert.False(
                    HasBorderGlyph(line),
                    $"a narrative line was stamped through by the table border (#372): '{line}'");
            }
        }

        // (3) Every frame is well-formed: a top-left corner is always closed on its own line.
        foreach (string line in lines.Where(l => l.Contains(TopLeft, StringComparison.Ordinal)))
        {
            Assert.True(
                line.IndexOf(TopRight, StringComparison.Ordinal) > line.IndexOf(TopLeft, StringComparison.Ordinal),
                $"a table frame's top border was truncated by an out-of-band write: '{line}'");
        }
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

        IReadOnlyList<string> lines = RenderedLines(console);
        string[] final = [.. lines.SkipWhile(l => !l.Contains("25 task(s)", StringComparison.Ordinal))];

        Assert.NotEmpty(final);
        Assert.Contains(
            final,
            l => l.Contains(
                "verifier advisory — 25 task(s), latest task-25: judge 'meets-spec' has no verifier condition",
                StringComparison.Ordinal));

        // The wave line survived a 25-strong burst: that is the coalescing dependency §4.3 names.
        string lastFrame = string.Join('\n', final);
        Assert.Contains("Wave 1/1: wave-01-foundation — 1 task(s)", lastFrame, StringComparison.Ordinal);

        // …and no earlier-lines elision was ever needed, because the burst folded rather than pushed.
        Assert.DoesNotContain("earlier line", lastFrame, StringComparison.Ordinal);
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

        IReadOnlyList<string> lines = RenderedLines(console);
        Assert.Contains(
            lines,
            l => l.Contains(
                "… 6 earlier lines — replay with: guardrails attach docs/plans/model-tiering-stage-2",
                StringComparison.Ordinal));

        // The 8 kept entries are the most recent 8 — #7 is gone, #14 is present.
        string tail = string.Join('\n', lines.TakeLast(20));
        Assert.Contains("Definition drift auto-resolved #14", tail, StringComparison.Ordinal);
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

        Assert.Contains(
            RenderedLines(console),
            l => l.Contains(
                "decision:wave  Wave 'wave-02-consumers' ran UNREVIEWED (5 task(s)): wave-02-consumers",
                StringComparison.Ordinal));
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

        IReadOnlyList<string> lines = RenderedLines(console);

        // The cell is inside the table (it has a border glyph on its line), and the vaguer sentence
        // AttemptStarting used to overwrite it with is gone.
        Assert.Contains(
            lines, l => l.Contains("attempt 1 GuardrailFailed", StringComparison.Ordinal) && HasBorderGlyph(l));
        Assert.DoesNotContain("previous attempt failed", console.Output, StringComparison.Ordinal);

        string tail = string.Join('\n', lines.TakeLast(12));
        Assert.Contains("retry 2/3", tail, StringComparison.Ordinal);
        Assert.Contains("attempt 1 GuardrailFailed", tail, StringComparison.Ordinal);
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

        Assert.Contains(
            RenderedLines(console),
            l => l.Contains(
                "model 01-a attempt 2: claude-sonnet-4-5 — MISMATCH: the route requested claude-opus-4-1",
                StringComparison.Ordinal));
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
