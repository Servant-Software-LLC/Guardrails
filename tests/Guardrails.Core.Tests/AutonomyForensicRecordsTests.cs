using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD (red) for the forensic-record substrate of autonomous mode (issue #361 Phase 3, doc 12
/// §6.1/§6.2/§6.3) — the durable audit surface the classifier, blocker-retry, assessment, and
/// escalation-sink tasks all record THROUGH. Two things are proven here:
///
/// <list type="bullet">
///   <item>the DATA is real and back-compatible — the additive <see cref="DecisionEntry"/> fields (§6.2)
///   round-trip through the journal serializer when present, a legacy entry WITHOUT them round-trips and
///   OMITS them (no null noise), and the new <see cref="DecisionTokens"/> exist alongside the shipped
///   tokens; and</item>
///   <item>the BEHAVIOR is not — <see cref="AutonomyJsonl.Append"/> is a throwing stub, so the writer
///   tests fail RED until the real append-only writer lands.</item>
/// </list>
///
/// Serialization mirrors <see cref="WaveJournalSchemaTests"/>: <see cref="JournalJson.Options"/> is the
/// same serializer <see cref="RunJournal"/> persists <c>state/run.json</c> (and its <c>decisions[]</c>)
/// with.
/// </summary>
public sealed class AutonomyForensicRecordsTests : IDisposable
{
    private readonly string _tempDir;

    public AutonomyForensicRecordsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gr-autonomy-forensic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static DecisionEntry RoundTrip(DecisionEntry entry) =>
        JsonSerializer.Deserialize<DecisionEntry>(
            JsonSerializer.Serialize(entry, JournalJson.Options), JournalJson.Options)!;

    // --- §6.2 DecisionEntry additive fields round-trip (real data → green) -------------------------

    [Fact]
    public void DecisionEntry_WithAutonomyFields_RoundTripsThroughJournalSerializer()
    {
        // A needs-human best-guess entry carrying the full §6.2 assessment surface.
        var bestGuess = new DecisionEntry
        {
            Boundary = "task",
            Policy = "auto",
            Decision = DecisionTokens.ProceededBestGuess,
            At = DateTimeOffset.Parse("2026-07-18T14:03:11Z"),
            Subject = "03-wire-config",
            Headline = "needs-human best-guessed below threshold",
            Detail = "Low blast radius; reversible; the deterministic guardrails gate the result.",
            Gate = "needs-human",
            Classification = "judgment-call",
            Criticality = "low",
            Confidence = "high",
            Threshold = "high",
            BestGuess = "System.Text.Json — it is the repo's existing serializer.",
            AssessmentRef = "logs/run-1/autonomy.jsonl"
        };

        string json = JsonSerializer.Serialize(bestGuess, JournalJson.Options);

        // The new fields serialize with their camelCase wire names...
        Assert.Contains("\"gate\": \"needs-human\"", json);
        Assert.Contains("\"classification\": \"judgment-call\"", json);
        Assert.Contains("\"criticality\": \"low\"", json);
        Assert.Contains("\"confidence\": \"high\"", json);
        Assert.Contains("\"threshold\": \"high\"", json);
        Assert.Contains("\"bestGuess\":", json);
        Assert.Contains("\"assessmentRef\":", json);
        // ...and round-trip losslessly (record equality covers every member).
        Assert.Equal(bestGuess, RoundTrip(bestGuess));

        // A hard-blocker-retry entry exercises the int? ledger fields (blockerAttempts/blockerWaitedSeconds).
        var blocker = new DecisionEntry
        {
            Boundary = "task",
            Policy = "auto",
            Decision = DecisionTokens.BlockerRetried,
            At = DateTimeOffset.Parse("2026-07-18T14:05:00Z"),
            Subject = "04-fetch-secrets",
            Headline = "transient blocker resolved within the ceiling",
            Detail = "429 backoff; recovered.",
            Gate = "blocker",
            Classification = "hard-blocker-retryable",
            Threshold = "high",
            BlockerAttempts = 3,
            BlockerWaitedSeconds = 42,
            AssessmentRef = "logs/run-1/autonomy.jsonl"
        };

        Assert.Equal(blocker, RoundTrip(blocker));
        Assert.Equal(3, RoundTrip(blocker).BlockerAttempts);
        Assert.Equal(42, RoundTrip(blocker).BlockerWaitedSeconds);

        // An answer-injected entry exercises the answerRef/answeredBy provenance fields (§6.2).
        var answered = new DecisionEntry
        {
            Boundary = "task",
            Policy = "auto",
            Decision = DecisionTokens.AnswerInjected,
            At = DateTimeOffset.Parse("2026-07-18T15:00:00Z"),
            Subject = "03-wire-config",
            Headline = "resume consumed a firstmate answer",
            Detail = "escalation 7 answered.",
            Gate = "needs-human",
            Classification = "judgment-call",
            AnswerRef = "logs/run-1/escalations/7-needs-human.answer.json",
            AnsweredBy = "firstmate@crew"
        };

        DecisionEntry answeredBack = RoundTrip(answered);
        Assert.Equal(answered, answeredBack);
        Assert.Equal("logs/run-1/escalations/7-needs-human.answer.json", answeredBack.AnswerRef);
        Assert.Equal("firstmate@crew", answeredBack.AnsweredBy);
    }

    [Fact]
    public void LegacyDecisionEntry_WithoutAutonomyFields_RoundTrips_AndOmitsThem()
    {
        // A shipped drift-boundary entry — none of the autonomous-mode fields set (the back-compat case).
        var legacy = new DecisionEntry
        {
            Boundary = "drift",
            Policy = "auto",
            Decision = DecisionTokens.AutoApplied,
            At = DateTimeOffset.Parse("2026-07-18T14:03:11Z"),
            Subject = "01-a, 02-b",
            Headline = "Definition drift auto-resolved (auto): rewound the plan branch and re-running 2 task(s)",
            Detail = "01-a: aaaa1111 -> bbbb2222"
        };

        string json = JsonSerializer.Serialize(legacy, JournalJson.Options);

        // Additive guarantee: the new optional fields are OMITTED entirely (no null noise) so an existing
        // decisions[] consumer sees the byte-identical shipped shape.
        Assert.DoesNotContain("\"gate\"", json);
        Assert.DoesNotContain("\"classification\"", json);
        Assert.DoesNotContain("\"criticality\"", json);
        Assert.DoesNotContain("\"confidence\"", json);
        Assert.DoesNotContain("\"threshold\"", json);
        Assert.DoesNotContain("\"bestGuess\"", json);
        Assert.DoesNotContain("\"blockerAttempts\"", json);
        Assert.DoesNotContain("\"blockerWaitedSeconds\"", json);
        Assert.DoesNotContain("\"assessmentRef\"", json);
        Assert.DoesNotContain("\"answerRef\"", json);
        Assert.DoesNotContain("\"answeredBy\"", json);

        // The required drift/task/wave shape is unchanged and round-trips losslessly.
        DecisionEntry back = RoundTrip(legacy);
        Assert.Equal(legacy, back);
        Assert.Null(back.Gate);
        Assert.Null(back.Criticality);
        Assert.Null(back.BlockerAttempts);
    }

    // --- §6.2 new decision tokens exist as constants (real → green) --------------------------------

    [Fact]
    public void DecisionTokens_NewAutonomyTokens_ExistAlongsideShipped()
    {
        // The shipped tokens are still available as constants.
        Assert.Equal("halted", DecisionTokens.Halted);
        Assert.Equal("prompted-approved", DecisionTokens.PromptedApproved);
        Assert.Equal("prompted-declined", DecisionTokens.PromptedDeclined);
        Assert.Equal("auto-applied", DecisionTokens.AutoApplied);

        // The autonomous-mode tokens (doc 12 §6.2) — asserted against the constant, not a bare literal.
        Assert.Equal("escalated", DecisionTokens.Escalated);
        Assert.Equal("proceeded-best-guess", DecisionTokens.ProceededBestGuess);
        Assert.Equal("proceeded-unreviewed", DecisionTokens.ProceededUnreviewed);
        Assert.Equal("blocker-retried", DecisionTokens.BlockerRetried);
        Assert.Equal("answer-injected", DecisionTokens.AnswerInjected);

        // A DecisionEntry carrying a new token round-trips it as the durable decision string.
        var entry = new DecisionEntry
        {
            Boundary = "wave",
            Policy = "auto",
            Decision = DecisionTokens.Escalated,
            Subject = "wave-03-classify-and-escalate",
            Headline = "criticality >= threshold — escalated to firstmate"
        };
        Assert.Equal(DecisionTokens.Escalated, RoundTrip(entry).Decision);
    }

    // --- §6.3 autonomy.jsonl writer (throwing stub → red) -----------------------------------------

    [Fact]
    public void AutonomyJsonl_Append_WritesOneLinePerGate()
    {
        string logsDir = Path.Combine(_tempDir, "logs");
        const string runId = "2026-07-18T14-03-11Z-a1b2";

        // Two appends ⇒ two lines (append-only). RED: Append throws NotImplementedException today.
        AutonomyJsonl.Append(logsDir, runId, SampleRecord("needs-human", DecisionTokens.ProceededBestGuess));
        AutonomyJsonl.Append(logsDir, runId, SampleRecord("wave-checkpoint", DecisionTokens.Escalated));

        string path = Path.Combine(logsDir, runId, "autonomy.jsonl");
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void AutonomyJsonl_Append_EachLineIsCompactJsonWithGateFields()
    {
        string logsDir = Path.Combine(_tempDir, "logs");
        const string runId = "run-xyz";

        // RED: Append throws today; when implemented, each line is one compact JSON object with §6.3 fields.
        AutonomyJsonl.Append(logsDir, runId, SampleRecord("needs-human", DecisionTokens.ProceededBestGuess));

        string path = Path.Combine(logsDir, runId, "autonomy.jsonl");
        string content = File.ReadAllText(path).TrimEnd('\n', '\r');

        // One compact single line (no embedded newline).
        Assert.DoesNotContain("\n", content);

        using JsonDocument doc = JsonDocument.Parse(content);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("at", out _));
        Assert.Equal("needs-human", root.GetProperty("gate").GetString());
        Assert.Equal("task", root.GetProperty("boundary").GetString());
        Assert.Equal("03-wire-config", root.GetProperty("subject").GetString());
        Assert.Equal("judgment-call", root.GetProperty("classification").GetString());
        Assert.Equal("low", root.GetProperty("criticality").GetString());
        Assert.Equal("high", root.GetProperty("confidence").GetString());
        Assert.Equal("high", root.GetProperty("threshold").GetString());
        Assert.Equal(DecisionTokens.ProceededBestGuess, root.GetProperty("decision").GetString());
        // Gate-specific fields (§6.3).
        Assert.False(string.IsNullOrEmpty(root.GetProperty("question").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("bestGuess").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("rationale").GetString()));
    }

    private static AutonomyRecord SampleRecord(string gate, string decision) => new()
    {
        At = DateTimeOffset.Parse("2026-07-18T14:03:11Z"),
        Gate = gate,
        Boundary = "task",
        Subject = "03-wire-config",
        Classification = "judgment-call",
        Criticality = "low",
        Confidence = "high",
        Threshold = "high",
        Decision = decision,
        Question = "Use System.Text.Json or Newtonsoft for the config reader?",
        BestGuess = "System.Text.Json — it is the repo's existing serializer (see src/**/*.csproj).",
        Rationale = "Low blast radius; reversible; the deterministic guardrails gate the result."
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
