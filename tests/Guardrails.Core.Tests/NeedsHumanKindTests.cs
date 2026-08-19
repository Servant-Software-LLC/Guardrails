using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #485 — the ONE decision point for the optional <c>needsHuman.kind</c> claim, plus its journal
/// wire shape. <c>AttemptOutcome.NeedsHuman</c> covers two situations that need OPPOSITE follow-ups
/// ("I cannot complete this work" vs "this guardrail is defective"); <see cref="NeedsHumanKinds"/> is the
/// single place that decides what a raw value means, so "the harness invents no default" is enforceable
/// rather than a convention five call sites can each drift from.
/// </summary>
public sealed class NeedsHumanKindTests
{
    // --- The wire tokens themselves -----------------------------------------------------------

    [Fact]
    public void Constants_AreTheContractTokens()
    {
        // Pinned deliberately: these two strings are the CONTRACT an agent writes into its fragment
        // (SSOT §9) and the harness persists into run.json. A rename is a breaking change.
        Assert.Equal("blocked-work", NeedsHumanKinds.BlockedWork);
        Assert.Equal("defective-guardrail", NeedsHumanKinds.DefectiveGuardrail);
    }

    // --- Parse: the only place "unrecognised ⇒ unclassified" is decided -----------------------

    [Theory]
    [InlineData("blocked-work", "blocked-work")]
    [InlineData("defective-guardrail", "defective-guardrail")]
    public void Parse_RecognisedValue_RoundTripsVerbatim(string raw, string expected) =>
        Assert.Equal(expected, NeedsHumanKinds.Parse(raw));

    [Theory]
    [InlineData(null)]                      // absent — every pre-#485 escalation
    [InlineData("")]                        // present but empty
    [InlineData(" ")]                       // whitespace
    [InlineData("BLOCKED-WORK")]            // wrong casing: an EXACT ordinal match is required
    [InlineData("Defective-Guardrail")]
    [InlineData("blocked-work ")]           // trailing space — not trimmed into a match
    [InlineData("blocked_work")]
    [InlineData("nonsense")]                // a value this harness version does not know
    [InlineData("some-future-kind")]        // forward compatibility: degrade, never guess
    public void Parse_AbsentOrUnrecognised_IsUnclassified_NeverADefault(string? raw) =>
        Assert.Null(NeedsHumanKinds.Parse(raw));

    // --- Terse: the width-scarce half of the SAME token, never a second vocabulary ------------

    [Theory]
    [InlineData("blocked-work", "work")]
    [InlineData("defective-guardrail", "guardrail")]
    public void Terse_IsTheDistinguishingHalfOfTheToken(string raw, string expected) =>
        Assert.Equal(expected, NeedsHumanKinds.Terse(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BLOCKED-WORK")]
    [InlineData("nonsense")]
    public void Terse_AbsentOrUnrecognised_IsNull(string? raw) => Assert.Null(NeedsHumanKinds.Terse(raw));

    [Fact]
    public void Terse_CannotDriftFromParse_ItIsDerivedFromIt()
    {
        // The terse form is the SUFFIX of the contract token, mechanically — so a future token rename
        // cannot leave a stale rendering behind in one of the five surfaces.
        Assert.EndsWith(NeedsHumanKinds.Terse(NeedsHumanKinds.BlockedWork)!, NeedsHumanKinds.BlockedWork, StringComparison.Ordinal);
        Assert.EndsWith(NeedsHumanKinds.Terse(NeedsHumanKinds.DefectiveGuardrail)!, NeedsHumanKinds.DefectiveGuardrail, StringComparison.Ordinal);
    }

    // --- Journal wire shape (SSOT §7): additive, omit-null, round-tripping --------------------

    [Fact]
    public void AttemptRecord_WithKind_SerializesTheCamelCaseField_AndRoundTrips()
    {
        AttemptRecord record = Attempt(NeedsHumanKinds.DefectiveGuardrail);

        string json = JsonSerializer.Serialize(record, JournalJson.Options);
        Assert.Contains("\"needsHumanKind\": \"defective-guardrail\"", json, StringComparison.Ordinal);

        AttemptRecord? read = JsonSerializer.Deserialize<AttemptRecord>(json, JournalJson.Options);
        Assert.Equal(NeedsHumanKinds.DefectiveGuardrail, read!.NeedsHumanKind);
    }

    [Fact]
    public void AttemptRecord_WithoutKind_OmitsTheFieldEntirely_NoNullNoise()
    {
        // Unclassified must add NOTHING to run.json — a pre-#485-shaped record stays byte-identical, and
        // a reader that has never heard of the field sees exactly what it always saw.
        string json = JsonSerializer.Serialize(Attempt(null), JournalJson.Options);
        Assert.DoesNotContain("needsHumanKind", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AttemptRecord_FromAPre485Journal_ReadsAsUnclassified()
    {
        const string legacy = """
            {
              "attempt": 1,
              "startedAt": "2026-01-01T00:00:00+00:00",
              "endedAt": "2026-01-01T00:00:05+00:00",
              "outcome": "needs-human",
              "logDir": "logs/r/01-x/attempt-1"
            }
            """;

        AttemptRecord? read = JsonSerializer.Deserialize<AttemptRecord>(legacy, JournalJson.Options);
        Assert.Null(read!.NeedsHumanKind);
    }

    private static AttemptRecord Attempt(string? kind) => new()
    {
        Attempt = 1,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(5),
        Outcome = AttemptOutcome.NeedsHuman,
        LogDir = "logs/r/01-x/attempt-1",
        NeedsHumanKind = kind
    };
}
