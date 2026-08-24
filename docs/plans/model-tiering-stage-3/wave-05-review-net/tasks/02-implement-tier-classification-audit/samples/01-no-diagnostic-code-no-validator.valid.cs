// The VALID half of the two-sided pair for guardrails/01-no-diagnostic-code-no-validator.ps1 (#468/#302).
// A representative CORRECT audit: findings only, no diagnostic code, no validator.
//
// It deliberately carries every banned token in COMMENT positions, in all three comment forms, because the
// valid half's job is to prove the comment strip works. Without that, the ban would false-RED a correct
// implementation whose author explained the ruling - and a false red dead-ends every attempt at
// needsHuman. This is the half authors skip and the half that pays.

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

            // No code is cited here, on purpose: naming one would tell the reader the harness blocks on
            // this. Compare a plain reading of new PlanValidator(config) - not something this file does.
            findings.Add(new TierClassificationFinding(
                task.Id,
                TierClassificationSubject.PromptTask,
                task.Action.Tier,
                task.Action.TierOrigin,
                $"'{task.Id}' is a prompt task nobody classified: no action.tier of its own and no " +
                "action.model / action.runner / action.effort pin. Add one, or record why it needs none."));
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
