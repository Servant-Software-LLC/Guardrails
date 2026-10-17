using System.Text;
using System.Text.Json;

namespace Guardrails.Core.Bundle;

/// <summary>
/// The field allow-list projection of SSOT §17.6.5, applied under <c>--without-agent-text</c> to <c>run.json</c>,
/// <c>attempt-provenance.json</c> and gate <c>result.json</c>: ids, statuses, outcomes, attempt numbers, timestamps,
/// durations, exit codes, hashes, and runner and model names survive; every other STRING value becomes
/// <c>[withheld: agent text]</c>. Numbers, booleans and object keys (task ids) are structure and stay.
/// <para>An allow-list, not a deny-list: a text field added to the journal later is withheld until someone decides it
/// is a fact.</para>
/// </summary>
public static class AgentTextProjection
{
    /// <summary>What a withheld field reads.</summary>
    public const string Withheld = "[withheld: agent text]";

    private static readonly HashSet<string> AllowedStringFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "runId", "planHash", "status", "outcome", "attempt", "startedAt", "endedAt", "evaluatedAt", "haltedAt", "at",
        "finishedAt", "processStartedAt", "definitionHash", "definitionHashAtSettle", "preflightsHash", "markerSha",
        "model", "requestedModel", "modelDigest", "gateway", "backendModel", "runner", "kind", "tier", "tierSource",
        "escalatedFrom", "effort", "segmentBranch", "worktreePath", "baseCommit", "phase", "host", "os",
        "harnessVersion", "skillVersion", "bootId", "name", "bucket", "needsHumanKind", "decision", "boundary", "subject",
        "gate", "planBranch", "deliveredToBranch", "deliveryTarget", "logDir", "waveDir", "version", "source",
    };

    /// <summary>Project <paramref name="json"/>; null when it does not parse (the caller excludes the file).</summary>
    public static string? Project(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                Write(document.RootElement, propertyName: null, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        }
    }

    private static void Write(JsonElement element, string? propertyName, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, property.Name, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(item, propertyName, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(propertyName is not null && AllowedStringFields.Contains(propertyName)
                    ? element.GetString()
                    : Withheld);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
