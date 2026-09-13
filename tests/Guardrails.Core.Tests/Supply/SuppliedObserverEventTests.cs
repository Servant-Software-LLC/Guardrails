using System.Reflection;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §2 step 3: the harness announces a supplied-resources commit on <see cref="IRunObserver"/> so
/// a run whose base changed underneath it says so — a silent base change is indistinguishable from a
/// harness bug when a later task behaves unexpectedly. This file pins the event's SHAPE (it carries the
/// paths that landed and the commit they landed in, tying an operator's read of the log to the §4
/// provenance record) and the CORE half of the forwarding contract — <see cref="RunEventStream"/> and
/// <see cref="ObserverProjection"/>, the two transparent decorators this project can see. The CLI half
/// (<c>ConsoleRunObserver</c>/<c>LiveRunObserver</c>/the two on-the-fly site decorators) is
/// <c>Guardrails.Integration.Tests</c>' <c>SuppliedObserverCliForwardingTests</c> — this project references
/// <see cref="Guardrails.Core"/> alone (see
/// <c>tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs</c>'s header), so a CLI type cannot
/// even compile here.
///
/// <para>
/// TDD red: neither <see cref="RunEventStream"/> nor <see cref="ObserverProjection"/> overrides
/// <see cref="IRunObserver.SuppliedResourcesCommitted"/> yet — this task adds only the interface's no-op
/// default (so every existing implementer keeps compiling) and these tests. Task 08 makes them green by
/// declaring the member on both. <see cref="ADecoratorThatDropsTheEvent_IsCaught"/> is the one exception:
/// it is a self-contained negative control, so it holds GREEN on the stub tree and after (see the header
/// of <c>guardrails/02-tests-fail-on-stubs.ps1</c> — DECLARED-EXEMPT from the red census).
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedObserverEventTests : IDisposable
{
    private readonly string _logsDir =
        Path.Combine(Path.GetTempPath(), "gr-supply-observer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logsDir, recursive: true); } catch (IOException) { }
    }

    // ── shared fixtures ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The innermost observer every CORE decorator must be transparent to. Records the WHOLE payload, not
    /// a count — the failure mode this file is about is exactly "the call never arrives at all", mirroring
    /// <c>ObserverForwardingSweepTests.RecordingObserver</c> (<c>tests/Guardrails.Integration.Tests/RunEvents</c>).
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(IReadOnlyList<string> Paths, string Commit)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }

        public void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit) =>
            Calls.Add((paths, commit));
    }

    /// <summary>
    /// A deliberately non-forwarding decorator — the negative control's fixture. It declares none of
    /// <see cref="IRunObserver"/>'s optional members, so <see cref="IRunObserver.SuppliedResourcesCommitted"/>
    /// resolves to the interface's own empty default and <c>_inner</c> never hears about it — exactly the
    /// defect <see cref="EveryDecorator_ForwardsTheEvent"/> exists to catch.
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

    // ── 1. the event's shape — MUST BE RED (RunEventStream does not persist it yet) ────────────────

    [Fact]
    public void Event_CarriesThePathsAndTheCommit()
    {
        // Declared as IRunObserver, not RunEventStream: SuppliedResourcesCommitted is a default-interface
        // member RunEventStream does not (yet) override, and a DIM resolves only through the interface
        // type — calling it through the concrete type would not even compile.
        IRunObserver stream = new RunEventStream(IRunObserver.Null, _logsDir, "R");

        stream.SuppliedResourcesCommitted(["vendor/mermaid.min.js", "vendor/plugin.js"], "a1b2c3d4");

        string eventsPath = Path.Combine(_logsDir, "events.jsonl");
        Assert.True(
            File.Exists(eventsPath),
            "RunEventStream did not append a row for SuppliedResourcesCommitted — the interface's no-op "
            + "default swallowed it, which is the exact silence design 40 §2 step 3 exists to close.");

        string line = File.ReadAllLines(eventsPath).Single();
        JsonNode row = JsonNode.Parse(line)!;

        Assert.Equal("supplied-resources-committed", row["kind"]!.GetValue<string>());
        Assert.Equal("a1b2c3d4", row["commit"]!.GetValue<string>());
        Assert.Equal(
            new[] { "vendor/mermaid.min.js", "vendor/plugin.js" },
            row["paths"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ── 2. every CORE decorator forwards it — MUST BE RED (neither declares it yet) ────────────────

    [Fact]
    public void EveryDecorator_ForwardsTheEvent()
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

        const string member = nameof(IRunObserver.SuppliedResourcesCommitted);

        // Non-vacuity floor: the member itself must exist, or the census below is vacuously true.
        Assert.NotNull(typeof(IRunObserver).GetMethod(member));

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            $"The following CORE transparent decorator(s) do NOT declare {member} — each inherits the "
            + "interface's empty default body and silently swallows the supplied-resources announcement "
            + "before it ever reaches whatever it wraps: " + string.Join(", ", missing));

        // Behavioural forward proof: the real Core-only composition (mirrors the Core.Execution portion of
        // RunCommand.BuildObserverChain — Guardrails.Cli is invisible to this project, so the chain is
        // rebuilt here rather than calling that method).
        var inner = new RecordingObserver();
        IRunObserver stream = new RunEventStream(inner, _logsDir, "R");
        IRunObserver chain = new ObserverProjection(stream, _logsDir);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4");

        Assert.Single(inner.Calls);
        Assert.Equal("a1b2c3d4", inner.Calls[0].Commit);
        Assert.Equal(["vendor/mermaid.min.js"], inner.Calls[0].Paths);
    }

    // ── 3. the negative control — DECLARED-EXEMPT from the red census; holds green throughout ──────

    [Fact]
    public void ADecoratorThatDropsTheEvent_IsCaught()
    {
        Assert.False(Declares(typeof(DroppingObserver), nameof(IRunObserver.SuppliedResourcesCommitted)));

        var inner = new RecordingObserver();
        IRunObserver decorator = new DroppingObserver(inner);

        decorator.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4");

        Assert.Empty(inner.Calls);
    }

    // ── declared exemption from the census above — copied from ObserverForwardingSweepTests ────────

    [Fact]
    public void NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything()
    {
        // Mirrors ObserverForwardingSweepTests' declared exemption for the two renderers (they don't
        // declare RunFinished because they're disposed first): NullObserver is deliberately excluded from
        // the hand-listed census above, by design — its whole contract (IRunObserver's own doc: "An
        // observer that does nothing") is to swallow every event, so declaring a forward here would be the
        // defect, not the gap.
        Type? nullObserver = typeof(IRunObserver).GetNestedType("NullObserver", BindingFlags.NonPublic);
        Assert.NotNull(nullObserver);
        Assert.False(Declares(nullObserver!, nameof(IRunObserver.SuppliedResourcesCommitted)));
    }
}
