// The ONE defect 03-appends-the-shared-reader.ps1 exists to catch: the disclosure is RE-IMPLEMENTED over
// the journal's supplied[] section instead of delegating to the shipped reader. It produces the very
// substrings SuppliedTerminalGateHaltTests asserts for a supply-only fixture, so the behavioural tests
// cannot tell it apart — and it silently drops refreshed[] entirely, which is the half the DECIDED answer
// to d41-terminal-gate-names-supply explicitly includes and the half UnauthoredContentNote's own doc
// comment says a one-section reader is the defect it exists to prevent.
// Identical to the .valid half apart from the hand-rolled rendering below.
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
                return document with { PlanGuardrails = section };
            }

            string headline = "Terminal gate FAILED on the merged HEAD: "
                              + string.Join(", ", failedChecks.Select(f => f.Name));

            // The defect: a private rendering over ONE of the two sections. It matches the fixture's
            // expected substrings for a supply, so the tests pass — and a refresh is never disclosed.
            var supplied = document.Supplied ?? [];
            if (supplied.Count > 0)
            {
                headline += " — unauthored content: " + string.Join(
                    "; ",
                    supplied.Select(s => $"supplied by {s.By} at {(s.Commit.Length <= 10 ? s.Commit : s.Commit[..10])}"));

                if (heartbeatOut is not null)
                {
                    foreach (var record in supplied)
                    {
                        heartbeatOut.WriteLine(
                            $"supplied by {record.By} at {record.Commit} ({record.Bytes} bytes: {string.Join(", ", record.Paths)})");
                    }
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
