using System.Text.Json;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// The per-wave <c>waves[]</c> journal section (SSOT §7/§14.5/§14.6) round-trips losslessly and is
/// additive: a FLAT plan's journal (no waves) omits the section entirely (backward-compatible with an
/// older reader). The entry/exit markers reuse the plan-phase section shapes.
/// </summary>
public sealed class WaveJournalSchemaTests
{
    private static JournalDocument RoundTrip(JournalDocument doc) =>
        JsonSerializer.Deserialize<JournalDocument>(
            JsonSerializer.Serialize(doc, JournalJson.Options), JournalJson.Options)!;

    [Fact]
    public void WavesSection_RoundTripsLosslessly()
    {
        var doc = new JournalDocument
        {
            RunId = "2026-07-08T00-00-00Z-abcd",
            PlanHash = "sha256:plan",
            Waves = new Dictionary<string, WaveJournalEntry>
            {
                ["wave-01-scaffold"] = new WaveJournalEntry
                {
                    Status = WaveStatus.Completed,
                    DefinitionHash = "sha256:wave1",
                    Entry = new PlanPreflightsSection
                    {
                        Status = PlanPhaseStatus.Passed,
                        PlanHash = "sha256:wave1",
                        EvaluatedAt = DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
                        Checks = [new PlanPreflightCheck { Name = "01-prior-materialized", Passed = true }]
                    },
                    Exit = new PlanGuardrailsSection
                    {
                        Status = PlanPhaseStatus.Passed,
                        PlanHash = "sha256:wave1",
                        FailedChecks = []
                    }
                },
                ["wave-02-build"] = new WaveJournalEntry { Status = WaveStatus.Pending }
            }
        };

        JournalDocument back = RoundTrip(doc);

        Assert.NotNull(back.Waves);
        Assert.Equal(2, back.Waves!.Count);

        WaveJournalEntry w1 = back.Waves["wave-01-scaffold"];
        Assert.Equal(WaveStatus.Completed, w1.Status);
        Assert.Equal("sha256:wave1", w1.DefinitionHash);
        Assert.Equal(PlanPhaseStatus.Passed, w1.Entry!.Status);
        Assert.Equal("01-prior-materialized", Assert.Single(w1.Entry.Checks).Name);
        Assert.Equal(PlanPhaseStatus.Passed, w1.Exit!.Status);

        Assert.Equal(WaveStatus.Pending, back.Waves["wave-02-build"].Status);
        Assert.Null(back.Waves["wave-02-build"].DefinitionHash);
    }

    [Fact]
    public void FlatPlanJournal_OmitsWavesSection()
    {
        var doc = new JournalDocument { RunId = "r", PlanHash = "sha256:p" };
        string json = JsonSerializer.Serialize(doc, JournalJson.Options);

        Assert.DoesNotContain("\"waves\"", json);
        Assert.Null(RoundTrip(doc).Waves);
    }

    [Theory]
    [InlineData(WaveStatus.Pending, "pending")]
    [InlineData(WaveStatus.Running, "running")]
    [InlineData(WaveStatus.Completed, "completed")]
    [InlineData(WaveStatus.NeedsHuman, "needs-human")]
    [InlineData(WaveStatus.Blocked, "blocked")]
    public void WaveStatus_SerializesToKebabString(WaveStatus status, string expected)
    {
        var doc = new JournalDocument
        {
            RunId = "r",
            PlanHash = "sha256:p",
            Waves = new Dictionary<string, WaveJournalEntry> { ["wave-01-x"] = new() { Status = status } }
        };

        string json = JsonSerializer.Serialize(doc, JournalJson.Options);
        Assert.Contains($"\"status\": \"{expected}\"", json);
    }
}
