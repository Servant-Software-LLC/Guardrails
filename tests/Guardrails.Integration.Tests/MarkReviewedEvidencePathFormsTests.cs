using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Review;

namespace Guardrails.Integration.Tests;

/// <summary>
/// <c>mark-reviewed --evidence</c> must record <c>source: review-artifact</c> for a report that genuinely
/// lives under <c>&lt;plan&gt;/state/reviews/</c> — <b>whatever path FORM names it</b> (issue #430).
///
/// <para>The defect: a relative <c>--evidence</c> argument was resolved against the PLAN directory rather
/// than the process working directory, so the documented invocation from a repo root —
/// <c>guardrails mark-reviewed docs/plans/p --evidence docs/plans/p/state/reviews/r.md</c> — re-rooted to
/// <c>&lt;plan&gt;/docs/plans/p/state/reviews/r.md</c>, could never lie under <c>&lt;plan&gt;/state/reviews/</c>,
/// and silently downgraded to <c>source: bare</c>. Every pre-#430 test passed an ABSOLUTE
/// <c>--evidence</c> path, which is exactly why the whole class of relative invocations went unnoticed:
/// this is the #366 silent-degradation failure mode inverted — a reviewer who did the full pass, wrote the
/// report, and followed the docs got recorded as a bare stamp.</para>
///
/// <para>Fixture note: the plan is built under the process working directory (NOT the system temp dir) so
/// the relative forms are genuinely relative and portable. The tests never mutate
/// <see cref="Directory.SetCurrentDirectory"/> — that is process-global state and other tests running in
/// parallel assert on <see cref="Directory.GetCurrentDirectory"/>.</para>
/// </summary>
public sealed class MarkReviewedEvidencePathFormsTests
{
    // ── path forms exercised by the matrix ───────────────────────────────────────────────────────────────

    /// <summary>How the PLAN FOLDER argument is written.</summary>
    public const string PlanAbsolute = "plan-absolute";

    /// <summary>The plan folder as a path relative to the process working directory.</summary>
    public const string PlanRelative = "plan-relative";

    /// <summary>The plan folder, absolute, with every separator written as <c>/</c> (Windows only).</summary>
    public const string PlanAbsoluteForwardSlashes = "plan-absolute-forward-slashes";

    /// <summary>The plan folder, absolute, with its casing mangled (Windows only).</summary>
    public const string PlanAbsoluteUpperCased = "plan-absolute-upper-cased";

    /// <summary>How the <c>--evidence</c> argument is written.</summary>
    public const string EvidenceAbsolute = "evidence-absolute";

    /// <summary>The report as a path relative to the process working directory — the defect's repro.</summary>
    public const string EvidenceRelative = "evidence-relative";

    /// <summary>The same relative path with an explicit <c>./</c> prefix.</summary>
    public const string EvidenceRelativeDotSlash = "evidence-relative-dot-slash";

    /// <summary>A working-directory-relative path that detours through <c>..</c> but lands back inside.</summary>
    public const string EvidenceRelativeTraversal = "evidence-relative-traversal";

    /// <summary>
    /// <c>state/reviews/&lt;file&gt;</c> — relative to the PLAN folder, the form a reviewer standing inside
    /// the plan folder types. Kept working as the documented fallback.
    /// </summary>
    public const string EvidencePlanRelative = "evidence-plan-relative";

    /// <summary>The relative report path with every separator written as <c>/</c> (Windows only).</summary>
    public const string EvidenceRelativeForwardSlashes = "evidence-relative-forward-slashes";

    /// <summary>The relative report path mixing <c>/</c> and <c>\</c> separators (Windows only).</summary>
    public const string EvidenceRelativeMixedSeparators = "evidence-relative-mixed-separators";

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drive the CLI in-process exactly as <see cref="MarkReviewedF2Tests"/> does.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(MarkReviewedCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText, io.ErrorText);
    }

    private static string ComputePlanHash(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return PlanDefinitionHash.Compute(load.Plan!);
    }

    /// <summary>A minimal review-report body embedding <paramref name="planHash"/> so F2a passes.</summary>
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

    /// <summary>
    /// A plan folder rooted under the process working directory (so relative forms are real), holding a
    /// valid review report at <c>state/reviews/review-01.md</c>.
    /// </summary>
    private sealed class PlanUnderWorkingDirectory : IDisposable
    {
        private readonly ScriptPlanBuilder _builder;

        public PlanUnderWorkingDirectory()
        {
            WorkingDirectory = Directory.GetCurrentDirectory();
            _builder = new ScriptPlanBuilder(WorkingDirectory).AddTask("01-first");

            PlanHash = ComputePlanHash(PlanDir);
            Directory.CreateDirectory(ReviewsDir);
            ReportBodyText = ReportBody(PlanHash);
            File.WriteAllText(ReportPath, ReportBodyText);
        }

        public string WorkingDirectory { get; }

        public string PlanDir => _builder.PlanDir;

        public string ReviewsDir => Path.Combine(PlanDir, "state", "reviews");

        public string ReportPath => Path.Combine(ReviewsDir, "review-01.md");

        public string PlanHash { get; }

        public string ReportBodyText { get; }

        /// <summary>The plan folder as a path relative to the working directory (a bare folder name here).</summary>
        public string RelativePlanDir => Path.GetRelativePath(WorkingDirectory, PlanDir);

        /// <summary>The report as a path relative to the working directory — the issue's repro form.</summary>
        public string RelativeReportPath => Path.GetRelativePath(WorkingDirectory, ReportPath);

        public void Dispose() => _builder.Dispose();
    }

    private static string PlanArgument(PlanUnderWorkingDirectory plan, string form) => form switch
    {
        PlanAbsolute => plan.PlanDir,
        PlanRelative => plan.RelativePlanDir,
        PlanAbsoluteForwardSlashes => plan.PlanDir.Replace('\\', '/'),
        PlanAbsoluteUpperCased => plan.PlanDir.ToUpperInvariant(),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unknown plan path form")
    };

    private static string EvidenceArgument(PlanUnderWorkingDirectory plan, string form) => form switch
    {
        EvidenceAbsolute => plan.ReportPath,
        EvidenceRelative => plan.RelativeReportPath,
        EvidenceRelativeDotSlash => "." + Path.DirectorySeparatorChar + plan.RelativeReportPath,
        // Out of the reviews folder and straight back in — resolves to the same file, and proves the
        // containment check works on the CANONICALISED path rather than on the literal string.
        EvidenceRelativeTraversal =>
            Path.Combine(Path.GetDirectoryName(plan.RelativeReportPath)!, "..", "reviews",
                Path.GetFileName(plan.ReportPath)),
        EvidencePlanRelative => Path.Combine("state", "reviews", Path.GetFileName(plan.ReportPath)),
        EvidenceRelativeForwardSlashes => plan.RelativeReportPath.Replace('\\', '/'),
        // e.g. `<plan>/state\reviews/review-01.md` — the shape a copy-paste between shells produces.
        EvidenceRelativeMixedSeparators =>
            plan.RelativeReportPath.Replace('\\', '/').Replace("state/reviews", "state\\reviews"),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "unknown evidence path form")
    };

    // ── the matrix: every path form must record review-artifact ──────────────────────────────────────────

    [Theory]
    // The issue's repro: both arguments relative, as any invocation from a repo root produces.
    [InlineData(PlanRelative, EvidenceRelative)]
    // The form that already worked pre-#430 — must keep working.
    [InlineData(PlanAbsolute, EvidenceAbsolute)]
    // Mixed relative/absolute, both ways round.
    [InlineData(PlanRelative, EvidenceAbsolute)]
    [InlineData(PlanAbsolute, EvidenceRelative)]
    // `./`-prefixed and `..`-detouring relative forms still land inside the reviews tree.
    [InlineData(PlanRelative, EvidenceRelativeDotSlash)]
    [InlineData(PlanRelative, EvidenceRelativeTraversal)]
    // The documented plan-relative fallback (a reviewer standing inside the plan folder).
    [InlineData(PlanAbsolute, EvidencePlanRelative)]
    [InlineData(PlanRelative, EvidencePlanRelative)]
    public async Task EveryPathForm_ForAReportUnderStateReviews_RecordsReviewArtifact(
        string planForm, string evidenceForm)
    {
        using var plan = new PlanUnderWorkingDirectory();
        await AssertRecordsReviewArtifact(plan, PlanArgument(plan, planForm), EvidenceArgument(plan, evidenceForm));
    }

    /// <summary>
    /// Windows path forms: separators are interchangeable (<c>/</c> and <c>\</c>, including mixed within
    /// one path) and comparison is case-insensitive. Gated to Windows because a backslash is a LEGAL
    /// filename character on Linux/macOS (the "mixed separator" path would name a different, non-existent
    /// file) and those filesystems are case-sensitive.
    /// </summary>
    [Theory]
    [InlineData(PlanAbsolute, EvidenceRelativeForwardSlashes)]
    [InlineData(PlanAbsolute, EvidenceRelativeMixedSeparators)]
    [InlineData(PlanAbsoluteForwardSlashes, EvidenceRelativeForwardSlashes)]
    [InlineData(PlanAbsoluteForwardSlashes, EvidenceRelative)]
    [InlineData(PlanRelative, EvidenceRelativeForwardSlashes)]
    [InlineData(PlanRelative, EvidenceRelativeMixedSeparators)]
    // Casing: the plan root arrives upper-cased, so containment must compare case-insensitively.
    [InlineData(PlanAbsoluteUpperCased, EvidenceRelative)]
    [InlineData(PlanAbsoluteUpperCased, EvidenceAbsolute)]
    public async Task WindowsSeparatorAndCaseForms_RecordReviewArtifact(string planForm, string evidenceForm)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Separator interchangeability and case-insensitive path comparison are Windows semantics; " +
            "a backslash is a legal filename character on Linux/macOS.");

        using var plan = new PlanUnderWorkingDirectory();
        await AssertRecordsReviewArtifact(plan, PlanArgument(plan, planForm), EvidenceArgument(plan, evidenceForm));
    }

    private static async Task AssertRecordsReviewArtifact(
        PlanUnderWorkingDirectory plan, string planArgument, string evidenceArgument)
    {
        (int exit, string output, string error) =
            await InvokeAsync("mark-reviewed", planArgument, "--evidence", evidenceArgument);

        Assert.Equal(ExitCodes.Success, exit);

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.NotNull(marker.Attestation);
        Assert.Equal("review-artifact", marker.Attestation!.Source);
        Assert.Equal(EvidenceClass.ReviewArtifact, ReviewAttestation.Classify(marker));

        // The recorded pointer is normalised to the plan-folder-relative path regardless of the input form,
        // and the digest is of the real report bytes — so the marker is byte-identical across path forms.
        Assert.NotNull(marker.Attestation.Evidence);
        Assert.Equal(
            Path.Combine("state", "reviews", "review-01.md").Replace('\\', '/'),
            marker.Attestation.Evidence!.ReportPath.Replace('\\', '/'));
        Assert.Equal(
            ReviewAttestation.ComputeReportDigest(plan.ReportBodyText),
            marker.Attestation.Evidence.ReportDigest);

        // A passing stamp says nothing about a downgrade, on either stream.
        Assert.DoesNotContain("DOWNGRAD", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WARNING", error, StringComparison.Ordinal);
    }

    // ── the negatives still downgrade — the fix must not weaken F2 ───────────────────────────────────────

    /// <summary>
    /// F2b, relative form: a report physically OUTSIDE <c>state/reviews/</c> (with a CORRECT embedded hash,
    /// so only containment can fail) still downgrades. Resolving relative paths against the working
    /// directory must not turn containment into a formality.
    /// </summary>
    [Fact]
    public async Task RelativeEvidence_OutsideStateReviews_StillDowngradesToBare()
    {
        using var plan = new PlanUnderWorkingDirectory();

        string outsideDir = Path.Combine(plan.PlanDir, "state");
        File.WriteAllText(Path.Combine(outsideDir, "outside-report.md"), ReportBody(plan.PlanHash));
        string relativeOutside = Path.GetRelativePath(
            plan.WorkingDirectory, Path.Combine(outsideDir, "outside-report.md"));
        Assert.False(Path.IsPathRooted(relativeOutside), "the fixture must exercise a genuinely relative path");

        (int exit, string output, string error) =
            await InvokeAsync("mark-reviewed", plan.RelativePlanDir, "--evidence", relativeOutside);

        Assert.Equal(ExitCodes.Success, exit); // never refuses — it downgrades

        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Null(marker.Attestation.Evidence);

        Assert.Contains("F2b", error, StringComparison.Ordinal);
        Assert.Contains("DOWNGRADED", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// F2b via a <c>..</c> escape written relatively: the canonicalised path leaves the reviews tree, so it
    /// downgrades — the containment check is on full paths, never a substring match.
    /// </summary>
    [Fact]
    public async Task RelativeEvidence_TraversalEscapingStateReviews_StillDowngradesToBare()
    {
        using var plan = new PlanUnderWorkingDirectory();

        File.WriteAllText(Path.Combine(plan.PlanDir, "state", "escaped.md"), ReportBody(plan.PlanHash));
        string escape = Path.Combine(
            Path.GetRelativePath(plan.WorkingDirectory, plan.ReviewsDir), "..", "escaped.md");

        (int exit, _, string error) =
            await InvokeAsync("mark-reviewed", plan.RelativePlanDir, "--evidence", escape);

        Assert.Equal(ExitCodes.Success, exit);
        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Null(marker.Attestation.Evidence);
        Assert.Contains("F2b", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// F2a, relative form: a report correctly filed under <c>state/reviews/</c> whose embedded
    /// <c>Plan-Definition-Hash:</c> does not match the plan still downgrades. The #430 fix loosened path
    /// RESOLUTION only — the plan-binding check is untouched.
    /// </summary>
    [Fact]
    public async Task RelativeEvidence_EmbeddedHashMismatch_StillDowngradesToBare()
    {
        using var plan = new PlanUnderWorkingDirectory();

        string stalePath = Path.Combine(plan.ReviewsDir, "review-stale.md");
        File.WriteAllText(stalePath,
            ReportBody("sha256:deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"));

        (int exit, string output, string error) = await InvokeAsync(
            "mark-reviewed", plan.RelativePlanDir,
            "--evidence", Path.GetRelativePath(plan.WorkingDirectory, stalePath));

        Assert.Equal(ExitCodes.Success, exit);
        ReviewMarker marker = RequireMarker(plan.PlanDir);
        Assert.Equal("bare", marker.Attestation!.Source);
        Assert.Null(marker.Attestation.Evidence);

        Assert.Contains("F2a", error, StringComparison.Ordinal);
        Assert.Contains("DOWNGRADED", output, StringComparison.Ordinal);
    }

    /// <summary>A named report that does not exist anywhere downgrades rather than crashing.</summary>
    [Fact]
    public async Task RelativeEvidence_MissingReport_DowngradesToBare()
    {
        using var plan = new PlanUnderWorkingDirectory();

        (int exit, _, string error) = await InvokeAsync(
            "mark-reviewed", plan.RelativePlanDir,
            "--evidence", Path.Combine(plan.RelativePlanDir, "state", "reviews", "no-such-report.md"));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("bare", RequireMarker(plan.PlanDir).Attestation!.Source);
        Assert.Contains("WARNING", error, StringComparison.Ordinal);
    }

    // ── the downgrade must be LOUD (issue #430 secondary) ────────────────────────────────────────────────

    /// <summary>
    /// A downgrade is announced on STDERR as a <c>WARNING:</c> naming the RESOLVED path — not as a
    /// <c>NOTE:</c> riding on the <c>OK:</c> line, which read as success — and the <c>OK:</c> line itself
    /// says the stamp was downgraded. The stamp is still written: mark-reviewed never refuses.
    /// </summary>
    [Fact]
    public async Task Downgrade_IsAnnouncedOnStderrAsAWarning_AndEchoedOnTheOkLine()
    {
        using var plan = new PlanUnderWorkingDirectory();

        string outside = Path.Combine(plan.PlanDir, "state", "outside.md");
        File.WriteAllText(outside, ReportBody(plan.PlanHash));

        (int exit, string output, string error) =
            await InvokeAsync("mark-reviewed", plan.PlanDir, "--evidence", outside);

        Assert.Equal(ExitCodes.Success, exit);

        Assert.StartsWith("WARNING:", error.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("DOWNGRADING to source: bare", error, StringComparison.Ordinal);
        Assert.Contains(outside, error, StringComparison.Ordinal);       // the resolved path, so it is diagnosable
        Assert.Contains(plan.ReviewsDir, error, StringComparison.Ordinal); // and where it was expected

        Assert.Contains("OK: marked reviewed", output, StringComparison.Ordinal);
        Assert.Contains("DOWNGRADED", output, StringComparison.Ordinal);
    }

    /// <summary>A plain bare stamp is not a downgrade — no <c>--evidence</c> was offered, so nothing warns.</summary>
    [Fact]
    public async Task BareStamp_WithNoEvidenceOffered_IsNotReportedAsADowngrade()
    {
        using var plan = new PlanUnderWorkingDirectory();

        (int exit, string output, string error) = await InvokeAsync("mark-reviewed", plan.RelativePlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("bare", RequireMarker(plan.PlanDir).Attestation!.Source);
        Assert.DoesNotContain("DOWNGRAD", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, error);
    }

    // ── the resolver itself, without touching process state ──────────────────────────────────────────────

    /// <summary>
    /// The resolution rule in isolation (<see cref="MarkReviewedCommand.ResolveEvidenceFile"/>): the
    /// working-directory reading is tried FIRST and the plan-relative reading is the fallback, decided by
    /// which candidate exists on disk. Direct coverage of the ordering matters because both readings can
    /// name a real file, and picking the wrong one would attest a file the user did not name.
    /// </summary>
    [Fact]
    public void ResolveEvidenceFile_PrefersTheWorkingDirectoryReading_ThenFallsBackToPlanRelative()
    {
        using var plan = new PlanUnderWorkingDirectory();

        // One relative argument that BOTH bases can turn into a real file. The scratch subtree is
        // guid-named so nothing another parallel test does can plant or remove either candidate.
        string scratch = "gr430-" + Guid.NewGuid().ToString("N");
        string argument = Path.Combine(scratch, "report.md");

        string planCandidate = Path.Combine(plan.PlanDir, argument);
        string shellCandidate = Path.Combine(plan.WorkingDirectory, argument);
        Directory.CreateDirectory(Path.GetDirectoryName(planCandidate)!);
        Directory.CreateDirectory(Path.GetDirectoryName(shellCandidate)!);

        try
        {
            // Only the plan-relative reading exists ⇒ the documented fallback resolves it.
            File.WriteAllText(planCandidate, "plan-relative");
            Assert.Equal(
                planCandidate,
                MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, plan.WorkingDirectory, argument));

            // Now both exist ⇒ the shell reading wins, because that is what the typed path means.
            File.WriteAllText(shellCandidate, "shell-relative");
            Assert.Equal(
                shellCandidate,
                MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, plan.WorkingDirectory, argument));

            // Neither reading exists ⇒ the shell reading is returned so the warning names what was typed.
            string missing = Path.Combine(scratch, "nope.md");
            Assert.Equal(
                Path.Combine(plan.WorkingDirectory, missing),
                MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, plan.WorkingDirectory, missing));
        }
        finally
        {
            Directory.Delete(Path.Combine(plan.WorkingDirectory, scratch), recursive: true);
        }
    }

    /// <summary>An absolute argument resolves to itself, whichever base is supplied.</summary>
    [Fact]
    public void ResolveEvidenceFile_AbsoluteArgument_IgnoresBothBases()
    {
        using var plan = new PlanUnderWorkingDirectory();

        Assert.Equal(
            plan.ReportPath,
            MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, plan.WorkingDirectory, plan.ReportPath));
        Assert.Equal(
            plan.ReportPath,
            MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, Path.GetTempPath(), plan.ReportPath));
    }

    /// <summary>An unusable path argument yields null (⇒ a downgrade) rather than throwing out of the command.</summary>
    [Fact]
    public void ResolveEvidenceFile_UnusablePath_ReturnsNullInsteadOfThrowing()
    {
        using var plan = new PlanUnderWorkingDirectory();

        Assert.Null(MarkReviewedCommand.ResolveEvidenceFile(plan.PlanDir, plan.WorkingDirectory, "bad\0path.md"));
    }
}
