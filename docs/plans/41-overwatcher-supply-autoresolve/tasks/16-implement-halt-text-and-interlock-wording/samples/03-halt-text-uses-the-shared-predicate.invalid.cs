// The ONE defect 03-halt-text-uses-the-shared-predicate.ps1 exists to catch: the shared predicate is
// CALLED, every assertion in SuppliedHaltTextTests passes - and the private ResourcePathToken regex is
// STILL THERE, a second, divergent spelling of "names a path" left in the file for the next reader to
// reach for. Design 41 §2.1's requirement is that there be ONE producer; no behavioural test can see the
// difference, because both spellings render identical text for every input the suite can supply.
// Identical to the .valid half apart from the surviving field and its use.
using System.CommandLine;
using System.Text.RegularExpressions;
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

    /// <summary>A workspace-relative path token: two or more '/'-joined segments.</summary>
    private static readonly Regex ResourcePathToken =
        new(@"[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+", RegexOptions.Compiled);

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

        // The leftover copy, still live: the shorthand line is built from the private regex's FIRST
        // match while the three commands above use the shared predicate. Every rendered assertion in
        // SuppliedHaltTextTests still passes, because the first match and the first shared token agree
        // on every input the suite supplies.
        string joined = string.Join(" ", resourcePaths);
        string shorthandPath = ResourcePathToken.Match(needsHuman.NeedsHumanQuestion ?? string.Empty).Value;

        return
        [
            "  This is a missing resource, not a scope problem: plan-folder edits reach a running plan; "
                + "code artifacts do not — re-scoping the task cannot conjure it. Supply it, then reset and "
                + "resume this task:",
            $"    guardrails supply {planDirectory} {joined}",
            $"    guardrails reset {planDirectory} {needsHuman.TaskId}",
            $"    guardrails run {planDirectory}",
            "  Once staged, the opt-in shorthand runs the same three steps in one call: "
                + $"guardrails supply --resume {planDirectory} {shorthandPath}"
        ];
    }

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
