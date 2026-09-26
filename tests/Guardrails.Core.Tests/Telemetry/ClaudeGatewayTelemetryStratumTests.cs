using System.Text.Json;
using Guardrails.Core.Journal;
using Guardrails.Core.Telemetry;

namespace Guardrails.Core.Tests;

/// <summary>
/// #782 follow-up — the telemetry corpus schema moved to version 4 for <c>gateway</c>/<c>backendModel</c>, a gateway
/// row is its own report stratum even when it maps a Claude model name, and a gateway dispatch that reported no
/// usage never vanishes from a rendered total.
/// </summary>
public sealed class ClaudeGatewayTelemetryStratumTests
{
    private static TelemetryRow Row(string? gateway = null, string? backend = null, string model = "claude-sonnet-4-5") => new()
    {
        SchemaVersion = TelemetryRow.CurrentSchemaVersion,
        RunId = "run-1",
        TaskId = "task-1",
        Attempt = 1,
        StartedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero),
        Outcome = "succeeded",
        Repo = "guardrails",
        Kind = "claude",
        Runner = "claude",
        Model = model,
        Gateway = gateway,
        BackendModel = backend
    };

    [Fact]
    public void SchemaVersion_IsFour_ForTheGatewayColumns() => Assert.Equal(4, TelemetryRow.CurrentSchemaVersion);

    [Fact]
    public void AnOlderRowWithoutTheGatewayColumns_StillReads_AsANonGatewayRow()
    {
        const string v3 = """
            {"schemaVersion":3,"runId":"r","taskId":"t","attempt":1,"startedAt":"2026-09-01T00:00:00+00:00",
             "endedAt":"2026-09-01T00:01:00+00:00","outcome":"succeeded","repo":"g","kind":"claude","runner":"claude",
             "model":"claude-sonnet-4-5","costUsd":0.5}
            """;

        TelemetryRow row = JsonSerializer.Deserialize<TelemetryRow>(v3, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(3, row.SchemaVersion);
        Assert.Null(row.Gateway);
        Assert.Equal("claude/claude/claude-sonnet-4-5", TelemetryFingerprint.Of(row));
    }

    [Fact]
    public void AGatewayRowMappingAClaudeModelName_IsNotPooledWithRealClaudeRows()
    {
        string claude = TelemetryFingerprint.Of(Row());
        string gateway = TelemetryFingerprint.Of(Row("http://127.0.0.1:4000", "http://127.0.0.1:8080 Qwen3.6"));

        Assert.Equal("claude/claude/claude-sonnet-4-5", claude);
        Assert.NotEqual(claude, gateway);
        Assert.Equal(
            "claude/claude/claude-sonnet-4-5 via gateway http://loopback:4000 backend http://127.0.0.1:8080 Qwen3.6",
            gateway);
    }

    [Fact]
    public void GatewayStrata_FoldLoopbackSpellings_AndSplitOnBackend()
    {
        Assert.Equal(
            TelemetryFingerprint.Of(Row("http://127.0.0.1:4000", "b1")),
            TelemetryFingerprint.Of(Row("http://localhost:4000/", "b1")));
        Assert.NotEqual(
            TelemetryFingerprint.Of(Row("http://127.0.0.1:4000", "b1")),
            TelemetryFingerprint.Of(Row("http://127.0.0.1:4000", "b2")));
        Assert.EndsWith("backend unverified", TelemetryFingerprint.Of(Row("http://127.0.0.1:4000", null)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.84, null, 2, "$1.8400 + 2 dispatch(es) without usage (gateway)")]
    [InlineData(1.84, 310_500L, 1, "$1.8400 + 310.5k tok + 1 dispatch(es) without usage (gateway)")]
    [InlineData(null, null, 3, "3 dispatch(es) without usage (gateway)")]
    [InlineData(null, 310_500L, 2, "310.5k tok + 2 dispatch(es) without usage (gateway)")]
    [InlineData(1.84, 310_500L, 0, "$1.8400 + 310.5k tok (gateway)")]
    public void Total_CountsGatewayDispatchesWithoutUsage_NeverDropsThem(
        double? cost, long? tokens, int unreported, string expected) =>
        Assert.Equal(expected, SpendFormat.Total((decimal?)cost, tokens, gatewayDispatchesWithoutUsage: unreported));
}
