// SAMPLE — the INVALID half of the two-sided pair for
// tasks/09-implement-cli-wiring/guardrails/01-composition-root-constructs-the-sink.ps1.
//
// THE ONE DEFECT THIS SAMPLE CARRIES: the composition root never CONSTRUCTS the sink. WebhookEventSink
// is fully built and fully unit-tested by tasks 06/07 and every one of those tests stays green over this
// file, so --on-event delivers nothing while the suite reports success — the #382 defect design §10
// calls "the row that matters most". Expected exit code: 1.
//
// It carries the three near-misses the guardrail's regex is shaped to reject, all in the open:
//
//   1. A COMMENT naming WebhookEventSink.TryStart( — with the parenthesis, so only the comment strip
//      keeps it out.
//   2. A STRING LITERAL containing "WebhookEventSink.TryStart(" — only the string strip keeps it out.
//   3. A bare nameof(WebhookEventSink.TryStart) — valid C#, NOT a string literal, so it survives both
//      strips. This is the #521 operator measured on 2026-08-28: a clause that stopped at the dotted NAME
//      was satisfied by two dead nameof expressions with ZERO invocations and exited 0. The trailing
//      `\s*\(` in the guardrail is what kills it — nameof puts a `)` after TryStart, never a `(`.
//
// This file is documentation, not compiled code: it lives under docs/ and is not in any csproj's glob.

using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Cli.Commands;

internal static class RunCommandSampleInvalid
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

        JournalDocument? diagramSeed = TryReadJournalForSeed(probe.Plan.PlanDirectory);

        // TODO(webhooks): call WebhookEventSink.TryStart(onEventUrl, onEventAuth, ua, notice, token) here,
        // per §3.3. Near-miss 1: a comment carrying the exact call text, including the parenthesis.
        if (!string.IsNullOrWhiteSpace(onEventUrl))
        {
            // Near-miss 2: a string literal carrying the exact call text.
            io.Out.WriteLine("--on-event is not wired yet; see WebhookEventSink.TryStart( in the design");

            // Near-miss 3: the #521 operator in the open. A bare nameof is not a string literal, so it
            // survives a comment/string strip and satisfies any clause that stops at the dotted NAME —
            // while the factory is never invoked and no sink is ever constructed.
            string entryPoint = nameof(WebhookEventSink.TryStart);
            _ = entryPoint;
        }

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

                // The parameters exist and are threaded — and they carry nothing, because there is no
                // sink to carry. Everything downstream of here is correct and inert.
                diagramObserver = BuildObserverChain(
                    liveObserver, logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed,
                    onRow: null,
                    includeDetail: onEventDetail);

                (report, scheduler) = await ExecuteAsync(probe.Plan, diagramObserver, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                diagramObserver = BuildObserverChain(
                    new ConsoleRunObserver(io.Out), logsRoot, runId, probe.Plan, logUrlForTask, diagramSeed,
                    onRow: null,
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
