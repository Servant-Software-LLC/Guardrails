using System.Reflection;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;
using Spectre.Console.Testing;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 41 §6: the CLI half of <see cref="IRunObserver.SuppliedResourcesCommitted"/>'s new third
/// argument, <c>by</c> — the operator-facing rendering ("a line in the live table and <c>--no-ui</c>
/// output") and the two on-the-fly site decorators. The CORE half (<see cref="RunEventStream"/>/
/// <see cref="ObserverProjection"/> and the event's own shape) is <c>Guardrails.Core.Tests</c>'
/// <c>SuppliedObserverEventTests</c>.
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
/// TDD red: the interface gains an ADDED three-argument overload with an EMPTY default body (the
/// two-argument member stays until task 13 replaces all seven call sites in the same change), and none of
/// the four CLI decorators override it yet — so the three-argument call lands on that empty body and
/// disappears.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedObserverCliForwardingTests
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

    /// <summary>A throwaway directory tree — the real chain's decorators write their artefacts under a real root.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "gr-supply-cli-sweep-" + Guid.NewGuid().ToString("N"));

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
    /// The innermost observer the whole production chain is supposed to be transparent to. Declares ONLY
    /// the three-argument method — replaced, never overloaded, for the same reason the interface's real
    /// implementers must be (design 41 §6).
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
    /// A member is DECLARED by a type when the type itself carries it with the SAME parameter list. Mirrors
    /// <c>ObserverForwardingSweepTests.Declares</c> exactly, upgraded to compare parameter TYPES rather than
    /// only the member name — a name-only match is exactly the defect design 41 §6 is written against: once
    /// the three-argument member exists, a decorator that kept the two-argument one satisfies a name-only
    /// census perfectly while the Scheduler's three-argument call still lands on the empty default body.
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

    // ── 1. ConsoleRunObserver, through the REAL --no-ui production chain — MUST BE RED ──────────────

    [Fact]
    public void ConsoleRunObserver_ForwardsTheEventWithTheSupplier()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "console-supply-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var writer = new StringWriter();
        var console = new ConsoleRunObserver(writer);

        // The REAL production chain — never a hand-built stack of decorators (ObserverForwardingSweepTests'
        // own rule): a hand-built chain would prove only that the decorators forward to EACH OTHER, never
        // that RunCommand actually assembles the --no-ui path this way.
        IRunObserver chain = RunCommand.BuildObserverChain(
            console, logsRoot, "console-supply-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "operator");

        string output = writer.ToString();
        Assert.Contains("a1b2c3d4", output);
        Assert.Contains("operator", output);
    }

    // ── 2. LiveRunObserver renders it, with the supplier — MUST BE RED ──────────────────────────────

    [Fact]
    public async Task LiveRunObserver_RendersTheSupplierInTheLiveTable()
    {
        var console = new TestConsole().Width(100).Interactive();
        TaskNode a = FlatTask("01-a");

        await using (var observer = new LiveRunObserver([a], console: console))
        {
            // Cast to IRunObserver: the three-argument SuppliedResourcesCommitted is a default-interface
            // member LiveRunObserver does not (yet) override, and a DIM resolves only through the interface
            // type. The variable stays typed as LiveRunObserver so `await using` keeps calling
            // IAsyncDisposable.DisposeAsync.
            ((IRunObserver)observer).SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "overwatcher");
        }

        // Design 41 §6: "the live table prints `supplied by overwatcher: 1 resource(s) committed <sha> —
        // <paths>`". Asserted piecewise, not as one contiguous substring: Spectre markup ([bold]/[grey])
        // renders as ANSI codes bracketing the commit span, so the commit sits between escape sequences
        // rather than flush against the surrounding plain text.
        string output = console.Output;
        Assert.Contains("supplied by overwatcher:", output);
        Assert.Contains("1 resource(s) committed", output);
        Assert.Contains("a1b2c3d4", output);
        Assert.Contains("vendor/mermaid.min.js", output);
    }

    // ── 3. ConsoleRunObserver's own --no-ui line, tested directly — MUST BE RED ──────────────────────

    [Fact]
    public void NoUi_PrintsTheSuppliedResourcesLineNamingTheSupplier()
    {
        var writer = new StringWriter();
        // Declared as IRunObserver, not ConsoleRunObserver: the three-argument SuppliedResourcesCommitted is
        // a default-interface member ConsoleRunObserver does not (yet) override, and a DIM resolves only
        // through the interface type — calling it through the concrete type would not even compile.
        IRunObserver observer = new ConsoleRunObserver(writer);

        observer.SuppliedResourcesCommitted(["vendor/mermaid.min.js", "vendor/plugin.js"], "a1b2c3d4", "overwatcher");

        string output = writer.ToString();
        // Design 41 §6's exact wording — an operator greps for this literal.
        Assert.Contains("[supplied] by overwatcher:", output);
        Assert.Contains(
            "[supplied] by overwatcher: 1 resource(s) committed a1b2c3d4: "
            + "vendor/mermaid.min.js, vendor/plugin.js",
            output);
    }

    // ── 4. the on-the-fly site decorators declare AND forward it, with the supplier — MUST BE RED ──

    [Fact]
    public void EveryCliDecorator_DeclaresAndForwardsTheEventWithTheSupplier()
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

        MethodInfo member = ThreeArgMember();

        string[] missing = [.. decorators.Where(d => !Declares(d.Resolved!, member)).Select(d => d.TypeName)];
        Assert.True(
            missing.Length == 0,
            $"The following CLI transparent decorator(s) do NOT declare {member.Name}(IReadOnlyList<string>, "
            + "string, string) — each inherits the interface's empty default body and silently swallows the "
            + "supplied-resources announcement (or its supplier) before it ever reaches whatever it wraps: "
            + string.Join(", ", missing));

        // Behavioural forward proof: the REAL production chain, exactly as ObserverForwardingSweepTests
        // drives it — proves the whole chain (Core AND Cli decorators alike) never drops the call or the
        // supplier it now carries.
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "cli-sweep-supply-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var inner = new RecordingObserver();

        IRunObserver chain = RunCommand.BuildObserverChain(
            inner, logsRoot, "cli-sweep-supply-run", plan, logUrlForTask: null, diagramSeed: null);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "operator");

        Assert.Single(inner.Calls);
        Assert.Equal("a1b2c3d4", inner.Calls[0].Commit);
        Assert.Equal("operator", inner.Calls[0].By);
        Assert.Equal(["vendor/mermaid.min.js"], inner.Calls[0].Paths);
    }

    // ── 5. the artifact test — proven at the produced events.jsonl row and console output ────────────

    [Fact]
    public void OverwatcherSupply_ReachesTheEventsJsonlRowAndTheNoUiLine()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "overwatcher-supply-run");
        TaskNode task = FlatTask("01-first");
        PlanDefinition plan = MinimalPlan([task]);
        var writer = new StringWriter();

        // The REAL chain, exactly as design 41 §6 requires this proven — never a hand-stacked decorator,
        // and the assertions below read what was PRODUCED rather than grepping a source file.
        IRunObserver chain = RunCommand.BuildObserverChain(
            new ConsoleRunObserver(writer), logsRoot, "overwatcher-supply-run", plan,
            logUrlForTask: null, diagramSeed: null);

        chain.SuppliedResourcesCommitted(["vendor/mermaid.min.js"], "a1b2c3d4", "overwatcher");

        string eventsPath = Path.Combine(logsRoot, "events.jsonl");
        Assert.True(
            File.Exists(eventsPath),
            $"events.jsonl was never written at '{eventsPath}' — the three-argument call never reached "
            + "RunEventStream through the real chain.");

        string line = File.ReadAllLines(eventsPath).Single(l => l.Contains("supplied-resources-committed"));
        JsonNode row = JsonNode.Parse(line)!;
        Assert.Equal("overwatcher", row["by"]!.GetValue<string>());

        Assert.Contains("[supplied] by overwatcher:", writer.ToString());
    }
}
