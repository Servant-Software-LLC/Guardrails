// VALID sample: the seam is extracted. One declaration, BOTH branches call it, and exactly ONE
// inline `new OnTheFlyDiagramObserver(` construction site — inside the extracted method.
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
        IRunObserver observer = BuildObserverChain(new ConsoleRunObserver(Out), logsRoot, runId, plan, seed);
        await Execute(observer);
    }
}
