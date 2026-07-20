using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Review;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The <c>mark-reviewed</c> <b>F2 stamp-time hygiene checks</b> surfacing through the real CLI (issue
/// #366, design <c>docs/plans/16-review-attestation-provenance.md</c> §4 (F2) / §5 (the command change)).
/// The command records a deterministic evidence class on the review marker — <c>review-artifact</c> when a
/// <c>/guardrails-review</c> report for <em>this</em> plan is present and passes the F2 checks (report
/// embeds the current <see cref="PlanDefinitionHash"/>; <c>reportPath</c> resolves under
/// <c>&lt;plan&gt;/state/reviews/</c>), otherwise <c>bare</c>. It <b>never refuses</b> a stamp: a bare
/// invocation, or a <c>review-artifact</c> attempt that fails F2, both still write a valid marker that
/// clears GR2025 — they just record <c>source: bare</c> (§5). The class gates nothing; it is read for
/// audit (§6).
///
/// <para>These tests are TDD <b>red</b> for #366: they COMPILE against the minimal
/// <see cref="MarkReviewedCommand"/> option stubs (<c>--evidence</c>/<c>--source</c>/<c>--reviewer</c>) but
/// MUST fail until F2 is implemented — the evidence-class stamp path is stubbed to throw, and the shipped
/// bare stamp records no <c>attestation</c> block yet.</para>
/// </summary>
public sealed class MarkReviewedF2Tests
{
    /// <summary>Drive the CLI in-process exactly as <see cref="ReviewMarkerCliTests"/> does.</summary>
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(ValidateCommand.Create(io));
        root.Add(MarkReviewedCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>The plan's current behavioral-definition hash — what a review report must embed (F2a).</summary>
    private static string ComputePlanHash(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return PlanDefinitionHash.Compute(load.Plan!);
    }

    /// <summary>Create <c>&lt;plan&gt;/state/reviews/</c> and return its absolute path.</summary>
    private static string ReviewsDir(string planDir)
    {
        string dir = Path.Combine(planDir, "state", "reviews");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A minimal review-report body embedding the given <paramref name="planHash"/> (F2a).</summary>
    private static string ReportBody(string planHash) =>
        "# /guardrails-review report\n" +
        $"Plan-Definition-Hash: {planHash}\n" +
        "\n" +
        "## Findings\n" +
        "No blockers. Verdict: reviewed.\n";

    private static ReviewMarker RequireMarker(string planDir)
    {
        ReviewMarker? marker = ReviewMarker.Read(planDir);
        Assert.NotNull(marker);
        return marker!;
    }

    // ── bare, unchanged: `mark-reviewed <folder>` stamps source: bare, clears GR2025, never refused ──────

    [Fact]
    public async Task Bare_NoEvidence_StampsSourceBare_ClearsGr2025_NeverRefused()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // The shipped manual-confirmation flow: a bare stamp is NEVER refused and clears the nudge.
        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(ReviewMarker.PathFor(plan.PlanDir)));

        (int valExit, string after) = await InvokeAsync("validate", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, valExit);
        Assert.DoesNotContain(DiagnosticCodes.ReviewMarkerMissingOrStale, after); // GR2025 cleared

        // The #366 red: the bare stamp now records an explicit `source: bare` evidence class.
        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Equal(EvidenceClass.Bare, ReviewAttestation.Classify(marker));
        Assert.Null(marker.Attestation.Evidence); // bare carries no evidence pointer
    }

    // ── F2 pass ⇒ review-artifact: a report for THIS plan, under state/reviews/, embedding the hash ──────

    [Fact]
    public async Task Evidence_ReportBindsToCurrentPlanHash_UnderReviews_StampsReviewArtifact()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string planHash = ComputePlanHash(plan.PlanDir);

        string reportPath = Path.Combine(ReviewsDir(plan.PlanDir), "review-01.md");
        string body = ReportBody(planHash);
        File.WriteAllText(reportPath, body);

        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", reportPath);
        Assert.Equal(ExitCodes.Success, exit); // never refuses

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("review-artifact", marker.Attestation!.Source);
        Assert.Equal(EvidenceClass.ReviewArtifact, ReviewAttestation.Classify(marker));

        Assert.NotNull(marker.Attestation.Evidence);
        // reportDigest is the newline-normalized sha256 of the report bytes (F7).
        Assert.Equal(ReviewAttestation.ComputeReportDigest(body), marker.Attestation.Evidence!.ReportDigest);
        // reportPath is recorded plan-folder-relative, under the hash-excluded state/reviews/ tree.
        string rel = marker.Attestation.Evidence.ReportPath.Replace('\\', '/');
        Assert.Contains("state/reviews/", rel);
        Assert.EndsWith("review-01.md", rel);
    }

    // ── F2 fail (embedded hash mismatch) ⇒ downgrade to bare, never a fabricated review-artifact ─────────

    [Fact]
    public async Task Evidence_EmbeddedHashMismatch_DowngradesToBare()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        string reportPath = Path.Combine(ReviewsDir(plan.PlanDir), "review-mismatch.md");
        // A report under state/reviews/ but embedding the WRONG plan hash — plan-binding (F2a) must fail.
        string wrongHash = "sha256:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        File.WriteAllText(reportPath, ReportBody(wrongHash));

        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", reportPath);
        Assert.Equal(ExitCodes.Success, exit); // downgraded, still never refused

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Equal(EvidenceClass.Bare, ReviewAttestation.Classify(marker));
        Assert.Null(marker.Attestation.Evidence); // no fabricated evidence class
    }

    // ── F2 fail (reportPath escapes state/reviews/ via `..`) ⇒ downgrade to bare ─────────────────────────

    [Fact]
    public async Task Evidence_ReportPathEscapesReviews_DowngradesToBare()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string planHash = ComputePlanHash(plan.PlanDir);

        // The report is physically OUTSIDE state/reviews/ (directly under state/) and carries the CORRECT
        // embedded hash — so ONLY the path-containment check (F2b) can fail here.
        string stateDir = Path.Combine(plan.PlanDir, "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "outside-report.md"), ReportBody(planHash));

        // Reached via a `..` escape out of state/reviews/ → resolves to <plan>/state/outside-report.md.
        string escapePath = Path.Combine(ReviewsDir(plan.PlanDir), "..", "outside-report.md");

        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", escapePath);
        Assert.Equal(ExitCodes.Success, exit);

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Null(marker.Attestation.Evidence);
    }

    // ── --source machine: an automated stamp is honestly labelled, never masquerades as review ───────────

    [Fact]
    public async Task SourceMachine_StampsSourceMachine()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--source", "machine");
        Assert.Equal(ExitCodes.Success, exit);

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("machine", marker.Attestation!.Source);
        Assert.Equal(EvidenceClass.Machine, ReviewAttestation.Classify(marker));
        Assert.Null(marker.Attestation.Evidence);
    }

    // ── --reviewer: records the self-reported, non-authoritative actor (still a bare stamp) ──────────────

    [Fact]
    public async Task Reviewer_RecordsSelfReportedActor()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--reviewer", "reviewer@example.com");
        Assert.Equal(ExitCodes.Success, exit);

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("reviewer@example.com", marker.Attestation!.Actor);
        // No evidence supplied ⇒ still a bare stamp; the reviewer id is only audit richness.
        Assert.Equal("bare", marker.Attestation.Source);
    }

    // ── symmetric digest: the SAME report content with CRLF vs LF newlines ⇒ identical reportDigest (F7) ─

    [Fact]
    public async Task Evidence_ReportDigest_IsSymmetricAcrossCrlfAndLf()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string planHash = ComputePlanHash(plan.PlanDir);
        string reviewsDir = ReviewsDir(plan.PlanDir);

        // Byte-for-byte the same content bar the newline style; both embed the correct hash so both pass F2.
        string bodyLf = ReportBody(planHash);
        string bodyCrlf = bodyLf.Replace("\n", "\r\n");

        string lfPath = Path.Combine(reviewsDir, "review-lf.md");
        string crlfPath = Path.Combine(reviewsDir, "review-crlf.md");
        File.WriteAllText(lfPath, bodyLf);
        File.WriteAllText(crlfPath, bodyCrlf);

        (int lfExit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", lfPath);
        Assert.Equal(ExitCodes.Success, lfExit);
        ReviewMarker lfMarker = RequireMarker(plan.PlanDir);
        Assert.NotNull(lfMarker.Attestation?.Evidence);
        string digestLf = lfMarker.Attestation!.Evidence!.ReportDigest;

        (int crlfExit, _) = await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", crlfPath);
        Assert.Equal(ExitCodes.Success, crlfExit);
        ReviewMarker crlfMarker = RequireMarker(plan.PlanDir);
        Assert.NotNull(crlfMarker.Attestation?.Evidence);
        string digestCrlf = crlfMarker.Attestation!.Evidence!.ReportDigest;

        // F7: the same CRLF/CR → LF normalization PlanDefinitionHash uses, so a CRLF and an LF checkout
        // of the same report digest identically.
        Assert.Equal(digestLf, digestCrlf);
        Assert.StartsWith("sha256:", digestLf);
    }
}
