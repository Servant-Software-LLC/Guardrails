using System.CommandLine;
using Guardrails.Core.Review;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails mark-reviewed [folder]</c> — record that <c>/guardrails-review</c> ran over the
/// CURRENT plan by writing the <c>state/guardrails-review.json</c> marker (SSOT §13, issues
/// #79/#131/#260). The WRITER half of the review nudge: with a fresh marker, <c>validate</c>/<c>run</c>
/// stop emitting the GR2025 "not reviewed" warning until the plan changes (the marker is keyed on the
/// <c>PlanDefinitionHash</c> — the plan's full behavioral definition, guardrail/preflight/action bodies
/// included — so any edit to that content reads as un-reviewed again). The <c>/guardrails-review</c>
/// skill invokes this at the end of a review — the skill can't compute the hash itself. The marker is
/// <b>committed as part of the reviewed plan</b>: it is an attestation about the committed plan content,
/// self-invalidating on any edit the hash covers (the nudge returns), and is NOT wiped by <c>--fresh</c>.
/// </summary>
public static class MarkReviewedCommand
{
    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        // ── issue #366 evidence-hygiene options (design 16-review-attestation-provenance §4/§5) ──────────
        // Present ONLY as minimal stubs for the TDD-red F2 tests: the review-artifact / evidence-class
        // stamp path they drive is not implemented yet (see the SetAction stub below). The plain BARE
        // stamp (`mark-reviewed <folder>` with none of these) keeps its shipped behaviour.
        var evidenceOption = new Option<string?>("--evidence")
        {
            Description = "Path to the /guardrails-review report artifact under <plan>/state/reviews/ that this stamp attests. On the F2 stamp-time checks passing (report embeds the current plan hash; path resolves under state/reviews/) the marker records source: review-artifact + evidence; on failure it downgrades to source: bare (SSOT §13, issue #366)."
        };

        var sourceOption = new Option<string?>("--source")
        {
            Description = "Explicit evidence class for the stamp: 'machine' for an automated flow (auto-breakdown / autonomous mode) so a machine stamp is honestly labelled and never masquerades as human review (issue #366)."
        };

        var reviewerOption = new Option<string?>("--reviewer")
        {
            Description = "Self-reported, NON-authoritative reviewer id recorded as attestation.actor (audit richness only — the CLI cannot authenticate an actor; issue #366)."
        };

        var command = new Command(
            "mark-reviewed",
            "Record that /guardrails-review ran over the current plan (writes the committed review marker).");
        command.Add(folderArgument);
        command.Add(evidenceOption);
        command.Add(sourceOption);
        command.Add(reviewerOption);

        command.SetAction(parseResult =>
        {
            string folder = FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out);
            string? evidence = parseResult.GetValue(evidenceOption);
            string? source = parseResult.GetValue(sourceOption);
            string? reviewer = parseResult.GetValue(reviewerOption);

            // #366 F2 STUB — the evidence-class stamp path (review-artifact / machine / self-reported
            // reviewer) is deliberately not implemented yet, so the TDD-red F2 tests fail against it.
            // Any of --evidence/--source/--reviewer selects that path. The shipped BARE stamp (none of
            // them) falls through to Run(...) unchanged and keeps clearing GR2025 exactly as today.
            if (evidence is not null || source is not null || reviewer is not null)
            {
                throw new NotImplementedException(
                    "mark-reviewed evidence-class stamping (F2, issue #366) is not implemented yet — " +
                    "--evidence/--source/--reviewer are stubbed pending the harness change.");
            }

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
            $"OK: marked reviewed (planDefinitionHash {ShortHash(eval.CurrentHash)} — the plan's full " +
            "behavioral definition, incl. guardrail/preflight/action bodies). " +
            "The /guardrails-review nudge stays clear until that content changes.");
        return ExitCodes.Success;
    }

    /// <summary>A short, display-friendly form of a <c>sha256:</c> plan hash (first 12 hex chars).</summary>
    private static string ShortHash(string hash)
    {
        string hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash["sha256:".Length..] : hash;
        return "sha256:" + (hex.Length <= 12 ? hex : hex[..12]);
    }
}
