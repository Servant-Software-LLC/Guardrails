using Guardrails.Cli.Ui;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Unit tests for the long-running-guardrail heartbeat (issue #331). No processes, no real timer, no
/// wall-clock waits — the clock is injected and <see cref="GuardrailHeartbeat.Tick"/> is driven directly,
/// so the elapsed/expected/over-budget behavior is asserted deterministically. Lives in Integration.Tests
/// only because that is the project referencing <c>Guardrails.Cli</c>; the tests themselves are fast/pure.
/// </summary>
public sealed class GuardrailHeartbeatTests
{
    private static GuardrailDefinition Guardrail(string name, int? expected) => new()
    {
        Name = name,
        Path = $"/fake/guardrails/{name}.sh",
        Kind = ActionKind.Script,
        ExpectedDurationSeconds = expected
    };

    // ─────────────────────────────────────────────────────────────────────────
    // FormatLine — the pure formatting seam
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatLine_NoExpected_ShowsNameAndElapsedOnly()
    {
        string line = GuardrailHeartbeat.FormatLine("03-bats-suite", TimeSpan.FromSeconds(272), expectedSeconds: null);
        Assert.Equal("guardrail 03-bats-suite: running (4m32s)...", line);
    }

    [Fact]
    public void FormatLine_WithExpected_OnTrack_ShowsElapsedAndExpectedHint()
    {
        // 12m30s elapsed, 15m expected (900s) — well under the over-budget multiple.
        string line = GuardrailHeartbeat.FormatLine("03-bats-suite", TimeSpan.FromSeconds(750), expectedSeconds: 900);
        Assert.Equal("guardrail 03-bats-suite: running (12m30s elapsed, expected ~15m)...", line);
    }

    [Fact]
    public void FormatLine_JustUnderOverBudgetMultiple_IsStillOnTrack()
    {
        // expected 5m (300s); elapsed 14m (840s) < 3× (900s) → NOT flagged.
        string line = GuardrailHeartbeat.FormatLine("03-bats-suite", TimeSpan.FromSeconds(840), expectedSeconds: 300);
        Assert.Equal("guardrail 03-bats-suite: running (14m00s elapsed, expected ~5m)...", line);
        Assert.DoesNotContain("over budget", line);
    }

    [Fact]
    public void FormatLine_PastOverBudgetMultiple_FlagsMayBeStuck()
    {
        // expected 5m (300s); elapsed 16m (960s) ≥ 3× (900s) → over-budget flag.
        string line = GuardrailHeartbeat.FormatLine("03-bats-suite", TimeSpan.FromSeconds(960), expectedSeconds: 300);
        Assert.Contains("16m00s elapsed", line);
        Assert.Contains("expected ~5m", line);
        Assert.Contains("over budget, may be stuck", line);
    }

    [Fact]
    public void FormatLine_SubMinuteExpected_ShownInSeconds()
    {
        string line = GuardrailHeartbeat.FormatLine("01-quick", TimeSpan.FromSeconds(60), expectedSeconds: 45);
        Assert.Equal("guardrail 01-quick: running (1m00s elapsed, expected ~45s)...", line);
    }

    [Fact]
    public void FormatLine_HourScaleExpected_ShownInHoursAndMinutes()
    {
        // expected 1h04m (3840s); elapsed 1h (3600s) < 3× → on track.
        string line = GuardrailHeartbeat.FormatLine("05-e2e", TimeSpan.FromSeconds(3600), expectedSeconds: 3840);
        Assert.Equal("guardrail 05-e2e: running (1h00m elapsed, expected ~1h04m)...", line);
    }

    [Fact]
    public void FormatLine_NonPositiveExpected_TreatedAsNoHint()
    {
        // A non-positive value never reaches here in practice (GR2035 rejects it), but defend anyway:
        // fall back to elapsed-only rather than rendering a nonsensical "expected ~0m".
        string line = GuardrailHeartbeat.FormatLine("01-x", TimeSpan.FromSeconds(90), expectedSeconds: 0);
        Assert.Equal("guardrail 01-x: running (1m30s)...", line);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tick — a guardrail running past the interval emits a heartbeat carrying its name + elapsed
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tick_WhileGuardrailRuns_EmitsHeartbeatCarryingNameAndElapsed()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        using var heartbeat = new GuardrailHeartbeat(lines.Add, () => now);

        heartbeat.GuardrailStarting(Guardrail("03-bats-suite", expected: null));
        now = now.AddMinutes(4).AddSeconds(32); // past the (default) interval
        heartbeat.Tick();

        string line = Assert.Single(lines);
        Assert.Contains("03-bats-suite", line);      // the specific guardrail is named
        Assert.Contains("4m32s", line);              // ...and the line carries elapsed wall-clock
    }

    [Fact]
    public void Tick_MeasuresElapsedFromEachGuardrailsOwnStart()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        using var heartbeat = new GuardrailHeartbeat(lines.Add, () => now);

        heartbeat.GuardrailStarting(Guardrail("01-build", expected: null));
        now = now.AddMinutes(1);
        heartbeat.GuardrailCompleted(Guardrail("01-build", expected: null));

        // The next guardrail's clock starts from ITS start, not the phase start.
        heartbeat.GuardrailStarting(Guardrail("02-suite", expected: 600));
        now = now.AddMinutes(2);
        heartbeat.Tick();

        string line = Assert.Single(lines);
        Assert.Contains("02-suite", line);
        Assert.Contains("2m00s elapsed", line);       // measured from 02-suite's start, not 3m
        Assert.Contains("expected ~10m", line);
    }

    [Fact]
    public void Tick_BeforeAnyGuardrail_And_AfterCompleted_EmitsNothing()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        using var heartbeat = new GuardrailHeartbeat(lines.Add, () => now);

        // No guardrail running yet.
        heartbeat.Tick();
        Assert.Empty(lines);

        // A guardrail that finished before a tick fired → the clock is stopped, no stray heartbeat.
        heartbeat.GuardrailStarting(Guardrail("01-fast", expected: null));
        heartbeat.GuardrailCompleted(Guardrail("01-fast", expected: null));
        now = now.AddMinutes(10);
        heartbeat.Tick();
        Assert.Empty(lines);
    }

    [Fact]
    public void Tick_AfterDispose_EmitsNothing()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        var heartbeat = new GuardrailHeartbeat(lines.Add, () => now);

        heartbeat.GuardrailStarting(Guardrail("01-slow", expected: null));
        heartbeat.Dispose();
        now = now.AddMinutes(30);
        heartbeat.Tick();

        Assert.Empty(lines);
    }
}
