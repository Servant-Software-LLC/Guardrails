using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests.Telemetry;

/// <summary>
/// Plan 30 §3.2/§3.3/§3.4 — the thirteen Phase-1 columns on <see cref="TelemetryRow"/>. This is a
/// COLLAPSED TDD pair over a pure data model: the record declaration IS the implementation, so there is
/// no stub-versus-real distinction to be red about. That makes the anti-tautology protection here
/// weaker than a stub-based pair's — nothing throws, so a hollow test would still pass. Three of the six
/// behaviours below carry the weight instead: two go through the REAL
/// <see cref="TelemetryCorpusStore.JsonOptions"/> rather than a fresh <see cref="JsonSerializerOptions"/>,
/// and two more read the declaration by REFLECTION — neither a hollow body can fake.
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class CorpusRowShapeTests
{
    private static readonly string[] Phase1ValueTypedColumnNames =
    [
        nameof(TelemetryRow.Turns),
        nameof(TelemetryRow.ActionMs),
        nameof(TelemetryRow.GuardrailMs),
        nameof(TelemetryRow.RouteWarm),
        nameof(TelemetryRow.CpuCount),
        nameof(TelemetryRow.TotalMemoryBytes),
        nameof(TelemetryRow.MaxParallelism)
    ];

    private static readonly string[] AllPhase1ColumnNames =
    [
        nameof(TelemetryRow.Bucket),
        nameof(TelemetryRow.ModelDigest),
        nameof(TelemetryRow.Turns),
        nameof(TelemetryRow.ActionMs),
        nameof(TelemetryRow.GuardrailMs),
        nameof(TelemetryRow.RouteWarm),
        nameof(TelemetryRow.Host),
        nameof(TelemetryRow.Os),
        nameof(TelemetryRow.CpuCount),
        nameof(TelemetryRow.TotalMemoryBytes),
        nameof(TelemetryRow.MaxParallelism),
        nameof(TelemetryRow.HarnessVersion),
        nameof(TelemetryRow.SkillVersion)
    ];

    private static TelemetryRow BaseRow() => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "run-1",
        TaskId = "task-1",
        Attempt = 1,
        StartedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero),
        Outcome = "succeeded",
        Repo = "guardrails"
    };

    [Fact]
    public void EveryPhase1ColumnRoundTripsThroughTheCorpusWireOptions()
    {
        TelemetryRow row = BaseRow() with
        {
            Bucket = "implementation",
            ModelDigest = "sha256:abc123",
            Turns = 7,
            ActionMs = 12345,
            GuardrailMs = 6789,
            RouteWarm = true,
            Host = "dave-mac-studio",
            Os = "macOS 15",
            CpuCount = 16,
            TotalMemoryBytes = 68719476736,
            MaxParallelism = 4,
            HarnessVersion = "1.0.0-preview.40",
            SkillVersion = "3.2.1"
        };

        string json = JsonSerializer.Serialize(row, TelemetryCorpusStore.JsonOptions);
        TelemetryRow roundTripped = JsonSerializer.Deserialize<TelemetryRow>(json, TelemetryCorpusStore.JsonOptions)
            ?? throw new InvalidOperationException("row did not round-trip to a TelemetryRow");

        Assert.Equal(row.Bucket, roundTripped.Bucket);
        Assert.Equal(row.ModelDigest, roundTripped.ModelDigest);
        Assert.Equal(row.Turns, roundTripped.Turns);
        Assert.Equal(row.ActionMs, roundTripped.ActionMs);
        Assert.Equal(row.GuardrailMs, roundTripped.GuardrailMs);
        Assert.Equal(row.RouteWarm, roundTripped.RouteWarm);
        Assert.Equal(row.Host, roundTripped.Host);
        Assert.Equal(row.Os, roundTripped.Os);
        Assert.Equal(row.CpuCount, roundTripped.CpuCount);
        Assert.Equal(row.TotalMemoryBytes, roundTripped.TotalMemoryBytes);
        Assert.Equal(row.MaxParallelism, roundTripped.MaxParallelism);
        Assert.Equal(row.HarnessVersion, roundTripped.HarnessVersion);
        Assert.Equal(row.SkillVersion, roundTripped.SkillVersion);
    }

    [Fact]
    public void AnUnsetPhase1ColumnIsWrittenAsNull_NotOmitted()
    {
        TelemetryRow row = BaseRow();

        string json = JsonSerializer.Serialize(row, TelemetryCorpusStore.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        foreach (string propertyName in AllPhase1ColumnNames)
        {
            string wireName = JsonNamingPolicy.CamelCase.ConvertName(propertyName);

            Assert.True(
                root.TryGetProperty(wireName, out JsonElement value),
                $"expected key '{wireName}' to be present (even as null) — it was omitted entirely");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }

    [Fact]
    public void AV1CorpusLineStillDeserializes_WithThePhase1ColumnsNull()
    {
        const string v1Line = """
            {
                "schemaVersion": 1,
                "runId": "run-old",
                "taskId": "task-old",
                "attempt": 1,
                "startedAt": "2026-01-01T00:00:00+00:00",
                "endedAt": "2026-01-01T00:01:00+00:00",
                "outcome": "succeeded",
                "model": "claude-sonnet-5",
                "runner": "claude",
                "kind": "claude",
                "tier": "medium",
                "tierSource": "task",
                "effort": null,
                "costUsd": 0.42,
                "inputTokens": 1000,
                "outputTokens": 200,
                "repo": "guardrails"
            }
            """;

        TelemetryRow row = JsonSerializer.Deserialize<TelemetryRow>(v1Line, TelemetryCorpusStore.JsonOptions)
            ?? throw new InvalidOperationException("v1 line did not deserialize to a TelemetryRow");

        Assert.Equal(1, row.SchemaVersion);
        Assert.Null(row.Bucket);
        Assert.Null(row.ModelDigest);
        Assert.Null(row.Turns);
        Assert.Null(row.ActionMs);
        Assert.Null(row.GuardrailMs);
        Assert.Null(row.RouteWarm);
        Assert.Null(row.Host);
        Assert.Null(row.Os);
        Assert.Null(row.CpuCount);
        Assert.Null(row.TotalMemoryBytes);
        Assert.Null(row.MaxParallelism);
        Assert.Null(row.HarnessVersion);
        Assert.Null(row.SkillVersion);
    }

    [Fact]
    public void TheSchemaVersionIsBumpedPastOne()
    {
        Assert.True(
            TelemetryRow.CurrentSchemaVersion > 1,
            $"expected CurrentSchemaVersion > 1, was {TelemetryRow.CurrentSchemaVersion}");
    }

    [Fact]
    public void NoPhase1ColumnIsRequired_SoAHistoricalRowStillReads()
    {
        var requiredPhase1Columns = new List<string>();

        foreach (string propertyName in AllPhase1ColumnNames)
        {
            PropertyInfo property = typeof(TelemetryRow).GetProperty(propertyName)
                ?? throw new InvalidOperationException($"TelemetryRow has no property named '{propertyName}'");

            bool isRequired = property.GetCustomAttributes(typeof(RequiredMemberAttribute), inherit: false).Length > 0;
            if (isRequired)
            {
                requiredPhase1Columns.Add(propertyName);
            }
        }

        Assert.True(
            requiredPhase1Columns.Count == 0,
            $"these Phase-1 columns are marked required, which would throw deserializing any row missing them: {string.Join(", ", requiredPhase1Columns)}");
    }

    [Fact]
    public void EveryValueTypedPhase1ColumnIsNullable_SoNoUnreportedFactReadsAsZero()
    {
        var nonNullableColumns = new List<string>();

        foreach (string propertyName in Phase1ValueTypedColumnNames)
        {
            PropertyInfo property = typeof(TelemetryRow).GetProperty(propertyName)
                ?? throw new InvalidOperationException($"TelemetryRow has no property named '{propertyName}'");

            bool isNullable = property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>);

            if (!isNullable)
            {
                nonNullableColumns.Add(propertyName);
            }
        }

        Assert.True(
            nonNullableColumns.Count == 0,
            $"these Phase-1 columns are not Nullable<T>, so an unreported fact would read as 0: {string.Join(", ", nonNullableColumns)}");
    }
}
