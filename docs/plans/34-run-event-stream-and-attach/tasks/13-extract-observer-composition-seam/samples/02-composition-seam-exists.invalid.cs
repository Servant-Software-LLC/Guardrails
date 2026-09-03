// INVALID sample: the exact defect this guardrail exists to catch — the seam was declared and the
// live-UI branch was migrated to it, but the --no-ui branch was LEFT INLINE. A later wiring task
// then wires one branch and silently leaves the other unwired, which is the half-fix that ships green.
namespace Guardrails.Cli.Commands;

internal sealed partial class RunCommand
{
    public static IRunObserver BuildObserverChain(IRunObserver inner, string logsRoot, string runId, Plan plan, string? diagramSeed)
    {
        var siteObserver = new OnTheFlyLogSiteObserver(inner, logsRoot, runId, plan.Tasks, null, plan.Waves);
        return new OnTheFlyDiagramObserver(siteObserver, logsRoot, plan, diagramSeed);
    }

    private async Task RunLiveAsync(Plan plan, string logsRoot, string runId, string? seed)
    {
        await using var liveObserver = new LiveRunObserver();
        IRunObserver observer = BuildObserverChain(liveObserver, logsRoot, runId, plan, seed);
        await Execute(observer);
    }

    private async Task RunHeadlessAsync(Plan plan, string logsRoot, string runId, string? seed)
    {
        var siteObserver = new OnTheFlyLogSiteObserver(new ConsoleRunObserver(Out), logsRoot, runId, plan.Tasks, null, plan.Waves);
        IRunObserver observer = new OnTheFlyDiagramObserver(siteObserver, logsRoot, plan, seed);
        await Execute(observer);
    }
}
