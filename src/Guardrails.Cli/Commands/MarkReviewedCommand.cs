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
///
/// <para>Issue #430: a RELATIVE <c>--evidence</c> path resolves against the process working directory
/// first (the shell convention every documented invocation from a repo root produces), then plan-relative
/// — see <see cref="ResolveEvidenceFile"/>. A genuine downgrade is announced as a <c>WARNING:</c> on
/// STDERR naming the resolved path, and echoed on the <c>OK:</c> line, so it cannot read as plain
/// success.</para>
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
            Description = "Path to the /guardrails-review report artifact under <plan>/state/reviews/ that this stamp attests. Relative paths are resolved against the CURRENT DIRECTORY as any shell path is (falling back to plan-relative), so both `docs/plans/p/state/reviews/r.md` from a repo root and `state/reviews/r.md` from inside the plan folder work. On the F2 stamp-time checks passing (report embeds the current plan hash; path resolves under state/reviews/) the marker records source: review-artifact + evidence; on failure it downgrades to source: bare with a WARNING on stderr (SSOT §13, issues #366/#430)."
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
        ReviewAttestation attestation = BuildAttestation(
            plan.PlanDirectory, currentHash, evidencePath, source, reviewer, io, out bool evidenceDowngraded);

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
            "The /guardrails-review nudge stays clear until that content changes." +
            // Issue #430: a downgrade is the silent-degradation shape #366 exists to prevent — the stamp
            // still succeeds, so the ONLY signal that the review report was not recorded is this echo of
            // the stderr WARNING, carried on the same line a scripted flow actually reads.
            (evidenceDowngraded
                ? " DOWNGRADED: --evidence did not qualify — this is a BARE stamp; see the WARNING on stderr."
                : string.Empty));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Resolve the <see cref="ReviewAttestation"/> to stamp. Precedence: an explicit <c>--source machine</c>
    /// wins (an automated flow honestly labels itself and never masquerades as a review-artifact);
    /// otherwise <c>--evidence</c> attempts the <c>review-artifact</c> class through the F2 checks,
    /// downgrading to <c>bare</c> on failure; with neither, a plain <c>bare</c> stamp. The self-reported
    /// <c>--reviewer</c> is recorded as <c>actor</c> on every class (audit richness only).
    ///
    /// <para><paramref name="evidenceDowngraded"/> reports whether an <c>--evidence</c> attempt was made
    /// and FAILED F2 (as distinct from never being made), so the caller can say so on the success line
    /// instead of letting a downgrade read as an ordinary bare stamp (issue #430).</para>
    /// </summary>
    private static ReviewAttestation BuildAttestation(
        string planDirectory,
        string currentHash,
        string? evidencePath,
        string? source,
        string? reviewer,
        IConsoleIo io,
        out bool evidenceDowngraded)
    {
        evidenceDowngraded = false;
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

            evidenceDowngraded = true;
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
    ///
    /// <para>Path RESOLUTION (which file the argument names — <see cref="ResolveEvidenceFile"/>) is decided
    /// first and separately from the F2 checks (whether that file qualifies), so a report the user really
    /// named but that fails a check is reported honestly instead of being silently swapped for a
    /// same-named file elsewhere (issue #430).</para>
    /// </summary>
    private static ReviewEvidence? TryBuildEvidence(
        string planDirectory, string currentHash, string evidencePath, IConsoleIo io)
    {
        string reviewsRoot = Path.GetFullPath(Path.Combine(planDirectory, "state", "reviews"));

        string? reportFull = ResolveEvidenceFile(planDirectory, Directory.GetCurrentDirectory(), evidencePath);
        if (reportFull is null)
        {
            AnnounceDowngrade(io, "F2", $"\"{evidencePath}\" is not a usable filesystem path.");
            return null;
        }

        // (b) Path containment — full-path, not substring, so a `..` escape resolving to <plan>/state/…
        // (or anywhere outside the reviews tree) fails. Both sides are full paths, and Path.GetRelativePath
        // applies the host filesystem's own casing rule (OrdinalIgnoreCase on Windows) and separator
        // normalization, so a `/`-vs-`\` or drive-case difference is not a mismatch.
        if (!IsContainedUnder(reviewsRoot, reportFull))
        {
            AnnounceDowngrade(io, "F2b",
                $"\"{evidencePath}\" resolved to \"{reportFull}\", which is not under this plan's reviews tree \"{reviewsRoot}\".");
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
            AnnounceDowngrade(io, "F2", $"the report at \"{reportFull}\" could not be read: {ex.Message}");
            return null;
        }

        // (a) Plan-binding — the report must embed the CURRENT plan hash. Missing or mismatched ⇒ downgrade.
        string? embeddedHash = ParseEmbeddedPlanHash(reportText);
        if (embeddedHash is null || !string.Equals(embeddedHash, currentHash, StringComparison.Ordinal))
        {
            string found = embeddedHash is null
                ? $"the report at \"{reportFull}\" embeds no {PlanHashLinePrefix} line"
                : $"the report at \"{reportFull}\" embeds {PlanHashLinePrefix} {ShortHash(embeddedHash)}";
            AnnounceDowngrade(io, "F2a", $"{found}, but this plan's current hash is {ShortHash(currentHash)}.");
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
    /// Resolve the <c>--evidence</c> argument to the full path of the report file it NAMES — pure path
    /// resolution, before any F2 judgement (issue #430). Returns <c>null</c> only when the argument is not
    /// a usable filesystem path at all.
    ///
    /// <para>A relative path typed on a command line means "relative to where I am standing" — the
    /// universal shell convention, what tab-completion produces, and what the <c>/guardrails-review</c>
    /// skill's documented <c>guardrails mark-reviewed &lt;folder&gt; --evidence &lt;report&gt;</c>
    /// invocation from a repo root always yields. Resolving such a path against the PLAN directory (as this
    /// did before #430) re-rooted <c>docs/plans/p/state/reviews/r.md</c> to
    /// <c>&lt;plan&gt;/docs/plans/p/state/reviews/r.md</c>, which can never lie under
    /// <c>&lt;plan&gt;/state/reviews/</c> — so F2b failed and every documented relative invocation was
    /// silently downgraded to <c>bare</c>. (Canonicalisation was never the defect: both sides already went
    /// through <see cref="Path.GetFullPath(string)"/>, and <see cref="Path.GetRelativePath"/> already
    /// applies the host's casing/separator rules. The BASE was wrong.)</para>
    ///
    /// <para>The plan-relative reading is kept as a FALLBACK — it is the natural form when standing inside
    /// the plan folder (<c>--evidence state/reviews/r.md</c>), and it is what the option's own help text
    /// implied — so the two candidates are tried in that order and the first that EXISTS wins. When neither
    /// exists the shell reading is returned, so the downgrade message names the path the user meant. An
    /// absolute argument collapses both candidates onto itself (<see cref="Path.Combine(string,string)"/>
    /// returns a rooted second operand unchanged), so absolute behaviour is unchanged.</para>
    ///
    /// <para>Public as a test seam — the Cli assembly ships no <c>InternalsVisibleTo</c> (same rationale as
    /// <c>RunCommand.Hyperlink</c>).</para>
    /// </summary>
    /// <param name="planDirectory">The plan's full path — the fallback resolution base.</param>
    /// <param name="workingDirectory">
    /// The directory a relative argument is interpreted against (the process working directory in
    /// production). A parameter rather than a direct <see cref="Directory.GetCurrentDirectory"/> read so
    /// tests can exercise every path form without mutating process-global state.
    /// </param>
    /// <param name="evidencePath">The raw <c>--evidence</c> argument, exactly as the user typed it.</param>
    public static string? ResolveEvidenceFile(string planDirectory, string workingDirectory, string evidencePath)
    {
        string shellRelative;
        string planRelative;
        try
        {
            shellRelative = Path.GetFullPath(Path.Combine(workingDirectory, evidencePath));
            planRelative = Path.GetFullPath(Path.Combine(planDirectory, evidencePath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Garbage in the argument must not crash the command — mark-reviewed never refuses a stamp
            // (invariant 5), so an unusable path is just another downgrade.
            return null;
        }

        // "Which file did you mean?" is answered by what is actually on disk, never by whether the answer
        // would pass F2 — deciding it the other way round would let a qualifying-but-unintended file be
        // attested in place of the one that was named.
        return File.Exists(shellRelative) ? shellRelative
            : File.Exists(planRelative) ? planRelative
            : shellRelative;
    }

    /// <summary>
    /// Announce an F2 downgrade LOUDLY (issue #430). A downgrade is exactly the silent-degradation shape
    /// #366 exists to prevent: the reviewer did the full pass, the stamp still succeeds with exit 0, and
    /// the only signal that the report was NOT recorded used to be a <c>NOTE:</c> on stdout sitting just
    /// above an <c>OK:</c> line — which reads as success. So it goes to STDERR with a <c>WARNING:</c>
    /// prefix, names the RESOLVED path (a path-form mistake is otherwise undiagnosable), and is echoed on
    /// the <c>OK:</c> line. The stamp is still written: this warns, it never refuses (invariant 5).
    /// </summary>
    private static void AnnounceDowngrade(IConsoleIo io, string check, string detail)
    {
        io.Error.WriteLine(
            $"WARNING: --evidence did NOT qualify as review-artifact ({check}) — DOWNGRADING to source: bare.");
        io.Error.WriteLine($"         {detail}");
        io.Error.WriteLine(
            "         The marker is still written and still clears GR2025, but this review is recorded as a " +
            "BARE stamp with no report attached. Re-run with a corrected --evidence path to record it.");
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
