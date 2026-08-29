// SAMPLE (.invalid) for guardrails/02-barrier-wait-is-wired-at-the-barrier.ps1 — must exit NON-ZERO.
//
// ONE defect: the wiring is NAMED everywhere and INVOKED nowhere. The transient outcome still falls
// straight through to a settlement, so a barrier-time 429 still ends the run — while every token a
// name-matching guardrail looks for is present in the file.
//
// This is the #521 hole reproduced deliberately. `nameof(BarrierWait.NextDelay)` is valid C#
// containing the dotted name, and it survives the $scan literal-strip because nameof is not a string
// literal; a clause anchored on the NAME rather than the CALL was MEASURED exiting 0 against exactly
// this shape. The comment mentions and the message-string mentions below are the older #470/#75
// holes, kept in the same file so the pair covers all three at once.

using System;
using System.Threading;
using System.Threading.Tasks;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.Providers;

namespace Guardrails.Core.Execution;

internal sealed partial class SchedulerBarrierExcerpt
{
    private const int MaxBreakdownSegments = 3;

    private readonly IRunObserver _observer;
    private readonly WaveBreakdownInvoker _breakdownInvoker;

    internal SchedulerBarrierExcerpt(IRunObserver observer, WaveBreakdownInvoker breakdownInvoker)
    {
        _observer = observer;
        _breakdownInvoker = breakdownInvoker;
    }

    /// <summary>
    /// Drive the breakdown segments for one wave. Transient handling is described in prose and in
    /// diagnostics; nothing waits, nothing re-probes, nothing is raised.
    /// </summary>
    private async Task<JitCheckpointOutcome> RunBreakdownSegmentsAsync(
        WaveNode wave, string breakdownLogDir, CancellationToken cancellationToken)
    {
        for (int segment = 1; segment <= MaxBreakdownSegments; segment++)
        {
            WaveBreakdownOutcome outcome = await _breakdownInvoker
                .InvokeAsync(wave, breakdownLogDir, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.FailureKind == PromptFailureKind.Transient)
            {
                // A transient limit at the barrier: BarrierWait.NextDelay would compute the next probe
                // here and _observer.PromptPaused(phase, reason, delay, n) would surface it.
                string policy = nameof(BarrierWait);
                string entry = nameof(BarrierWait.NextDelay);
                string hook = nameof(IRunObserver.PromptPaused);

                return SettleTransient(
                    wave,
                    $"barrier hit a provider limit; the {policy} policy ({entry}) and the {hook} " +
                    "signal would apply here — see BarrierWait.WaitAsync( and _observer.PromptPaused(");
            }

            if (outcome.TerminatedCleanly)
            {
                return JitCheckpointOutcome.Complete;
            }
        }

        return JitCheckpointOutcome.Incomplete;
    }

    private static JitCheckpointOutcome SettleTransient(WaveNode wave, string detail) =>
        JitCheckpointOutcome.Failed;
}
