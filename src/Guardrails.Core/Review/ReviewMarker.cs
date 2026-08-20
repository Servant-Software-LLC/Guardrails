using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Review;

/// <summary>
/// The review marker (SSOT §13, issues #79/#260): a small file <c>state/guardrails-review.json</c> that
/// records a human ran <c>/guardrails-review</c> over the CURRENT plan. It carries a timestamp and the
/// plan's <see cref="PlanDefinitionHash"/> at review time — the plan's whole <b>behavioral</b>
/// definition — so an EDITED plan reads as un-reviewed again.
///
/// <para>It keys on the broad <see cref="PlanDefinitionHash"/> (§7.3), NOT the narrow
/// <see cref="PlanHash"/> (§7): unlike the journal's resume hash, this one covers guardrail, preflight,
/// and action <b>bodies</b>, so editing a guardrail's logic after review (broadening a grep, dropping an
/// assertion, <c>exit 0</c>-ing a real check) re-stales the marker and re-raises GR2025 (issue #260) —
/// bodies are exactly what a review scrutinizes most. The on-disk wire field stays named
/// <c>planHash</c> for back-compat; a pre-#260 marker simply reads <em>stale</em> once via the natural
/// hash mismatch (the broader hash differs) and nudges for one re-review.</para>
///
/// <para>It is <b>committed as part of the reviewed plan</b>, alongside the committed task folder and
/// the review's edits. It is an attestation about the COMMITTED plan content, not about a particular
/// checkout: because it is <see cref="PlanDefinitionHash"/>-keyed it <b>self-invalidates the instant any
/// reviewed file — a <c>task.json</c>, <c>guardrails.json</c>, an <c>action.*</c>, or any
/// guardrail/preflight body or <c>.json</c> sidecar — changes the hash</b> (the review nudge returns),
/// so it can never falsely vouch for changed content. That self-invalidation is exactly what makes
/// committing it safe — a stale marker reads as un-reviewed rather than as a false green.</para>
///
/// <para><b>Scope — per wave on a waved plan</b> (SSOT §13 <em>Multi-wave plans</em>, issues #471/#472/#488).
/// A flat plan has exactly one marker at the plan root, keyed on <see cref="PlanDefinitionHash"/>, unchanged.
/// A WAVED plan carries one marker per wave at <c>&lt;plan&gt;/&lt;wave&gt;/state/guardrails-review.json</c>,
/// keyed on that wave's <see cref="Journal.WaveDefinitionHash"/> — see <see cref="KeyHash"/> for why the
/// shipped wave hash and not a fourth one, and <see cref="EvaluateWave"/> for the back-compat voucher rule.
/// Without this, every successful JIT breakdown of wave N+1 moved <see cref="PlanDefinitionHash"/> and
/// de-attested wave N's review (#488) — a staleness warning that fires on every healthy run is noise, and
/// noise is how a REAL post-review guardrail weakening gets waved through later.</para>
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

    /// <summary>
    /// The current marker schema version — bumped to 2 for the issue-#366 attestation block (§4). It is
    /// written as a SIGNAL, never a gate: readers classify by the presence of the <c>attestation</c>
    /// block and its <c>source</c>, never by this integer (see <see cref="ReviewAttestation.Classify"/>).
    /// </summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Tolerate a malformed attestation block: it deserializes to null (→ classified `legacy`) rather
        // than throwing and losing the whole marker, mirroring the tolerant `Read` (§4 field rules).
        Converters = { new TolerantAttestationConverter() }
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

    /// <summary>
    /// The <see cref="PlanDefinitionHash"/> (<c>sha256:</c>-prefixed) computed at review time. The wire
    /// field keeps the historical name <c>planHash</c> for back-compat (§13); the VALUE is the broad
    /// behavioral-definition hash (§7.3), not the narrow journal <see cref="PlanHash"/>.
    /// </summary>
    [JsonPropertyName("planHash")]
    public string PlanHash { get; init; } = string.Empty;

    /// <summary>
    /// OPTIONAL evidence-hygiene attestation (issue #366, §4): the deterministic evidence class plus
    /// the audit trail (source, self-reported tool/actor, and a review-report pointer). ADDITIVE and
    /// back-compat — absent on a pre-#366 (v1) marker, which classifies
    /// <see cref="EvidenceClass.Legacy"/>. Omitted from the wire when null (F7
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/>) so a legacy marker stays byte-identical to
    /// today. Classification (<see cref="ReviewAttestation.Classify"/>) keys on this block's presence
    /// and its <c>source</c>, NEVER on <see cref="Version"/>.
    /// </summary>
    [JsonPropertyName("attestation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewAttestation? Attestation { get; init; }

    /// <summary>
    /// Absolute path to the marker file for the given ATTESTATION TARGET's folder
    /// (<c>state/guardrails-review.json</c>). The target is the plan root for a flat plan, and a WAVE
    /// folder on a waved plan (SSOT §13/§14.1, issues #472/#488): both layouts already carry a
    /// hash-excluded <c>state/</c> tree, so the same relative path serves both.
    /// </summary>
    public static string PathFor(string targetDirectory) =>
        Path.Combine(Path.GetFullPath(targetDirectory), "state", FileName);

    /// <summary>
    /// Serialize this marker to its wire JSON. The issue-#366 write path bumps <c>version</c> to 2 and
    /// applies <see cref="JsonIgnoreCondition.WhenWritingNull"/> to the new optional members (so a
    /// <c>bare</c> stamp emits no <c>"actor": null</c> / <c>"evidence": null</c> noise), while the
    /// required top-three fields keep their current <see cref="JsonIgnoreCondition.Never"/>
    /// serialization for byte-exact back-compat (§4).
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>
    /// Read the marker for <paramref name="targetDirectory"/> (a plan root or a wave folder), or null when
    /// it is absent or unparseable. A present-but-corrupt marker reads as null (treated as <em>missing</em>
    /// by <see cref="Evaluate"/>) — never throws, mirroring the tolerant manifest/journal readers.
    /// </summary>
    public static ReviewMarker? Read(string targetDirectory)
    {
        string path = PathFor(targetDirectory);
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
    /// The folder whose <c>state/</c> holds the marker for an attestation target: the wave folder when
    /// <paramref name="wave"/> is given, else the plan root. One spelling, so the reader, the writer, and
    /// the <c>--evidence</c> containment check can never disagree about where a target's marker lives.
    /// </summary>
    public static string TargetDirectory(PlanDefinition plan, WaveNode? wave) =>
        wave?.Directory ?? plan.PlanDirectory;

    /// <summary>
    /// The hash a marker for this target KEYS ON — the single decision point behind issues #471/#472/#488.
    /// A flat plan (or a whole-plan stamp) keys on <see cref="Journal.PlanDefinitionHash"/>; a WAVE keys on
    /// the already-shipped <see cref="Journal.WaveDefinitionHash"/> (§14.5).
    ///
    /// <para><b>Why the wave hash and not a fourth hash.</b> SSOT §13's older wording said "that wave's own
    /// <c>PlanDefinitionHash</c>", which taken literally would mint a FOURTH member of the hash family
    /// differing from a shipped one by a single file. <see cref="Journal.WaveDefinitionHash"/> already folds
    /// exactly the wave's authored surface (its tasks' <c>TaskDefinitionHash</c> values + the wave's
    /// <c>guardrails/**</c> and <c>preflights/**</c>) and already EXCLUDES the shared plan-root
    /// <c>guardrails.json</c> for precisely the reason Open Decision C gives — "a config edit must not
    /// re-stale every already-run upstream wave". <c>Scheduler.EscalateReviewGate</c> already uses it as a
    /// review-gate escalation's <c>DefinitionHash</c>, so the precedent that this is the right key for a
    /// wave-scoped REVIEW concern is already in the code.</para>
    ///
    /// <para><b>Accepted residual (design 20 §8.3).</b> <see cref="Journal.WaveDefinitionHash"/> also folds
    /// the wave's <c>brief.md</c>, which <see cref="Journal.PlanDefinitionHash"/> deliberately excludes as
    /// breakdown INPUT. So editing a brief after review re-stales THAT wave's marker. Accepted: it is a
    /// HUMAN edit to a file inside the wave (the whole complaint in #471/#488 is staling from a MACHINE side
    /// effect), it errs toward under-attestation, and it costs far less than the drift risk of a fourth hash.
    /// <b>Flip condition:</b> if brief edits on reviewed waves become a routine source of GR2025 noise,
    /// split a <c>WaveReviewHash</c> that omits the brief and pin both against each other in one test.</para>
    /// </summary>
    public static string KeyHash(PlanDefinition plan, WaveNode? wave) =>
        wave is null ? Journal.PlanDefinitionHash.Compute(plan) : Journal.WaveDefinitionHash.Compute(wave);

    /// <summary>
    /// Write a marker for an attestation target — <paramref name="plan"/>, or one <paramref name="wave"/>
    /// of it — recording <see cref="KeyHash"/> and <paramref name="reviewedAt"/>. The production writer is
    /// <c>guardrails mark-reviewed</c> (invoked by the <c>/guardrails-review</c> skill), which writes the
    /// marker itself so it can stamp the #366 attestation block; this overload is the plain form.
    /// Creates <c>state/</c> if needed.
    /// </summary>
    public static void Write(PlanDefinition plan, DateTimeOffset reviewedAt, WaveNode? wave = null)
    {
        string path = PathFor(TargetDirectory(plan, wave));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = new ReviewMarker
        {
            Version = CurrentVersion,
            ReviewedAt = reviewedAt,
            PlanHash = KeyHash(plan, wave)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(marker, WriteOptions));
    }

    /// <summary>
    /// Deterministically classify the review state of <paramref name="plan"/> AS A WHOLE against its
    /// plan-root marker: <see cref="ReviewState.Missing"/> when no (parseable) marker exists,
    /// <see cref="ReviewState.Stale"/> when the marker's recorded hash no longer matches the plan's
    /// current <see cref="Journal.PlanDefinitionHash"/> (any reviewed file — including a guardrail/
    /// preflight/action body — changed since review), and <see cref="ReviewState.Reviewed"/> when they
    /// match. Pure compare — no model in the loop.
    ///
    /// <para>This is the WHOLE-PLAN evaluation and is unchanged. On a FLAT plan it is the only one there
    /// is. On a WAVED plan it is no longer what gets surfaced (see <see cref="EvaluateAll"/>): it survives
    /// as the back-compat VOUCHER consulted by <see cref="EvaluateWave"/>.</para>
    /// </summary>
    public static ReviewEvaluation Evaluate(PlanDefinition plan)
    {
        ReviewMarker? marker = Read(plan.PlanDirectory);
        if (marker is null || string.IsNullOrWhiteSpace(marker.PlanHash))
        {
            return new ReviewEvaluation(ReviewState.Missing, ReviewedHash: null, CurrentHash: Journal.PlanDefinitionHash.Compute(plan));
        }

        string current = Journal.PlanDefinitionHash.Compute(plan);
        return string.Equals(marker.PlanHash, current, StringComparison.Ordinal)
            ? new ReviewEvaluation(ReviewState.Reviewed, marker.PlanHash, current)
            : new ReviewEvaluation(ReviewState.Stale, marker.PlanHash, current);
    }

    /// <summary>
    /// Classify ONE WAVE's review state (SSOT §13 <em>Multi-wave plans</em>, issues #471/#472/#488) against
    /// <c>&lt;plan&gt;/&lt;wave&gt;/state/guardrails-review.json</c>, keyed on <see cref="KeyHash"/> — that
    /// wave's <see cref="Journal.WaveDefinitionHash"/>.
    ///
    /// <para><b>This is the fix.</b> <see cref="Journal.PlanDefinitionHash"/> folds EVERY wave's
    /// <c>guardrails/**</c> and <c>preflights/**</c> (§7.3 step 5, #386), and a JIT breakdown authors
    /// exactly those folders for wave N+1 — so with a single plan-level marker, every SUCCESSFUL breakdown
    /// de-attested wave N, which was reviewed, stamped, run, green, and completely unchanged (#488). A
    /// wave-scoped write now moves only that wave's hash and touches only that wave's marker.</para>
    ///
    /// <para><b>Back-compat fallback (design 20 §8.4).</b> A wave with NO wave marker reads
    /// <see cref="ReviewState.Reviewed"/> iff the plan-level marker exists AND is FRESH (its recorded hash
    /// equals the current <see cref="Journal.PlanDefinitionHash"/>). That is honest rather than lenient: a
    /// plan-level marker can only be fresh if nothing in the plan has changed since it was stamped, which is
    /// precisely when it is entitled to vouch. The moment any wave is authored or edited the plan marker
    /// goes stale and every wave falls through to its own marker (missing ⇒ nudge) — so today's committed
    /// waved plans do not all light up, and no wave is ever vouched for by a marker that has itself
    /// moved.</para>
    /// </summary>
    public static ReviewEvaluation EvaluateWave(PlanDefinition plan, WaveNode wave)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return EvaluateWave(wave, new Lazy<ReviewEvaluation>(() => Evaluate(plan)));
    }

    /// <summary>
    /// <see cref="EvaluateWave(PlanDefinition, WaveNode)"/> with the plan-level voucher supplied lazily, so
    /// a whole-plan sweep computes <see cref="Journal.PlanDefinitionHash"/> at most ONCE (it re-reads every
    /// file in the plan) and not at all when every wave carries its own marker.
    /// </summary>
    private static ReviewEvaluation EvaluateWave(WaveNode wave, Lazy<ReviewEvaluation> planLevel)
    {
        ArgumentNullException.ThrowIfNull(wave);

        // The wave arm of KeyHash — the same value `mark-reviewed` stamps for this target.
        string current = Journal.WaveDefinitionHash.Compute(wave);
        ReviewMarker? marker = Read(wave.Directory);
        if (marker is not null && !string.IsNullOrWhiteSpace(marker.PlanHash))
        {
            return string.Equals(marker.PlanHash, current, StringComparison.Ordinal)
                ? new ReviewEvaluation(ReviewState.Reviewed, marker.PlanHash, current, wave.Dir)
                : new ReviewEvaluation(ReviewState.Stale, marker.PlanHash, current, wave.Dir);
        }

        // No wave marker — a FRESH plan-level marker still vouches; a stale one vouches for nothing.
        return planLevel.Value.State == ReviewState.Reviewed
            ? new ReviewEvaluation(ReviewState.Reviewed, planLevel.Value.ReviewedHash, current, wave.Dir)
            : new ReviewEvaluation(ReviewState.Missing, ReviewedHash: null, current, wave.Dir);
    }

    /// <summary>
    /// Every attestation evaluation that should be SURFACED for <paramref name="plan"/> — the one contract
    /// the <c>validate</c> and <c>run</c> nudges share.
    ///
    /// <list type="bullet">
    ///   <item><b>Flat plan</b> (no waves): exactly one whole-plan <see cref="Evaluate"/> — byte-for-byte
    ///     today's behaviour.</item>
    ///   <item><b>Waved plan</b>: one <see cref="EvaluateWave"/> per wave, and the whole-plan evaluation is
    ///     NOT surfaced. Emitting both would re-introduce #488 verbatim: authoring wave N+1 moves
    ///     <see cref="Journal.PlanDefinitionHash"/>, so a plan-level nudge would fire on every healthy JIT
    ///     run and the signal would die of noise. The plan-level marker keeps exactly one job — the fresh
    ///     voucher of <see cref="EvaluateWave"/>.</item>
    ///   <item><b>Un-authored (JIT stub) waves are SKIPPED</b> — a wave with no tasks and no wave-level
    ///     gates has nothing a review could attest, and nudging to review a wave that does not exist yet is
    ///     the wolf-cry this change exists to stop. The nudge appears the moment the breakdown authors it,
    ///     which is exactly the JIT boundary §13 promises (and the same boundary
    ///     <c>Scheduler.EscalateReviewGate</c> already escalates at, on the same hash).</item>
    /// </list>
    ///
    /// <para><b>Known residual — the plan SHELL, once waves are stamped individually.</b> The plan-root
    /// <c>guardrails.json</c>, <c>guardrails/**</c> and <c>preflights/**</c> are folded by no wave hash. While
    /// a waved plan is attested ONLY at plan level, a shell edit still surfaces — it stales the plan marker,
    /// and every wave then falls through to <see cref="ReviewState.Missing"/>. But once a wave carries its own
    /// marker, an edit to the plan-root gate bodies re-stales nothing. It is a real, named gap: closing it
    /// needs either a plan-shell hash — a FOURTH hash, deliberately rejected here — or per-wave re-staling on
    /// a shell edit, which is the upstream-wave re-staling Open Decision C forbids. <b>Flip condition:</b> if
    /// a post-review edit to a waved plan's ROOT gate bodies shows up as a real weakening vector, split a
    /// <c>PlanShellDefinitionHash</c> (config + plan-root gates only) and key the plan-level marker on it for
    /// waved plans, keeping the per-wave markers exactly as they are.</para>
    /// </summary>
    public static IReadOnlyList<ReviewEvaluation> EvaluateAll(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Waves.Count == 0)
        {
            return [Evaluate(plan)];
        }

        // One lazy plan-level voucher for the whole sweep: PlanDefinitionHash re-reads every file in the
        // plan, and it is needed only for waves that carry no marker of their own.
        var planLevel = new Lazy<ReviewEvaluation>(() => Evaluate(plan));
        return plan.Waves
            .Where(HasAuthoredContent)
            .Select(wave => EvaluateWave(wave, planLevel))
            .ToList();
    }

    /// <summary>
    /// True when a wave carries something a review could actually attest — any task, or any wave-level
    /// exit/entry gate. False for a JIT stub (a <c>brief.md</c> and an empty <c>tasks/</c>), which is
    /// skipped by <see cref="EvaluateAll"/>.
    /// </summary>
    private static bool HasAuthoredContent(WaveNode wave) =>
        wave.Tasks.Count > 0 || wave.Guardrails.Count > 0 || wave.Preflights.Count > 0;

    /// <summary>
    /// Read-tolerant converter for the optional <see cref="Attestation"/> block: a well-formed block
    /// deserializes normally, but a MALFORMED one (e.g. <c>evidence</c> a string where an object is
    /// expected) yields <c>null</c> rather than throwing and taking the whole marker down with it —
    /// so a marker with the three top fields intact stays readable and classifies
    /// <see cref="EvidenceClass.Legacy"/> (§4 field rules; mirrors the tolerant <see cref="Read"/>).
    /// Registered only in <see cref="ReadOptions"/>; the write path (<see cref="WriteOptions"/>) serializes
    /// the record directly so the per-member <see cref="JsonIgnoreCondition.WhenWritingNull"/> rules apply.
    /// </summary>
    private sealed class TolerantAttestationConverter : JsonConverter<ReviewAttestation>
    {
        // A converter-free options instance for the inner (de)serialize — using ReadOptions here would
        // recurse into this same converter. Case-insensitive to match the tolerant reader.
        private static readonly JsonSerializerOptions Plain = new() { PropertyNameCaseInsensitive = true };

        public override ReviewAttestation? Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                return document.RootElement.Deserialize<ReviewAttestation>(Plain);
            }
            catch (JsonException)
            {
                // Malformed block — tolerate it to null (→ `legacy`) rather than fail the marker read.
                return null;
            }
        }

        public override void Write(
            Utf8JsonWriter writer, ReviewAttestation value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, Plain);
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
/// Which command surface is emitting the review nudge. It selects the REMEDIATION clause, because the
/// two surfaces do not offer the same escape hatch: <c>--skip-review-check</c> exists only on
/// <c>run</c>, so telling a <c>validate</c> user to pass it printed an instruction that errors with
/// "Unrecognized command or argument" when followed (issue #410).
/// </summary>
public enum ReviewNudgeSurface
{
    /// <summary>
    /// <c>guardrails validate</c>. Advisory only — the warning never fails the command, so there is
    /// nothing to suppress and no <c>--skip-review-check</c> flag to offer.
    /// </summary>
    Validate,

    /// <summary><c>guardrails run</c>, whose pre-flight nudge IS suppressible with <c>--skip-review-check</c>.</summary>
    Run
}

/// <summary>
/// The result of <see cref="ReviewMarker.Evaluate"/>: the <see cref="State"/> plus the reviewed and
/// current <c>sha256:</c> hashes (short forms are surfaced in the GR2025 warning / run nudge).
/// </summary>
/// <param name="State">The review state.</param>
/// <param name="ReviewedHash">The hash recorded at review time, or null when missing.</param>
/// <param name="CurrentHash">The target's current hash (plan or wave — see <paramref name="WaveDir"/>).</param>
/// <param name="WaveDir">
/// The wave directory this evaluation is scoped to (issues #472/#488), or null for a whole-plan
/// evaluation. When set, the hashes are that wave's <see cref="Journal.WaveDefinitionHash"/> and the nudge
/// names the wave plus the wave-scoped remedy — an operator must be able to tell WHICH wave is unreviewed,
/// and be handed a command that stamps only that wave.
/// </param>
public readonly record struct ReviewEvaluation(
    ReviewState State, string? ReviewedHash, string CurrentHash, string? WaveDir = null)
{
    /// <summary>True when the plan should be nudged (missing or stale) — i.e. NOT freshly reviewed.</summary>
    public bool ShouldWarn => State is ReviewState.Missing or ReviewState.Stale;

    /// <summary>
    /// The one-line, human-actionable nudge for <see cref="ShouldWarn"/> states (shared by the GR2025
    /// validate warning and the run pre-flight nudge), or null when freshly reviewed. Names the
    /// reviewed-vs-current short hash on a stale plan so the change is visible.
    ///
    /// <para>The DIAGNOSIS half is identical on both surfaces; only the remediation differs, because
    /// only <c>run</c> has a <c>--skip-review-check</c> flag (issue #410 — the shared string used to
    /// recommend that flag to <c>validate</c> users, whose shell then rejected it).</para>
    /// </summary>
    /// <param name="surface">The command emitting the nudge; selects the remediation clause.</param>
    public string? NudgeMessage(ReviewNudgeSurface surface) => State switch
    {
        ReviewState.Missing =>
            $"{Subject} hasn't been through /guardrails-review — run it{Remedy(surface)}",
        ReviewState.Stale =>
            $"{Subject} has changed since /guardrails-review (reviewed {Short(ReviewedHash)}, now {Short(CurrentHash)}) — " +
            $"re-run it{Remedy(surface)}",
        _ => null
    };

    /// <summary>
    /// What this evaluation is ABOUT — "this plan", or the named wave on a waved plan. On a waved plan the
    /// nudge is per wave (§13), so a bare "this plan" would leave the operator hunting for which of five
    /// waves it means, and would misdescribe a warning that says nothing about the other four.
    /// </summary>
    private string Subject => WaveDir is null ? "this plan" : $"wave '{WaveDir}'";

    /// <summary>
    /// The surface-specific remediation tail. <c>run</c> keeps the suppression flag it actually has;
    /// <c>validate</c> is pointed at the real remedy instead — <c>guardrails mark-reviewed</c>, the
    /// writer half that clears GR2025 — plus the fact that this warning does not fail the command,
    /// so there is nothing to suppress in the first place.
    /// </summary>
    private string Remedy(ReviewNudgeSurface surface) => surface switch
    {
        ReviewNudgeSurface.Run => ", or pass --skip-review-check to proceed.",
        // The wave form spells the wave into the command, because `mark-reviewed <plan>` would stamp the
        // WHOLE plan — over-attesting every other wave — which is the mistake #472 forced on reviewers when
        // the wave-scoped invocation did not work.
        _ => WaveDir is null
            ? ", then record it with `guardrails mark-reviewed`. This warning is advisory — validate still succeeds."
            : $", then record it with `guardrails mark-reviewed <plan>/{WaveDir}` (that wave only). " +
              "This warning is advisory — validate still succeeds."
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
