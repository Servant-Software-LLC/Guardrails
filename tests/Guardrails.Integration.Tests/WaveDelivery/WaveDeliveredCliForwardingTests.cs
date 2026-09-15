using System.Reflection;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Spectre.Console.Testing;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Design 39 §5 "Wiring and halts": the CLI half of <see cref="IRunObserver.WaveDelivered"/>'s forwarding
/// contract — the two on-the-fly site decorators, and the operator-facing rendering
/// ("a line in the live table and <c>--no-ui</c> output"). The CORE half (<c>RunEventStream</c>/
/// <c>ObserverProjection</c>, the event's own interface member, and the projection's <c>observer.jsonl</c>
/// shape) is <c>Guardrails.Core.Tests</c>' <c>WaveDeliveredEventTests</c>.
///
/// <para>
/// <b>This class MUST live here, not in <c>Guardrails.Core.Tests</c> (review, 2026-09-11).</b>
/// <c>Guardrails.Core.Tests</c> references <c>Guardrails.Core</c> and nothing else — see
/// <c>tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs</c>'s header — and
/// <see cref="ConsoleRunObserver"/>/<see cref="LiveRunObserver"/>/<see cref="OnTheFlyDiagramObserver"/>/
/// <see cref="OnTheFlyLogSiteObserver"/> all live in <c>src/Guardrails.Cli</c>, which no task in this plan
/// may add a project reference to. Putting this class in Core.Tests would make the honest test unable to
/// compile.
/// </para>
///
/// <para>
/// TDD red: none of the two CLI decorators override <see cref="IRunObserver.WaveDelivered"/> yet, and
/// neither renderer prints anything for it — this task adds only the interface's no-op default. Task 13
/// makes these tests green by declaring the member on both decorators and rendering the announcement on
/// both renderers.
/// </para>
///
/// <para>
/// <see cref="WaveDeliveredRecord"/> is task 09's stub on this task's base: every getter throws
/// <see cref="NotImplementedException"/> until task 10 lands. Every fixture here builds one with an object
/// initializer and never reads a member back off it.
/// </para>
/// </summary>
public sealed class WaveDeliveredCliForwardingTests
{
    // ── shared fixtures — mirrors ObserverForwardingSweepTests (tests/Guardrails.Integration.Tests/RunEvents) ──

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    private static PlanDefinition MinimalPlan(IReadOnlyList<TaskNode> tasks) => new()
    {
        PlanDirectory = "/fake/plan",
        Workspace = "/fake",
        Config = new RunConfig { Version = 1 },
        Tasks = tasks
    };

    private static WaveNode FixtureWave(string dir = "wave-02-provision") => new()
    {
        Dir = dir,
        Number = 2,
        Slug = "provision",
        Directory = $"/fake/plan/{dir}",
        Tasks = []
    };

    private static WaveDeliveredRecord FixtureDelivery(string commit = "a1b2c3d4") => new()
    {
        Status = WaveDeliveryStatus.Delivered,
        StartedAt = DateTimeOffset.UtcNow,
        Commit = commit,
        Covers = ["wave-01-bootstrap", "wave-02-provision"]
    };

    /// <summary>A throwaway directory tree — the real chain's decorators write their artefacts under a real root.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "gr-wave-delivered-cli-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public string Dir(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>The innermost observer the whole production chain is supposed to be transparent to.</summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(WaveNode Wave, WaveDeliveredRecord Delivery)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }

        public void WaveDelivered(WaveNode wave, WaveDeliveredRecord delivery) => Calls.Add((wave, delivery));
    }

    /// <summary>Mirrors <c>ObserverForwardingSweepTests.Declares</c> exactly.</summary>
    private static bool Declares(Type type, string methodName) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == methodName || m.Name.EndsWith("." + methodName, StringComparison.Ordinal));

    // ── 1. the two CLI decorators forward it, through the REAL production chain — MUST BE RED ────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void EveryCliDecorator_ForwardsTheEvent()
    {
        Assembly cliAssembly = typeof(ConsoleRunObserver).Assembly;

        (string TypeName, Type? Resolved)[] decorators =
        [
            ("Guardrails.Cli.Ui.OnTheFlyDiagramObserver",
                cliAssembly.GetType("Guardrails.Cli.Ui.OnTheFlyDiagramObserver", throwOnError: false)),
            ("Guardrails.Cli.Ui.OnTheFlyLogSiteObserver",
                cliAssembly.GetType("Guardrails.Cli.Ui.OnTheFlyLogSiteObserver", throwOnError: false)),
        ];

        // Non-vacuity floor (copied from ObserverForwardingSweepTests).
        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(
            unresolved.Length == 0,
            $"{unresolved.Length} of {decorators.Length} CLI transparent-decorator type(s) did not resolve "
            + $"via reflection — the type may have moved or been renamed: {string.Join(", ", unresolved)}");

        const string member = nameof(IRunObserver.WaveDelivered);
        Assert.NotNull(typeof(IRunObserver).GetMethod(member)); // non-vacuity floor

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            $"The following CLI transparent decorator(s) do NOT declare {member} — each inherits the "
            + "interface's empty default body and silently swallows the delivery announcement before it "
            + "ever reaches whatever it wraps: " + string.Join(", ", missing));

        // Behavioural forward proof: the REAL production chain (RunCommand.BuildObserverChain), exactly as
        // ObserverForwardingSweepTests drives it — never a hand-built stack of decorators, which would prove
        // only that the decorators forward to EACH OTHER, never that RunCommand actually assembles them
        // this way. Pass ONE WaveDeliveredRecord instance through and assert the SAME instance reaches the
        // inner observer.
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "wave-delivered-cli-sweep-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var inner = new RecordingObserver();

        IRunObserver chain = RunCommand.BuildObserverChain(
            inner, logsRoot, "wave-delivered-cli-sweep-run", plan, logUrlForTask: null, diagramSeed: null);

        WaveNode wave = FixtureWave();
        WaveDeliveredRecord delivery = FixtureDelivery();

        chain.WaveDelivered(wave, delivery);

        Assert.Single(inner.Calls);
        Assert.Same(wave, inner.Calls[0].Wave);
        Assert.Same(delivery, inner.Calls[0].Delivery);
    }

    // ── 2. ConsoleRunObserver renders the delivered wave and commit — MUST BE RED ────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ConsoleRunObserver_PrintsTheDeliveredWaveAndCommit()
    {
        var writer = new StringWriter();
        // Declared as IRunObserver, not ConsoleRunObserver: WaveDelivered is a default-interface member
        // ConsoleRunObserver does not (yet) override, and a DIM resolves only through the interface type —
        // calling it through the concrete type would not even compile.
        IRunObserver observer = new ConsoleRunObserver(writer);

        observer.WaveDelivered(FixtureWave("wave-02-provision"), FixtureDelivery("a1b2c3d4"));

        string output = writer.ToString();
        Assert.Contains("wave-02-provision", output);
        Assert.Contains("a1b2c3d4", output);
    }

    // ── 3. LiveRunObserver renders the delivered wave and commit — MUST BE RED ───────────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public async Task LiveRunObserver_PrintsTheDeliveredWaveAndCommit()
    {
        var console = new TestConsole().Width(100).Interactive();
        TaskNode a = FlatTask("01-a");

        await using (var observer = new LiveRunObserver([a], console: console))
        {
            // Cast to IRunObserver: WaveDelivered is a default-interface member LiveRunObserver does not
            // (yet) override, and a DIM resolves only through the interface type. The variable stays typed
            // as LiveRunObserver so `await using` keeps calling IAsyncDisposable.DisposeAsync.
            ((IRunObserver)observer).WaveDelivered(FixtureWave("wave-02-provision"), FixtureDelivery("a1b2c3d4"));
        }

        string output = console.Output;
        Assert.Contains("wave-02-provision", output);
        Assert.Contains("a1b2c3d4", output);
    }
}
