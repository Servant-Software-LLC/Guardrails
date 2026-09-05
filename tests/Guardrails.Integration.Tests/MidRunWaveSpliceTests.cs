using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console.Testing;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #404 / design 37 §5.2 — a wave the run LOADED as an empty JIT stub, whose eleven tasks the harness
/// authored half an hour later, must acquire real rows the moment the breakdown settles. Before this, the
/// operator saw <c>wave-02-consumers — JIT breakdown │ authored 18:42 │ 11 task folders</c> and then nothing:
/// every row green, every clock stopped, a finished run byte-identical to one with eleven tasks in flight.
///
/// <para>Two seams are pinned here: the pure <see cref="LiveRunObserver.SpliceWave"/> row-plan transform, and
/// the rendered result through an injected <see cref="TestConsole"/> — including §0.4's live-state hazard,
/// which the splice is the first caller ever to trip.</para>
/// </summary>
public sealed class MidRunWaveSpliceTests
{
    private static TestConsole Console(int width = 120) => new TestConsole().Width(width).Interactive();

    private static ActionDefinition Action(string dir) => new() { Path = $"{dir}/action.sh", Kind = ActionKind.Script };

    private static TaskNode Task(string waveDir, string folder)
    {
        string dir = $"/fake/plan/{waveDir}/tasks/{folder}";
        return new TaskNode
        {
            Id = $"{waveDir}/{folder}",
            WaveDir = waveDir,
            Directory = dir,
            Description = $"fixture — {folder}",
            Action = Action(dir),
            Guardrails = []
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

    private static WaveBreakdownContext BreakdownContext(string waveDir, string root) => new()
    {
        WaveDir = waveDir,
        Index = 2,
        Total = 2,
        Ceiling = TimeSpan.FromMinutes(30),
        TasksDirectory = Path.Combine(root, waveDir, "tasks"),
        StreamLogPath = Path.Combine(root, "breakdown", "stream.jsonl"),
        IntentManifestPath = null,
        BreakdownLogDir = Path.Combine(root, "breakdown"),
        ComposedPromptBytes = 4096
    };

    /// <summary>A throwaway directory tree — the phase probe stats real paths.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-404-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    private static IReadOnlyList<string> RenderedLines(TestConsole console) =>
    [
        .. console.Output
            .Replace("╯", "╯\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd('\r', ' '))
            .Where(l => l.Length > 0)
    ];

    /// <summary>The LAST rendered frame — everything after the final top-left corner.</summary>
    private static string FinalFrame(TestConsole console)
    {
        IReadOnlyList<string> lines = RenderedLines(console);
        int start = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains('╭', StringComparison.Ordinal))
            {
                start = i;
            }
        }

        return string.Join('\n', lines.Skip(start));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The pure seam (§7.3).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpliceWave_ReplacesTheStub_AndReDerivesTheFlattenedListInStrictWaveOrder()
    {
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"), Task("wave-01-foundation", "02-b"));
        WaveNode stub = Wave("wave-02-consumers", 2);
        WaveNode w3 = Wave("wave-03-delivery", 3, Task("wave-03-delivery", "01-a"));

        WaveNode authored = Wave(
            "wave-02-consumers", 2,
            Task("wave-02-consumers", "01-author-repo-tests"),
            Task("wave-02-consumers", "02-implement-repo"));

        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) =
            LiveRunObserver.SpliceWave([.. w1.Tasks, .. w3.Tasks], [w1, stub, w3], authored);

        Assert.Equal(["wave-01-foundation", "wave-02-consumers", "wave-03-delivery"], waves.Select(w => w.Dir));
        Assert.Equal(2, waves[1].Tasks.Count);

        // The union is RE-DERIVED in wave order, never appended to — so the spliced tasks land between
        // wave-01's and wave-03's, exactly where the loader would have put them (SSOT §14.2).
        Assert.Equal(
            [
                "wave-01-foundation/01-a",
                "wave-01-foundation/02-b",
                "wave-02-consumers/01-author-repo-tests",
                "wave-02-consumers/02-implement-repo",
                "wave-03-delivery/01-a"
            ],
            tasks.Select(t => t.Id));
    }

    [Fact]
    public void SpliceWave_ForAWaveTheTableWasNeverToldAbout_ReturnsTheInputsUnchanged()
    {
        // A flat plan, or an `attach` pointed at a different plan: growing a row set the operator's plan
        // does not have would be worse than showing nothing.
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode alien = Wave("wave-99-elsewhere", 99, Task("wave-99-elsewhere", "01-a"));

        (IReadOnlyList<TaskNode> tasks, IReadOnlyList<WaveNode> waves) =
            LiveRunObserver.SpliceWave([.. w1.Tasks], [w1], alien);

        Assert.Equal(["wave-01-foundation/01-a"], tasks.Select(t => t.Id));
        Assert.Single(waves);

        (IReadOnlyList<TaskNode> flatTasks, IReadOnlyList<WaveNode> flatWaves) =
            LiveRunObserver.SpliceWave([.. w1.Tasks], [], alien);
        Assert.Single(flatTasks);
        Assert.Empty(flatWaves);
    }

    [Fact]
    public void SpliceWave_IsIdempotent_SoAReplayedEventCannotDuplicateRows()
    {
        WaveNode stub = Wave("wave-02-consumers", 2);
        WaveNode authored = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-a"));

        (IReadOnlyList<TaskNode> once, IReadOnlyList<WaveNode> onceWaves) =
            LiveRunObserver.SpliceWave([], [stub], authored);
        (IReadOnlyList<TaskNode> twice, _) =
            LiveRunObserver.SpliceWave(once, onceWaves, authored);

        Assert.Single(twice);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // LiveTableRows.Plan's new breakdownWaves argument — additive, and inert when omitted.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_WithoutBreakdownWaves_IsByteIdenticalToBefore_ForAnAuthoredWavedPlan()
    {
        // The #485 rule: the dominant shape must cost nothing. An authored waved plan produces the same
        // ordered row list whether the new argument is omitted or passed empty.
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode w2 = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-a"));
        IReadOnlyList<TaskNode> tasks = [.. w1.Tasks, .. w2.Tasks];

        IReadOnlyList<LiveTableRow> omitted =
            LiveTableRows.Plan(tasks, [w1, w2], new HashSet<string>(), showAllTasks: false);
        IReadOnlyList<LiveTableRow> empty =
            LiveTableRows.Plan(tasks, [w1, w2], new HashSet<string>(), showAllTasks: false, new HashSet<string>());

        Assert.Equal(
            [new TaskLiveRow("wave-01-foundation/01-a"), new TaskLiveRow("wave-02-consumers/01-a")], omitted);
        Assert.Equal(omitted, empty);
    }

    [Fact]
    public void Plan_KeepsThePhaseRowForAWaveThatRanABreakdown_EvenOnceItHasTasks()
    {
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode authored = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-a"));
        IReadOnlyList<TaskNode> tasks = [.. w1.Tasks, .. authored.Tasks];

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(
            tasks, [w1, authored], new HashSet<string>(), showAllTasks: false,
            new HashSet<string>(StringComparer.Ordinal) { "wave-02-consumers" });

        Assert.Equal(
            [
                new TaskLiveRow("wave-01-foundation/01-a"),
                new WavePhaseLiveRow("wave-02-consumers"),
                new TaskLiveRow("wave-02-consumers/01-a")
            ],
            rows);
    }

    [Fact]
    public void Plan_ACollapsedWaveStillLosesItsPhaseRow_BecauseTheSummaryLineIsTheWholePoint()
    {
        // #379's collapse is unchanged: a settled wave is ONE line, phase row included.
        WaveNode authored = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-a"));

        IReadOnlyList<LiveTableRow> rows = LiveTableRows.Plan(
            [.. authored.Tasks], [authored],
            new HashSet<string>(StringComparer.Ordinal) { "wave-02-consumers" },
            showAllTasks: false,
            new HashSet<string>(StringComparer.Ordinal) { "wave-02-consumers" });

        Assert.Equal([new WaveSummaryLiveRow("wave-02-consumers", 1)], rows);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The rendered result (§5.2 B2) and the halting path (§5.2 B3).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaveBreakdownFinished_WithAnAuthoredWave_GivesEveryNewTaskARow_BeneathTheSettledPhaseRow()
    {
        using var tree = new TempTree();
        TestConsole console = Console();
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode stub = Wave("wave-02-consumers", 2);

        WaveNode authored = Wave(
            "wave-02-consumers", 2,
            Task("wave-02-consumers", "01-author-repo-tests"),
            Task("wave-02-consumers", "02-implement-repo"),
            Task("wave-02-consumers", "03-wire-consumers"));

        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", tree.Root);

        await using (var observer = new LiveRunObserver(
            [.. w1.Tasks], planDirectory: tree.Root, runId: "run-1", waves: [w1, stub], console: console))
        {
            observer.WaveBreakdownStarting(context);

            string before = FinalFrame(console);
            Assert.DoesNotContain("01-author-repo-tests", before, StringComparison.Ordinal);

            observer.WaveBreakdownFinished(
                context, TimeSpan.FromSeconds(1122), authoredTaskCount: 3, failureKind: null, authoredWave: authored);
        }

        string frame = FinalFrame(console);

        // Every authored task is now a row…
        Assert.Contains("01-author-repo-tests", frame, StringComparison.Ordinal);
        Assert.Contains("02-implement-repo", frame, StringComparison.Ordinal);
        Assert.Contains("03-wire-consumers", frame, StringComparison.Ordinal);

        // …and the settled phase row STAYS above them, carrying the authoring provenance across the rebuild.
        // 1122s renders `authored 18:42` — the only live record of what the breakdown cost (§5.2 B2).
        Assert.Contains("wave-02-consumers — JIT breakdown", frame, StringComparison.Ordinal);
        Assert.Contains("authored 18:42", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("no tasks yet — authored at the barrier", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaveBreakdownFinished_OnAHaltingPath_SplicesNothing_AndKeepsTheStubRow()
    {
        // §5.2 B3: the whole splice path is gated on a NON-NULL authoredWave, which Scheduler passes only
        // where the run will proceed (`proceeding ? authoredWave : null`). Escalate and halt cannot regress.
        using var tree = new TempTree();
        TestConsole console = Console();
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode stub = Wave("wave-02-consumers", 2);
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", tree.Root);

        await using (var observer = new LiveRunObserver(
            [.. w1.Tasks], planDirectory: tree.Root, runId: "run-1", waves: [w1, stub], console: console))
        {
            observer.WaveBreakdownStarting(context);
            observer.WaveBreakdownFinished(
                context, TimeSpan.FromSeconds(1800), authoredTaskCount: 0, failureKind: "timeout",
                authoredWave: null);
        }

        string frame = FinalFrame(console);
        Assert.Contains("wave-02-consumers — JIT breakdown", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("wave-02-consumers/", frame, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // §0.4 — the live-state hazard the splice is the first caller ever to trip.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnderAllTasks_TheSpliceRebuild_DoesNotWipeACompletedTasksGreenStatusBackToPending()
    {
        // Before #404 this rebuild ran exactly once, at construction (WaveFinished's collapse is guarded off
        // by --all-tasks), so RebuildRows re-seeding every row to `pending` was provably safe. The mid-run
        // splice makes it run with completed work on screen. This is that regression, pinned.
        using var tree = new TempTree();
        TestConsole console = Console();
        TaskNode done = Task("wave-01-foundation", "01-a");
        WaveNode w1 = Wave("wave-01-foundation", 1, done);
        WaveNode stub = Wave("wave-02-consumers", 2);
        WaveNode authored = Wave("wave-02-consumers", 2, Task("wave-02-consumers", "01-author-repo-tests"));
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", tree.Root);

        await using (var observer = new LiveRunObserver(
            [done], waves: [w1, stub], showAllTasks: true, console: console))
        {
            observer.TaskStarting(done);
            observer.TaskFinished(new TaskResult
            {
                TaskId = done.Id, Outcome = TaskOutcome.Succeeded, Summary = "selector wired"
            });

            Assert.Contains("succeeded", FinalFrame(console), StringComparison.Ordinal);

            observer.WaveBreakdownStarting(context);
            observer.WaveBreakdownFinished(
                context, TimeSpan.FromSeconds(60), authoredTaskCount: 1, failureKind: null, authoredWave: authored);
        }

        string frame = FinalFrame(console);
        string[] rows = [.. frame.Split('\n').Where(l => l.Contains("wave-01-foundation/01-a", StringComparison.Ordinal))];

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Contains("succeeded", row, StringComparison.Ordinal);
            Assert.DoesNotContain("pending", row, StringComparison.Ordinal);
        });

        // The Detail cell survived too — not just the status word.
        Assert.Contains("selector wired", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASplicedTasksEventsReachItsRow_WhichIsWhatMakesTheThreeStateAnswerPossible()
    {
        // §3.2: the operator tells "working" from "the harness settled green and stopped" by the spliced
        // row carrying `running` and a clock. Before #404, Update() returned early for an unknown id and
        // every one of these events was a silent no-op.
        using var tree = new TempTree();
        TestConsole console = Console();
        WaveNode w1 = Wave("wave-01-foundation", 1, Task("wave-01-foundation", "01-a"));
        WaveNode stub = Wave("wave-02-consumers", 2);
        TaskNode spliced = Task("wave-02-consumers", "01-author-repo-tests");
        WaveNode authored = Wave("wave-02-consumers", 2, spliced);
        WaveBreakdownContext context = BreakdownContext("wave-02-consumers", tree.Root);

        await using (var observer = new LiveRunObserver(
            [.. w1.Tasks], waves: [w1, stub], console: console))
        {
            observer.WaveBreakdownStarting(context);
            observer.WaveBreakdownFinished(
                context, TimeSpan.FromSeconds(60), authoredTaskCount: 1, failureKind: null, authoredWave: authored);
            observer.TaskStarting(spliced);
        }

        string frame = FinalFrame(console);
        string row = frame.Split('\n').Single(l => l.Contains("01-author-repo-tests", StringComparison.Ordinal));
        Assert.Contains("running", row, StringComparison.Ordinal);
    }
}
