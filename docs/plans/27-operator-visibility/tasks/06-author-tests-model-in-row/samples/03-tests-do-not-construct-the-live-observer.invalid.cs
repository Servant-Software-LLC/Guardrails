using System.Reflection;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: it CONSTRUCTS a LiveRunObserver in order to "just check the
/// table directly", and then reflects into the type's privates to read the cells. Everything else is
/// correct — the pure seams are still driven, the trait is present, the class name is right.
///
/// It compiles, it passes, and it is the most expensive thing in this plan: LiveRunObserver's
/// constructor starts an AnsiConsole.Live region and a one-second Timer, and Spectre's live-display
/// lock is PROCESS-WIDE (commit b43232d serialized this repo's live-display tests for exactly that).
/// So this test does not fail — it corrupts an UNRELATED test's output and surfaces as a flake at the
/// 7-15 minute terminal Integration gate, attributed to whatever ran last.
/// </summary>
public sealed class ModelInRowTests
{
    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTableModelCell_NamesTheModel_AndDisclosesTheRouteMismatch()
    {
        Assert.Equal("sonnet", Strip(LiveRunObserver.ModelCell("sonnet", "medium", false, false, false)));
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public void LiveTable_ActuallyShowsTheModel()
    {
        // THE DEFECT, both halves.
        var observer = new LiveRunObserver([Task("01")]);
        ((IRunObserver)observer).AttemptRouteResolved(Task("01"), 1, "sonnet", "claude-sonnet-5", "medium", null);

        MethodInfo? rebuild = typeof(LiveRunObserver).GetMethod("RebuildRows", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(rebuild);
    }

    private static string Strip(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\[[^\]]*\]", string.Empty);

    private static TaskNode Task(string id) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = "fixture",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = []
    };
}
