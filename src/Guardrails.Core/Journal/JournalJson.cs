using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardrails.Core.Journal;

/// <summary>
/// Serialization plumbing for <c>run.json</c>: camelCase property names, indented output,
/// and converters mapping the C# enums to the SSOT §7 kebab-case strings
/// (<c>needs-human</c>, <c>invalid-fragment</c>, …). Reads tolerate comments and trailing
/// commas (humans may inspect/patch the journal).
/// </summary>
public static class JournalJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    /// <summary>
    /// The SSOT §7 outcome token for an <see cref="AttemptOutcome"/> (e.g. <c>guardrail-failed</c>).
    /// The single source of truth for the kebab spelling, reused by the JSON converter and by
    /// prompt-context labelling (issue #26).
    /// </summary>
    public static string OutcomeToken(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Succeeded => "succeeded",
        AttemptOutcome.ActionFailed => "action-failed",
        AttemptOutcome.GuardrailFailed => "guardrail-failed",
        AttemptOutcome.Timeout => "timeout",
        AttemptOutcome.OutputCap => "output-cap",
        AttemptOutcome.MaxTurns => "max-turns",
        AttemptOutcome.RateLimited => "rate-limited",
        AttemptOutcome.Cancelled => "cancelled",
        AttemptOutcome.InvalidFragment => "invalid-fragment",
        AttemptOutcome.NeedsHuman => "needs-human",
        AttemptOutcome.PermissionDenied => "permission-denied",
        AttemptOutcome.TaskPreflightFailed => "task-preflight-failed",
        AttemptOutcome.NoRoute => "no-route",
        _ => throw new JsonException($"Unhandled attempt outcome '{outcome}'.")
    };

    /// <summary>
    /// The SSOT §7 status token for a <see cref="PlanPhaseStatus"/> (e.g. <c>plan-preflight-failed</c>).
    /// The single source of truth for the kebab spelling of the top-level plan-phase sections, reused by
    /// the JSON converter (two-scope preflights F9 split).
    /// </summary>
    public static string PlanPhaseToken(PlanPhaseStatus status) => status switch
    {
        PlanPhaseStatus.Passed => "passed",
        PlanPhaseStatus.PlanPreflightFailed => "plan-preflight-failed",
        PlanPhaseStatus.PlanGuardrailFailed => "plan-guardrail-failed",
        _ => throw new JsonException($"Unhandled plan phase status '{status}'.")
    };

    /// <summary>
    /// The SSOT §7 token for a <see cref="RunHaltKind"/> (e.g. <c>wave-entry-gate-failed</c>) — the single
    /// source of truth for the kebab spelling of the top-level <c>halt.kind</c> field (issue #432).
    /// </summary>
    public static string RunHaltToken(RunHaltKind kind) => kind switch
    {
        RunHaltKind.PlanPreflightFailed => "plan-preflight-failed",
        RunHaltKind.WaveEntryGateFailed => "wave-entry-gate-failed",
        RunHaltKind.WaveExitGateFailed => "wave-exit-gate-failed",
        RunHaltKind.PlanGuardrailFailed => "plan-guardrail-failed",
        _ => throw new JsonException($"Unhandled run halt kind '{kind}'.")
    };

    /// <summary>
    /// The SSOT §7 token for a <see cref="DeliveryOutcome"/> (e.g. <c>fast-forwarded</c>,
    /// <c>not-attempted</c>) — the single source of truth for the kebab spelling of
    /// <c>delivery.outcome</c> (issue #542), reused by the JSON converter and by any run-report labelling.
    /// </summary>
    public static string DeliveryOutcomeToken(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.NotAttempted => "not-attempted",
        DeliveryOutcome.FastForwarded => "fast-forwarded",
        DeliveryOutcome.Merged => "merged",
        DeliveryOutcome.Conflict => "conflict",
        DeliveryOutcome.DirtyWorkingTree => "dirty-working-tree",
        DeliveryOutcome.HookRejected => "hook-rejected",
        DeliveryOutcome.BranchMoved => "branch-moved",
        _ => throw new JsonException($"Unhandled delivery outcome '{outcome}'.")
    };

    /// <summary>
    /// The SSOT §7 token for a <see cref="HarnessWriteDisposition"/> (<c>applied</c> | <c>rejected</c> |
    /// <c>denied</c> | <c>not-applied</c> | <c>failed</c>) — the single source of truth for the kebab
    /// spelling of <c>harnessWrite.disposition</c> and of each <c>harnessWrite.entries[].disposition</c>
    /// (issue #532). Explicit rather than <c>Enum.ToString</c> for the same reason
    /// <see cref="TierSourceToken"/> is: <c>not-applied</c> carries a hyphen, and the default
    /// System.Text.Json enum handling would write the ORDINAL, which is worse still for a file humans and
    /// unlinked tooling read.
    /// </summary>
    public static string HarnessWriteDispositionToken(HarnessWriteDisposition disposition) => disposition switch
    {
        HarnessWriteDisposition.Applied => "applied",
        HarnessWriteDisposition.Rejected => "rejected",
        HarnessWriteDisposition.Denied => "denied",
        HarnessWriteDisposition.NotApplied => "not-applied",
        HarnessWriteDisposition.Failed => "failed",
        _ => throw new JsonException($"Unhandled harness-write disposition '{disposition}'.")
    };

    /// <summary>
    /// The SSOT §7 / DoR §12.4 token for a <see cref="Journal.TierSource"/> (<c>task</c> |
    /// <c>plan-default</c> | <c>override</c>) — the single source of truth for the kebab spelling of
    /// <c>provenance.tierSource</c>, reused by the JSON converter and by any run-report labelling
    /// (model tiering #201).
    ///
    /// <para>Explicit rather than <c>Enum.ToString</c> for the same reason
    /// <c>PromptRunnerKinds.Token</c> is: <c>plan-default</c> carries a hyphen, so the C# member name
    /// is not the wire spelling. The default System.Text.Json enum handling would write the ORDINAL,
    /// which is worse still — the journal is read by humans and by tooling that never links against
    /// this assembly.</para>
    /// </summary>
    public static string TierSourceToken(TierSource source) => source switch
    {
        TierSource.Task => "task",
        TierSource.PlanDefault => "plan-default",
        TierSource.Override => "override",
        _ => throw new JsonException($"Unhandled tier source '{source}'.")
    };

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new TaskStatusConverter());
        options.Converters.Add(new AttemptOutcomeConverter());
        options.Converters.Add(new PlanPhaseStatusConverter());
        options.Converters.Add(new WaveStatusConverter());
        options.Converters.Add(new RunHaltKindConverter());
        options.Converters.Add(new TierSourceConverter());
        options.Converters.Add(new DeliveryOutcomeConverter());
        options.Converters.Add(new HarnessWriteDispositionConverter());
        return options;
    }

    /// <summary>Maps <see cref="HarnessWriteDisposition"/> to/from the SSOT §7 <c>harnessWrite</c> strings (issue #532).</summary>
    private sealed class HarnessWriteDispositionConverter : JsonConverter<HarnessWriteDisposition>
    {
        public override HarnessWriteDisposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "applied" => HarnessWriteDisposition.Applied,
                "rejected" => HarnessWriteDisposition.Rejected,
                "denied" => HarnessWriteDisposition.Denied,
                "not-applied" => HarnessWriteDisposition.NotApplied,
                "failed" => HarnessWriteDisposition.Failed,
                _ => throw new JsonException($"Unknown harness-write disposition '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, HarnessWriteDisposition value, JsonSerializerOptions options) =>
            writer.WriteStringValue(HarnessWriteDispositionToken(value));
    }

    /// <summary>
    /// Maps <see cref="Journal.TierSource"/> to/from the DoR §12.4 <c>provenance.tierSource</c> strings
    /// (model tiering #201). Registered for the NON-nullable enum; System.Text.Json wraps it for the
    /// <c>TierSource?</c> property, and the property's <c>WhenWritingNull</c> ignore condition means a
    /// null source is ABSENT from the journal — never the string <c>"null"</c>, and never a
    /// <c>"tierSource": null</c> key on a legacy-fallback or script attempt.
    /// </summary>
    private sealed class TierSourceConverter : JsonConverter<TierSource>
    {
        public override TierSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "task" => TierSource.Task,
                "plan-default" => TierSource.PlanDefault,
                "override" => TierSource.Override,
                _ => throw new JsonException($"Unknown tier source '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, TierSource value, JsonSerializerOptions options) =>
            writer.WriteStringValue(TierSourceToken(value));
    }

    /// <summary>Maps <see cref="RunHaltKind"/> to/from the SSOT §7 <c>halt.kind</c> strings (issue #432).</summary>
    private sealed class RunHaltKindConverter : JsonConverter<RunHaltKind>
    {
        public override RunHaltKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "plan-preflight-failed" => RunHaltKind.PlanPreflightFailed,
                "wave-entry-gate-failed" => RunHaltKind.WaveEntryGateFailed,
                "wave-exit-gate-failed" => RunHaltKind.WaveExitGateFailed,
                "plan-guardrail-failed" => RunHaltKind.PlanGuardrailFailed,
                _ => throw new JsonException($"Unknown run halt kind '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, RunHaltKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(RunHaltToken(value));
    }

    /// <summary>Maps <see cref="DeliveryOutcome"/> to/from the SSOT §7 <c>delivery.outcome</c> strings (issue #542).</summary>
    private sealed class DeliveryOutcomeConverter : JsonConverter<DeliveryOutcome>
    {
        public override DeliveryOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "not-attempted" => DeliveryOutcome.NotAttempted,
                "fast-forwarded" => DeliveryOutcome.FastForwarded,
                "merged" => DeliveryOutcome.Merged,
                "conflict" => DeliveryOutcome.Conflict,
                "dirty-working-tree" => DeliveryOutcome.DirtyWorkingTree,
                "hook-rejected" => DeliveryOutcome.HookRejected,
                "branch-moved" => DeliveryOutcome.BranchMoved,
                _ => throw new JsonException($"Unknown delivery outcome '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, DeliveryOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(DeliveryOutcomeToken(value));
    }

    /// <summary>Maps <see cref="WaveStatus"/> to/from the SSOT §7/§14 wave status strings.</summary>
    private sealed class WaveStatusConverter : JsonConverter<WaveStatus>
    {
        public override WaveStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "pending" => WaveStatus.Pending,
                "running" => WaveStatus.Running,
                "completed" => WaveStatus.Completed,
                "needs-human" => WaveStatus.NeedsHuman,
                "blocked" => WaveStatus.Blocked,
                _ => throw new JsonException($"Unknown wave status '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, WaveStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                WaveStatus.Pending => "pending",
                WaveStatus.Running => "running",
                WaveStatus.Completed => "completed",
                WaveStatus.NeedsHuman => "needs-human",
                WaveStatus.Blocked => "blocked",
                _ => throw new JsonException($"Unhandled wave status '{value}'.")
            });
    }

    /// <summary>Maps <see cref="TaskStatus"/> to/from the SSOT §7 status strings.</summary>
    private sealed class TaskStatusConverter : JsonConverter<TaskStatus>
    {
        public override TaskStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "pending" => TaskStatus.Pending,
                "running" => TaskStatus.Running,
                "succeeded" => TaskStatus.Succeeded,
                "needs-human" => TaskStatus.NeedsHuman,
                "blocked" => TaskStatus.Blocked,
                "failed" => TaskStatus.Failed,
                _ => throw new JsonException($"Unknown task status '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, TaskStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                TaskStatus.Pending => "pending",
                TaskStatus.Running => "running",
                TaskStatus.Succeeded => "succeeded",
                TaskStatus.NeedsHuman => "needs-human",
                TaskStatus.Blocked => "blocked",
                TaskStatus.Failed => "failed",
                _ => throw new JsonException($"Unhandled task status '{value}'.")
            });
    }

    /// <summary>Maps <see cref="AttemptOutcome"/> to/from the SSOT §7 outcome strings.</summary>
    private sealed class AttemptOutcomeConverter : JsonConverter<AttemptOutcome>
    {
        public override AttemptOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "succeeded" => AttemptOutcome.Succeeded,
                "action-failed" => AttemptOutcome.ActionFailed,
                "guardrail-failed" => AttemptOutcome.GuardrailFailed,
                "timeout" => AttemptOutcome.Timeout,
                "output-cap" => AttemptOutcome.OutputCap,
                "max-turns" => AttemptOutcome.MaxTurns,
                "rate-limited" => AttemptOutcome.RateLimited,
                "cancelled" => AttemptOutcome.Cancelled,
                "invalid-fragment" => AttemptOutcome.InvalidFragment,
                "needs-human" => AttemptOutcome.NeedsHuman,
                "permission-denied" => AttemptOutcome.PermissionDenied,
                "task-preflight-failed" => AttemptOutcome.TaskPreflightFailed,
                "no-route" => AttemptOutcome.NoRoute,
                _ => throw new JsonException($"Unknown attempt outcome '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, AttemptOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(OutcomeToken(value));
    }

    /// <summary>Maps <see cref="PlanPhaseStatus"/> to/from the SSOT §7 plan-phase status strings.</summary>
    private sealed class PlanPhaseStatusConverter : JsonConverter<PlanPhaseStatus>
    {
        public override PlanPhaseStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                "passed" => PlanPhaseStatus.Passed,
                "plan-preflight-failed" => PlanPhaseStatus.PlanPreflightFailed,
                "plan-guardrail-failed" => PlanPhaseStatus.PlanGuardrailFailed,
                _ => throw new JsonException($"Unknown plan phase status '{value}'.")
            };
        }

        public override void Write(Utf8JsonWriter writer, PlanPhaseStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(PlanPhaseToken(value));
    }
}
