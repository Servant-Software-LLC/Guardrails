using System.Reflection;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.WaveDelivery;

/// <summary>
/// Design 39 §5 "Wiring and halts": the Scheduler writes <c>waves.&lt;dir&gt;.delivered</c> around every
/// barrier delivery and raises <see cref="IRunObserver.WaveDelivered"/> only for a record whose status is
/// <c>delivered</c>, and only AFTER that record is persisted — an observer must never see a result the
/// journal does not hold. This file pins the event's CORE half: the interface member itself (added beside
/// <see cref="IRunObserver.WaveStarting"/>/<see cref="IRunObserver.WaveFinished"/> as a no-op default so
/// every existing implementer keeps compiling), the two CORE transparent decorators
/// (<see cref="RunEventStream"/>/<see cref="ObserverProjection"/>), and the projection's <c>observer.jsonl</c>
/// shape. The CLI half (<c>Guardrails.Cli.ConsoleRunObserver</c>/<c>LiveRunObserver</c> rendering and the two
/// on-the-fly site decorators) is <c>Guardrails.Integration.Tests</c>' <c>WaveDeliveredCliForwardingTests</c>
/// — <c>Guardrails.Core.Tests</c> references <c>Guardrails.Core</c> and nothing else (see
/// <c>tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs</c>'s header), so a CLI type cannot
/// even compile here.
///
/// <para>
/// TDD red: neither <see cref="RunEventStream"/> nor <see cref="ObserverProjection"/> overrides the new
/// member yet — this task adds only the interface's no-op default and these tests. Task 12 makes the CORE
/// decorators and the projection green. <see cref="ADecoratorThatDropsTheEvent_IsCaught"/> and
/// <see cref="RunEventStream_AppendsNoEventsRowForADelivery"/> are the two declared exemptions from the red
/// census — both hold GREEN on this tree and after.
/// </para>
///
/// <para>
/// No payload-shape test of the RAISED record lives here (review, 2026-09-13): a shape test of a record
/// handed to a no-op default passes on the stub, and which record the Scheduler raises — and when — cannot
/// be seen from this task's files. Task 28 pins that against the real Scheduler.
/// </para>
///
/// <para>
/// <see cref="WaveDeliveredRecord"/> is task 09's stub on this task's base: every getter throws
/// <see cref="NotImplementedException"/> until task 10 lands. Every fixture here builds one with an object
/// initializer and never reads a member back off it — the same-instance assertions below need only
/// reference identity, and the shape assertions below compare against the LITERAL values the test itself
/// passed in, never a round-trip through the record.
/// </para>
/// </summary>
public sealed class WaveDeliveredEventTests : IDisposable
{
    private readonly string _logsDir =
        Path.Combine(Path.GetTempPath(), "gr-wave-delivered-event-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logsDir, recursive: true); } catch (IOException) { }
    }

    // ── shared fixtures ──────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// The innermost observer every CORE decorator must be transparent to. Records the WHOLE payload, not a
    /// count — the failure mode this file is about is exactly "the call never arrives at all", mirroring
    /// <c>ObserverForwardingSweepTests.RecordingObserver</c> and <c>SuppliedObserverEventTests.RecordingObserver</c>.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(WaveNode Wave, WaveDeliveredRecord Delivery)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }

        public void WaveDelivered(WaveNode wave, WaveDeliveredRecord delivery) => Calls.Add((wave, delivery));
    }

    /// <summary>
    /// A deliberately non-forwarding decorator — the negative control's fixture. It declares none of
    /// <see cref="IRunObserver"/>'s optional members, so <see cref="IRunObserver.WaveDelivered"/> resolves
    /// to the interface's own empty default and <c>_inner</c> never hears about it — exactly the defect
    /// <see cref="ADecoratorThatDropsTheEvent_IsCaught"/> exists to catch.
    /// </summary>
    private sealed class DroppingObserver(IRunObserver inner) : IRunObserver
    {
        public void TaskStarting(TaskNode task) => inner.TaskStarting(task);
        public void TaskFinished(TaskResult result) => inner.TaskFinished(result);
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => inner.GuardrailFinished(task, result);
        public void PlanHashMismatch(string previousPlanHash) => inner.PlanHashMismatch(previousPlanHash);
    }

    /// <summary>
    /// A member is DECLARED by a type when the type itself carries it, mirroring
    /// <c>ObserverForwardingSweepTests.Declares</c> exactly — inheriting the interface's empty default
    /// declares nothing, which is precisely the state this file must be able to see.
    /// </summary>
    private static bool Declares(Type type, string methodName) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == methodName || m.Name.EndsWith("." + methodName, StringComparison.Ordinal));

    // ── 1. every CORE decorator forwards it — MUST BE RED (neither declares it yet) ─────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void EveryCoreDecorator_ForwardsTheEvent()
    {
        Assembly coreAssembly = typeof(IRunObserver).Assembly;

        (string TypeName, Type? Resolved)[] decorators =
        [
            ("Guardrails.Core.Execution.RunEventStream",
                coreAssembly.GetType("Guardrails.Core.Execution.RunEventStream", throwOnError: false)),
            ("Guardrails.Core.Execution.ObserverProjection",
                coreAssembly.GetType("Guardrails.Core.Execution.ObserverProjection", throwOnError: false)),
        ];

        // Non-vacuity floor (copied from ObserverForwardingSweepTests): a type lookup that silently
        // returned null must fail LOUDLY and by name — a sweep that quietly skipped an unresolved type
        // would report success over the ones it still checked while proving nothing about the one that
        // vanished.
        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(
            unresolved.Length == 0,
            $"{unresolved.Length} of {decorators.Length} CORE transparent-decorator type(s) did not "
            + $"resolve via reflection — the type may have moved or been renamed: {string.Join(", ", unresolved)}");

        const string member = nameof(IRunObserver.WaveDelivered);

        // Non-vacuity floor: the member itself must exist, or the census below is vacuously true.
        Assert.NotNull(typeof(IRunObserver).GetMethod(member));

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            $"The following CORE transparent decorator(s) do NOT declare {member} — each inherits the "
            + "interface's empty default body and silently swallows the delivery announcement before it "
            + "ever reaches whatever it wraps: " + string.Join(", ", missing));

        // Behavioural forward proof: the real Core-only composition (mirrors the Core.Execution portion of
        // RunCommand.BuildObserverChain — Guardrails.Cli is invisible to this project, so the chain is
        // rebuilt here rather than calling that method). Pass ONE WaveDeliveredRecord instance through and
        // assert the SAME instance reaches the inner observer — a decorator that rebuilds or re-reads the
        // record is not forwarding it.
        var inner = new RecordingObserver();
        IRunObserver stream = new RunEventStream(inner, _logsDir, "R");
        IRunObserver chain = new ObserverProjection(stream, _logsDir);

        WaveNode wave = FixtureWave();
        WaveDeliveredRecord delivery = FixtureDelivery();

        chain.WaveDelivered(wave, delivery);

        Assert.Single(inner.Calls);
        Assert.Same(wave, inner.Calls[0].Wave);
        Assert.Same(delivery, inner.Calls[0].Delivery);
    }

    // ── 2. the negative control — DECLARED-EXEMPT from the red census; holds green throughout ──────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ADecoratorThatDropsTheEvent_IsCaught()
    {
        Assert.False(Declares(typeof(DroppingObserver), nameof(IRunObserver.WaveDelivered)));

        var inner = new RecordingObserver();
        IRunObserver decorator = new DroppingObserver(inner);

        decorator.WaveDelivered(FixtureWave(), FixtureDelivery());

        Assert.Empty(inner.Calls);
    }

    // ── 3. ObserverProjection appends the delivery to observer.jsonl — MUST BE RED ──────────────────

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void ObserverProjection_AppendsTheDeliveryToObserverJsonl()
    {
        // Declared as IRunObserver, not ObserverProjection: WaveDelivered is a default-interface member
        // ObserverProjection does not (yet) override, and a DIM resolves only through the interface type —
        // calling it through the concrete type would not even compile.
        IRunObserver projection = new ObserverProjection(IRunObserver.Null, _logsDir);

        WaveNode wave = FixtureWave("wave-03-migrate");
        WaveDeliveredRecord delivery = new()
        {
            Status = WaveDeliveryStatus.Delivered,
            StartedAt = DateTimeOffset.UtcNow,
            Commit = "deadbeef",
            Covers = ["wave-01-bootstrap", "wave-02-provision", "wave-03-migrate"]
        };

        projection.WaveDelivered(wave, delivery);

        string observerPath = Path.Combine(_logsDir, "observer.jsonl");
        Assert.True(
            File.Exists(observerPath),
            "ObserverProjection did not append a row for WaveDelivered — the interface's no-op default "
            + "swallowed it, which drops the delivery from the record `guardrails attach` tails.");

        string line = File.ReadAllLines(observerPath).Single();
        JsonNode row = JsonNode.Parse(line)!;

        Assert.Equal("WaveDelivered", row["member"]!.GetValue<string>());
        Assert.Equal("wave-03-migrate", row["waveDir"]!.GetValue<string>());
        Assert.Equal("deadbeef", row["commit"]!.GetValue<string>());
        Assert.Equal(
            new[] { "wave-01-bootstrap", "wave-02-provision", "wave-03-migrate" },
            row["covers"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ── 4. RunEventStream writes no events.jsonl row for a delivery — DECLARED-EXEMPT; holds green ───

    [Fact]
    [Trait("Category", "WaveDelivery")]
    public void RunEventStream_AppendsNoEventsRowForADelivery()
    {
        IRunObserver stream = new RunEventStream(IRunObserver.Null, _logsDir, "R");

        stream.WaveDelivered(FixtureWave(), FixtureDelivery());

        string eventsPath = Path.Combine(_logsDir, "events.jsonl");
        Assert.False(
            File.Exists(eventsPath),
            "RunEventStream wrote an events.jsonl row for WaveDelivered — DECIDED (design 39 §5): a "
            + "delivery's durable, machine-readable record is run.json's waves.<dir>.delivered, persisted "
            + "before the event is raised, and events.jsonl gains no wave-delivered kind.");
    }
}
