using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Plan 30 §3.2/§3.4 — <b>the worktree half of the Phase-1 attempt facts</b>. Worktree is the DEFAULT
/// execution mode, so the failure this file guards is the ordinary one, not a corner case:
/// <c>JournalModel.cs</c> already records it in prose (grep <i>"A member hung directly off the attempt
/// record"</i>) and <c>RunReport.cs</c> carries the worked example on
/// <see cref="PendingAttempt.Usage"/> (grep <i>"WITHOUT this line the value the record above sets reaches
/// serial runs only"</i>). A Phase-1 fact journalled correctly in serial mode and dropped in worktree mode
/// produces a corpus silently missing the majority of its rows' data, with a green run and a green test
/// suite either side of it.
///
/// <para><b>The two settle paths.</b> <c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c> (SERIAL)
/// builds an <see cref="AttemptRecord"/> itself and calls <c>RecordAttempt</c>, so the fact lands in
/// <c>run.json</c>. <c>AttemptJournaler.ValidateFragmentForSettle</c> (WORKTREE, the default) builds a
/// <see cref="PendingAttempt"/> and calls no journal method at all —
/// <c>Scheduler.RecordSucceededSettle</c> later turns it into the real record. §3.1's provenance already
/// rides both paths (shipped as <c>3129919</c>); the three members here do NOT ride it, which is why they
/// are the ones under test: <see cref="PendingAttempt.Turns"/>, <see cref="PendingAttempt.Segments"/> and
/// <see cref="PendingAttempt.Bucket"/>.</para>
///
/// <para><b>Every journal here is a REAL <see cref="RunJournal"/> over a temp plan folder</b>, never a
/// fake: the subject is what actually reaches the record, and a fake journal would make each assertion be
/// about the test's own scaffolding.</para>
///
/// <para><b>All five are RED on this tree, and there are no declared exemptions.</b> Nothing sets
/// <c>Bucket</c>, <c>Turns</c> or <c>Segments</c> on the worktree carrier, and nothing passes a bucket to
/// the settle recorder, so every honest test here fails. That is a RUNTIME red rather than a throwing-stub
/// red — every member asserted on already exists (tasks 03 and 04 declared them) and simply nobody sets it
/// — which is precisely why each test has to earn its red rather than get one for free.
/// <c>16-carry-phase1-facts-through-the-worktree-settle</c> turns them green.</para>
///
/// <para>Behaviours 1–4 stop at the journaller: they prove the CARRIER is populated. Behaviour 5
/// (<see cref="TheWorktreeSettle_JournalsTheBucketAndTheDefinitionHashInTheirOwnSlots"/>) is the only one
/// that drives a real <see cref="Scheduler"/>, and it proves the carried values reach the JOURNAL — in
/// their own fields at task grain, and on the journalled attempt record at attempt grain.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class WorktreeSettlePhase1Tests : IDisposable
{
    /// <summary>Behaviour 2's turn count: not <c>0</c>, not <c>1</c>, and not either segment value.</summary>
    private const int CarriedTurns = 9;

    /// <summary>Behaviour 3's action segment. Distinct from <see cref="CarriedGuardrailMs"/> so a swap of the
    /// two adjacent <c>long?</c> members of <see cref="AttemptSegments"/> cannot pass, and it is not a
    /// plausible real elapsed time for a fixture that runs no process.</summary>
    private const long CarriedActionMs = 4321;

    /// <summary>Behaviour 3's guardrail segment — see <see cref="CarriedActionMs"/>.</summary>
    private const long CarriedGuardrailMs = 87;

    /// <summary>
    /// Behaviour 5's bucket. A WORD, deliberately: it could never be mistaken for a definition hash, which
    /// is the confusion that test exists to catch. It is an INPUT the test hands to the settle, not a
    /// classification the harness made.
    /// </summary>
    private const string SettleBucket = "implementation";

    /// <summary>Behaviour 5's turn count — see <see cref="CarriedTurns"/> for why it is neither 0 nor 1.</summary>
    private const int SettleTurns = 7;

    /// <summary>Behaviour 5's action segment — see <see cref="CarriedActionMs"/>.</summary>
    private const long SettleActionMs = 1234;

    /// <summary>Behaviour 5's guardrail segment — see <see cref="CarriedActionMs"/>.</summary>
    private const long SettleGuardrailMs = 56;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr30-wsp1-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorktreeSettlePhase1Tests() => Directory.CreateDirectory(_root);

    // ── 1 — the worktree carrier carries the BUCKET ──────────────────────────────────────────────

    /// <summary>
    /// §3.2: the bucket is a fact about a task's write surface and gate shape, and the worktree settle is
    /// built from a <see cref="PendingAttempt"/> that must carry it — the serial path's
    /// <c>06-journal-the-bucket-serial</c> counterpart reaches <c>run.json</c> on its own, this one cannot.
    ///
    /// <para><b>Two differently-shaped tasks in one drive, on purpose.</b> A single task would be satisfied
    /// by an implementation that stamps one constant. These two differ ONLY in their write surface and
    /// guardrail archetype — <c>src/**</c> + a <c>tests-pass</c> gate versus <c>tests/**</c> + a
    /// <c>tests-fail-on-stubs</c> gate — so each must come back with ITS OWN bucket, which is what "the
    /// task's bucket" actually means. Neither expected value is read off the task's name (§3.2's legend:
    /// "a bucket is a fact about a task, never one read off its name"), and the two ids are deliberately
    /// unrelated to the buckets they classify as.</para>
    /// </summary>
    [Fact]
    public void TheWorktreePendingAttempt_CarriesTheBucket()
    {
        PlanDefinition plan = LoadPlan(
            "bucket",
            new FixtureTask("01-aaa-unrelated-name", ["src/**"], "02-something-tests-pass.sh"),
            new FixtureTask("02-zzz-unrelated-name", ["tests/**"], "01-tests-fail-on-stubs.sh"));

        (AttemptJournaler journaller, _) = NewJournaller(plan);

        PendingAttempt srcOnly = DriveWorktreeSettle(journaller, TaskIn(plan, "01-aaa-unrelated-name"));
        PendingAttempt testsOnly = DriveWorktreeSettle(journaller, TaskIn(plan, "02-zzz-unrelated-name"));

        Assert.Equal(TaskFingerprintBucket.Implementation, srcOnly.Bucket);
        Assert.Equal(TaskFingerprintBucket.TestAuthoring, testsOnly.Bucket);
    }

    // ── 2 — the worktree carrier carries the TURN COUNT ──────────────────────────────────────────

    /// <summary>
    /// §3.4's "turns-used (computed, printed and discarded today)". The count arrives on
    /// <see cref="ActionRun.Turns"/>, which <c>ValidateFragmentForSettle</c> already receives as a
    /// parameter — so the worktree carrier needs no new dependency to publish it, and an omission here is
    /// the whole defect.
    /// </summary>
    [Fact]
    public void TheWorktreePendingAttempt_CarriesTheTurnCount()
    {
        PlanDefinition plan = LoadPlan("turns", new FixtureTask("01-solo", ["src/**"], "02-tests-pass.sh"));
        (AttemptJournaler journaller, _) = NewJournaller(plan);

        PendingAttempt pending = DriveWorktreeSettle(
            journaller, TaskIn(plan, "01-solo"), NewAction(turns: CarriedTurns), NewGuardrails());

        Assert.Equal(CarriedTurns, pending.Turns);
    }

    // ── 3 — the worktree carrier carries the SEGMENTED DURATIONS ─────────────────────────────────

    /// <summary>
    /// §3.4's "segmented durations": how much of the attempt's elapsed time the action itself ran versus
    /// the guardrail suite that graded it. The two halves arrive on DIFFERENT parameters —
    /// <see cref="ActionRun.ActionMs"/> and <see cref="GuardrailRunResult.GuardrailMs"/> — and land on the
    /// two adjacent <c>long?</c> members of one <see cref="AttemptSegments"/>, so the values here are
    /// distinct: a transposition puts <see cref="CarriedGuardrailMs"/> where
    /// <see cref="CarriedActionMs"/> belongs and this fails, where two equal-looking placeholders would
    /// both still "match".
    /// </summary>
    [Fact]
    public void TheWorktreePendingAttempt_CarriesTheSegments()
    {
        PlanDefinition plan = LoadPlan("segments", new FixtureTask("01-solo", ["src/**"], "02-tests-pass.sh"));
        (AttemptJournaler journaller, _) = NewJournaller(plan);

        PendingAttempt pending = DriveWorktreeSettle(
            journaller,
            TaskIn(plan, "01-solo"),
            NewAction(actionMs: CarriedActionMs),
            NewGuardrails(guardrailMs: CarriedGuardrailMs));

        Assert.NotNull(pending.Segments);
        Assert.Equal(CarriedActionMs, pending.Segments!.ActionMs);
        Assert.Equal(CarriedGuardrailMs, pending.Segments.GuardrailMs);
    }

    // ── 4 — the AGREEMENT test: every Phase-1 attempt member, on BOTH settle paths ────────────────

    /// <summary>
    /// The plan's real invariant, and the reason this pair exists: a member declared only on
    /// <see cref="AttemptRecord"/> lands in serial mode and silently vanishes in worktree mode. Both entry
    /// points are driven with the SAME <see cref="TaskNode"/>, the SAME <see cref="ActionRun"/> and the
    /// SAME <see cref="GuardrailRunResult"/>, then compared member by member.
    ///
    /// <para><b>The assertion is TWO-SIDED, not an implication, and that is deliberate.</b> The method name
    /// reads like an implication — "everything set on the serial record is also set on the worktree record"
    /// — and the implication form is VACUOUSLY TRUE on the tree this test runs against: neither path sets
    /// anything yet, so "for every member set on the serial side…" quantifies over an empty set and the
    /// test is green while asserting nothing at all. That is a hollow test wearing the right name. What is
    /// asserted instead is that for each Phase-1 member the serial record carries a value AND the worktree
    /// carrier carries one. It is red today for both reasons, and it goes green only when task 16 has
    /// genuinely closed the gap on both paths. Do not "simplify" it back to the implication.</para>
    ///
    /// <para><b>The three carriers are named by hand, and this list is hand-maintained — that is a fact
    /// about the codebase, not an invariant anything enforces.</b> Nothing marks a member as a "Phase-1
    /// carrier": there is no attribute, no marker interface, no naming convention. So reflection over
    /// <see cref="PendingAttempt"/>'s properties cannot tell <see cref="PendingAttempt.Turns"/> (a carrier)
    /// from <see cref="PendingAttempt.LogDir"/> or <see cref="PendingAttempt.CostUsd"/> (not carriers), and
    /// each carrier's counterpart lives on a different type with no mechanical link back to it — there is
    /// nothing to enumerate. A by-name lookup would also make ABSENT and PRESENT-BUT-NULL
    /// indistinguishable: a member renamed out from under this test would read as an unset value and send
    /// the next reader to the wrong file, which is the hollow-test failure this pair exists to catch.
    /// Ordinary member access is bound at compile time and cannot fail that way — a rename becomes a build
    /// error, which is the feedback you want. Whoever declares a FOURTH Phase-1 carrier adds it here; what
    /// catches a carrier declared with no counterpart at all is
    /// <c>03-extend-the-journal-record-shape</c>'s and <c>04-extend-the-transport-record-shape</c>'s shape
    /// censuses, together with §3.2 and §3.4 of the plan.</para>
    ///
    /// <para>Note the grain split the two sides have to respect: <c>Turns</c> and <c>Segments</c> are
    /// ATTEMPT grain and their serial counterparts sit on <see cref="AttemptRecord"/>, while
    /// <c>Bucket</c> is TASK grain — constant across a task's own retries within one run — so its
    /// counterpart is <see cref="TaskJournalEntry.Bucket"/>, NOT a member of <see cref="AttemptRecord"/>.
    /// The test therefore looks in both places.</para>
    /// </summary>
    [Fact]
    public void EveryPhase1AttemptMemberSetOnTheSerialRecord_IsAlsoSetOnTheWorktreeRecord()
    {
        PlanDefinition plan = LoadPlan(
            "agreement", new FixtureTask("01-solo", ["src/**"], "02-something-tests-pass.sh"));
        TaskNode task = TaskIn(plan, "01-solo");

        // ONE ActionRun and ONE GuardrailRunResult, handed to BOTH entry points: any difference between the
        // two sides below is therefore a difference in the PATH, never in what it was told.
        ActionRun action = NewAction(turns: CarriedTurns, actionMs: CarriedActionMs);
        GuardrailRunResult guardrails = NewGuardrails(guardrailMs: CarriedGuardrailMs);

        (AttemptJournaler journaller, RunJournal journal) = NewJournaller(plan);

        // ── the SERIAL path: builds its own AttemptRecord and journals it ───────────────────────
        string logDir = NewLogDir(plan, task, attempt: 1);
        AttemptResult serial = journaller.CompleteSucceededOrInvalidFragment(
            task, attemptNumber: 1, startedAt: DateTimeOffset.UtcNow,
            relativeLogDir: RelativeLogDir(task, attempt: 1), logDir: logDir,
            fragmentOutPath: Path.Combine(logDir, "no-fragment-was-written.json"),
            action, guardrails, isFinal: false);

        // ── the WORKTREE path: builds a PendingAttempt and journals nothing ─────────────────────
        PendingAttempt worktree = DriveWorktreeSettle(journaller, task, action, guardrails, attempt: 1);

        // ── positive controls: both paths really ran and really produced their artifact ─────────
        Assert.Equal(TaskOutcome.Succeeded, serial.Result.Outcome);
        Assert.True(journal.Document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry),
            $"the serial settle journalled no entry for '{task.Id}' at all, so there is no record to "
            + "compare the worktree carrier against and this pin would be vacuous");
        Assert.True(entry!.Attempts.Count == 1,
            $"the serial settle journalled {entry.Attempts.Count} attempt record(s); expected exactly one, "
            + "so the record read below is unambiguously the one this drive produced");

        AttemptRecord serialRecord = entry.Attempts[0];

        // ── the pin: for each Phase-1 member, BOTH sides carry a value ──────────────────────────
        AssertBothSidesCarry("Turns", "AttemptRecord.Turns", serialRecord.Turns, worktree.Turns);
        AssertBothSidesCarry("Segments", "AttemptRecord.Segments", serialRecord.Segments, worktree.Segments);
        AssertBothSidesCarry("Bucket", "TaskJournalEntry.Bucket", entry.Bucket, worktree.Bucket);
    }

    // ── 5 — the SLOT test: the real Scheduler, and which FIELD each value came out in ────────────

    /// <summary>
    /// The only test in this file that drives a real <see cref="Scheduler"/>, and the only one that proves
    /// the two things nothing else in this plan proves: that the scheduler hands each carried value to the
    /// RIGHT parameter, and that the values it carries land on the record the worktree settle actually
    /// journals.
    ///
    /// <para><b>The defect it pins.</b> Task 16 widens <c>ISchedulerJournal.RecordSettleWithAttempt</c>
    /// with a <c>string? bucket = null</c> parameter, landing it directly beside the existing
    /// <c>string? definitionHash</c>. Two adjacent parameters of the same type mean every confusion between
    /// them COMPILES: a positional call one argument short binds <c>pending.Bucket</c> to
    /// <c>definitionHash</c> and defaults the bucket to null. It costs two facts at once, and neither
    /// failure is loud — the bucket is dropped, so every worktree run's task entry renders
    /// <c>(unbucketed)</c> (§3.2's exact defect); AND <see cref="TaskJournalEntry.DefinitionHash"/> is
    /// stamped with a bucket string, which is the field a resume's drift check compares and the #322
    /// safe-suffix rewind corroborates a commit's <c>Guardrails-Task-Hash:</c> trailer against. That damage
    /// surfaces later, as a rewind discarding work it should have kept. Task 16's source-shape guardrail
    /// reads <c>Scheduler.cs</c> as TEXT: it can see the shape of the call, but not which field the value
    /// landed in. This test can.</para>
    ///
    /// <para><b>Both directions at task grain, deliberately.</b> Asserting only that each field is non-null,
    /// or only one of the two directions, is satisfied by a swap: the bucket is checked to BE the bucket and
    /// NOT the hash, and the hash to BE the hash and NOT the bucket. The expected hash is taken from the
    /// loaded plan's own <see cref="TaskNode.DefinitionHashAtLoad"/> and is asserted non-null and
    /// <c>sha256:</c>-prefixed FIRST, so a fixture that silently produced no hash cannot make the comparison
    /// vacuous — two placeholders that looked alike would both still "match" under a slot slip.</para>
    ///
    /// <para><b>The three attempt-grain values are distinctive for the same reason.</b>
    /// <see cref="SettleTurns"/> is neither 0 nor 1 nor either segment value, and
    /// <see cref="SettleActionMs"/>/<see cref="SettleGuardrailMs"/> cannot be transposed without this
    /// noticing. That half is not decoration: it is the ONLY assertion in this plan that <c>Turns</c> and
    /// <c>Segments</c> reach a worktree JOURNAL RECORD — behaviour 4 compares the serial
    /// <see cref="AttemptRecord"/> against the <see cref="PendingAttempt"/> carrier, and neither side of
    /// that comparison is the record <c>Scheduler.RecordSucceededSettle</c> builds.</para>
    ///
    /// <para><b>This test constructs its own <see cref="PendingAttempt"/>, and that does NOT make it
    /// hollow.</b> The hollow shape is asserting about the object you just built. Here the object is the
    /// INPUT to the code under test and the subject is the SETTLE: which journal field each value came out
    /// in, and whether it came out at all. It is also the only way the recorder call under test runs — both
    /// shipped Scheduler fixtures leave <see cref="TaskResult.PendingAttempt"/> null, which makes the settle
    /// take the attempt-less <c>RecordSettle</c> fallback.</para>
    ///
    /// <para><b>RED today, three times over</b>: nothing passes a bucket to the recorder, and the
    /// scheduler's own <see cref="AttemptRecord"/> initializer reads neither <c>Turns</c> nor
    /// <c>Segments</c> off <c>pending</c>.</para>
    /// </summary>
    [Fact]
    public async Task TheWorktreeSettle_JournalsTheBucketAndTheDefinitionHashInTheirOwnSlots()
    {
        using var builder = new WavePlanBuilder();
        builder.Task("wave-01-scaffold", "01-config");
        PlanDefinition plan = builder.Load().Plan!;

        // The wave-qualified id ("wave-01-scaffold/01-config"), never the bare folder name.
        TaskNode task = Assert.Single(plan.Tasks);

        // The hash the loader pinned, and the value SettleAsync passes down to RecordSucceededSettle.
        // Asserted BEFORE the run: a fixture that produced no hash would make the comparisons below vacuous.
        Assert.NotNull(task.DefinitionHashAtLoad);
        Assert.StartsWith("sha256:", task.DefinitionHashAtLoad, StringComparison.Ordinal);

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var scheduler = new Scheduler(
            plan, new CarryingExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(),
            observer: IRunObserver.Null,
            maxParallelism: 4,
            reVerifier: null);

        RunReport report = await scheduler.RunAsync(plan, Ct);

        // ── positive control: the deferred green settle really happened ─────────────────────────
        Assert.True(report.AllSucceeded,
            "the run must be wholly green for the settle under test to have run; outcomes: "
            + string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        Assert.True(journal.Document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry),
            $"'{task.Id}' has no journal entry at all, so no settle was recorded");
        Assert.Equal(JournalTaskStatus.Succeeded, entry!.Status);

        // ── TASK grain, both directions: neither field may hold the other's value ───────────────
        Assert.Equal(SettleBucket, entry.Bucket);
        Assert.NotEqual(task.DefinitionHashAtLoad, entry.Bucket);
        Assert.Equal(task.DefinitionHashAtLoad, entry.DefinitionHash);
        Assert.NotEqual(SettleBucket, entry.DefinitionHash);

        // ── ATTEMPT grain, on the record the settle journalled ──────────────────────────────────
        // Counted before it is indexed, so an empty list fails as "the settle journalled no attempt record
        // at all" rather than as an index crash whose message names nothing.
        Assert.True(entry.Attempts.Count == 1,
            $"the worktree settle journalled {entry.Attempts.Count} attempt record(s) for '{task.Id}'; "
            + "expected exactly one, appended by RecordSettleWithAttempt");

        AttemptRecord settled = entry.Attempts[0];
        Assert.Equal(SettleTurns, settled.Turns);
        Assert.NotNull(settled.Segments);
        Assert.Equal(SettleActionMs, settled.Segments!.ActionMs);
        Assert.Equal(SettleGuardrailMs, settled.Segments.GuardrailMs);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Behaviour 5's executor: every task succeeds, DEFERS its settle to the Scheduler's B1
    /// (<c>Scheduler.SettleAsync</c>, which a real worktree run always takes) and — unlike both shipped
    /// Scheduler fixtures — CARRIES a <see cref="PendingAttempt"/>, without which the settle falls back to
    /// the attempt-less <c>RecordSettle</c> and the recorder call under test never runs.
    /// </summary>
    private sealed class CarryingExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct) =>
            Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true,
                PendingAttempt = new PendingAttempt
                {
                    Attempt = 1,
                    StartedAt = DateTimeOffset.UtcNow,
                    LogDir = $"logs/run/{task.Id}/attempt-1",
                    ActionExitCode = 0,
                    Bucket = SettleBucket,
                    Turns = SettleTurns,
                    Segments = new AttemptSegments
                    {
                        ActionMs = SettleActionMs,
                        GuardrailMs = SettleGuardrailMs
                    }
                }
            });
    }

    /// <summary>
    /// The real <c>AttemptJournaler</c> over a real <see cref="RunJournal"/> and a real
    /// <see cref="StateManager"/>, both rooted at <paramref name="plan"/>'s own temp directory. Never a
    /// fake: these tests are about what reaches the record, and a fake journal would make the assertions be
    /// about the test's own scaffolding.
    /// </summary>
    private static (AttemptJournaler Journaller, RunJournal Journal) NewJournaller(PlanDefinition plan)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        return (new AttemptJournaler(stateManager, journal), journal);
    }

    /// <summary>
    /// Drive the WORKTREE success path and return the <see cref="PendingAttempt"/> it built. The fragment
    /// path deliberately names a file that is never written, so the plain success branch runs with no
    /// fragment validation involved.
    /// <para>Obtaining the carrier from <c>ValidateFragmentForSettle</c> — rather than constructing one —
    /// is what makes behaviours 1–4 capable of failing at all.</para>
    /// </summary>
    private PendingAttempt DriveWorktreeSettle(
        AttemptJournaler journaller,
        TaskNode task,
        ActionRun? action = null,
        GuardrailRunResult? guardrails = null,
        int attempt = 1)
    {
        PlanDefinition plan = _plans[task.Id];
        string logDir = NewLogDir(plan, task, attempt);

        AttemptResult result = journaller.ValidateFragmentForSettle(
            task, attempt, startedAt: DateTimeOffset.UtcNow,
            relativeLogDir: RelativeLogDir(task, attempt), logDir: logDir,
            fragmentOutPath: Path.Combine(logDir, "no-fragment-was-written.json"),
            action ?? NewAction(), guardrails ?? NewGuardrails(), isFinal: false);

        Assert.Equal(TaskOutcome.Succeeded, result.Result.Outcome);
        Assert.True(result.Result.DeferredSettle,
            $"'{task.Id}' did not defer its settle, so this is not the worktree path");
        Assert.NotNull(result.Result.PendingAttempt);
        return result.Result.PendingAttempt!;
    }

    /// <summary>
    /// One Phase-1 member, on both settle paths at once. The message names WHICH member is missing on WHICH
    /// side, and names the counterpart type — the grain split (attempt vs task) is the thing a reader most
    /// often gets wrong when chasing one of these.
    /// </summary>
    private static void AssertBothSidesCarry(
        string member, string serialCounterpart, object? serialValue, object? worktreeValue)
    {
        Assert.True(serialValue is not null,
            $"Phase-1 member '{member}' is NOT set on the SERIAL side ({serialCounterpart}): "
            + "AttemptJournaler.CompleteSucceededOrInvalidFragment journalled nothing for it.");

        Assert.True(worktreeValue is not null,
            $"Phase-1 member '{member}' is NOT set on the WORKTREE side (PendingAttempt.{member}): "
            + "AttemptJournaler.ValidateFragmentForSettle left the carrier null, so the value reaches "
            + "serial runs only and silently vanishes in the DEFAULT execution mode.");
    }

    private static ActionRun NewAction(int? turns = null, long? actionMs = null) => new()
    {
        Succeeded = true,
        ExitCode = 0,
        TimedOut = false,
        Turns = turns,
        ActionMs = actionMs
    };

    private static GuardrailRunResult NewGuardrails(long? guardrailMs = null) => new()
    {
        Results = [],
        AnyFailed = false,
        TimedOut = false,
        GuardrailMs = guardrailMs
    };

    private static string RelativeLogDir(TaskNode task, int attempt) =>
        $"logs/run/{task.Id}/attempt-{attempt}";

    private static string NewLogDir(PlanDefinition plan, TaskNode task, int attempt)
    {
        string logDir = Path.Combine(
            plan.PlanDirectory, "logs", "run", task.Id, "attempt-" + attempt);
        Directory.CreateDirectory(logDir);
        return logDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a real, loadable plan folder on disk
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One fixture task: the folder name, the <c>writeScope</c> roots and the ONE guardrail file it carries.
    /// Those last two are the only inputs <see cref="TaskFingerprintBucket.Classify"/> reads, which is what
    /// lets behaviour 1 vary the bucket without varying anything a name-reading implementation could latch
    /// onto.
    /// </summary>
    private sealed record FixtureTask(string Id, string[] WriteScope, string GuardrailFile);

    /// <summary>Every plan a test built, keyed by the id of each task in it — so a drive can find the plan
    /// directory a task belongs to without every call site threading it through.</summary>
    private readonly Dictionary<string, PlanDefinition> _plans = new(StringComparer.Ordinal);

    private PlanDefinition LoadPlan(string name, params FixtureTask[] tasks)
    {
        string planDir = Path.Combine(_root, name);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(
            Path.Combine(planDir, "guardrails.json"), """{ "version": 1, "maxParallelism": 1 }""");

        foreach (FixtureTask task in tasks)
        {
            string taskDir = Path.Combine(planDir, "tasks", task.Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string scope = string.Join(", ", task.WriteScope.Select(root => $"\"{root}\""));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""{ "description": "{{task.Id}}", "writeScope": [{{scope}}] }""");
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\nexit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", task.GuardrailFile),
                "#!/bin/sh\n# catches: nothing - a fixture gate, never executed\nexit 0\n");
        }

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        Assert.NotNull(load.Plan);

        foreach (TaskNode node in load.Plan!.Tasks)
        {
            _plans[node.Id] = load.Plan;
        }

        return load.Plan;
    }

    private static TaskNode TaskIn(PlanDefinition plan, string taskId) =>
        plan.Tasks.Single(t => t.Id == taskId);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
