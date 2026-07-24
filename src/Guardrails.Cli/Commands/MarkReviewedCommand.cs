using System.CommandLine;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
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
///
/// <para>Evidence hygiene (issue #366, design <c>docs/plans/16-review-attestation-provenance.md</c>
/// §4/§5): the stamp records a deterministic <c>attestation.source</c> evidence class —
/// <c>review-artifact</c> when <c>--evidence</c> points at a <c>/guardrails-review</c> report for
/// <em>this</em> plan that passes the F2 stamp-time checks (the report embeds the current
/// <see cref="PlanDefinitionHash"/>, and <c>reportPath</c> resolves under <c>&lt;plan&gt;/state/reviews/</c>);
/// <c>machine</c> for an honestly-labelled automated flow (<c>--source machine</c>); otherwise <c>bare</c>.
/// It <b>never refuses</b> a stamp (invariant 5): a bare invocation, or a <c>review-artifact</c> attempt
/// that fails F2, both still clear GR2025 — they just record <c>source: bare</c> (a downgrade, never a
/// fabricated class). The class gates nothing; it is read for audit (§6).</para>
/// </summary>
public static class MarkReviewedCommand
{
    private const string SourceReviewArtifact = "review-artifact";
    private const string SourceBare = "bare";
    private const string SourceMachine = "machine";

    /// <summary>The line a <c>/guardrails-review</c> report embeds so the CLI can bind it to a plan (F2a).</summary>
    private const string PlanHashLinePrefix = "Plan-Definition-Hash:";

    public static Command Create(IConsoleIo io)
    {
        var folderArgument = FolderArgument.Create();

        // ── issue #366 evidence-hygiene options (design 16-review-attestation-provenance §4/§5) ──────────
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
            return Run(folder, io, evidence, source, reviewer);
        });

        return command;
    }

    private static int Run(string folder, IConsoleIo io, string? evidencePath, string? source, string? reviewer)
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

        PlanDefinition plan = probe.Plan;
        string currentHash = PlanDefinitionHash.Compute(plan);

        // Resolve the deterministic evidence class (issue #366, §4/§5). mark-reviewed NEVER refuses a
        // stamp (invariant 5): an F2 failure DOWNGRADES to `bare`, it does not error out.
        ReviewAttestation attestation = BuildAttestation(plan.PlanDirectory, currentHash, evidencePath, source, reviewer, io);

        // Always write the marker ourselves so the attestation block is stamped (ReviewMarker.Write does
        // not carry it). The planHash + WhenWritingNull serialization keep byte-exact back-compat (§4).
        var marker = new ReviewMarker
        {
            Version = ReviewMarker.CurrentVersion,
            ReviewedAt = DateTimeOffset.UtcNow,
            PlanHash = currentHash,
            Attestation = attestation
        };
        WriteMarker(plan.PlanDirectory, marker);

        ReviewEvaluation eval = ReviewMarker.Evaluate(plan);
        io.Out.WriteLine(
            $"OK: marked reviewed (source: {attestation.Source}, planDefinitionHash {ShortHash(eval.CurrentHash)} — " +
            "the plan's full behavioral definition, incl. guardrail/preflight/action bodies). " +
            "The /guardrails-review nudge stays clear until that content changes.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Resolve the <see cref="ReviewAttestation"/> to stamp. Precedence: an explicit <c>--source machine</c>
    /// wins (an automated flow honestly labels itself and never masquerades as a review-artifact);
    /// otherwise <c>--evidence</c> attempts the <c>review-artifact</c> class through the F2 checks,
    /// downgrading to <c>bare</c> on failure; with neither, a plain <c>bare</c> stamp. The self-reported
    /// <c>--reviewer</c> is recorded as <c>actor</c> on every class (audit richness only).
    /// </summary>
    private static ReviewAttestation BuildAttestation(
        string planDirectory, string currentHash, string? evidencePath, string? source, string? reviewer, IConsoleIo io)
    {
        string tool = "guardrails " + GuardrailsVersion.Current;

        // --source machine: honestly-labelled automated stamp; carries no evidence pointer (§5).
        if (string.Equals(source, SourceMachine, StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewAttestation { Source = SourceMachine, Tool = tool, Actor = reviewer };
        }

        // --evidence <path>: attempt review-artifact. On the F2 checks passing, stamp review-artifact +
        // evidence; on EITHER check failing, fall through and downgrade to bare — never fabricate a class.
        if (evidencePath is not null)
        {
            ReviewEvidence? evidence = TryBuildEvidence(planDirectory, currentHash, evidencePath, io);
            if (evidence is not null)
            {
                return new ReviewAttestation
                {
                    Source = SourceReviewArtifact,
                    Tool = tool,
                    Actor = reviewer,
                    Evidence = evidence
                };
            }
        }

        return new ReviewAttestation { Source = SourceBare, Tool = tool, Actor = reviewer };
    }

    /// <summary>
    /// The F2 stamp-time hygiene checks (§4): return the <see cref="ReviewEvidence"/> to record only when
    /// the report at <paramref name="evidencePath"/> passes BOTH — (b) path containment: it resolves under
    /// <c>&lt;plan&gt;/state/reviews/</c> (full-path containment, not a substring; rejects <c>..</c> escapes
    /// and out-of-tree paths); and (a) plan-binding: it embeds a <c>Plan-Definition-Hash:</c> line equal to
    /// <paramref name="currentHash"/> (the marker's <see cref="PlanDefinitionHash"/>). Any failure (missing/
    /// unreadable report, escaped path, missing/mismatched embedded hash) returns <c>null</c> ⇒ the caller
    /// downgrades to <c>bare</c>.
    /// </summary>
    private static ReviewEvidence? TryBuildEvidence(
        string planDirectory, string currentHash, string evidencePath, IConsoleIo io)
    {
        // Resolve the report path (absolute or plan-relative) to a full path, collapsing any `..`.
        string reportFull = Path.GetFullPath(Path.Combine(planDirectory, evidencePath));
        string reviewsRoot = Path.GetFullPath(Path.Combine(planDirectory, "state", "reviews"));

        // (b) Path containment — full-path, not substring, so a `..` escape resolving to <plan>/state/…
        // (or anywhere outside the reviews tree) fails.
        if (!IsContainedUnder(reviewsRoot, reportFull))
        {
            io.Out.WriteLine(
                "NOTE: --evidence path does not resolve under state/reviews/ — downgrading to source: bare (F2b).");
            return null;
        }

        // The report bytes must be readable to digest + parse; a missing/unreadable report ⇒ downgrade.
        string reportText;
        try
        {
            reportText = File.ReadAllText(reportFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            io.Out.WriteLine("NOTE: --evidence report could not be read — downgrading to source: bare (F2).");
            return null;
        }

        // (a) Plan-binding — the report must embed the CURRENT plan hash. Missing or mismatched ⇒ downgrade.
        string? embeddedHash = ParseEmbeddedPlanHash(reportText);
        if (embeddedHash is null || !string.Equals(embeddedHash, currentHash, StringComparison.Ordinal))
        {
            io.Out.WriteLine(
                "NOTE: --evidence report does not embed the current plan hash — downgrading to source: bare (F2a).");
            return null;
        }

        // Both checks pass: record the plan-folder-relative path (under the hash-excluded state/reviews/
        // tree) and the symmetric, newline-normalized digest of the report bytes (F7).
        return new ReviewEvidence
        {
            ReportPath = Path.GetRelativePath(planDirectory, reportFull),
            ReportDigest = ReviewAttestation.ComputeReportDigest(reportText)
        };
    }

    /// <summary>
    /// Parse the <c>Plan-Definition-Hash:</c> line a <c>/guardrails-review</c> report embeds (F2a), or
    /// null when absent. Line endings are tolerated (LF or CRLF) — the value is trimmed.
    /// </summary>
    private static string? ParseEmbeddedPlanHash(string reportText)
    {
        foreach (string rawLine in reportText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(PlanHashLinePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[PlanHashLinePrefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> (a full path) lies under <paramref name="root"/> (a full
    /// path) — full-path containment, computed via the OS-appropriate relative-path comparison so a
    /// <c>..</c> escape or a different volume is rejected. Not a substring test.
    /// </summary>
    private static bool IsContainedUnder(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    /// <summary>Persist <paramref name="marker"/> to <c>state/guardrails-review.json</c>, creating <c>state/</c> if needed.</summary>
    private static void WriteMarker(string planDirectory, ReviewMarker marker)
    {
        string path = ReviewMarker.PathFor(planDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, marker.ToJson());
    }

    /// <summary>A short, display-friendly form of a <c>sha256:</c> plan hash (first 12 hex chars).</summary>
    private static string ShortHash(string hash)
    {
        string hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash["sha256:".Length..] : hash;
        return "sha256:" + (hex.Length <= 12 ? hex : hex[..12]);
    }
}
