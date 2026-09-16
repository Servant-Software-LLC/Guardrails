// A COMPLETE, representative CORRECT artifact for 03-appends-the-shared-reader.ps1 (#468/#302): the
// terminal plan-guardrail phase after task 18, appending the SHIPPED UnauthoredContentNote's headline
// suffix and detail lines - the same reader Scheduler.BuildGateHalt already calls at a wave gate - and
// never reaching into the journal's two sections itself. Kept complete rather than a fragment: an
// incomplete valid sample fails for a DIFFERENT reason and masks the real one.
//
// This header deliberately quotes none of the property accesses the guardrail bans (taxonomy 13).
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Cli;

public static class PlanGuardrailPhase
{
    public static async Task<bool> EvaluateAsync(
        PlanDefinition plan,
        ProcessRunner processRunner,
        TextWriter? heartbeatOut,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (plan.PlanGuardrails.Count == 0)
        {
            return true;
        }

        string currentHash = PlanHash.Compute(plan);
        string evalWorkspace = PlanPhaseWorkspace.Resolve(plan, cancellationToken);

        var interpreterMap = InterpreterMap.CreateDefault(plan.Config);
        var reVerifier = new GuardrailReVerifier(processRunner, interpreterMap);

        ReVerifyResult result = await reVerifier
            .ReVerifyAsync(evalWorkspace, plan.PlanGuardrails, new ReVerifyOptions(), cancellationToken)
            .ConfigureAwait(false);

        List<FailedGuardrail> failedChecks = result.FailedGuardrails
            .Select(f => new FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList();

        var section = new PlanGuardrailsSection
        {
            Status = result.Passed ? PlanPhaseStatus.Passed : PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = currentHash,
            FailedChecks = failedChecks,
            EvaluatedAt = DateTimeOffset.UtcNow
        };

        PlanPhaseJournalWriter.Update(plan.PlanDirectory, document =>
        {
            if (result.Passed)
            {
                // A passing gate writes no halt at all — the disclosure rides on the halt and must never
                // become an unconditional announcement on a green run.
                return document with { PlanGuardrails = section };
            }

            string headline = "Terminal gate FAILED on the merged HEAD: "
                              + string.Join(", ", failedChecks.Select(f => f.Name));

            // Design 41 §6 "Later gate halts": the SAME reader the wave entry/exit halts use. It returns
            // null when there is no unauthored content, so a run that supplied and refreshed nothing keeps
            // a byte-identical headline.
            if (UnauthoredContentNote.HeadlineSuffix(document) is { } suffix)
            {
                headline += suffix;
            }

            // RunHalt carries no Detail field, so the per-record lines go to the phase's own writer —
            // in production the run's console. Empty means write nothing, not a blank line.
            if (heartbeatOut is not null)
            {
                IReadOnlyList<string> detailLines = UnauthoredContentNote.DetailLines(document);
                foreach (string line in detailLines)
                {
                    heartbeatOut.WriteLine(line);
                }
            }

            var halt = new RunHalt
            {
                Kind = RunHaltKind.PlanGuardrailFailed,
                HaltedAt = DateTimeOffset.UtcNow,
                Headline = headline,
                FailedChecks = failedChecks
            };

            return document with { PlanGuardrails = section, Halt = halt };
        });

        return result.Passed;
    }
}
