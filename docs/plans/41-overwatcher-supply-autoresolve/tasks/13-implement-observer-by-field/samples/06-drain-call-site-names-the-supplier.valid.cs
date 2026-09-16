namespace Guardrails.Core.Execution;

/// <summary>
/// The CORRECT shape: the boundary drain's announcement names the same supplier as the supplied[]
/// record written immediately above it.
/// </summary>
internal sealed partial class Scheduler
{
    private void DrainSuppliedAtTaskBoundary(IntegrationHandle integ, WorktreeHandle handle)
    {
        SuppliedDrainResult drained = SuppliedDrain.Drain(
            integ.IntegrationWorktreePath, _plan.PlanDirectory, _journal.RunId, by: "operator");

        if (drained.CommitSha is not { } commitSha)
        {
            return;
        }

        if (_journal is Journal.RunJournal runJournal)
        {
            runJournal.RecordSupplied(new Journal.SuppliedRecord
            {
                At = DateTimeOffset.UtcNow,
                Commit = commitSha,
                Paths = drained.CommittedPaths,
                Bytes = drained.TotalBytes,
                By = "operator"
            });
        }

        // Design 40 §2 step 3: a run whose base changed underneath it must say so — and it must name the
        // same supplier the supplied[] record above does (design 41 §6).
        _observer.SuppliedResourcesCommitted(drained.CommittedPaths, commitSha, "operator");
    }
}
