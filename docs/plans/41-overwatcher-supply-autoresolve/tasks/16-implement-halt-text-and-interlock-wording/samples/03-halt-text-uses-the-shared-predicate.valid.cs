// A COMPLETE, representative CORRECT artifact for 03-halt-text-uses-the-shared-predicate.ps1
// (#468/#302): the RunCommand surfaces after task 16, with the shared MissingResourceSignal as the ONE
// producer of the path-token match, no private regex left behind, and both interlock wordings
// generalised to a machine decision. Kept complete rather than a fragment - an incomplete valid sample
// fails for a DIFFERENT reason and masks the real one.
//
// This header deliberately quotes none of the strings the guardrail bans (taxonomy 13).
using System.CommandLine;
using Guardrails.Core.Execution;

namespace Guardrails.Cli.Commands;

public static class RunCommand
{
    public static Command Create()
    {
        var mergeOnSuccessOption = new Option<bool>("--merge-on-success")
        {
            Description = "On a wholly-green run, merge the plan branch into your original branch at run end (SSOT §5.3). Forces mergeOnSuccess ON regardless of guardrails.json, AND is the operator override that delivers work a machine decision would otherwise hold back on the plan branch (#361/#597). Delivery is the DEFAULT, so this flag matters only when such a decision was recorded."
        };

        var command = new Command("run", "Run a plan folder's task DAG to green.");
        command.Add(mergeOnSuccessOption);
        return command;
    }

    /// <summary>
    /// The missing-resource carve-out of a blocked-work halt (design 40 §3/§5, design 41 §2.1). The
    /// path-token match comes from the SHARED predicate, so the halt text and the overwatcher's
    /// missing-resource consult can never disagree about what counts as naming a path.
    /// </summary>
    private static IReadOnlyList<string> MissingResourceHaltLines(TaskResult needsHuman, string logsRoot)
    {
        if (NeedsHumanKinds.Parse(needsHuman.NeedsHumanKind) != NeedsHumanKinds.BlockedWork)
        {
            return [];
        }

        IReadOnlyList<string> resourcePaths = MissingResourceSignal.Paths(needsHuman.NeedsHumanQuestion);
        if (resourcePaths.Count == 0)
        {
            return [];
        }

        string? planDirectory = Path.GetDirectoryName(Path.GetDirectoryName(logsRoot));
        if (string.IsNullOrEmpty(planDirectory))
        {
            return [];
        }

        string joined = string.Join(" ", resourcePaths);

        return
        [
            "  This is a missing resource, not a scope problem: plan-folder edits reach a running plan; "
                + "code artifacts do not — re-scoping the task cannot conjure it. Supply it, then reset and "
                + "resume this task:",
            $"    guardrails supply {planDirectory} {joined}",
            $"    guardrails reset {planDirectory} {needsHuman.TaskId}",
            $"    guardrails run {planDirectory}",
            "  Once staged, the opt-in shorthand runs the same three steps in one call: "
                + $"guardrails supply --resume {planDirectory} {joined}"
        ];
    }

    /// <summary>
    /// The loud end-of-run notice for a wholly-green run whose work was not delivered (issue #340/#597).
    /// The suppressing decision is named by INTERPOLATION, so the banner needs no knowledge of which
    /// tokens hold delivery - design 41 §6 adds a third one without touching this code.
    /// </summary>
    public static void RenderUndeliveredWorkWarning(RunReport report, string planBranch, TextWriter output)
    {
        if (report.DeliverySuppressingDecision is { } suppressing)
        {
            output.WriteLine(
                "mergeOnSuccess is ON. Delivery was held back by the autonomous-mode interlock (#361):");
            output.WriteLine(
                $"this run recorded '{suppressing.Decision}' at '{suppressing.Subject}' ({suppressing.Boundary} boundary),");
            output.WriteLine(
                "so machine-decided work is never auto-delivered. The verified work is sitting on branch");
            output.WriteLine($"'{planBranch}', NOT on your checkout.");
            output.WriteLine(
                "JUDGE THE DECISION FIRST — run.json → decisions[]. A machine decision that a later attempt");
            output.WriteLine(
                "superseded is stale; one that shaped the result you are looking at is not. Then either:");
        }
    }
}
