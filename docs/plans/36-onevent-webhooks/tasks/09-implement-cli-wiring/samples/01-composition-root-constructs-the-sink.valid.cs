// SAMPLE — the VALID half of the two-sided pair for
// tasks/09-implement-cli-wiring/guardrails/01-composition-root-constructs-the-sink.ps1.
//
// A stand-in for src/Guardrails.Cli/Commands/RunCommand.cs, reduced to the region the guardrail scans.
// It CALLS WebhookEventSink.TryStart at the construction point design §3.3 pins: after the `diagramSeed`
// local is read, above the `OnTheFlyDiagramObserver? diagramObserver = null;` bracket, with `await using`
// so the sink's dispose unwinds AFTER the RunFinished bracket's finally and BEFORE the log server's
// transport teardown. Expected exit code: 0.
//
// This file is documentation, not compiled code: it lives under docs/ and is not in any csproj's glob.

using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

internal static class RunCommandSampleValid
{
    private static async Task<int> RunAsync(
        RunProbe probe,
        string runId,
        LogServer? logServer,
        string? onEventUrl,
        string? onEventAuth,
        bool onEventDetail,
        IConsoleIo io,
        CancellationToken cancellationToken)
    {
        string logsRoot = Path.Combine(probe.Plan.PlanDirectory, "logs", runId);
        Func<string, string?>? logUrlForTask = logServer is null ? null : logServer.UrlForTask;

        // Seed the live status diagram from the freshly-persisted journal (issue #219, SSOT §10.1).
        JournalDocument? diagramSeed = TryReadJournalForSeed(probe.Plan.PlanDirectory);

        // §3.3 step 1: the sink is constructed BEFORE the observer chain and disposed AFTER the
        // RunFinished bracket. `await using` compiles to an implicit try/finally whose scope encloses the
        // explicit bracket below, so the unwind order is: RunFinished finally → this dispose →
        // logServer.DisposeAsync(). That is the corrected plan 35 §9.3 rule — signal wind-down first,
        // drain second, tear the transport down last.
        await using var eventSink = WebhookEventSink.TryStart(   // null when no --on-event URL
            onEventUrl,
            onEventAuth,
            $"guardrails/{GuardrailsVersion.Current}",           // §4.3: the CLI injects the version
            io.Out.WriteLine,                                    // buffered; flushed after the Live region
            cancellationToken);                                  // §3.3 step 4: selects the budget only

        RunReport report;
        Scheduler scheduler;

        OnTheFlyDiagramObserver? diagramObserver = null;
        int? resolvedExitCode = null;
        string? faultKind = null;
        try
        {
            if (live)
            {
                await using var liveObserver = new LiveRunObserver(
                    probe.Plan.Tasks, logUrlForTask, probe.Plan.PlanDirectory, runId,
                    probe.Plan.Waves, allTasks);

                // Both parameters are UNDEFAULTED on BuildObserverChain (§10 row 6), so this call site is
                // forced by the compiler to say what it delivers.
                diagramObserver = BuildObserverChain(
                    liveObserver, logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed,
                    onRow: eventSink is null ? null : eventSink.Emit,
                    includeDetail: onEventDetail);

                (report, scheduler) = await ExecuteAsync(probe.Plan, diagramObserver, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                diagramObserver = BuildObserverChain(
                    new ConsoleRunObserver(io.Out), logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed,
                    onRow: eventSink is null ? null : eventSink.Emit,
                    includeDetail: onEventDetail);

                (report, scheduler) = await ExecuteAsync(probe.Plan, diagramObserver, cancellationToken)
                    .ConfigureAwait(false);
            }

            resolvedExitCode = Finish(report, scheduler);
            return resolvedExitCode.Value;
        }
        catch (Exception ex)
        {
            faultKind = ex.GetType().Name;
            throw;
        }
        finally
        {
            diagramObserver?.RunFinished(runId, resolvedExitCode, faultKind);
        }
    }
}
