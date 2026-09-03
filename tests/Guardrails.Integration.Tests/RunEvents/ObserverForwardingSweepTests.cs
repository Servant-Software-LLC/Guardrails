using System.Reflection;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// <see cref="IRunObserver"/>'s optional members all have empty default bodies, so a transparent decorator
/// that simply omits one still compiles, still satisfies the interface, and silently swallows that event in
/// every mode — the trap already documented on <see cref="IRunObserver.AttemptModelResolved"/>,
/// <see cref="IRunObserver.WaveGateFinished"/>, <see cref="IRunObserver.VerifierAdvisoryFound"/>, and now
/// <see cref="IRunObserver.RunFinished"/>.
///
/// <para>The two prior reflection guards (<c>WaveGateForwardingTests</c>, <c>AttemptModelForwardingTests</c>)
/// each swept only the <c>Guardrails.Cli</c> assembly, so the two <c>Guardrails.Core</c> projections —
/// <see cref="RunEventStream"/> and <see cref="ObserverProjection"/> — were unguarded. This file supersedes
/// both narrower sweeps with one exhaustive census across BOTH assemblies (test 1), plus a behavioural
/// forward proof neither reflection sweep can give: a decorator that DECLARES a member and then never calls
/// <c>_inner</c> passes every reflection sweep while still swallowing the event (test 2).</para>
/// </summary>
public sealed class ObserverForwardingSweepTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>A throwaway directory tree — the real chain's decorators write their artefacts under a real root.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-observer-sweep-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// The innermost observer the whole chain is supposed to be transparent to. Records the WHOLE payload,
    /// not a count — the failure mode this file is about is exactly "the call never arrives at all".
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(int? ExitCode, string? FaultKind)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void RunFinished(int? exitCode, string? faultKind) => Calls.Add((exitCode, faultKind));
    }

    /// <summary>
    /// A member is DECLARED by a type when the type itself carries it — an implicit override or an explicit
    /// interface implementation (private, named <c>Guardrails.Core.Execution.IRunObserver.&lt;Member&gt;</c>).
    /// Inheriting the interface's empty default declares nothing, which is precisely the state these tests
    /// must be able to see.
    /// </summary>
    private static bool Declares(Type type, string methodName) =>
        type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == methodName || m.Name.EndsWith("." + methodName, StringComparison.Ordinal));

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. The exhaustive reflection census — MUST BE RED (RunFinished is the newly-added member).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void EveryTransparentDecorator_DeclaresEveryIRunObserverMember()
    {
        Assembly coreAssembly = typeof(IRunObserver).Assembly;
        Assembly cliAssembly = typeof(ConsoleRunObserver).Assembly;

        (Assembly Asm, string TypeName)[] decoratorNames =
        [
            (coreAssembly, "Guardrails.Core.Execution.RunEventStream"),
            (coreAssembly, "Guardrails.Core.Execution.ObserverProjection"),
            (cliAssembly, "Guardrails.Cli.Ui.OnTheFlyDiagramObserver"),
            (cliAssembly, "Guardrails.Cli.Ui.OnTheFlyLogSiteObserver"),
        ];

        (string TypeName, Type? Resolved)[] decorators =
        [
            .. decoratorNames.Select(d => (d.TypeName, Resolved: d.Asm.GetType(d.TypeName, throwOnError: false)))
        ];

        // Non-vacuity floor: a type lookup that silently returned null must fail LOUDLY and by name — a
        // sweep that quietly skipped an unresolved type would report success over the ones it still checked
        // while proving nothing about the one that vanished.
        string[] unresolved = [.. decorators.Where(d => d.Resolved is null).Select(d => d.TypeName)];
        Assert.True(
            unresolved.Length == 0,
            $"{unresolved.Length} of {decorators.Length} transparent-decorator type(s) did not resolve via "
            + $"reflection — the type may have moved or been renamed: {string.Join(", ", unresolved)}");

        MethodInfo[] members = typeof(IRunObserver).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // Non-vacuity floor: a filtering bug that hollowed this list would make the sweep below vacuously
        // true no matter what the decorators actually declare.
        Assert.NotEmpty(members);

        string[] missing =
        [
            .. decorators.SelectMany(d => members
                .Where(m => !Declares(d.Resolved!, m.Name))
                .Select(m => $"{d.Resolved!.FullName} : {m.Name}"))
        ];

        Assert.True(
            missing.Length == 0,
            "The following (type, member) pairs are IRunObserver members a transparent decorator does NOT "
            + "declare — each inherits the interface's empty default body and silently swallows that event "
            + "before it ever reaches whatever the decorator wraps:\n" + string.Join("\n", missing));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. The behavioural forward proof — MUST BE RED (no decorator forwards RunFinished yet).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void RunFinished_ReachesTheEventStream_ThroughTheWholeChain()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "run-finished-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var inner = new RecordingObserver();

        // The REAL production chain — never a hand-built stack of decorators. A hand-built chain would
        // prove only that the decorators forward to EACH OTHER, never that RunCommand actually assembles
        // them this way; it would also pin a RunEventStream constructor signature this file has no business
        // fixing if a later task changes it.
        IRunObserver chain = RunCommand.BuildObserverChain(
            inner, logsRoot, "run-finished-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.RunFinished(0, null);
        chain.RunFinished(null, "InvalidOperationException");

        Assert.Equal(2, inner.Calls.Count);

        Assert.Equal(0, inner.Calls[0].ExitCode);
        Assert.Null(inner.Calls[0].FaultKind);

        Assert.Null(inner.Calls[1].ExitCode);
        Assert.Equal("InvalidOperationException", inner.Calls[1].FaultKind);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. Declared exemption from the census above — a correct implementation leaves this GREEN.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheTwoRenderers_DoNotDeclareRunFinished_BecauseTheyAreDisposedFirst()
    {
        // In RunCommand, `await using var liveObserver = new LiveRunObserver(...)` is scoped to the
        // `if (live)` block, so the live observer — and the Spectre live region it owns — is disposed
        // BEFORE RunFinished is ever raised on the chain. RunFinished is therefore the FIRST IRunObserver
        // call either renderer could ever receive AFTER that teardown. Declaring it on a renderer would be a
        // use-after-dispose bug, not a style choice about whether a renderer "should" render a completion
        // line — the next reader who thinks a completion line would look nice must see THIS reason, not a
        // nicer-sounding wrong one.
        Assert.False(
            Declares(typeof(LiveRunObserver), nameof(IRunObserver.RunFinished)),
            $"{nameof(LiveRunObserver)} declares {nameof(IRunObserver.RunFinished)}, but by the time that "
            + "event fires the live observer has already been disposed — RunCommand's `await using` scopes "
            + "it to the `if (live)` block — so handling it here would be a use-after-dispose.");

        Assert.False(
            Declares(typeof(ConsoleRunObserver), nameof(IRunObserver.RunFinished)),
            $"{nameof(ConsoleRunObserver)} declares {nameof(IRunObserver.RunFinished)}. Nothing about the "
            + $"console path is disposed early, but symmetry with {nameof(LiveRunObserver)} still matters: "
            + "neither renderer is meant to own the run's completion line.");
    }
}
