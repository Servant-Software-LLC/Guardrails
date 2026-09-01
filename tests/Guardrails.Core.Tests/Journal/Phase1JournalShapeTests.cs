using System.Text.Json;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`: introducing that nested namespace
// anywhere in this assembly shadows the production `Guardrails.Core.Journal` namespace for every
// unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves an enclosing
// nested namespace before a `using`-imported one) — see JudgeSpendRecordingTests.cs, which explains
// and follows the same rule.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 30 §3.2/§3.3/§3.4 — the six Phase-1 journal-shape members and their two new records
/// (<see cref="AttemptSegments"/>, <see cref="RunEnvironment"/>). This is a COLLAPSED TDD pair: a
/// record declaration IS the implementation, so there is no stub-versus-real distinction to be red
/// about, and a test that merely constructs an object and asserts nothing meaningful would pass even
/// against a member missing its <c>[JsonIgnore(Condition = WhenWritingNull)]</c> attribute — the one
/// defect a property-exists test cannot see, because <see cref="JournalJson"/> sets
/// <c>DefaultIgnoreCondition = Never</c> and the value still round-trips perfectly either way.
///
/// <para>Every "omitted when null" half below therefore serializes through the REAL
/// <see cref="JournalJson.Options"/> and asserts on the emitted JSON text (the
/// <c>JudgeSpendRecordingTests</c> <see cref="JsonDocument"/>/<c>TryGetProperty</c> idiom), paired with
/// a positive control — the same shape with the value SET — so the absence assertion cannot pass
/// vacuously against a serializer that emitted nothing at all.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class Phase1JournalShapeTests
{
    private static JsonElement ParseRoot(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    // --- 1. Bucket rides TaskJournalEntry ------------------------------------------------------

    [Fact]
    public void BucketRidesTheTaskEntry_AndIsOmittedWhenNull()
    {
        var withBucket = new TaskJournalEntry { Status = JournalTaskStatus.Succeeded, Bucket = "implementation" };
        string jsonWith = JsonSerializer.Serialize(withBucket, JournalJson.Options);
        JsonElement rootWith = ParseRoot(jsonWith);

        Assert.True(rootWith.TryGetProperty("bucket", out JsonElement bucketElement));
        Assert.Equal("implementation", bucketElement.GetString());

        TaskJournalEntry roundTripped = JsonSerializer.Deserialize<TaskJournalEntry>(jsonWith, JournalJson.Options)!;
        Assert.Equal("implementation", roundTripped.Bucket);

        var withoutBucket = new TaskJournalEntry { Status = JournalTaskStatus.Succeeded };
        string jsonWithout = JsonSerializer.Serialize(withoutBucket, JournalJson.Options);
        JsonElement rootWithout = ParseRoot(jsonWithout);

        Assert.False(rootWithout.TryGetProperty("bucket", out _));
    }

    // --- 2. ModelDigest rides AttemptProvenance ------------------------------------------------

    [Fact]
    public void ModelDigestRidesTheProvenance_AndIsOmittedWhenNull()
    {
        var withDigest = new AttemptProvenance { ModelDigest = "sha256:abc123" };
        string jsonWith = JsonSerializer.Serialize(withDigest, JournalJson.Options);
        JsonElement rootWith = ParseRoot(jsonWith);

        Assert.True(rootWith.TryGetProperty("modelDigest", out JsonElement digestElement));
        Assert.Equal("sha256:abc123", digestElement.GetString());

        AttemptProvenance roundTripped = JsonSerializer.Deserialize<AttemptProvenance>(jsonWith, JournalJson.Options)!;
        Assert.Equal("sha256:abc123", roundTripped.ModelDigest);

        var withoutDigest = new AttemptProvenance();
        string jsonWithout = JsonSerializer.Serialize(withoutDigest, JournalJson.Options);
        JsonElement rootWithout = ParseRoot(jsonWithout);

        Assert.False(rootWithout.TryGetProperty("modelDigest", out _));
    }

    // --- 3. RouteWarm rides AttemptProvenance --------------------------------------------------

    [Fact]
    public void RouteWarmRidesTheProvenance_AndIsOmittedWhenNull()
    {
        var warm = new AttemptProvenance { RouteWarm = true };
        string jsonWarm = JsonSerializer.Serialize(warm, JournalJson.Options);
        JsonElement rootWarm = ParseRoot(jsonWarm);

        Assert.True(rootWarm.TryGetProperty("routeWarm", out JsonElement warmElement));
        Assert.True(warmElement.GetBoolean());

        AttemptProvenance roundTrippedWarm = JsonSerializer.Deserialize<AttemptProvenance>(jsonWarm, JournalJson.Options)!;
        Assert.True(roundTrippedWarm.RouteWarm);

        var cold = new AttemptProvenance { RouteWarm = false };
        string jsonCold = JsonSerializer.Serialize(cold, JournalJson.Options);
        JsonElement rootCold = ParseRoot(jsonCold);

        Assert.True(rootCold.TryGetProperty("routeWarm", out JsonElement coldElement));
        Assert.False(coldElement.GetBoolean());

        AttemptProvenance roundTrippedCold = JsonSerializer.Deserialize<AttemptProvenance>(jsonCold, JournalJson.Options)!;
        Assert.False(roundTrippedCold.RouteWarm);

        var unknown = new AttemptProvenance();
        string jsonUnknown = JsonSerializer.Serialize(unknown, JournalJson.Options);
        JsonElement rootUnknown = ParseRoot(jsonUnknown);

        Assert.False(rootUnknown.TryGetProperty("routeWarm", out _));
    }

    // --- 4. Turns rides AttemptRecord ----------------------------------------------------------

    [Fact]
    public void TurnsRideTheAttemptRecord_AndAreOmittedWhenNull()
    {
        var withTurns = NewAttemptRecord() with { Turns = 7 };
        string jsonWith = JsonSerializer.Serialize(withTurns, JournalJson.Options);
        JsonElement rootWith = ParseRoot(jsonWith);

        Assert.True(rootWith.TryGetProperty("turns", out JsonElement turnsElement));
        Assert.Equal(7, turnsElement.GetInt32());

        AttemptRecord roundTripped = JsonSerializer.Deserialize<AttemptRecord>(jsonWith, JournalJson.Options)!;
        Assert.Equal(7, roundTripped.Turns);

        var withoutTurns = NewAttemptRecord();
        string jsonWithout = JsonSerializer.Serialize(withoutTurns, JournalJson.Options);
        JsonElement rootWithout = ParseRoot(jsonWithout);

        Assert.False(rootWithout.TryGetProperty("turns", out _));
    }

    // --- 5. Segments rides AttemptRecord -------------------------------------------------------

    [Fact]
    public void SegmentsRideTheAttemptRecord_AndAreOmittedWhenNull()
    {
        var withSegments = NewAttemptRecord() with
        {
            Segments = new AttemptSegments { ActionMs = 1200, GuardrailMs = 340 }
        };
        string jsonWith = JsonSerializer.Serialize(withSegments, JournalJson.Options);
        JsonElement rootWith = ParseRoot(jsonWith);

        Assert.True(rootWith.TryGetProperty("segments", out JsonElement segmentsElement));
        Assert.Equal(1200, segmentsElement.GetProperty("actionMs").GetInt64());
        Assert.Equal(340, segmentsElement.GetProperty("guardrailMs").GetInt64());

        AttemptRecord roundTripped = JsonSerializer.Deserialize<AttemptRecord>(jsonWith, JournalJson.Options)!;
        Assert.Equal(1200, roundTripped.Segments?.ActionMs);
        Assert.Equal(340, roundTripped.Segments?.GuardrailMs);

        var withoutSegments = NewAttemptRecord();
        string jsonWithout = JsonSerializer.Serialize(withoutSegments, JournalJson.Options);
        JsonElement rootWithout = ParseRoot(jsonWithout);

        Assert.False(rootWithout.TryGetProperty("segments", out _));
    }

    // --- 6. Environment rides JournalDocument --------------------------------------------------

    [Fact]
    public void RunEnvironmentRidesTheDocument_AndIsOmittedWhenNull()
    {
        var environment = new RunEnvironment
        {
            Host = "dev-box",
            Os = "Windows 11",
            CpuCount = 16,
            TotalMemoryBytes = 68_719_476_736,
            MaxParallelism = 4,
            HarnessVersion = "1.0.0-preview.40",
            SkillVersion = "plan-breakdown@3"
        };
        var withEnvironment = NewDocument() with { Environment = environment };
        string jsonWith = JsonSerializer.Serialize(withEnvironment, JournalJson.Options);
        JsonElement rootWith = ParseRoot(jsonWith);

        Assert.True(rootWith.TryGetProperty("environment", out JsonElement envElement));
        Assert.Equal("dev-box", envElement.GetProperty("host").GetString());
        Assert.Equal("Windows 11", envElement.GetProperty("os").GetString());
        Assert.Equal(16, envElement.GetProperty("cpuCount").GetInt32());
        Assert.Equal(68_719_476_736, envElement.GetProperty("totalMemoryBytes").GetInt64());
        Assert.Equal(4, envElement.GetProperty("maxParallelism").GetInt32());
        Assert.Equal("1.0.0-preview.40", envElement.GetProperty("harnessVersion").GetString());
        Assert.Equal("plan-breakdown@3", envElement.GetProperty("skillVersion").GetString());

        JournalDocument roundTripped = JsonSerializer.Deserialize<JournalDocument>(jsonWith, JournalJson.Options)!;
        Assert.Equal("dev-box", roundTripped.Environment?.Host);
        Assert.Equal("Windows 11", roundTripped.Environment?.Os);
        Assert.Equal(16, roundTripped.Environment?.CpuCount);
        Assert.Equal(68_719_476_736, roundTripped.Environment?.TotalMemoryBytes);
        Assert.Equal(4, roundTripped.Environment?.MaxParallelism);
        Assert.Equal("1.0.0-preview.40", roundTripped.Environment?.HarnessVersion);
        Assert.Equal("plan-breakdown@3", roundTripped.Environment?.SkillVersion);

        var withoutEnvironment = NewDocument();
        string jsonWithout = JsonSerializer.Serialize(withoutEnvironment, JournalJson.Options);
        JsonElement rootWithout = ParseRoot(jsonWithout);

        Assert.False(rootWithout.TryGetProperty("environment", out _));
    }

    // --- 7. Every Phase-1 member survives a full round trip together --------------------------

    [Fact]
    public void EveryPhase1MemberRoundTripsThroughJournalJson()
    {
        const string taskId = "02-implement";

        var attempt = new AttemptRecord
        {
            Attempt = 1,
            StartedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 9, 1, 12, 5, 0, TimeSpan.Zero),
            Outcome = AttemptOutcome.Succeeded,
            LogDir = $"logs/run-1/{taskId}/attempt-1",
            Turns = 12,
            Segments = new AttemptSegments { ActionMs = 1500, GuardrailMs = 500 },
            Provenance = new AttemptProvenance { ModelDigest = "sha256:digest-xyz", RouteWarm = true }
        };

        var taskEntry = new TaskJournalEntry
        {
            Status = JournalTaskStatus.Succeeded,
            Bucket = "implementation",
            Attempts = [attempt]
        };

        var environment = new RunEnvironment
        {
            Host = "dev-box",
            Os = "Windows 11",
            CpuCount = 16,
            TotalMemoryBytes = 68_719_476_736,
            MaxParallelism = 4,
            HarnessVersion = "1.0.0-preview.40",
            SkillVersion = "plan-breakdown@3"
        };

        var document = new JournalDocument
        {
            RunId = "run-1",
            PlanHash = "sha256:plan-1",
            Environment = environment,
            Tasks = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal) { [taskId] = taskEntry }
        };

        string json = JsonSerializer.Serialize(document, JournalJson.Options);
        JournalDocument roundTripped = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;

        RunEnvironment? roundTrippedEnvironment = roundTripped.Environment;
        Assert.Equal("dev-box", roundTrippedEnvironment?.Host);
        Assert.Equal("Windows 11", roundTrippedEnvironment?.Os);
        Assert.Equal(16, roundTrippedEnvironment?.CpuCount);
        Assert.Equal(68_719_476_736, roundTrippedEnvironment?.TotalMemoryBytes);
        Assert.Equal(4, roundTrippedEnvironment?.MaxParallelism);
        Assert.Equal("1.0.0-preview.40", roundTrippedEnvironment?.HarnessVersion);
        Assert.Equal("plan-breakdown@3", roundTrippedEnvironment?.SkillVersion);

        TaskJournalEntry roundTrippedTask = roundTripped.Tasks[taskId];
        Assert.Equal("implementation", roundTrippedTask.Bucket);

        AttemptRecord roundTrippedAttempt = Assert.Single(roundTrippedTask.Attempts);
        Assert.Equal(12, roundTrippedAttempt.Turns);
        Assert.Equal(1500, roundTrippedAttempt.Segments?.ActionMs);
        Assert.Equal(500, roundTrippedAttempt.Segments?.GuardrailMs);
        Assert.Equal("sha256:digest-xyz", roundTrippedAttempt.Provenance?.ModelDigest);
        Assert.True(roundTrippedAttempt.Provenance?.RouteWarm);
    }

    private static AttemptRecord NewAttemptRecord() => new()
    {
        Attempt = 1,
        StartedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 9, 1, 12, 5, 0, TimeSpan.Zero),
        Outcome = AttemptOutcome.Succeeded,
        LogDir = "logs/run-1/01-impl/attempt-1"
    };

    private static JournalDocument NewDocument() => new()
    {
        RunId = "run-1",
        PlanHash = "sha256:plan-1"
    };
}
