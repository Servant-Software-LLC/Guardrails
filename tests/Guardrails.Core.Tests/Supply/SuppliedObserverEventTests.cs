using System.Reflection;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §6: <see cref="IRunObserver.SuppliedResourcesCommitted"/> gains a third argument, <c>by</c> —
/// <c>events.jsonl</c> is what an unattended consumer reads, and a record able to name only one supplier
/// (never distinguishing an operator's drain from the overwatcher's auto-resolve) is not provenance. This
/// file pins the event's SHAPE (paths, commit AND by) and the CORE half of the forwarding contract —
/// <see cref="RunEventStream"/> and <see cref="ObserverProjection"/>, the two transparent decorators this
/// project can see. The CLI half (<c>ConsoleRunObserver</c>/<c>LiveRunObserver</c>/the two on-the-fly site
/// decorators) is <c>Guardrails.Integration.Tests</c>' <c>SuppliedObserverCliForwardingTests</c> — this
/// project references <see cref="Guardrails.Core"/> alone (see
/// <c>tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs</c>'s header), so a CLI type cannot
/// even compile here.
///
/// <para>
/// TDD red: the interface gains an ADDED three-argument overload (the two-argument member stays until task
/// 13 replaces all seven call sites in the same change) with an EMPTY default body, and neither
/// <see cref="RunEventStream"/> nor <see cref="ObserverProjection"/> overrides it yet — so the
/// three-argument call lands on that empty body and disappears. <see cref="ADecoratorThatDropsTheEvent_IsCaught"/>
/// and <see cref="NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything"/> are the two
/// exceptions: self-contained negative controls, DECLARED-EXEMPT from the red census (see the header of
/// <c>guardrails/02-tests-fail-on-stubs.ps1</c>).
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
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
    /// The innermost observer every CORE decorator must be transparent to. Records the WHOLE payload,
    /// including the supplier — the failure mode this file is about is exactly "the call (or the `by` it
    /// carries) never arrives at all", mirroring
    /// <c>ObserverForwardingSweepTests.RecordingObserver</c> (<c>tests/Guardrails.Integration.Tests/RunEvents</c>).
    /// Declares ONLY the three-argument method — replaced, never overloaded, for the same reason the
    /// interface's real implementers must be (design 41 §6).
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(IReadOnlyList<string> Paths, string Commit, string By)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }

        public void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) =>
            Calls.Add((paths, commit, by));
    }

    /// <summary>
    /// A deliberately non-forwarding decorator — the negative control's fixture. It declares neither form
    /// of <see cref="IRunObserver.SuppliedResourcesCommitted"/>, so the three-argument call resolves to the
    /// interface's own empty default and <c>_inner</c> never hears about it — exactly the defect
    /// <see cref="EveryDecorator_ForwardsTheEventWithTheSupplier"/> exists to catch.
    /// </summary>
    private sealed class DroppingObserver(IRunObserver inner) : IRunObserver
    {
        public void TaskStarting(TaskNode task) => inner.TaskStarting(task);
        public void TaskFinished(TaskResult result) => inner.TaskFinished(result);
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => inner.GuardrailFinished(task, result);
        public void PlanHashMismatch(string previousPlanHash) => inner.PlanHashMismatch(previousPlanHash);
    }

    /// <summary>
    /// A member is DECLARED by a type when the type itself carries it with the SAME parameter list — a
    /// name-only match is exactly the defect design 41 §6 is written against: once the three-argument
    /// member exists, a decorator that kept the two-argument one satisfies a name-only census perfectly
    /// while the Scheduler's three-argument call still lands on the empty default body. The EndsWith branch
    /// admits an explicit interface implementation, named
    /// <c>Guardrails.Core.Execution.IRunObserver.SuppliedResourcesCommitted</c>.
    /// </summary>
    private static bool Declares(Type type, MethodInfo member) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => (m.Name == member.Name || m.Name.EndsWith("." + member.Name, StringComparison.Ordinal))
                      && m.GetParameters().Select(p => p.ParameterType)
                           .SequenceEqual(member.GetParameters().Select(p => p.ParameterType)));

    private static MethodInfo ThreeArgMember() =>
        typeof(IRunObserver).GetMethod(
            nameof(IRunObserver.SuppliedResourcesCommitted),
            [typeof(IReadOnlyList<string>), typeof(string), typeof(string)])
        ?? throw new InvalidOperationException(
            "IRunObserver has no three-argument SuppliedResourcesCommitted(IReadOnlyList<string>, string, "
            + "string) overload — the stub this task adds is missing.");

    // ── 1. the event's shape — MUST BE RED (RunEventStream does not persist `by` yet) ──────────────

    [Fact]
    public void Event_CarriesThePathsTheCommitAndTheSupplier()
    {
        // Declared as IRunObserver, not RunEventStream: the three-argument SuppliedResourcesCommitted is a
        // default-interface member RunEventStream does not (yet) override, and a DIM resolves only through
        // the interface type — calling it through the concrete type would not even compile.
        IRunObserver stream = new RunEventStream(IRunObserver.Null, _logsDir, "R");

        stream.SuppliedResourcesCommitted(["vendor/mermaid.min.js", "vendor/plugin.js"], "a1b2c3d4", "operator");

        string eventsPath = Path.Combine(_logsDir, "events.jsonl");
        Assert.True(
            File.Exists(eventsPath),
            "RunEventStream did not append a row for SuppliedResourcesCommitted — the interface's empty "
            + "three-argument default swallowed it, which is the exact silence design 41 §6 exists to close.");

        string line = File.ReadAllLines(eventsPath).Single();
        JsonNode row = JsonNode.Parse(line)!;

        Assert.Equal("supplied-resources-committed", row["kind"]!.GetValue<string>());
        Assert.Equal("a1b2c3d4", row["commit"]!.GetValue<string>());
        Assert.Equal("operator", row["by"]!.GetValue<string>());
        Assert.Equal(
            new[] { "vendor/mermaid.min.js", "vendor/plugin.js" },
            row["paths"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ── 2. every CORE decorator forwards it, with the supplier — MUST BE RED ────────────────────────

    [Fact]
    public void EveryDecorator_ForwardsTheEventWithTheSupplier()
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

        MethodInfo member = ThreeArgMember();

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            $"The following CORE transparent decorator(s) do NOT declare {member.Name}(IReadOnlyList<string>, "
            + "string, string) — each inherits the interface's empty default body and silently swallows the "
            + "supplied-resources announcement (or its supplier) before it ever reaches whatever it wraps: "
            + string.Join(", ", missing));

        // Behavioural forward proof: the real Core-only composition (mirrors the Core.Execution portion of
        // RunCommand.BuildObserverChain — Guardrails.Cli is invisible to this project, so the chain is
        // rebuilt here rather than calling that method).
        var inner = new RecordingObserver();
        IRunObserver stream = new RunEventStream(inner, _logsDir, "R");
        IRunObserver chain = new ObserverProjection(stream, _logsDir);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "operator");

        Assert.Single(inner.Calls);
        Assert.Equal("a1b2c3d4", inner.Calls[0].Commit);
        Assert.Equal("operator", inner.Calls[0].By);
        Assert.Equal(["vendor/mermaid.min.js"], inner.Calls[0].Paths);
    }

    // ── 3. the two-argument member is REPLACED, not overloaded — MUST BE RED until task 13 ─────────

    [Fact]
    public void TheTwoArgumentMember_IsReplacedNotOverloaded()
    {
        const string name = nameof(IRunObserver.SuppliedResourcesCommitted);

        // Non-vacuity floor: the three-argument overload must exist, or a vanished member would make the
        // absence of the two-argument one below meaningless — this is the ONE row that fails if task 13
        // takes the cheap route of leaving the old member in place beside the new one.
        Assert.NotNull(typeof(IRunObserver).GetMethod(
            name, [typeof(IReadOnlyList<string>), typeof(string), typeof(string)]));

        Assert.Null(typeof(IRunObserver).GetMethod(
            name, [typeof(IReadOnlyList<string>), typeof(string)]));
    }

    // ── 4. the negative control — DECLARED-EXEMPT from the red census; holds green throughout ──────

    [Fact]
    public void ADecoratorThatDropsTheEvent_IsCaught()
    {
        Assert.False(Declares(typeof(DroppingObserver), ThreeArgMember()));

        var inner = new RecordingObserver();
        IRunObserver decorator = new DroppingObserver(inner);

        decorator.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "operator");

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
        Assert.False(Declares(nullObserver!, ThreeArgMember()));
    }
}
