using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`, despite this file living under Journal/ —
// exactly as the sibling `Journal/JudgeSpendRecordingTests.cs:9-14` already records. Declaring that
// nested namespace ANYWHERE in this assembly shadows the production `Guardrails.Core.Journal` namespace
// for every unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves a member
// of the enclosing namespace before a `using`-imported one), and `OverwatchNoVerdictTests.cs`'s
// `Journal.TaskStatus.Running` then fails to compile — a file outside this task's write scope to fix.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 30 §3.2 — the task-fingerprint bucket REACHES <c>run.json</c>. <see cref="TaskFingerprintBucket"/>
/// (task 02) can name a bucket and <see cref="TaskJournalEntry.Bucket"/> (task 03) can carry one, but
/// nothing populates it yet: <see cref="AttemptJournaler"/> never computes it and passes it to
/// <see cref="RunJournal.RecordAttempt"/>. That wiring is task <c>06-journal-the-bucket-serial</c>; this
/// suite gates it.
///
/// <para><b>Real subject, real journal.</b> Every test drives the REAL <see cref="AttemptJournaler"/>
/// against a REAL <see cref="RunJournal"/> over a temp plan directory and reads the result off
/// <c>journal.Document.Tasks[taskId].Bucket</c> — never a fake journal, which would let the wiring task
/// satisfy this file without touching the code path a run actually takes.</para>
///
/// <para><b>TDD red, and it is a RUNTIME red.</b> Both dependencies already shipped, so this file COMPILES
/// against today's tree: <c>Bucket</c> exists to read, <c>Classify</c> exists to name the expected value.
/// Every test below fails at runtime because the recorded <c>Bucket</c> is <c>null</c> — nothing computes
/// it — not because anything is missing to compile against.</para>
///
/// <para><b>Behaviour 2 is the §2 survivorship lesson one level down, not padding.</b> §2 measured that
/// every one of 23 failed attempts in plan 27 carried no provenance, so each routed stratum contained only
/// its own successes and read 100% first-pass — the 100% was survivorship, not a measurement. A bucket
/// that lands on successes alone reproduces the identical defect at the bucket grain: a bucket populated
/// only by <see cref="AttemptJournaler.CompleteSucceededOrInvalidFragment"/> and never by
/// <see cref="AttemptJournaler.FailedAttempt"/> would make a hard bucket's failures invisible to it. A
/// failure is evidence too.</para>
///
/// <para><b>The anti-tautology rule for the two agreement tests (3 and 5).</b> On today's tree every
/// journaled <c>Bucket</c> is <c>null</c>, so asserting only <c>Assert.Equal(firstEntry.Bucket,
/// secondEntry.Bucket)</c> would pass vacuously (null == null) against the fully unwired code and certify
/// nothing. Every test below therefore asserts a CONCRETE expected bucket — the
/// <see cref="TaskFingerprintBucket"/> constant the fixture's <c>writeScope</c> and guardrail names are
/// chosen to produce — on every journaled entry it reads, with the bare equality (when present) added on
/// top rather than substituted for it.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class TaskBucketJournalTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr30-bucket-journal-" + Guid.NewGuid().ToString("N"));

    public TaskBucketJournalTests() => Directory.CreateDirectory(_root);

    // ── 1. a SUCCEEDED serial settle writes the task's bucket onto its journal entry ────────────────

    /// <summary>
    /// §3.2's table: <c>src/**</c> only, gated by a <c>tests-pass</c> guardrail, is <c>implementation</c>.
    /// Drives <see cref="AttemptJournaler.CompleteSucceededOrInvalidFragment"/> with a
    /// <c>fragmentOutPath</c> that does not exist, so the plain success path runs with no
    /// <c>StateManager.MergeFragment</c> involvement.
    /// </summary>
    [Fact]
    public void SucceededSettle_JournalsTheBucket()
    {
        const string taskId = "01-implement-thing";
        TaskNode task = BuildTask(taskId, writeScope: ["src/**"], "02-something-tests-pass");
        (AttemptJournaler journaler, RunJournal journal) = BuildJournaler(BuildPlan(task));

        SettleSucceeded(journaler, task, attemptNumber: 1, isFinal: true);

        TaskJournalEntry entry = journal.Document.Tasks[taskId];
        Assert.Equal(TaskFingerprintBucket.Implementation, entry.Bucket);
    }

    // ── 2. a FAILED attempt journals the bucket too (the §2 survivorship lesson, one level down) ────

    /// <summary>
    /// §3.2's table: <c>tests/**</c> only, gated by a TDD-red guardrail, is <c>test-authoring</c>. Drives
    /// <see cref="AttemptJournaler.FailedAttempt"/> with <see cref="AttemptOutcome.GuardrailFailed"/> —
    /// the action ran, but the attempt did not converge — and asserts the bucket landed anyway. A bucket
    /// wired to the succeeded path alone would leave this null, reproducing §2's defect at the bucket
    /// grain: `test-authoring` reading as the easy bucket because its failures were filtered out.
    /// </summary>
    [Fact]
    public void FailedAttempt_JournalsTheBucketToo()
    {
        const string taskId = "01-author-tests";
        TaskNode task = BuildTask(taskId, writeScope: ["tests/**"], "01-tests-fail-on-stubs");
        (AttemptJournaler journaler, RunJournal journal) = BuildJournaler(BuildPlan(task));

        SettleFailed(journaler, task, attemptNumber: 1, isFinal: false);

        TaskJournalEntry entry = journal.Document.Tasks[taskId];
        Assert.Equal(TaskFingerprintBucket.TestAuthoring, entry.Bucket);
    }

    // ── 3. two DIFFERENT task ids with identical writeScope/guardrails journal the SAME bucket ──────

    /// <summary>
    /// The report legend's constraint, made executable: "a bucket is a fact about a task, never one read
    /// off its name." The two <see cref="TaskNode"/>s below differ in <see cref="TaskNode.Id"/> and
    /// NOTHING else the classifier can see — same <c>writeScope</c>, same guardrail names — so an
    /// implementation that peeks at the id (or infers from it) would diverge here while one that reads
    /// only <c>writeScope</c>/guardrails would not.
    /// </summary>
    [Fact]
    public void TheBucketIsComputedFromWriteScopeAndGuardrails_NotFromTheTaskName()
    {
        TaskNode first = BuildTask("aaa-completely-different-name", writeScope: ["src/**"], "02-something-tests-pass");
        TaskNode second = BuildTask("zzz-another-unrelated-name", writeScope: ["src/**"], "02-something-tests-pass");
        (AttemptJournaler journaler, RunJournal journal) = BuildJournaler(BuildPlan(first, second));

        SettleSucceeded(journaler, first, attemptNumber: 1, isFinal: true);
        SettleSucceeded(journaler, second, attemptNumber: 1, isFinal: true);

        TaskJournalEntry firstEntry = journal.Document.Tasks[first.Id];
        TaskJournalEntry secondEntry = journal.Document.Tasks[second.Id];

        Assert.Equal(TaskFingerprintBucket.Implementation, firstEntry.Bucket);
        Assert.Equal(TaskFingerprintBucket.Implementation, secondEntry.Bucket);
        Assert.Equal(firstEntry.Bucket, secondEntry.Bucket);
    }

    // ── 4. a task declaring writeScope: [] journals no-write ─────────────────────────────────────────

    /// <summary>§3.2's table: an empty <c>writeScope</c> — the deliberate "writes nothing" declaration
    /// (verification and state-only tasks) — is <c>no-write</c>, regardless of guardrail shape.</summary>
    [Fact]
    public void ATaskThatWritesNothing_JournalsNoWrite()
    {
        const string taskId = "01-verify-only";
        TaskNode task = BuildTask(taskId, writeScope: [], "01-check");
        (AttemptJournaler journaler, RunJournal journal) = BuildJournaler(BuildPlan(task));

        SettleSucceeded(journaler, task, attemptNumber: 1, isFinal: true);

        TaskJournalEntry entry = journal.Document.Tasks[taskId];
        Assert.Equal(TaskFingerprintBucket.NoWrite, entry.Bucket);
    }

    // ── 5. two attempts of the SAME task journal the same bucket ─────────────────────────────────────

    /// <summary>
    /// The bucket is TASK grain, not attempt grain (§3.2: both inputs — <c>writeScope</c> and guardrail
    /// archetypes — are constant across a task's own retries within one run). Attempt 1 fails; attempt 2
    /// (the same <see cref="TaskNode"/>, same id) then succeeds. Both reads must land on the SAME concrete
    /// bucket — read after each settle, not just at the end, so a hypothetical implementation that stamps
    /// the bucket only once and then clears it on a later write would be caught too.
    /// </summary>
    [Fact]
    public void TheBucketIsStableAcrossARetryOfTheSameTask()
    {
        const string taskId = "01-implement-thing";
        TaskNode task = BuildTask(taskId, writeScope: ["src/**"], "02-something-tests-pass");
        (AttemptJournaler journaler, RunJournal journal) = BuildJournaler(BuildPlan(task));

        SettleFailed(journaler, task, attemptNumber: 1, isFinal: false);
        string? bucketAfterAttempt1 = journal.Document.Tasks[taskId].Bucket;
        Assert.Equal(TaskFingerprintBucket.Implementation, bucketAfterAttempt1);

        SettleSucceeded(journaler, task, attemptNumber: 2, isFinal: true);
        string? bucketAfterAttempt2 = journal.Document.Tasks[taskId].Bucket;
        Assert.Equal(TaskFingerprintBucket.Implementation, bucketAfterAttempt2);

        Assert.Equal(bucketAfterAttempt1, bucketAfterAttempt2);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a minimal PlanDefinition/TaskNode built in memory (never loaded through PlanLoader —
    // AttemptJournaler and RunJournal need no files on disk beyond what StateManager/RunJournal write
    // themselves; Journal.PlanHash.Compute tolerates a missing task.json/guardrails.json).
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private TaskNode BuildTask(string id, IReadOnlyList<string> writeScope, params string[] guardrailNames)
    {
        string taskDir = Path.Combine(_root, "tasks", id);

        var guardrails = guardrailNames
            .Select(name => new GuardrailDefinition
            {
                Name = name,
                Path = Path.Combine(taskDir, "guardrails", name + ".sh"),
                Kind = ActionKind.Script
            })
            .ToList();

        return new TaskNode
        {
            Id = id,
            Directory = taskDir,
            Description = "fixture task for TaskBucketJournalTests",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.sh"), Kind = ActionKind.Script },
            Guardrails = guardrails,
            WriteScope = writeScope
        };
    }

    private PlanDefinition BuildPlan(params TaskNode[] tasks) => new()
    {
        PlanDirectory = _root,
        Workspace = _root,
        Config = new RunConfig { Version = 1 },
        Tasks = tasks
    };

    private static (AttemptJournaler Journaler, RunJournal Journal) BuildJournaler(PlanDefinition plan)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        return (new AttemptJournaler(stateManager, journal), journal);
    }

    /// <summary>
    /// Drives <see cref="AttemptJournaler.CompleteSucceededOrInvalidFragment"/> with a
    /// <c>fragmentOutPath</c> that never exists, so the plain success path runs with no
    /// <c>StateManager.MergeFragment</c> involvement.
    /// </summary>
    private void SettleSucceeded(AttemptJournaler journaler, TaskNode task, int attemptNumber, bool isFinal)
    {
        string logDir = Path.Combine(_root, "logs", task.Id, $"attempt-{attemptNumber}");
        string fragmentOutPath = Path.Combine(logDir, "state-fragment.json"); // deliberately never written

        var action = new ActionRun { Succeeded = true, ExitCode = 0, TimedOut = false };
        var guardrails = new GuardrailRunResult { Results = [], AnyFailed = false, TimedOut = false };

        journaler.CompleteSucceededOrInvalidFragment(
            task, attemptNumber, DateTimeOffset.UtcNow,
            relativeLogDir: $"logs/{task.Id}/attempt-{attemptNumber}",
            logDir, fragmentOutPath, action, guardrails, isFinal);
    }

    /// <summary>Drives <see cref="AttemptJournaler.FailedAttempt"/> with a <c>guardrail-failed</c> outcome.</summary>
    private void SettleFailed(AttemptJournaler journaler, TaskNode task, int attemptNumber, bool isFinal)
    {
        string logDir = Path.Combine(_root, "logs", task.Id, $"attempt-{attemptNumber}");
        Directory.CreateDirectory(logDir); // FailedAttempt writes feedback.md here

        var result = new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.GuardrailFailed,
            ActionExitCode = 1,
            Summary = "guardrail(s) failed: fixture"
        };

        journaler.FailedAttempt(
            task, attemptNumber, DateTimeOffset.UtcNow,
            relativeLogDir: $"logs/{task.Id}/attempt-{attemptNumber}",
            logDir, feedback: "fixture feedback\n", isFinal, AttemptOutcome.GuardrailFailed, result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
