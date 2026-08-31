// Sample: a CORRECT Scheduler.cs fragment for 03-watch-is-wired.ps1 -> that file's Scheduler clauses
// must all pass. Stage into a scratch tree at src/Guardrails.Core/Execution/Scheduler.cs alongside the
// DecisionEntry.cs and RunReport.cs samples in this folder.
//
// Note the trap it carries: a COMMENT that names Poll and Rebaseline while explaining the design. The
// clauses read comment-stripped source, so the comment neither satisfies nor trips them.
namespace Guardrails.Core.Execution;

internal sealed partial class Scheduler
{
    private readonly LivePlanEditWatch _planEditWatch;

    // The watch is polled on the scheduler's own thread at two boundaries that already exist -
    // dispatch and settle. No new thread, no lock, no daemon, and deliberately no FileSystemWatcher:
    // it would fire on the harness's own writes and needs a debounce policy. Rebaseline() is called
    // plan-wide after each harness writer.
    private void Wire(PlanDefinition plan)
    {
        _planEditWatch = new LivePlanEditWatch(plan);
    }

    private void OnDispatch() => ReportEdits(_planEditWatch.Poll());

    private void OnSettle() => ReportEdits(_planEditWatch.Poll());

    private void AfterWaveBreakdown() => _planEditWatch.Rebaseline();

    private void AfterInventoryRevert() => _planEditWatch.Rebaseline();

    private void AfterSweepIncompleteTrailingTaskFolders() => _planEditWatch.Rebaseline();

    private void AfterQuarantineWholeTasksFolder() => _planEditWatch.Rebaseline();

    private void AfterDriftResolved() => _planEditWatch.Rebaseline();

    private void ReportEdits(IReadOnlyList<PlanEdit> edits) { }
}
