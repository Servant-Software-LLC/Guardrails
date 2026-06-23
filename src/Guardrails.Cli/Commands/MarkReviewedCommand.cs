using System.CommandLine;
using Guardrails.Core.Review;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails mark-reviewed [folder]</c> — record that <c>/guardrails-review</c> ran over the
/// CURRENT plan by writing the <c>state/guardrails-review.json</c> marker (SSOT §13, issues
/// #79/#131). The WRITER half of the review nudge: with a fresh marker, <c>validate</c>/<c>run</c>
/// stop emitting the GR2025 "not reviewed" warning until the plan changes (the marker is
/// plan-hash-keyed, so an edited plan reads as un-reviewed again). The <c>/guardrails-review</c> skill
/// invokes this at the end of a review — the skill can't compute the <c>PlanHash</c> itself. The
/// marker is <b>committed as part of the reviewed plan</b>: it is an attestation about the committed
/// plan content, planHash-keyed so it self-invalidates on any edit (the nudge returns), and is NOT
/// wiped by <c>--fresh</c>.
/// </summary>
public static class MarkReviewedCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command(
            "mark-reviewed",
            "Record that /guardrails-review ran over the current plan (writes the committed review marker).");
        command.Add(folderArgument);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            return Run(folder, io);
        });

        return command;
    }

    private static int Run(string folder, IConsoleIo io)
    {
        // A review marker vouches for a plan that LOADS and is structurally valid; a plan with
        // parse/schema errors cannot be honestly marked reviewed (you'd be vouching for something that
        // won't run). Print the diagnostics and refuse. A missing/stale review marker is a WARNING, not
        // an error, so it never makes HasErrors true — an otherwise-valid plan marks cleanly.
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, io.Out);
            io.Out.WriteLine("\nFAILED: cannot mark an invalid plan as reviewed — fix the errors above first.");
            return ExitCodes.HarnessError;
        }

        ReviewMarker.Write(probe.Plan, DateTimeOffset.UtcNow);
        ReviewEvaluation eval = ReviewMarker.Evaluate(probe.Plan);
        io.Out.WriteLine(
            $"OK: marked reviewed (planHash {ShortHash(eval.CurrentHash)}). " +
            "The /guardrails-review nudge stays clear until the plan changes.");
        return ExitCodes.Success;
    }

    /// <summary>A short, display-friendly form of a <c>sha256:</c> plan hash (first 12 hex chars).</summary>
    private static string ShortHash(string hash)
    {
        string hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash["sha256:".Length..] : hash;
        return "sha256:" + (hex.Length <= 12 ? hex : hex[..12]);
    }
}
