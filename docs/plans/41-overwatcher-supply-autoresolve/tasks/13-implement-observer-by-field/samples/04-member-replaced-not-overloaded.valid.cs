namespace Guardrails.Core.Execution;

/// <summary>Observes a run. Optional members carry empty default bodies.</summary>
public interface IRunObserver
{
    void TaskStarting(TaskNode task);

    /// <summary>
    /// The harness committed one or more supplied files onto the run's own base (design 40 §2 step 3; design 41
    /// §5). <paramref name="paths"/> are the workspace-relative destinations the files now occupy;
    /// <paramref name="commit"/> is the SHA of the commit that carries them, whose trailers are
    /// <c>Supplied-By: &lt;by&gt;</c> and <c>Guardrails-Run: &lt;runId&gt;</c> (design 40 §4); and
    /// <paramref name="by"/> is the supplier that commit and its <c>supplied[]</c> record name
    /// (<c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c>). Raised once per such commit, after
    /// its <c>supplied[]</c> record is written: by <c>Scheduler.DrainSuppliedAtTaskBoundary</c> at a task
    /// boundary, and by the Scheduler's missing-resource auto-resolve (design 41). The run-start drain in
    /// <c>RunCommand</c> does not raise it today.
    ///
    /// <para><b>Why this matters more than it looks.</b> A run whose base changed underneath it must SAY
    /// so — a silent base change is indistinguishable from a harness bug when a later task behaves
    /// unexpectedly.</para>
    ///
    /// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
    /// still forward it EXPLICITLY: an unforwarded call resolves to this empty body and the disclosure is
    /// swallowed silently, in every mode. <see cref="NullObserver"/> is the one legitimate exception.</para>
    /// </summary>
    void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) { }

    /// <summary>An observer that does nothing.</summary>
    static IRunObserver Null { get; } = new NullObserver();

    private sealed class NullObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
    }
}
