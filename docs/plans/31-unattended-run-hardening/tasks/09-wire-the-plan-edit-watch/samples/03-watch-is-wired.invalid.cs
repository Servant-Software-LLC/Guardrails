// Sample: the ONE defect 03-watch-is-wired.ps1 exists to catch -> must exit NON-ZERO.
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
        // TODO wire the watch
    }

    private void OnDispatch() => ReportEdits(_planEditWatch.Poll());

    private void OnSettle() { }

    x

    x

    x

    x

    x

    private void ReportEdits(IReadOnlyList<PlanEdit> edits) { }
}
