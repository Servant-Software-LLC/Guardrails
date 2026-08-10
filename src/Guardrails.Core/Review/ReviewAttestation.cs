using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Guardrails.Core.Hashing;

namespace Guardrails.Core.Review;

/// <summary>
/// The evidence-hygiene attestation block on the review marker (issue #366, design
/// <c>docs/plans/16-review-attestation-provenance.md</c> §4). ADDITIVE and back-compat: a pre-#366
/// marker has no attestation block and classifies <see cref="EvidenceClass.Legacy"/>. The CLI cannot
/// authenticate an actor (a human and a machine invoke the same <c>mark-reviewed</c>), so
/// <see cref="Source"/> records the deterministically-verifiable EVIDENCE CLASS, and
/// <see cref="Actor"/>/<see cref="Tool"/> are clearly-labeled, NON-authoritative self-reports.
///
/// <para>Read for audit by humans and tooling, NEVER by the Scheduler — it gates nothing (§6). Its
/// value is telling "a real <c>/guardrails-review</c> pass ran and left a report" apart from "a bare
/// marker was written," deterministically and on the record — non-adversarial evidence hygiene, not a
/// forgery deterrent (§3).</para>
/// </summary>
public sealed record ReviewAttestation
{
    /// <summary>
    /// The evidence class the CLI verified — wire values <c>review-artifact</c> | <c>bare</c> |
    /// <c>machine</c> (§4). It is never written as <c>legacy</c>: that is a read-time-only
    /// classification of a marker that carries NO attestation block. Self-reported string; readers
    /// classify tolerantly via <see cref="Classify"/> and never gate on the marker's integer version.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Self-reported CLI build that stamped the marker (e.g. <c>guardrails 1.1.0</c>).
    /// Informational and NON-authoritative — audit richness, not trust (§4). Omitted from the wire
    /// when null (F7 <see cref="JsonIgnoreCondition.WhenWritingNull"/>).
    /// </summary>
    [JsonPropertyName("tool")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    /// <summary>
    /// OPTIONAL, self-reported, NON-authoritative reviewer id (§4, DA-4). Any surfacing MUST label it
    /// non-authoritative (e.g. <c>reviewer (self-reported): …</c>). Omitted from the wire when null
    /// (F7 <see cref="JsonIgnoreCondition.WhenWritingNull"/>), so a bare stamp carries no
    /// <c>"actor": null</c> noise.
    /// </summary>
    [JsonPropertyName("actor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    /// <summary>
    /// The review-report evidence — present IFF <see cref="Source"/> is <c>review-artifact</c>; null
    /// (and omitted from the wire, F7) for <c>bare</c>/<c>machine</c> stamps (§4).
    /// </summary>
    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewEvidence? Evidence { get; init; }

    /// <summary>
    /// Deterministically classify a marker's evidence class from the PRESENCE of its attestation block
    /// and that block's <see cref="Source"/> — NEVER from the marker's integer <c>version</c> (§4). A
    /// marker with no (or an unreadable) block classifies <see cref="EvidenceClass.Legacy"/>.
    /// </summary>
    public static EvidenceClass Classify(ReviewMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        // No block (a v1 / pre-#366 marker, or one whose block was tolerated to null on read) ⇒ legacy.
        // NEVER key on marker.Version: an old integer with a real block still classifies by the block,
        // and a bumped integer with no block is still legacy (§4, "readers never gate on version").
        if (marker.Attestation is not { } attestation)
        {
            return EvidenceClass.Legacy;
        }

        return attestation.Source switch
        {
            "review-artifact" => EvidenceClass.ReviewArtifact,
            "bare" => EvidenceClass.Bare,
            "machine" => EvidenceClass.Machine,
            // An unrecognized (or empty) self-reported source carries no class we can stand behind —
            // treat it as legacy rather than crediting it, matching the tolerant "malformed ⇒ legacy" spirit.
            _ => EvidenceClass.Legacy
        };
    }

    /// <summary>
    /// The <c>sha256:</c>-prefixed digest of a review report's text after the SAME newline
    /// normalization <see cref="Journal.PlanDefinitionHash"/> uses (CRLF/CR → LF), so a CRLF and an LF
    /// checkout of the same report digest IDENTICALLY. The rule is symmetric across the writer (stamp
    /// time) and any reader that re-checks the digest (F7).
    /// </summary>
    public static string ComputeReportDigest(string reportContent)
    {
        ArgumentNullException.ThrowIfNull(reportContent);

        // The SAME newline normalization PlanDefinitionHash folds every plan file through (CRLF/CR → LF),
        // so a CRLF checkout and an LF checkout of the same report digest identically — symmetric across
        // the stamp-time writer and any reader that re-checks it (F7).
        string normalized = HashText.NormalizeNewlines(reportContent);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// The review-report evidence pointer — present only for <c>source: review-artifact</c> (§4). Both
/// fields are recorded at stamp time, after the F2 plan-binding + path-containment checks pass.
/// </summary>
public sealed record ReviewEvidence
{
    /// <summary>
    /// Plan-folder-relative path to the committed review report under the hash-EXCLUDED
    /// <c>state/reviews/</c> tree (§4, §7.3), so the report can never re-stale the marker it attests.
    /// </summary>
    [JsonPropertyName("reportPath")]
    public string ReportPath { get; init; } = string.Empty;

    /// <summary>
    /// <c>sha256:</c> digest of the report bytes, newline-normalized at stamp time via
    /// <see cref="ReviewAttestation.ComputeReportDigest"/> (F7).
    /// </summary>
    [JsonPropertyName("reportDigest")]
    public string ReportDigest { get; init; } = string.Empty;
}

/// <summary>
/// The deterministic evidence class of a review marker (§4), the result of
/// <see cref="ReviewAttestation.Classify"/>. Classified by the presence of the attestation block and
/// its <c>source</c>, NEVER by the marker's integer <c>version</c>.
/// </summary>
public enum EvidenceClass
{
    /// <summary>Read-time only: a marker with NO attestation block (a pre-#366 / v1 marker).</summary>
    Legacy,

    /// <summary>A bare stamp — <c>mark-reviewed</c> with no valid review artifact (or an F2 downgrade).</summary>
    Bare,

    /// <summary>Explicitly stamped by an automated flow (auto-breakdown / autonomous mode); never masquerades as human review.</summary>
    Machine,

    /// <summary>A <c>/guardrails-review</c> report was present, passed the F2 stamp-time checks, and was digested.</summary>
    ReviewArtifact
}
