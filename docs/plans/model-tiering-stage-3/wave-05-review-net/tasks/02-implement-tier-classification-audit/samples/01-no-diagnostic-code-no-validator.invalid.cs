// The INVALID half of the two-sided pair for guardrails/01-no-diagnostic-code-no-validator.ps1 (#468/#302).
//
// Byte-identical to the .valid.cs sibling except for ONE defect, and it is the likeliest one: the finding's
// message CITES A DIAGNOSTIC CODE. That reads as helpful cross-referencing and is the subtlest way the
// ruling gets broken, because it tells the reader the harness blocks on a model-quality opinion - which is
// the belief the ruling exists to prevent. It is also the case a comments-only strip must still catch,
// since the citation lives in a message string rather than in a comment.
//
// The probe beside this file mutates the VALID sample once per remaining ban, so all four clauses are
// proven live rather than only this one.

using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The deterministic half of the #229 model-appropriateness net. A plan carrying tags but no routing block
/// anywhere is already GR2049's business, so this audit stays silent there rather than emitting a second
/// opinion on the same config.
/// </summary>
public static class TierClassificationAudit
{
    /* The nearest neighbour is DiagnosticCodes.PinAndTierCoexist, which fires when a full pin and a tier
       COEXIST - the opposite end of the same axis from this finding, which fires when NEITHER is present. */
    public static bool IsTieringConfigured(PlanDefinition plan) =>
        plan.Config.Tiering is not null ||
        plan.Config.PromptRunners.Values.Any(runner => runner.Routing is not null);

    public static IReadOnlyList<TierClassificationFinding> Audit(PlanDefinition plan)
    {
        if (!IsTieringConfigured(plan))
        {
            return [];
        }

        List<TierClassificationFinding> findings = [];
        foreach (TaskNode task in plan.Tasks)
        {
            if (task.Action.Kind != ActionKind.Prompt) continue;
            if (task.Action.TierOrigin == TierOrigin.Task) continue;
            if (task.Action.Model is not null || task.Action.Runner is not null || task.Action.Effort is not null) continue;

            findings.Add(new TierClassificationFinding(
                task.Id,
                TierClassificationSubject.PromptTask,
                task.Action.Tier,
                task.Action.TierOrigin,
                $"'{task.Id}' is a prompt task nobody classified: no action.tier of its own and no " +
                "action.model / action.runner / action.effort pin. See GR2053 for the mirror case."));
        }

        return findings;
    }

    public static IReadOnlyList<string> ClassifiableSubjects(PlanDefinition plan) =>
        [.. plan.Tasks.Where(t => t.Action.Kind == ActionKind.Prompt).Select(t => t.Id)];
}

public enum TierClassificationSubject { PromptTask, PromptJudge }

public sealed record TierClassificationFinding(
    string SubjectId,
    TierClassificationSubject Kind,
    string? ResolvedTier,
    TierOrigin Origin,
    string Detail);
