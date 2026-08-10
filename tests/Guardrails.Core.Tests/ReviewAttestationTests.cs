using Guardrails.Core.Review;

namespace Guardrails.Core.Tests;

/// <summary>
/// The review-marker <b>attestation</b> block (issue #366, design
/// <c>docs/plans/16-review-attestation-provenance.md</c> §4/§5/§7): an ADDITIVE, back-compat evidence
/// class + audit trail on <c>state/guardrails-review.json</c>. It gates nothing (§6) — the recorded
/// class is read by humans and tooling to tell "a real <c>/guardrails-review</c> pass ran and left a
/// report" apart from "a bare marker was written," deterministically and on the record.
///
/// <para>These tests are TDD <b>red</b> for #366: they compile against the minimal stubs
/// (<see cref="ReviewAttestation"/>, <see cref="ReviewMarker.Attestation"/>,
/// <see cref="ReviewMarker.ToJson"/>) but MUST fail until the behaviour is implemented — the write
/// path, the read-tolerant classification, and the symmetric report-digest helper are all absent.</para>
/// </summary>
public sealed class ReviewAttestationTests : IDisposable
{
    private readonly string _planDir;

    public ReviewAttestationTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-attest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); } catch (IOException) { }
    }

    private static DateTimeOffset When() => new(2026, 6, 22, 14, 3, 11, TimeSpan.Zero);

    /// <summary>Write raw marker JSON to the plan's <c>state/</c> path and read it back via the tolerant reader.</summary>
    private ReviewMarker? ReadJson(string json)
    {
        string markerPath = ReviewMarker.PathFor(_planDir);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, json);
        return ReviewMarker.Read(_planDir);
    }

    /// <summary>Serialize a marker to its wire JSON and read it back — a full attestation round-trip.</summary>
    private ReviewMarker? RoundTrip(ReviewMarker marker)
    {
        string json = marker.ToJson(); // #366 stub throws here until the write path exists.
        return ReadJson(json);
    }

    // ── round-trip: every attestation field survives serialize → deserialize ─────────────────────────

    [Fact]
    public void Attestation_V2ReviewArtifactMarker_RoundTripsPreservingEveryField()
    {
        var marker = new ReviewMarker
        {
            Version = 2,
            ReviewedAt = When(),
            PlanHash = "sha256:1a2b3c4d",
            Attestation = new ReviewAttestation
            {
                Source = "review-artifact",
                Tool = "guardrails 1.1.0",
                Actor = "david.maltby@hotmail.com",
                Evidence = new ReviewEvidence
                {
                    ReportPath = "state/reviews/review-1a2b3c4d5e6f-2026-06-22T140311Z.md",
                    ReportDigest = "sha256:deadbeefcafe"
                }
            }
        };

        ReviewMarker? back = RoundTrip(marker);

        Assert.NotNull(back);
        Assert.Equal(2, back!.Version);
        Assert.Equal(marker.ReviewedAt, back.ReviewedAt);
        Assert.Equal("sha256:1a2b3c4d", back.PlanHash);

        Assert.NotNull(back.Attestation);
        Assert.Equal("review-artifact", back.Attestation!.Source);
        Assert.Equal("guardrails 1.1.0", back.Attestation.Tool);
        Assert.Equal("david.maltby@hotmail.com", back.Attestation.Actor);

        Assert.NotNull(back.Attestation.Evidence);
        Assert.Equal("state/reviews/review-1a2b3c4d5e6f-2026-06-22T140311Z.md", back.Attestation.Evidence!.ReportPath);
        Assert.Equal("sha256:deadbeefcafe", back.Attestation.Evidence.ReportDigest);
    }

    // ── back-compat / legacy: a v1 marker (no block) and a malformed block both read tolerantly ──────

    [Fact]
    public void Classify_V1MarkerWithNoAttestationBlock_IsLegacy()
    {
        // A pre-#366 marker: the three top fields, no attestation block. It deserializes with the block
        // absent and CLASSIFIES as `legacy` — no user is worse off, it simply carries no evidence class.
        ReviewMarker? marker = ReadJson(
            """{ "version": 1, "reviewedAt": "2026-06-22T14:03:11Z", "planHash": "sha256:abc" }""");

        Assert.NotNull(marker);
        Assert.Null(marker!.Attestation);
        Assert.Equal(EvidenceClass.Legacy, ReviewAttestation.Classify(marker));
    }

    [Fact]
    public void Read_MalformedAttestationBlock_IsTolerant_BlockNull_ClassifiesLegacy_NeverThrows()
    {
        // A marker with the three top fields intact but a MALFORMED attestation block must read
        // tolerantly (mirror ReviewMarker.Read): never throw, keep the top fields, and deserialize the
        // block as null → classified `legacy`.
        ReviewMarker? marker = ReadJson(
            """
            {
              "version": 2,
              "reviewedAt": "2026-06-22T14:03:11Z",
              "planHash": "sha256:abc",
              "attestation": { "evidence": "this-should-be-an-object-not-a-string" }
            }
            """);

        Assert.NotNull(marker);                       // top-three intact — the block malformation is tolerated
        Assert.Equal("sha256:abc", marker!.PlanHash);
        Assert.Null(marker.Attestation);              // malformed block reads as null
        Assert.Equal(EvidenceClass.Legacy, ReviewAttestation.Classify(marker));
    }

    // ── the reader classifies by the block + its source, NEVER by the integer version ────────────────

    [Fact]
    public void Classify_KeysOnAttestationBlock_NotOnTheIntegerVersion()
    {
        // Old integer version, but a real evidence block → classify by the block (review-artifact),
        // not by the version.
        var attestedButOldVersion = new ReviewMarker
        {
            Version = 1,
            ReviewedAt = When(),
            PlanHash = "sha256:abc",
            Attestation = new ReviewAttestation
            {
                Source = "review-artifact",
                Tool = "guardrails 1.1.0",
                Evidence = new ReviewEvidence { ReportPath = "state/reviews/r.md", ReportDigest = "sha256:d" }
            }
        };

        // New integer version, but no block → still `legacy`. Version alone never implies a class.
        var newVersionNoBlock = new ReviewMarker
        {
            Version = 2,
            ReviewedAt = When(),
            PlanHash = "sha256:abc",
            Attestation = null
        };

        Assert.Equal(EvidenceClass.ReviewArtifact, ReviewAttestation.Classify(attestedButOldVersion));
        Assert.Equal(EvidenceClass.Legacy, ReviewAttestation.Classify(newVersionNoBlock));
    }

    // ── WhenWritingNull: a bare stamp emits no null-optional noise; the top-three stay present ────────

    [Fact]
    public void Attestation_BareStamp_OmitsNullActorAndEvidence_KeepsRequiredTopThree()
    {
        var bare = new ReviewMarker
        {
            Version = 2,
            ReviewedAt = When(),
            PlanHash = "sha256:abc",
            Attestation = new ReviewAttestation
            {
                Source = "bare",
                Tool = "guardrails 1.1.0"
                // Actor + Evidence null — must NOT appear on the wire.
            }
        };

        string json = bare.ToJson();

        // The bare block carries only source (+ tool) — no `"actor": null` / `"evidence": null` noise.
        Assert.Contains("\"source\"", json);
        Assert.Contains("bare", json);
        Assert.Contains("\"tool\"", json);
        Assert.DoesNotContain("actor", json);
        Assert.DoesNotContain("evidence", json);

        // The required top-three keep their `Never` serialization (byte-exact back-compat).
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"reviewedAt\"", json);
        Assert.Contains("\"planHash\"", json);
    }

    // ── symmetric report digest: same content, any newline style → identical digest ──────────────────

    [Fact]
    public void ComputeReportDigest_IsSymmetricAcrossNewlineStyles_AndContentSensitive()
    {
        string reportLf =
            "# Review of plan\n" +
            "Plan-Definition-Hash: sha256:abc123\n" +
            "Verdict: blockers addressed\n";
        string reportCrlf = reportLf.Replace("\n", "\r\n");
        string reportCr = reportLf.Replace("\n", "\r");

        string lf = ReviewAttestation.ComputeReportDigest(reportLf);
        string crlf = ReviewAttestation.ComputeReportDigest(reportCrlf);
        string cr = ReviewAttestation.ComputeReportDigest(reportCr);

        // Same content, different newline styles → identical digest (the F7 symmetry rule: the SAME
        // CRLF/CR → LF normalization PlanDefinitionHash uses, applied before sha256).
        Assert.Equal(lf, crlf);
        Assert.Equal(lf, cr);
        Assert.StartsWith("sha256:", lf);

        // A genuine (non-newline) content change DOES change the digest.
        Assert.NotEqual(lf, ReviewAttestation.ComputeReportDigest(reportLf + "one more finding\n"));
    }
}
