namespace Guardrails.Core.Telemetry;

/// <summary>
/// One row of the local telemetry corpus (charter §9, <c>model-evidence-and-graduation</c>) — one
/// attempt's route, cost, timing and outcome, appended once to an append-only JSONL file under a
/// machine-scoped corpus root (<see cref="TelemetryCorpusStore"/>).
///
/// <para><b>STUB (#535, task 01).</b> This is the shape the journal ETL (task 05) writes into and the
/// report (task 08) reads back out of; nothing here computes anything. <see cref="SchemaVersion"/> exists
/// because the corpus outlives any one build — a row written by an older harness must say which shape it
/// is rather than be silently misread by a newer reader.</para>
/// </summary>
public sealed record TelemetryRow
{
    /// <summary>The current row shape. Bump whenever a field is added, renamed, or reinterpreted.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The shape of this row — see <see cref="CurrentSchemaVersion"/>.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The run this attempt belongs to — one leg of the idempotency key (charter §9).</summary>
    public required string RunId { get; init; }

    /// <summary>The task's folder name within the plan — the second leg of the idempotency key.</summary>
    public required string TaskId { get; init; }

    /// <summary>1-based attempt number within the task — the third leg of the idempotency key.</summary>
    public required int Attempt { get; init; }

    /// <summary>UTC start time of this attempt. Also what <see cref="TelemetryCorpusStore"/> rotates the file on.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>UTC end time of this attempt.</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>The attempt's terminal outcome, as its wire token (e.g. <c>succeeded</c>, <c>guardrail-failed</c>).</summary>
    public required string Outcome { get; init; }

    /// <summary>The fully resolved model the attempt ran on. Null for a script attempt.</summary>
    public string? Model { get; init; }

    /// <summary>The resolved <c>promptRunners</c> block name. Null for a script attempt.</summary>
    public string? Runner { get; init; }

    /// <summary>The resolved block's <c>kind</c> wire token (e.g. <c>claude</c>, <c>openai-compat</c>). Null for a script attempt.</summary>
    public string? Kind { get; init; }

    /// <summary>The rung the attempt resolved on (<c>easy</c>|<c>medium</c>|<c>hard</c>). Absent for a legacy-fallback or pinned route.</summary>
    public string? Tier { get; init; }

    /// <summary>Which site supplied the rung (<c>task</c>|<c>plan-default</c>|<c>override</c>). Absent alongside <see cref="Tier"/>.</summary>
    public string? TierSource { get; init; }

    /// <summary>The reasoning effort the attempt ran at. Absent when the runner used its own default.</summary>
    public string? Effort { get; init; }

    /// <summary>
    /// What the attempt cost, or null when the runner never reported a cost — NOT the same claim as a
    /// recorded <c>0</c> (charter §6; the same null-versus-zero distinction
    /// <c>Guardrails.Core.Journal.JournalTierSpend</c> already draws). Independent of
    /// <see cref="InputTokens"/>/<see cref="OutputTokens"/>: a costless local provider reports volume and
    /// no money, a runner that reports no usage reports money and no volume.
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Prompt input tokens, or null when the runner never reported usage. Independently nullable from <see cref="CostUsd"/>.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Prompt output tokens, or null when the runner never reported usage. Independently nullable from <see cref="CostUsd"/>.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>The repo this attempt ran in — a recorded dimension, never a pooling key (charter §9).</summary>
    public required string Repo { get; init; }

    /// <summary>
    /// The task's fingerprint bucket (plan 30 §3.2) — a fact about the task's <c>writeScope</c> roots and
    /// guardrail archetypes at attempt time (e.g. <c>test-authoring</c>, <c>implementation</c>,
    /// <c>structural</c>, <c>code+tests</c>, <c>documentation</c>, <c>no-write</c>), never a fact read off
    /// the task's name — the report's own legend forbids that reading. Null for a row written before this
    /// column existed, or if the bucket could not be classified. What it does NOT claim: it says nothing
    /// about the task's difficulty, which is <see cref="Tier"/>, a separate column.
    /// </summary>
    public string? Bucket { get; init; }

    /// <summary>
    /// The provider's model digest (plan 30 §3.3) — distinct from <see cref="Model"/>'s tag: its whole
    /// purpose is to catch a provider that swaps the weights under a stable tag, so a re-quantized local
    /// model must not be pooled with the original as one sample. A Claude row's digest is PERMANENTLY
    /// null — the Claude CLI stream carries a model tag and no fingerprint at all;
    /// <c>ClaudeStreamParser</c> extracts <c>num_turns</c>, usage, cost and <c>model</c>, nothing else. An
    /// <c>openai-compat</c> row carries a digest only where the engine volunteers
    /// <c>system_fingerprint</c>, which many do not. Null therefore means "the provider exposed none", NOT
    /// "the harness lost it".
    /// </summary>
    public string? ModelDigest { get; init; }

    /// <summary>Turns the attempt used, or null when the runner never reported it (charter §6 null-versus-zero; §15.2).</summary>
    public int? Turns { get; init; }

    /// <summary>The action phase's wall time in milliseconds, or null when it was never measured.</summary>
    public long? ActionMs { get; init; }

    /// <summary>The guardrail phase's wall time in milliseconds, or null when it was never measured.</summary>
    public long? GuardrailMs { get; init; }

    /// <summary>
    /// True when the attempt's route resolved warm, false when it resolved cold. Null means no route
    /// resolved at all — a script attempt — which is a different claim from <c>false</c> ("the route was
    /// cold").
    /// </summary>
    public bool? RouteWarm { get; init; }

    /// <summary>The machine hostname the attempt ran on, or null when never recorded.</summary>
    public string? Host { get; init; }

    /// <summary>The operating system the attempt ran on, or null when never recorded.</summary>
    public string? Os { get; init; }

    /// <summary>The machine's logical CPU count, or null when never recorded.</summary>
    public int? CpuCount { get; init; }

    /// <summary>
    /// The machine's total memory in bytes, or null when never recorded — the unified memory that
    /// distinguishes what quantization a given model tag actually ran at on Apple silicon.
    /// </summary>
    public long? TotalMemoryBytes { get; init; }

    /// <summary>The effective concurrency the run used, or null when never recorded.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>The harness version that produced this row, or null when never recorded.</summary>
    public string? HarnessVersion { get; init; }

    /// <summary>The skill version the attempt ran under, or null when never recorded.</summary>
    public string? SkillVersion { get; init; }
}
