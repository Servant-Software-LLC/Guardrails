using System.Reflection;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §6: <see cref="IRunObserver.SuppliedResourcesCommitted"/> gains a third argument, <c>by</c> —
/// a record able to name only one supplier is not provenance, and <c>events.jsonl</c> is what an
/// unattended consumer reads. This file pins the THREE-argument event's shape and the CORE half of the
/// forwarding contract — <see cref="RunEventStream"/> and <see cref="ObserverProjection"/>, the two
/// transparent decorators this project can see. The CLI half
/// (<c>ConsoleRunObserver</c>/<c>LiveRunObserver</c>/the two on-the-fly site decorators) is
/// <c>Guardrails.Integration.Tests</c>' <c>SuppliedObserverCliForwardingTests</c> — this project references
/// <see cref="Guardrails.Core"/> alone (see
/// <c>tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs</c>'s header), so a CLI type cannot
/// even compile here.
///
/// <para>
/// TDD red: this task ADDS the three-argument member as a stub alongside the two-argument one (never a
/// swap — that is task 13's job, in the same change that migrates all six implementers and the one caller,
/// <c>Scheduler.cs:4927</c>). Neither <see cref="RunEventStream"/> nor <see cref="ObserverProjection"/>
/// overrides the three-argument form yet, so every census and forward-proof below is red until task 13
/// declares it on both. <see cref="ADecoratorThatDropsTheEvent_IsCaught"/> and
/// <see cref="NullObserver_DoesNotDeclareTheEvent_BecauseItsContractIsToSwallowEverything"/> are the two
/// exceptions: both are self-contained negative controls, DECLARED-EXEMPT from the red census (see
/// <c>guardrails/02-tests-fail-on-stubs.ps1</c>'s header) — they hold GREEN on the stub tree and after.
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
    /// The innermost observer every CORE decorator must be transparent to. Records the WHOLE payload, not
    /// a count — the failure mode this file is about is exactly "the call never arrives at all", mirroring
    /// <c>ObserverForwardingSweepTests.RecordingObserver</c> (<c>tests/Guardrails.Integration.Tests/RunEvents</c>).
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(IReadOnlyList<string> Paths, string Commit, string By)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }

        // Replaced, never overloaded — for the same reason the interface member is (design 41 §6): a
        // double that declared both forms could not prove which one a caller actually reached.
        public void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) =>
            Calls.Add((paths, commit, by));
    }

    /// <summary>
    /// A deliberately non-forwarding decorator — the negative control's fixture. It declares NEITHER form
    /// of <see cref="IRunObserver.SuppliedResourcesCommitted"/>, so the three-argument call resolves to the
    /// interface's own empty default and <c>_inner</c> never hears about it — exactly the defect
    /// <see cref="SuppliedObserverEventTests.EveryDecorator_ForwardsTheEventWithTheSupplier"/> exists to catch.
    /// </summary>
    private sealed class DroppingObserver(IRunObserver inner) : IRunObserver
    {
        public void TaskStarting(TaskNode task) => inner.TaskStarting(task);
        public void TaskFinished(TaskResult result) => inner.TaskFinished(result);
        public void GuardrailFinished(TaskNode task, GuardrailResult result) => inner.GuardrailFinished(task, result);
        public void PlanHashMismatch(string previousPlanHash) => inner.PlanHashMismatch(previousPlanHash);
    }

    /// <summary>
    /// A member is DECLARED by a type when the type itself carries it with the SAME parameter list — an
    /// implicit override or an explicit interface implementation (named
    /// <c>Guardrails.Core.Execution.IRunObserver.&lt;Member&gt;</c>). Comparing PARAMETER TYPES, not just
    /// the name, is what stops a decorator that kept the two-argument
    /// <see cref="IRunObserver.SuppliedResourcesCommitted"/> from satisfying a census for the three-argument
    /// one (design 41 §6) — the exact defect a name-only guard would hide.
    /// </summary>
    private static bool Declares(Type type, MethodInfo member) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => (m.Name == member.Name || m.Name.EndsWith("." + member.Name, StringComparison.Ordinal))
                      && m.GetParameters().Select(p => p.ParameterType)
                           .SequenceEqual(member.GetParameters().Select(p => p.ParameterType)));

    private static MethodInfo ThreeArgumentMember() =>
        typeof(IRunObserver).GetMethod(
            nameof(IRunObserver.SuppliedResourcesCommitted),
            [typeof(IReadOnlyList<string>), typeof(string), typeof(string)])
        ?? throw new InvalidOperationException(
            "IRunObserver has no three-argument SuppliedResourcesCommitted(IReadOnlyList<string>, string, "
            + "string) — the stub this task adds is missing.");

    // ── 1. the event's shape — MUST BE RED (RunEventStream does not persist `by` yet) ───────────────

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
            "RunEventStream did not append a row for the three-argument SuppliedResourcesCommitted — the "
            + "interface's empty default swallowed it, which is the exact silence design 41 §6 exists to close.");

        string line = File.ReadAllLines(eventsPath).Single();
        JsonNode row = JsonNode.Parse(line)!;

        Assert.Equal("supplied-resources-committed", row["kind"]!.GetValue<string>());
        Assert.Equal("a1b2c3d4", row["commit"]!.GetValue<string>());
        Assert.Equal("operator", row["by"]!.GetValue<string>());
        Assert.Equal(
            new[] { "vendor/mermaid.min.js", "vendor/plugin.js" },
            row["paths"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ── 2. every CORE decorator forwards it with the supplier — MUST BE RED (neither declares it yet) ──

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

        // Non-vacuity floor: a type lookup that silently returned null must fail LOUDLY and by name — a
        // sweep that quietly skipped an unresolved type would report success over the ones it still checked
        // while proving nothing about the one that vanished.
        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(
            unresolved.Length == 0,
            $"{unresolved.Length} of {decorators.Length} CORE transparent-decorator type(s) did not "
            + $"resolve via reflection — the type may have moved or been renamed: {string.Join(", ", unresolved)}");

        // Non-vacuity floor: the three-argument member itself must exist, or the census below is vacuous.
        MethodInfo member = ThreeArgumentMember();

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            "The following CORE transparent decorator(s) do NOT declare SuppliedResourcesCommitted"
            + "(IReadOnlyList<string>, string, string) — each inherits the interface's empty default body "
            + "and silently swallows the supplied-resources announcement before it ever reaches whatever it "
            + "wraps: " + string.Join(", ", missing));

        // Behavioural forward proof: the real Core-only composition (mirrors the Core.Execution portion of
        // RunCommand.BuildObserverChain — Guardrails.Cli is invisible to this project, so the chain is
        // rebuilt here rather than calling that method).
        var inner = new RecordingObserver();
        IRunObserver stream = new RunEventStream(inner, _logsDir, "R");
        IRunObserver chain = new ObserverProjection(stream, _logsDir);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "operator");

        Assert.Single(inner.Calls);
        Assert.Equal("a1b2c3d4", inner.Calls[0].Commit);
        Assert.Equal(["vendor/mermaid.min.js"], inner.Calls[0].Paths);
        Assert.Equal("operator", inner.Calls[0].By);
    }

    // ── 3. the member is replaced, never overloaded — MUST BE RED while this task's stub stands ─────

    [Fact]
    public void TheTwoArgumentMember_IsReplacedNotOverloaded()
    {
        // Non-vacuity floor: the three-argument overload must be found, or the assertion below is vacuous.
        MethodInfo? threeArgument = typeof(IRunObserver).GetMethod(
            nameof(IRunObserver.SuppliedResourcesCommitted),
            [typeof(IReadOnlyList<string>), typeof(string), typeof(string)]);
        Assert.True(
            threeArgument is not null,
            "IRunObserver has no three-argument SuppliedResourcesCommitted(IReadOnlyList<string>, string, "
            + "string) — the stub this task adds is missing.");

        // This is what makes "replaced, never overloaded" a fact rather than an intention (design 41 §6):
        // it is red while this task's own stub stands (the two-argument member is still there, so the
        // three-argument one is an ADDED overload) and goes green only once task 13 deletes the old member.
        MethodInfo? twoArgument = typeof(IRunObserver).GetMethod(
            nameof(IRunObserver.SuppliedResourcesCommitted),
            [typeof(IReadOnlyList<string>), typeof(string)]);
        Assert.Null(twoArgument);
    }

    // ── 4. the negative control — DECLARED-EXEMPT from the red census; holds green throughout ──────

    [Fact]
    public void ADecoratorThatDropsTheEvent_IsCaught()
    {
        MethodInfo member = ThreeArgumentMember();

        Assert.False(Declares(typeof(DroppingObserver), member));

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
        Assert.False(Declares(nullObserver!, ThreeArgumentMember()));
    }
}
