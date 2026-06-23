using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Review;

/// <summary>
/// The review marker (SSOT §13, issue #79): a small file <c>state/guardrails-review.json</c> that
/// records a human ran <c>/guardrails-review</c> over the CURRENT plan. It carries a timestamp and
/// the plan's <see cref="PlanHash"/> at review time, so an EDITED plan reads as un-reviewed again
/// (the same way the journal's <c>planHash</c> detects a changed plan on resume).
///
/// <para>It is <b>committed as part of the reviewed plan</b>, alongside the committed task folder and
/// the review's edits. It is an attestation about the COMMITTED plan content, not about a particular
/// checkout: because it is <see cref="PlanHash"/>-keyed it <b>self-invalidates the instant any
/// <c>task.json</c>/<c>guardrails.json</c> changes the plan hash</b> (the review nudge returns), so it
/// can never falsely vouch for changed content. That self-invalidation is exactly what makes
/// committing it safe — a stale marker reads as un-reviewed rather than as a false green.</para>
///
/// <para>The harness only READS the marker and computes staleness; the <c>/guardrails-review</c> skill
/// WRITES it. Surfacing is warn-never-block: <c>guardrails validate</c> emits
/// <see cref="Loading.DiagnosticCodes.ReviewMarkerMissingOrStale"/> (GR2025, a warning) and
/// <c>guardrails run</c> prints the same nudge (suppressible with <c>--skip-review-check</c>).
/// Because it is a committed plan artifact (not per-run runtime state), <c>--fresh</c> does NOT
/// wipe it (SSOT §6.1).</para>
/// </summary>
public sealed record ReviewMarker
{
    /// <summary>The marker file name under <c>state/</c>.</summary>
    public const string FileName = "guardrails-review.json";

    /// <summary>The current marker schema version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Schema version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>UTC time the review completed (ISO-8601).</summary>
    [JsonPropertyName("reviewedAt")]
    public DateTimeOffset ReviewedAt { get; init; }

    /// <summary>The <see cref="PlanHash"/> (<c>sha256:</c>-prefixed) computed at review time.</summary>
    [JsonPropertyName("planHash")]
    public string PlanHash { get; init; } = string.Empty;

    /// <summary>Absolute path to the marker file for the given plan folder (<c>state/guardrails-review.json</c>).</summary>
    public static string PathFor(string planDirectory) =>
        Path.Combine(Path.GetFullPath(planDirectory), "state", FileName);

    /// <summary>
    /// Read the marker for <paramref name="planDirectory"/>, or null when it is absent or
    /// unparseable. A present-but-corrupt marker reads as null (treated as <em>missing</em> by
    /// <see cref="Evaluate"/>) — never throws, mirroring the tolerant manifest/journal readers.
    /// </summary>
    public static ReviewMarker? Read(string planDirectory)
    {
        string path = PathFor(planDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReviewMarker>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write a marker for <paramref name="plan"/> recording the current <see cref="PlanHash"/> and
    /// <paramref name="reviewedAt"/>. Provided for completeness/testing — the production writer is the
    /// <c>/guardrails-review</c> skill. Creates <c>state/</c> if needed.
    /// </summary>
    public static void Write(PlanDefinition plan, DateTimeOffset reviewedAt)
    {
        string path = PathFor(plan.PlanDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = new ReviewMarker
        {
            Version = CurrentVersion,
            ReviewedAt = reviewedAt,
            PlanHash = Journal.PlanHash.Compute(plan)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(marker, WriteOptions));
    }

    /// <summary>
    /// Deterministically classify the review state of <paramref name="plan"/> against its on-disk
    /// marker: <see cref="ReviewState.Missing"/> when no (parseable) marker exists,
    /// <see cref="ReviewState.Stale"/> when the marker's <see cref="PlanHash"/> no longer matches the
    /// plan's current <see cref="PlanHash"/> (the plan's task structure changed since review), and
    /// <see cref="ReviewState.Reviewed"/> when they match. Pure compare — no model in the loop.
    /// </summary>
    public static ReviewEvaluation Evaluate(PlanDefinition plan)
    {
        ReviewMarker? marker = Read(plan.PlanDirectory);
        if (marker is null || string.IsNullOrWhiteSpace(marker.PlanHash))
        {
            return new ReviewEvaluation(ReviewState.Missing, ReviewedHash: null, CurrentHash: Journal.PlanHash.Compute(plan));
        }

        string current = Journal.PlanHash.Compute(plan);
        return string.Equals(marker.PlanHash, current, StringComparison.Ordinal)
            ? new ReviewEvaluation(ReviewState.Reviewed, marker.PlanHash, current)
            : new ReviewEvaluation(ReviewState.Stale, marker.PlanHash, current);
    }
}

/// <summary>The review state of a plan against its marker (SSOT §13).</summary>
public enum ReviewState
{
    /// <summary>No (parseable) review marker exists — the plan was never reviewed (or the marker was not committed).</summary>
    Missing,

    /// <summary>A marker exists but its plan hash no longer matches — the plan changed since review.</summary>
    Stale,

    /// <summary>A marker exists and its plan hash matches the current plan — reviewed and fresh.</summary>
    Reviewed
}

/// <summary>
/// The result of <see cref="ReviewMarker.Evaluate"/>: the <see cref="State"/> plus the reviewed and
/// current <c>sha256:</c> hashes (short forms are surfaced in the GR2025 warning / run nudge).
/// </summary>
/// <param name="State">The review state.</param>
/// <param name="ReviewedHash">The plan hash recorded at review time, or null when missing.</param>
/// <param name="CurrentHash">The plan's current plan hash.</param>
public readonly record struct ReviewEvaluation(ReviewState State, string? ReviewedHash, string CurrentHash)
{
    /// <summary>True when the plan should be nudged (missing or stale) — i.e. NOT freshly reviewed.</summary>
    public bool ShouldWarn => State is ReviewState.Missing or ReviewState.Stale;

    /// <summary>
    /// The one-line, human-actionable nudge for <see cref="ShouldWarn"/> states (shared by the GR2025
    /// validate warning and the run pre-flight nudge), or null when freshly reviewed. Names the
    /// reviewed-vs-current short hash on a stale plan so the change is visible.
    /// </summary>
    public string? NudgeMessage => State switch
    {
        ReviewState.Missing =>
            "this plan hasn't been through /guardrails-review — run it, or pass --skip-review-check to proceed.",
        ReviewState.Stale =>
            $"this plan has changed since /guardrails-review (reviewed {Short(ReviewedHash)}, now {Short(CurrentHash)}) — " +
            "re-run /guardrails-review, or pass --skip-review-check to proceed.",
        _ => null
    };

    /// <summary>A short, display-friendly form of a <c>sha256:</c> hash (the first 12 hex chars).</summary>
    private static string Short(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "(none)";
        }

        string hex = hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash["sha256:".Length..] : hash;
        return hex.Length <= 12 ? hex : hex[..12];
    }
}
