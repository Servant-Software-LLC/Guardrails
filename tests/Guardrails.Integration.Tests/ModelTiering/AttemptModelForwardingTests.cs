using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The DECORATOR surface of the attempt-model disclosure (#349). <see cref="IRunObserver.AttemptModelResolved"/>
/// is a DEFAULT interface member, so a decorator that simply omits it still compiles, still satisfies the
/// interface, and silently swallows the event: the call resolves to the empty body on the interface and the
/// inner observer — the live table, the console, the log site — is never told. Nothing anywhere reports the
/// loss, in any mode.
///
/// <para><b>Both shipped decorators sit in BOTH chains</b> (<c>OnTheFlyDiagramObserver</c> wraps
/// <c>OnTheFlyLogSiteObserver</c> wraps live-or-console), so one missing override takes the disclosure away
/// from every operator, not from a corner case. That is exactly how
/// <see cref="IRunObserver.VerifierAdvisoryFound"/> and the #469 breakdown phase were each lost once already;
/// <c>JitBreakdownVisibilityTests.T12_OnTheFlyLogSiteObserver_ForwardsBothPhaseMembers</c> is this file's
/// precedent. The reflection sweep that used to live in this file — checking a THIRD decorator added later
/// cannot re-open the same hole unnoticed — now lives in
/// <c>RunEvents.ObserverForwardingSweepTests.EveryTransparentDecorator_DeclaresEveryIRunObserverMember</c>,
/// which sweeps every <see cref="IRunObserver"/> member across both assemblies rather than just this one
/// member in the CLI assembly.</para>
///
/// <para><b>Nothing here re-derives the route.</b> Wave 2 already folded the observed model over the
/// requested one ONCE, at the attempt, and made the pair a recorded fact
/// (<see cref="Guardrails.Core.Journal.AttemptProvenance.Model"/> = best-known-actual;
/// <see cref="Guardrails.Core.Journal.AttemptProvenance.RequestedModel"/> = written ONLY when the two
/// differ, so its PRESENCE is the mismatch signal and there is no separate flag). These tests only prove the
/// pair survives the trip through a decorator — which is why each one is invoked twice: once in the
/// MISMATCH shape (<c>requestedModel</c> present) and once in the ORDINARY shape (<c>requestedModel</c>
/// null). A decorator that forwarded the model but hard-coded <c>null</c> for the request would satisfy a
/// one-shape test while destroying the only signal the field exists to carry.</para>
///
/// <para><b>Every call goes through <see cref="IRunObserver"/>, never the concrete type.</b> A decorator that
/// has not declared the member yet has no class-level method to bind to, so the concrete-type form would be a
/// COMPILE error rather than the behavioural red this file is for — and the interface form is the dispatch
/// path <see cref="Scheduler"/> actually uses, which is the thing being proven.</para>
/// </summary>
public sealed class AttemptModelForwardingTests
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

    /// <summary>A throwaway directory tree — both decorators write their artefacts under a real root.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-349-" + Guid.NewGuid().ToString("N"));

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
    /// The inner observer a decorator is supposed to be transparent to. It records the WHOLE payload, not a
    /// count: the failure mode this wave is about is a decorator that arrives with a mangled or hollowed
    /// argument list just as much as one that never arrives at all.
    /// </summary>
    private sealed class RecordingObserver : IRunObserver
    {
        public List<(TaskNode Task, int Attempt, string Model, string? RequestedModel)> Calls { get; } = [];

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }

        public void PlanHashMismatch(string previousPlanHash) { }

        public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
            Calls.Add((task, attempt, model, requestedModel));
    }

    /// <summary>
    /// Both shapes of the pair, unchanged, in order. Shared so the two decorators are held to the SAME bar —
    /// a forwarding rule that is right in one chain and lossy in the other is the #469 failure again.
    /// </summary>
    private static void AssertBothShapesReachedTheInner(RecordingObserver inner, TaskNode task)
    {
        Assert.Equal(2, inner.Calls.Count);

        // The MISMATCH shape: the runner reported a model the route did not ask for, so wave 2 recorded
        // BOTH. Losing `requestedModel` here loses the only evidence separating "the provider served
        // something else" from "my routing is misconfigured".
        Assert.Same(task, inner.Calls[0].Task);
        Assert.Equal(1, inner.Calls[0].Attempt);
        Assert.Equal("observed-model", inner.Calls[0].Model);
        Assert.Equal("requested-model", inner.Calls[0].RequestedModel);

        // The ORDINARY shape: the two agreed, so there is no requested model — and a decorator must forward
        // that null AS null, not substitute the model and turn every quiet attempt into a fake disagreement.
        Assert.Same(task, inner.Calls[1].Task);
        Assert.Equal(2, inner.Calls[1].Attempt);
        Assert.Equal("route-model", inner.Calls[1].Model);
        Assert.Null(inner.Calls[1].RequestedModel);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The two decorators that ship today.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LogSiteDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver()
    {
        using var tree = new TempTree();
        string logsRoot = tree.Dir("logs", "test-run");
        var inner = new RecordingObserver();
        TaskNode task = FlatTask("01-first");
        var decorator = new OnTheFlyLogSiteObserver(inner, logsRoot, "test-run", [task], liveUrlForTask: null);

        ((IRunObserver)decorator).AttemptModelResolved(task, 1, "observed-model", "requested-model");
        ((IRunObserver)decorator).AttemptModelResolved(task, 2, "route-model", null);

        AssertBothShapesReachedTheInner(inner, task);
    }

    [Fact]
    public void DiagramDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver()
    {
        using var tree = new TempTree();
        var inner = new RecordingObserver();
        TaskNode task = FlatTask("01-first");
        var plan = new PlanDefinition
        {
            PlanDirectory = "/fake/plan",
            Workspace = "/fake",
            Config = new RunConfig { Version = 1 },
            Tasks = [task]
        };
        var decorator = new OnTheFlyDiagramObserver(inner, tree.Dir("logs"), plan, journalForSeed: null);

        ((IRunObserver)decorator).AttemptModelResolved(task, 1, "observed-model", "requested-model");
        ((IRunObserver)decorator).AttemptModelResolved(task, 2, "route-model", null);

        AssertBothShapesReachedTheInner(inner, task);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The reflection sweep that used to live here — over every IRunObserver decorator in the CLI assembly,
    // checking each declares AttemptModelResolved — is superseded by
    // RunEvents.ObserverForwardingSweepTests.EveryTransparentDecorator_DeclaresEveryIRunObserverMember,
    // which sweeps EVERY member across BOTH assemblies (this file's sweep covered only Guardrails.Cli, so
    // the two Guardrails.Core projections were never checked).
    // ─────────────────────────────────────────────────────────────────────────────────────────
}
