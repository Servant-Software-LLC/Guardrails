namespace Guardrails.Core.Execution;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the announcement names "overwatcher" while the supplied[] record
/// written six lines above it says "operator". It compiles, every test in this pair passes (none of them
/// drives the Scheduler), and every consumer of events.jsonl is then told that the harness supplied a
/// file the OPERATOR staged by hand — the false provenance design 41 §5 calls out, pointing the opposite
/// way from #712's own defect. The call is otherwise correctly migrated to the three-argument form, so
/// the valid/invalid diff is exactly the supplier's name.
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

        _observer.SuppliedResourcesCommitted(drained.CommittedPaths, commitSha, "overwatcher");
    }
}
