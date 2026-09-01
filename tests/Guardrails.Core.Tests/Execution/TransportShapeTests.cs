using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Prompts;

// Deliberately NOT nested as `Guardrails.Core.Tests.Execution`: introducing that nested namespace
// anywhere in this assembly shadows the production `Guardrails.Core.Execution` namespace for every
// unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves an enclosing
// nested namespace before a `using`-imported one) — see JudgeSpendRecordingTests.cs, which explains
// and follows the same rule.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 30 §3.3/§3.4 — the four TRANSPORT hops (<see cref="PromptResult"/>, <see cref="ActionRun"/>,
/// <see cref="GuardrailRunResult"/>, <see cref="PendingAttempt"/>) that carry a Phase-1 fact from the
/// runner/clock to <c>run.json</c>. This is a COLLAPSED TDD pair: a record declaration IS the
/// implementation, so there is no stub-versus-real distinction to be red about, and a test that merely
/// constructs an object and asserts on the value it just set is close to hollow — nothing throws, so a
/// member declared with an eager default (<c>= 0</c>, an empty record) would still pass a naive test.
///
/// <para>Each round-trip test below therefore asserts BOTH halves: the member carries the value set
/// (the eager-default defect can't fake this if the test also checks default-null) AND a freshly
/// constructed instance defaults it to null (the eager-default defect CAN'T fake this half — a member
/// declared <c>= 0</c> or <c>= TimeSpan.Zero</c> fails it outright). The fifth test is the one assertion
/// here that a hollow body cannot satisfy at all: a reflection check that every Phase-1 carrier on
/// <see cref="PendingAttempt"/> has a same-named counterpart at the next hop, which a "set it and read
/// it back" test can pass while leaving the datum with nowhere to land.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TransportShapeTests
{
    // --- 1. PromptResult.ModelDigest -----------------------------------------------------------

    [Fact]
    public void PromptResultCarriesAModelDigest()
    {
        var withDigest = new PromptResult
        {
            Completed = true,
            IsError = false,
            Summary = "ok",
            ModelDigest = "sha256:abc123"
        };
        Assert.Equal("sha256:abc123", withDigest.ModelDigest);

        var withoutDigest = new PromptResult { Completed = true, IsError = false, Summary = "ok" };
        Assert.Null(withoutDigest.ModelDigest);
    }

    // --- 2. ActionRun.ModelDigest / Turns / ActionMs -------------------------------------------

    [Fact]
    public void ActionRunCarriesTheDigestTurnsAndActionMs()
    {
        var populated = new ActionRun
        {
            Succeeded = true,
            ExitCode = 0,
            TimedOut = false,
            ModelDigest = "sha256:def456",
            Turns = 9,
            ActionMs = 4200
        };
        Assert.Equal("sha256:def456", populated.ModelDigest);
        Assert.Equal(9, populated.Turns);
        Assert.Equal(4200, populated.ActionMs);

        var empty = new ActionRun { Succeeded = true, ExitCode = 0, TimedOut = false };
        Assert.Null(empty.ModelDigest);
        Assert.Null(empty.Turns);
        Assert.Null(empty.ActionMs);
    }

    // --- 3. GuardrailRunResult.GuardrailMs -----------------------------------------------------

    [Fact]
    public void GuardrailRunResultCarriesGuardrailMs()
    {
        var populated = new GuardrailRunResult
        {
            Results = [],
            AnyFailed = false,
            TimedOut = false,
            GuardrailMs = 780
        };
        Assert.Equal(780, populated.GuardrailMs);

        var empty = new GuardrailRunResult { Results = [], AnyFailed = false, TimedOut = false };
        Assert.Null(empty.GuardrailMs);
    }

    // --- 4. PendingAttempt.Turns / Segments / Bucket -------------------------------------------

    [Fact]
    public void PendingAttemptCarriesTurnsSegmentsAndBucket()
    {
        var populated = NewPendingAttempt() with
        {
            Turns = 11,
            Segments = new AttemptSegments { ActionMs = 1000, GuardrailMs = 250 },
            Bucket = "implementation"
        };
        Assert.Equal(11, populated.Turns);
        Assert.Equal(1000, populated.Segments?.ActionMs);
        Assert.Equal(250, populated.Segments?.GuardrailMs);
        Assert.Equal("implementation", populated.Bucket);

        PendingAttempt empty = NewPendingAttempt();
        Assert.Null(empty.Turns);
        Assert.Null(empty.Segments);
        Assert.Null(empty.Bucket);
    }

    // --- 5. Every Phase-1 PendingAttempt carrier has a next-hop counterpart --------------------

    [Fact]
    public void EveryPendingAttemptCarrierHasAnAttemptRecordCounterpart()
    {
        string[] phase1CarrierNames = ["Turns", "Segments", "Bucket"];

        foreach (string name in phase1CarrierNames)
        {
            bool hasAttemptRecordCounterpart = typeof(AttemptRecord).GetProperty(name) is not null;
            bool hasTaskJournalEntryCounterpart = typeof(TaskJournalEntry).GetProperty(name) is not null;

            Assert.True(
                hasAttemptRecordCounterpart || hasTaskJournalEntryCounterpart,
                $"PendingAttempt.{name} has no counterpart of the same name on either " +
                $"Journal.AttemptRecord or Journal.TaskJournalEntry — this datum would have nowhere " +
                $"to be written at the worktree settle.");
        }
    }

    private static PendingAttempt NewPendingAttempt() => new()
    {
        Attempt = 1,
        StartedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        LogDir = "logs/run-1/01-impl/attempt-1"
    };
}
