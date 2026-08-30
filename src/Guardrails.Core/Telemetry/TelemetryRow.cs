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
    public const int CurrentSchemaVersion = 1;

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
}
