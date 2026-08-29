using System.Reflection;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// Representative CORRECT shape, reduced to the properties this guardrail measures. It drives the PURE
/// static seams, it constructs the two DECORATORS (which is fine — their constructors touch no live
/// region), and it uses <c>typeof(LiveRunObserver).Assembly</c> plus <c>BindingFlags</c> for a
/// type-level sweep, which is the pattern <c>AttemptModelForwardingTests</c> already ships and which
/// this task's prompt points at. None of that is banned; only CONSTRUCTING the observer and reflecting
/// INTO it are.
///
/// Note the file also NAMES the banned things — in a doc comment, in a nameof(), and in an assertion
/// message string — because those are exactly the places a raw-text scan would false-RED on and a
/// two-level strip must not: "do not write new LiveRunObserver(...)" appears in prose right here.
/// </summary>
public sealed class ModelInRowTests
{
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch()
    {
        Assert.Equal("sonnet", Strip(LiveRunObserver.ModelCell("sonnet", "medium", false, false, false)));
        Assert.Equal("sonnet !", Strip(LiveRunObserver.ModelCell("sonnet", "medium", true, false, false)));
        Assert.DoesNotContain("MISMATCH", LiveRunObserver.ModelCell("sonnet", "medium", true, false, false));
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCellFromRoute_MapsTheLaunchEvent_AndFlagsAClimb()
    {
        foreach (string runner in new[] { "haiku", "sonnet", "opus" })
        {
            foreach (string? tier in new string?[] { null, "easy", "medium", "hard" })
            {
                foreach (string? requested in new string?[] { null, "medium" })
                {
                    Assert.Equal(
                        LiveRunObserver.ModelCell(runner, tier, requested is not null, false, false),
                        LiveRunObserver.ModelCellFromRoute(runner, tier, requested));
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver()
    {
        var inner = new RecordingObserver();
        var decorator = new OnTheFlyLogSiteObserver(inner, TempRoot(), "run", [], liveUrlForTask: null);

        ((IRunObserver)decorator).AttemptRouteResolved(Task("01"), 1, "sonnet", "claude-sonnet-5", "hard", "medium");
        ((IRunObserver)decorator).AttemptRouteResolved(Task("01"), 2, "sonnet", "claude-sonnet-5", "medium", null);

        Assert.Equal(2, inner.RouteCalls.Count);
        Assert.Equal("medium", inner.RouteCalls[0].RequestedTier);
        Assert.Null(inner.RouteCalls[1].RequestedTier);
    }

    /// <summary>
    /// A TYPE-level sweep. It constructs nothing and touches no live region, so it is legitimate — the
    /// ban is on `new LiveRunObserver(...)` and on typeof(LiveRunObserver).GetMethod(...), not on the
    /// reflection API. This mirrors AttemptModelForwardingTests' own third test.
    /// </summary>
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void EveryForwardingObserver_DeclaresAttemptRouteResolved()
    {
        Type[] forwarders = typeof(LiveRunObserver).Assembly
            .GetTypes()
            .Where(t => !t.IsInterface && typeof(IRunObserver).IsAssignableFrom(t))
            .ToArray();

        Assert.Contains(typeof(OnTheFlyLogSiteObserver), forwarders);

        foreach (Type t in forwarders.Where(Forwards))
        {
            MethodInfo[] declared = t.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.Contains(declared, m => m.Name.EndsWith(nameof(IRunObserver.AttemptRouteResolved), StringComparison.Ordinal));
        }
    }

    private static bool Forwards(Type t) =>
        t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IRunObserver)));

    private static string Strip(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[[^\]]*\]", string.Empty);

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "gr-524-" + Guid.NewGuid().ToString("N"));

    private static TaskNode Task(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = "fixture",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = []
    };

    private sealed class RecordingObserver : IRunObserver
    {
        public List<(string Runner, string Model, string? Tier, string? RequestedTier)> RouteCalls { get; } = [];

        public void AttemptRouteResolved(
            TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
            RouteCalls.Add((runner, model, tier, requestedTier));
    }
}
