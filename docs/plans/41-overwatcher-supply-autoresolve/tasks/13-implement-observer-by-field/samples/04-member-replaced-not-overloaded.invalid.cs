namespace Guardrails.Core.Execution;

/// <summary>Observes a run. Optional members carry empty default bodies.</summary>
public interface IRunObserver
{
    void TaskStarting(TaskNode task);

    /// <summary>
    /// THE ONE DEFECT THIS SAMPLE CARRIES: the three-argument member was ADDED beside the two-argument
    /// one instead of REPLACING it. Everything else is correct — the doc comment is design 41 §8's, the
    /// new member is declared, both stale claims are gone — and the solution still compiles, which is
    /// precisely the problem: any decorator that kept the two-argument method satisfies the compiler and
    /// a name-only forwarding guard, while the Scheduler's three-argument call lands on the empty
    /// default body below and the announcement disappears in every mode.
    /// </summary>
    void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit) { }

    /// <summary>
    /// The harness committed one or more supplied files onto the run's own base (design 40 §2 step 3; design 41
    /// §5). <paramref name="paths"/> are the workspace-relative destinations the files now occupy;
    /// <paramref name="commit"/> is the SHA of the commit that carries them; and
    /// <paramref name="by"/> is the supplier that commit and its <c>supplied[]</c> record name
    /// (<c>operator</c>, <c>overwatcher</c>, or <c>task:&lt;folder&gt;</c>).
    /// </summary>
    void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) { }

    /// <summary>An observer that does nothing.</summary>
    static IRunObserver Null { get; } = new NullObserver();

    private sealed class NullObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
    }
}
