// SAMPLE (.valid) for guardrails/02-barrier-wait-is-wired-at-the-barrier.ps1 — must exit 0.
//
// A complete, compilable-shaped excerpt of the wave-barrier region this task edits: a barrier segment
// loop that CONSTRUCTS the BarrierWait policy, RAISES the existing PromptPaused hook with the policy's
// own reason, waits, and re-probes — rather than settling the wave on a transient provider limit.
// Kept complete (usings, namespace, type, real constructs) per dotnet.md §22: an incomplete valid
// sample fails for a different reason and masks the one the pair exists to expose.
//
// This file is a REGEX SUBJECT, not a contract. It is never compiled and the member signatures below
// (BarrierWait's constructor, NextDelay/Reason/WaitAsync, InvokeAsync's parameter list) are
// ILLUSTRATIVE — the real BarrierWait surface is whatever BarrierWaitTests pins, and the guardrail
// only requires that BarrierWait is CONSTRUCTED-or-CALLED and PromptPaused is INVOKED. Do not treat
// the shapes here as the API to implement.

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
    /// Drive the breakdown segments for one wave. A transient provider limit at this barrier PAUSES
    /// and re-probes (issue #511) instead of ending the run — the same posture TaskExecutor already
    /// takes inside a task (issue #115), reusing the same observer signal.
    /// </summary>
    private async Task<JitCheckpointOutcome> RunBreakdownSegmentsAsync(
        WaveNode wave, string breakdownLogDir, CancellationToken cancellationToken)
    {
        // Bounded per BARRIER, not per segment: a rate-limit wait is not an authoring attempt, so it
        // must not consume the wave's MaxBreakdownSegments budget.
        var barrierWait = new BarrierWait(BarrierWait.DefaultProbeInterval, BarrierWait.DefaultCeiling);

        for (int segment = 1; segment <= MaxBreakdownSegments; segment++)
        {
            WaveBreakdownOutcome outcome = await _breakdownInvoker
                .InvokeAsync(wave, breakdownLogDir, cancellationToken)
                .ConfigureAwait(false);

            while (outcome.FailureKind == PromptFailureKind.Transient)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return JitCheckpointOutcome.Cancelled;
                }

                if (!barrierWait.CanWaitAgain())
                {
                    // The named bound: the limit never cleared inside the barrier's ceiling, so the
                    // wave settles with the RATE-LIMIT cause rather than "did not complete cleanly".
                    return SettleRateLimited(wave, barrierWait);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                TimeSpan delay = barrierWait.NextDelay(now, resetInstant: null);

                // REUSE of the shipped hook (IRunObserver.cs:84). The synthetic TaskNode stands for the
                // wave's breakdown PHASE; its id is the phase row key the live table already indexes —
                // "<waveDir>/(breakdown)". The Cli-side owner of that spelling is
                // WavePhaseLiveRow.KeyFor(waveDir, WavePhaseLiveRow.BreakdownPhase); Core cannot
                // reference Cli, so the convention is reproduced here and cross-referenced there.
                var phase = new TaskNode { Id = $"{wave.Dir}/(breakdown)" };
                _observer.PromptPaused(phase, barrierWait.Reason(now, resetInstant: null), delay, barrierWait.ProbeCount + 1);

                await barrierWait.WaitAsync(now, resetInstant: null, cancellationToken).ConfigureAwait(false);

                outcome = await _breakdownInvoker
                    .InvokeAsync(wave, breakdownLogDir, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (outcome.TerminatedCleanly)
            {
                return JitCheckpointOutcome.Complete;
            }
        }

        return JitCheckpointOutcome.Incomplete;
    }

    private static JitCheckpointOutcome SettleRateLimited(WaveNode wave, BarrierWait barrierWait) =>
        JitCheckpointOutcome.RateLimited;
}
