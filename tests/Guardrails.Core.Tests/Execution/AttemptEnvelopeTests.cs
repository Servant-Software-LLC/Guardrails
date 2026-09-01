using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Execution`: introducing that nested namespace
// anywhere in this assembly shadows the production `Guardrails.Core.Execution` namespace for every
// unqualified `Journal.X`/`Execution.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves an
// enclosing nested namespace before a `using`-imported one) — see TransportShapeTests.cs, which explains
// and follows the same rule.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 30 §3.4 — the turn count ("computed, printed and discarded today") and the segmented attempt
/// durations. Both facts die at <see cref="ActionRun.FromPrompt"/>: <see cref="PromptResult.NumTurns"/>
/// already reaches the runner boundary, and <see cref="ActionRun.Turns"/> / <see cref="ActionRun.ActionMs"/>
/// / <see cref="GuardrailRunResult.GuardrailMs"/> already carry the shape (task 04) — nothing populates
/// any of it yet. This file writes no production code; <c>12-record-the-turn-count</c> and
/// <c>12a-segment-the-attempt-durations</c> do that, against the pins below.
///
/// <para><b>The seam: a stub <see cref="IPromptRunner"/> is the only fake.</b> Every behavioural row drives
/// a REAL <see cref="TaskExecutor"/> through a REAL <see cref="Scheduler"/> (serial / shared-workspace mode
/// — no worktree provider, <c>maxParallelism: 1</c> — the <see cref="Journal.ExecutedDefinitionHashTests"/>
/// idiom) over a plan loaded from real files on disk via <see cref="PlanLoader"/>. A test that hand-built an
/// <c>ActionRun</c> and called a journaller method would prove the journaller and say nothing about
/// <c>FromPrompt</c>, which is where the number is actually dropped today — exactly how
/// <c>AttemptRecord.Usage</c> shipped structurally dead with every guardrail green (#475).</para>
///
/// <para><b>Positive controls, not just red bars.</b> A red row cannot by itself distinguish "the feature
/// is missing" from "the fixture never reached the recorder it names" — both are red. Every method below
/// therefore asserts, BEFORE the field under test: (A1) the attempt exists at the index read, (A2) its
/// <c>Outcome</c> is the expected token, (A3) — for the three rows where <c>Outcome</c> alone cannot tell
/// two roads apart — the row's own discriminator, and (B) wherever a model ran, that the stub's own
/// distinctive <c>CostUsd</c>/<c>Usage</c> arrived on the SAME record (proof the road from the runner to
/// the journal is connected at all, riding the identical carrier <c>Turns</c> will ride).</para>
///
/// <para><b>Four DECLARED EXEMPTIONS.</b> <see cref="AttemptTurnsTests.AScriptAction_RecordsNoTurnCount"/>,
/// <see cref="AttemptTurnsTests.ATaskPreflightFailure_RecordsNoTurnCount"/>,
/// <see cref="AttemptSegmentsTests.ATaskPreflightFailure_RecordsNoSegments"/> and
/// <see cref="AttemptSegmentsTests.APreAttemptCancel_RecordsNoSegments"/> assert a null that already holds
/// on today's tree (nothing populates the field either way) and must STAY null — the null-vs-zero line
/// <c>TelemetryRow.CostUsd</c> already draws. They are GREEN today; their job is to stay green through
/// tasks 12/12a, never to be contrived into failing.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class AttemptTurnsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- 1. a prompt action's reported turn count reaches the attempt record -------------------------

    [Fact]
    public async Task APromptActionsTurnCount_ReachesTheAttemptRecord()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Turns = AttemptEnvelopeFixture.DistinctiveTurns,
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.Equal(AttemptEnvelopeFixture.DistinctiveTurns, attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 2. a script action records NO turn count — null, never 0 ------------------------------------

    [Fact]
    public async Task AScriptAction_RecordsNoTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0");
            var runner = new StubPromptRunner();
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
            Assert.Equal(0, attempt.ActionExitCode);

            Assert.Null(attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 3. an attempt that FAILED still records its turn count (plan §2's survivorship lesson) ------

    [Fact]
    public async Task AFailedAttempt_StillRecordsItsTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 1");
            var runner = new StubPromptRunner
            {
                Turns = AttemptEnvelopeFixture.DistinctiveTurns,
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.Equal(AttemptEnvelopeFixture.DistinctiveTurns, attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 4. a needs-human attempt still records its turn count ---------------------------------------

    [Fact]
    public async Task ANeedsHumanAttempt_StillRecordsItsTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Turns = AttemptEnvelopeFixture.DistinctiveTurns,
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                NeedsHumanFragmentJson = AttemptEnvelopeFixture.NeedsHumanFragmentJson
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.NeedsHuman, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.Equal(AttemptEnvelopeFixture.DistinctiveTurns, attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 5. a permission-denied attempt still records its turn count ---------------------------------

    [Fact]
    public async Task APermissionWallAttempt_StillRecordsItsTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Succeeded = false,
                Turns = AttemptEnvelopeFixture.DistinctiveTurns,
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                BlockedWritePaths = [AttemptEnvelopeFixture.ClaudeDirWallPath]
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.PermissionDenied, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.Equal(AttemptEnvelopeFixture.DistinctiveTurns, attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 6. a task-preflight failure records NO turn count — null, never 0 (DECLARED EXEMPTION) ------

    [Fact]
    public async Task ATaskPreflightFailure_RecordsNoTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0", preflightBody: "exit 1");
            var runner = new StubPromptRunner();
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.TaskPreflightFailed, attempt.Outcome);

            Assert.Null(attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 7. a MID-ATTEMPT cancelled attempt still records its turn count -----------------------------

    [Fact]
    public async Task AMidAttemptCancel_StillRecordsItsTurnCount()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runner = new StubPromptRunner
            {
                Turns = AttemptEnvelopeFixture.DistinctiveTurns,
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                CancelDuringRun = cts
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, cts.Token);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Cancelled, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.Equal(AttemptEnvelopeFixture.DistinctiveTurns, attempt.Turns);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }
}

/// <summary>
/// See <see cref="AttemptTurnsTests"/> for the fixture doctrine (real run, stub <see cref="IPromptRunner"/>,
/// positive controls before the field under test). This class carries the segmented-duration half of plan
/// 30 §3.4: <see cref="Journal.AttemptSegments.ActionMs"/> / <see cref="Journal.AttemptSegments.GuardrailMs"/>.
///
/// <para><b>Duration assertions are lower bounds only.</b> Every timed row asserts a segment is at least
/// the fixture's own known delay/sleep — never an upper bound, which is how a duration test flakes on a
/// loaded CI box — and, wherever both segments are asserted, that their sum does not exceed the attempt's
/// own wall time (<c>EndedAt - StartedAt</c>): the envelope cannot be larger than the envelope.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class AttemptSegmentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- 8. the action phase's elapsed time reaches AttemptRecord.Segments.ActionMs ------------------

    [Fact]
    public async Task TheActionsElapsedTime_ReachesTheAttemptSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.True(attempt.Segments.ActionMs >= AttemptEnvelopeFixture.ActionDelayMs,
                $"ActionMs was {attempt.Segments.ActionMs}ms; expected at least the stub's " +
                $"{AttemptEnvelopeFixture.ActionDelayMs}ms delay.");
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 9. the guardrail phase's elapsed time reaches AttemptRecord.Segments.GuardrailMs ------------

    [Fact]
    public async Task TheGuardrailsElapsedTime_ReachesTheAttemptSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true,
                guardrailBody: AttemptEnvelopeFixture.SleepThenExit(AttemptEnvelopeFixture.GuardrailDelayMs, exitCode: 0));
            var runner = new StubPromptRunner
            {
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Succeeded, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.GuardrailMs);
            Assert.True(attempt.Segments.GuardrailMs >= AttemptEnvelopeFixture.GuardrailDelayMs,
                $"GuardrailMs was {attempt.Segments.GuardrailMs}ms; expected at least the guardrail's " +
                $"{AttemptEnvelopeFixture.GuardrailDelayMs}ms sleep.");
            // The cheapest wrong implementation copies one clock into both members.
            Assert.NotEqual(attempt.Segments.ActionMs, attempt.Segments.GuardrailMs);
            AttemptEnvelopeFixture.AssertEnvelopeWithinWallClock(attempt);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 10. an attempt that FAILED still records both segments ---------------------------------------

    [Fact]
    public async Task AFailedAttempt_StillRecordsItsSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true,
                guardrailBody: AttemptEnvelopeFixture.SleepThenExit(AttemptEnvelopeFixture.GuardrailDelayMs, exitCode: 1));
            var runner = new StubPromptRunner
            {
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.NotNull(attempt.Segments.GuardrailMs);
            Assert.NotEqual(attempt.Segments.ActionMs, attempt.Segments.GuardrailMs);
            AttemptEnvelopeFixture.AssertEnvelopeWithinWallClock(attempt);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 11. a needs-human attempt still records its action segment (GuardrailMs untouched) ----------

    [Fact]
    public async Task ANeedsHumanAttempt_StillRecordsItsActionSegment()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                NeedsHumanFragmentJson = AttemptEnvelopeFixture.NeedsHumanFragmentJson
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.NeedsHuman, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            // This path settles BEFORE any guardrail runs, so Segments is half-populated by design —
            // assert ActionMs only; GuardrailMs is deliberately not referenced here at all.
            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.True(attempt.Segments.ActionMs >= AttemptEnvelopeFixture.ActionDelayMs);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 12. a permission-denied attempt still records its action segment (GuardrailMs untouched) ----

    [Fact]
    public async Task APermissionWallAttempt_StillRecordsItsActionSegment()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            var runner = new StubPromptRunner
            {
                Succeeded = false,
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                BlockedWritePaths = [AttemptEnvelopeFixture.ClaudeDirWallPath]
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.PermissionDenied, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            // Same half-populated-by-design shape as behaviour 11: no guardrail ran here either.
            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.True(attempt.Segments.ActionMs >= AttemptEnvelopeFixture.ActionDelayMs);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 13. a structural-wall halt records BOTH segments ----------------------------------------------

    [Fact]
    public async Task AStructuralWallHalt_RecordsBothSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            // defaultRetries: 2 (budget 3) is the point: the discriminator against the ORDINARY
            // guardrail-failed road (which records the SAME "guardrail-failed" outcome string) is the
            // halt DECISION — exactly one recorded attempt regardless of the budget on offer.
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 2, promptAction: true,
                guardrailBody: AttemptEnvelopeFixture.SleepThenExit(AttemptEnvelopeFixture.GuardrailDelayMs, exitCode: 1));
            var runner = new StubPromptRunner
            {
                Succeeded = true, // the action itself succeeds; only the guardrail fails this attempt
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                BlockedWritePaths = [AttemptEnvelopeFixture.ClaudeDirWallPath]
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            Assert.True(
                journal.Document.Tasks.TryGetValue(AttemptEnvelopeFixture.TaskId, out TaskJournalEntry? entry),
                $"'{AttemptEnvelopeFixture.TaskId}' has no journal entry at all.");
            AttemptRecord attempt = Assert.Single(entry!.Attempts);
            Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);

            Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);
            Assert.NotEmpty(attempt.FailedGuardrails);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.NotNull(attempt.Segments.GuardrailMs);
            Assert.NotEqual(attempt.Segments.ActionMs, attempt.Segments.GuardrailMs);
            AttemptEnvelopeFixture.AssertEnvelopeWithinWallClock(attempt);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 14. a MID-ATTEMPT cancelled attempt still records its action segment -------------------------

    [Fact]
    public async Task AMidAttemptCancel_StillRecordsItsActionSegment()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: true, guardrailBody: "exit 0");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runner = new StubPromptRunner
            {
                Delay = TimeSpan.FromMilliseconds(AttemptEnvelopeFixture.ActionDelayMs),
                CostUsd = AttemptEnvelopeFixture.DistinctiveCostUsd,
                Usage = AttemptEnvelopeFixture.DistinctiveUsage,
                CancelDuringRun = cts
            };
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, cts.Token);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.Cancelled, attempt.Outcome);
            AttemptEnvelopeFixture.AssertConnectivity(attempt);

            // This path settles before any guardrail runs too — GuardrailMs is not referenced.
            Assert.NotNull(attempt.Segments);
            Assert.NotNull(attempt.Segments!.ActionMs);
            Assert.True(attempt.Segments.ActionMs >= AttemptEnvelopeFixture.ActionDelayMs);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 15. a task-preflight failure records NO segments at all (DECLARED EXEMPTION) ------------------

    [Fact]
    public async Task ATaskPreflightFailure_RecordsNoSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0", preflightBody: "exit 1");
            var runner = new StubPromptRunner();
            RunJournal journal = await AttemptEnvelopeFixture.RunSerialAsync(plan, runner, Ct);

            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal);
            Assert.Equal(AttemptOutcome.TaskPreflightFailed, attempt.Outcome);

            Assert.Null(attempt.Segments);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }

    // --- 16. the PRE-ATTEMPT cancelled record carries NO segments — null, never a zeroed pair --------

    [Fact]
    public void APreAttemptCancel_RecordsNoSegments()
    {
        string root = AttemptEnvelopeFixture.NewRoot();
        try
        {
            // The only row in this file NOT driven through a run: the pre-attempt cancel site fires only
            // in the narrow window between the mid-attempt cancellation check and the transient-pause
            // return, so no fixture reaches it through the Scheduler without a flaky race. Call the
            // journaller directly instead — Guardrails.Core.Tests has InternalsVisibleTo into
            // Guardrails.Core — passing EXACTLY what TaskExecutor's own pre-attempt call site passes.
            PlanDefinition plan = AttemptEnvelopeFixture.WritePlan(
                root, defaultRetries: 0, promptAction: false, guardrailBody: "exit 0");

            var stateManager = new StateManager(plan.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(plan);
            var journaler = new AttemptJournaler(stateManager, journal);
            TaskNode task = plan.Tasks.Single();

            journaler.Cancelled(
                task,
                attemptNumber: 1,
                startedAt: DateTimeOffset.UtcNow,
                relativeLogDir: "unused",
                new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = "",
                    StandardError = "",
                    TimedOut = false,
                    Duration = TimeSpan.Zero
                },
                costUsd: null);

            // The ONLY control this row can carry: the record actually reached the journal. Asserting
            // Outcome here would be self-deception — Cancelled() hard-codes it, so the assertion would be
            // checking a value THIS TEST caused, never a value that discriminates a fixture bug.
            AttemptRecord attempt = AttemptEnvelopeFixture.ReadAttempt(journal, task.Id);

            Assert.Null(attempt.Segments);
        }
        finally { AttemptEnvelopeFixture.DeleteBestEffort(root); }
    }
}

/// <summary>
/// Shared fixture plumbing for <see cref="AttemptTurnsTests"/> and <see cref="AttemptSegmentsTests"/>:
/// writes a real one-task plan to disk, drives it through a real serial-mode <see cref="Scheduler"/> with
/// a stub <see cref="IPromptRunner"/> (the ONLY fake in this file — the child model process is faked, the
/// runner interface never is), and reads the settled <see cref="AttemptRecord"/> back off the journal.
/// </summary>
file static class AttemptEnvelopeFixture
{
    public const string TaskId = "01-task";

    /// <summary>A turn count with no plausible accidental origin (not an attempt/exit-code-shaped number).</summary>
    public const int DistinctiveTurns = 7;

    /// <summary>The connectivity control's cost — the exact literal the plan's own prompt suggests.</summary>
    public const decimal DistinctiveCostUsd = 0.4242m;

    public static readonly PromptUsage DistinctiveUsage = new() { InputTokens = 5150, OutputTokens = 3070 };

    /// <summary>Comfortably larger than timer granularity (a couple hundred ms), per plan §3.4's guidance.</summary>
    public const int ActionDelayMs = 300;

    /// <summary>Deliberately different from <see cref="ActionDelayMs"/> so the two segments cannot collide.</summary>
    public const int GuardrailDelayMs = 450;

    public const string ClaudeDirWallPath = ".claude/settings.local.json";

    public const string NeedsHumanFragmentJson =
        """{"needsHuman":{"question":"which approach should this task take?","kind":"blocked-work"}}""";

    private static bool Win => OperatingSystem.IsWindows();

    private static string ActionScriptName => Win ? "action.ps1" : "action.sh";

    private static string CheckFileName => Win ? "01-check.ps1" : "01-check.sh";

    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "gr-attempt-envelope-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Writes one plan with one task under <paramref name="root"/>/plan and loads it through the REAL
    /// <see cref="PlanLoader"/>. <paramref name="promptAction"/> selects a <c>.prompt.md</c> action (the
    /// stub runner is invoked) versus a plain script action (exit 0; the stub is never invoked).
    /// <paramref name="preflightBody"/>, when non-null, adds a <c>tasks/&lt;id&gt;/preflights/</c> check
    /// carrying the enforced <c>catches:</c> declaration (GR2027).
    /// </summary>
    public static PlanDefinition WritePlan(
        string root, int defaultRetries, bool promptAction, string guardrailBody, string? preflightBody = null)
    {
        string planDir = Path.Combine(root, "plan");

        Write(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "workspace": ".",
              "maxParallelism": 1,
              "defaultTimeoutSeconds": 60,
              "defaultRetries": {{defaultRetries}},
              "promptRunners": { "default": "stub", "stub": { "command": "stub" } }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", TaskId);

        string actionJson = promptAction
            ? """{ "path": "action.prompt.md" }"""
            : $$"""{ "path": "{{ActionScriptName}}" }""";

        Write(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "attempt-envelope fixture", "dependsOn": [], "writeScope": [], "action": {{actionJson}} }""");

        if (promptAction)
        {
            Write(Path.Combine(taskDir, "action.prompt.md"), "Do the thing.\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, ActionScriptName), ScriptBody("exit 0"));
        }

        WriteExecutable(Path.Combine(taskDir, "guardrails", CheckFileName), ScriptBody(guardrailBody));

        if (preflightBody is not null)
        {
            const string catches =
                "# catches: the producer's contribution is absent in this consumer's inherited bytes";
            WriteExecutable(
                Path.Combine(taskDir, "preflights", CheckFileName), ScriptBody(catches + "\n" + preflightBody));
        }

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    /// <summary>
    /// A SERIAL / shared-workspace run (no worktree provider, <c>maxParallelism: 1</c>) — the
    /// <c>ExecutedDefinitionHashTests.RunSerialAsync</c> idiom — through a REAL <see cref="TaskExecutor"/>
    /// and <see cref="Scheduler"/>. Returns the journal only: every assertion in this file reads the
    /// durable journal document, never the in-memory <see cref="RunReport"/>.
    /// </summary>
    public static async Task<RunJournal> RunSerialAsync(PlanDefinition plan, IPromptRunner runner, CancellationToken ct)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        await scheduler.RunAsync(plan, ct);
        return journal;
    }

    /// <summary>
    /// Road control A1: the attempt EXISTS at the index read. A record that never landed reads as a null
    /// exactly like a correct one, so this must run before anything indexes into the list.
    /// </summary>
    public static AttemptRecord ReadAttempt(RunJournal journal, string taskId = TaskId, int index = 0)
    {
        Assert.True(
            journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all — the fixture never reached the recorder this row names.");
        Assert.True(
            entry!.Attempts.Count > index,
            $"'{taskId}' recorded {entry.Attempts.Count} attempt(s); index {index} is out of range — " +
            "the fixture never reached the recorder this row names.");
        return entry.Attempts[index];
    }

    /// <summary>
    /// Connectivity control B: the stub's own reported <c>CostUsd</c>/<c>Usage</c> arrived on this exact
    /// record — proof the road from the runner to the journal is connected, riding the same
    /// <see cref="ActionRun"/> carrier <c>Turns</c> will ride. NOT a road control on its own (every
    /// carrying recorder journals these two fields identically) — always called AFTER the road controls.
    /// </summary>
    public static void AssertConnectivity(AttemptRecord attempt)
    {
        Assert.Equal(DistinctiveCostUsd, attempt.CostUsd);
        Assert.NotNull(attempt.Usage);
        Assert.Equal(DistinctiveUsage.InputTokens, attempt.Usage!.InputTokens);
        Assert.Equal(DistinctiveUsage.OutputTokens, attempt.Usage.OutputTokens);
    }

    /// <summary>The envelope bound: ActionMs + GuardrailMs can never exceed the attempt's own wall time.</summary>
    public static void AssertEnvelopeWithinWallClock(AttemptRecord attempt)
    {
        Assert.NotNull(attempt.Segments);
        long wallMs = (long)(attempt.EndedAt - attempt.StartedAt).TotalMilliseconds;
        long sumMs = (attempt.Segments!.ActionMs ?? 0) + (attempt.Segments.GuardrailMs ?? 0);
        Assert.True(
            sumMs <= wallMs,
            $"segments summed to {sumMs}ms but the attempt's own wall time was only {wallMs}ms.");
    }

    /// <summary>An OS-appropriate guardrail body that sleeps then exits, so its own elapsed time is measurable.</summary>
    public static string SleepThenExit(int milliseconds, int exitCode)
    {
        if (Win)
        {
            return $"Start-Sleep -Milliseconds {milliseconds}\nexit {exitCode}";
        }

        // Fractional seconds, built without culture-sensitive double formatting: GNU and BSD `sleep`
        // both accept e.g. "0.450".
        string seconds = $"{milliseconds / 1000}.{milliseconds % 1000:D3}";
        return $"sleep {seconds}\nexit {exitCode}";
    }

    public static void DeleteBestEffort(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private static string ScriptBody(string body) => Win ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }
}

/// <summary>
/// The only fake in this file (SSOT §9 seam): implements <see cref="IPromptRunner"/> directly, so every
/// hop between it and <c>run.json</c> — <see cref="ActionRun.FromPrompt"/>, <see cref="AttemptJournaler"/>,
/// the real <see cref="Scheduler"/> — runs for real. Configure the fields a scenario needs; unset fields
/// keep their neutral defaults (a plain succeeded attempt with no turns/cost/usage/walls reported).
/// </summary>
file sealed class StubPromptRunner : IPromptRunner
{
    public string Name => "stub";

    public int? Turns { get; init; }

    public bool Succeeded { get; init; } = true;

    /// <summary>How long <see cref="RunAsync"/> waits before returning — the measurable action duration.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public decimal? CostUsd { get; init; }

    public PromptUsage? Usage { get; init; }

    public IReadOnlyList<string> BlockedWritePaths { get; init; } = [];

    /// <summary>When set, written to the invocation's <c>GUARDRAILS_STATE_OUT</c> path before returning.</summary>
    public string? NeedsHumanFragmentJson { get; init; }

    /// <summary>When set, cancelled immediately before returning — simulating a MID-attempt cancel.</summary>
    public CancellationTokenSource? CancelDuringRun { get; init; }

    public async Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            // CancellationToken.None deliberately: this delay must complete even when the SAME run's
            // token is about to be cancelled by this very call, below.
            await Task.Delay(Delay, CancellationToken.None);
        }

        CancelDuringRun?.Cancel();

        if (NeedsHumanFragmentJson is not null &&
            invocation.Environment.TryGetValue("GUARDRAILS_STATE_OUT", out string? stateOutPath))
        {
            File.WriteAllText(stateOutPath, NeedsHumanFragmentJson);
        }

        return new PromptResult
        {
            Completed = true,
            IsError = !Succeeded,
            NumTurns = Turns,
            CostUsd = CostUsd,
            Usage = Usage,
            BlockedWritePaths = BlockedWritePaths,
            Summary = Succeeded ? "stub completed" : "stub reported an error"
        };
    }
}
