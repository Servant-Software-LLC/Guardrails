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
        return options;
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
