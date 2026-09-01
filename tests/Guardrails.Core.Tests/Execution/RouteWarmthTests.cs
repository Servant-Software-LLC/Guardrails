using System.Reflection;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Plan 30 §3.4 — the <b>warm/cold</b> item. The plan names the flag and does not define it; the
/// definition these tests pin is the BREAKDOWN's choice (task <c>13-author-tests-route-warmth</c>'s
/// prompt), open to a maintainer replacing it: an attempt is COLD on the FIRST attempt this run resolves
/// against a given <c>(runner, model)</c> pair, WARM on every later attempt resolving the same pair, and
/// <c>null</c> — never <c>false</c> — when no route resolved at all (a script action).
///
/// <para><b>Grain is <c>(runner, model)</c>, not the runner alone</b> — two models served by one runner
/// are two different first invocations. <b><c>null</c> is a third value, not a synonym for
/// <c>false</c></b> — <see cref="AttemptProvenance.RouteWarm"/> is <c>bool?</c> precisely so a script
/// attempt (which invoked no model) never reads as a first-invocation penalty.</para>
///
/// <para><b>Task <c>03-extend-the-journal-record-shape</c> already declared the member</b> — this file
/// compiles against today's tree and is red at RUNTIME, not at compile time. Nothing populates
/// <see cref="AttemptProvenance.RouteWarm"/> yet, so a correct test that asks for a value observes
/// <c>null</c> today.</para>
///
/// <para><b>Reached by reflection.</b> <c>TaskExecutor.BuildProvenance(TaskNode, WorktreeHandle,
/// TierResolution?)</c> is PRIVATE — it is where every launch-time provenance fact is set, and it is
/// where warmth will be set (task <c>14-record-whether-the-route-was-warm</c>). It is a PINNED DEPENDENCY
/// of this file: that task must not rename or re-shape its signature, because <see cref="BuildProvenance"/>
/// below reaches it only by name, the same house precedent as
/// <c>TopologyM0BookkeepingTests.RunContext_HasDirectoryOwnerMap_StringToString</c>.</para>
///
/// <para><b>TDD red census: three red, two DECLARED EXEMPTIONS.</b>
/// <see cref="TheFirstAttemptOnARoute_IsCold"/>, <see cref="ASecondAttemptOnTheSameRoute_IsWarm"/> and
/// <see cref="ADifferentModelOnTheSameRunner_IsColdAgain"/> each obtain a REAL provenance object off a
/// real (if reflectively-invoked) <see cref="TaskExecutor"/> and assert on its
/// <see cref="AttemptProvenance.RouteWarm"/> — they FAIL today, because nothing sets the flag.
/// <see cref="AScriptActionWithNoRoute_RecordsNoWarmth"/> and
/// <see cref="WarmthRidesTheProvenance_SoItReachesBothSettlePaths"/> are true today and must STAY true;
/// they are written, never skipped, so the census can see them run.</para>
///
/// <para><b>Concurrency.</b> One <see cref="TaskExecutor"/> serves a whole run and parallel workers call
/// into it concurrently, so under real parallelism WHICH of two simultaneous first attempts on one route
/// is the cold one is a race — acceptable, because the invariant is "exactly one cold per
/// <c>(runner, model)</c> pair per run", not a particular ordering. Behaviours 1-3 below therefore drive
/// one <see cref="TaskExecutor"/> SEQUENTIALLY and assert nothing about ordering under concurrency.</para>
/// </summary>
[Trait("Category", "ModelEvidence")]
public sealed class RouteWarmthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-warmth-" + Guid.NewGuid().ToString("N"));

    public RouteWarmthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // ── 1. the first attempt on a route is cold ────────────────────────────────────────────────────

    [Fact]
    public void TheFirstAttemptOnARoute_IsCold()
    {
        TaskExecutor executor = NewExecutor();
        TaskNode task = NewTask(ActionKind.Prompt);

        AttemptProvenance? provenance =
            BuildProvenance(executor, task, new WorktreeHandle(), Route("runner-a", "model-x"));

        Assert.NotNull(provenance);
        Assert.False(
            provenance!.RouteWarm,
            "the FIRST attempt this run resolves against a (runner, model) pair must record " +
            "RouteWarm = false (Assert.False fails on both null and true, so a still-unset flag is " +
            "correctly red here).");
    }

    // ── 2. a second attempt on the same route is warm ──────────────────────────────────────────────

    [Fact]
    public void ASecondAttemptOnTheSameRoute_IsWarm()
    {
        TaskExecutor executor = NewExecutor();
        TaskNode task = NewTask(ActionKind.Prompt);

        // First call establishes "runner-a"/"model-x" as SEEN this run. Two SEPARATE TierResolution
        // instances carrying the same values, deliberately — identity must be data-based, not a check
        // that only happens to pass because the same object reference was reused.
        BuildProvenance(executor, task, new WorktreeHandle(), Route("runner-a", "model-x"));

        AttemptProvenance? second =
            BuildProvenance(executor, task, new WorktreeHandle(), Route("runner-a", "model-x"));

        Assert.NotNull(second);
        Assert.True(
            second!.RouteWarm,
            "a SECOND attempt against the SAME (runner, model) pair, on the SAME executor, must record " +
            "RouteWarm = true.");
    }

    // ── 3. a different model on the same runner is a different route, cold again ───────────────────

    [Fact]
    public void ADifferentModelOnTheSameRunner_IsColdAgain()
    {
        TaskExecutor executor = NewExecutor();
        TaskNode task = NewTask(ActionKind.Prompt);

        BuildProvenance(executor, task, new WorktreeHandle(), Route("runner-a", "model-x"));

        AttemptProvenance? differentModel =
            BuildProvenance(executor, task, new WorktreeHandle(), Route("runner-a", "model-y"));

        Assert.NotNull(differentModel);
        Assert.False(
            differentModel!.RouteWarm,
            "the runner alone is not the identity: \"runner-a\"/\"model-y\" is a DIFFERENT (runner, " +
            "model) pair from \"runner-a\"/\"model-x\", so its first attempt is cold again, even though " +
            "the runner already ran once this run.");
    }

    // ── 4. a script action resolves no route, so it records no warmth (DECLARED EXEMPTION: green today) ──

    /// <summary>
    /// Green today by construction — nothing sets <see cref="AttemptProvenance.RouteWarm"/> yet, so a
    /// script attempt's warmth is already absent. Written so it survives task 14 unmodified: this is what
    /// stops warmth being recorded as <c>false</c> for a script attempt once the flag is actually wired.
    ///
    /// <para><b>Observed shape on today's tree: NO provenance object at all</b>, not a provenance whose
    /// <see cref="AttemptProvenance.RouteWarm"/> is null. <c>BuildProvenance</c>'s early return —
    /// <c>model is null &amp;&amp; !realSegment</c> — fires here because <paramref name="route"/> is null
    /// (no model) and <c>new WorktreeHandle()</c> is not a real git segment (serial mode). The assertion
    /// below is written to accept EITHER shape regardless, because that early-return boundary is not this
    /// task's to pin — only the absence of warmth is.</para>
    /// </summary>
    [Fact]
    public void AScriptActionWithNoRoute_RecordsNoWarmth()
    {
        TaskExecutor executor = NewExecutor();
        TaskNode task = NewTask(ActionKind.Script);

        AttemptProvenance? provenance = BuildProvenance(executor, task, new WorktreeHandle(), route: null);

        Assert.True(
            provenance is null || provenance.RouteWarm is null,
            "a script action resolves no route at all, so warmth must be ABSENT — either no provenance " +
            "object, or a provenance whose RouteWarm is null — and never false.");
    }

    // ── 5. warmth rides the provenance, never the record (DECLARED EXEMPTION: green today) ────────────

    /// <summary>
    /// Green today — task 03 already declared <see cref="AttemptProvenance.RouteWarm"/> and nowhere else.
    /// Same shape and citation as <c>09-author-tests-digest-reaches-the-provenance</c>'s
    /// <c>TheDigestRidesTheProvenance_SoItReachesBothSettlePaths</c>.
    ///
    /// <para><see cref="AttemptRecord.Provenance"/> is the only member that already rides
    /// <c>PendingAttempt</c>, and therefore the only one that reaches BOTH record-construction paths — the
    /// serial <c>AttemptJournaler</c> and <c>Scheduler.RecordSucceededSettle</c> (the DEFAULT worktree
    /// mode). A member hung directly off <see cref="AttemptRecord"/> lands in serial mode and silently
    /// vanishes in worktree mode (<c>JournalModel.cs</c>, the doc comment on
    /// <see cref="AttemptProvenance.Judge"/>). This is the tripwire on a later refactor moving
    /// <see cref="AttemptProvenance.RouteWarm"/> off the provenance.</para>
    /// </summary>
    [Fact]
    public void WarmthRidesTheProvenance_SoItReachesBothSettlePaths()
    {
        PropertyInfo? onProvenance = typeof(AttemptProvenance).GetProperty(
            nameof(AttemptProvenance.RouteWarm), BindingFlags.Public | BindingFlags.Instance);
        Assert.True(
            onProvenance is not null,
            "AttemptProvenance no longer declares RouteWarm — warmth would have nowhere to ride that " +
            "reaches both the serial and worktree-mode settle paths.");
        Assert.Equal(typeof(bool?), onProvenance!.PropertyType);

        // A string literal, not nameof(AttemptRecord.RouteWarm), on purpose: that member must NOT exist,
        // and a nameof reference to a member that does not exist would not COMPILE — this file would stop
        // being a red (or, here, a declared-green) test and become a broken one.
        PropertyInfo? onRecord = typeof(AttemptRecord).GetProperty(
            "RouteWarm", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(
            onRecord is null,
            "AttemptRecord must NOT declare its own RouteWarm: a member hung directly off the attempt " +
            "record lands in serial mode and silently vanishes in worktree mode. Warmth must ride " +
            "AttemptProvenance instead, exactly like ModelDigest beside it.");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────

    private static TierResolution Route(string runnerName, string model) =>
        new() { RunnerName = runnerName, Model = model };

    private TaskNode NewTask(ActionKind kind)
    {
        string dir = Path.Combine(_root, "tasks", "01-task");
        return new TaskNode
        {
            Id = "01-task",
            Directory = dir,
            Description = "a task used only to carry an ActionDefinition into BuildProvenance",
            Action = new ActionDefinition
            {
                Path = Path.Combine(dir, kind == ActionKind.Prompt ? "action.prompt.md" : "action.ps1"),
                Kind = kind
            },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(dir, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        };
    }

    /// <summary>
    /// A real <see cref="TaskExecutor"/> over a real (empty) plan folder under <see cref="_root"/> — the
    /// six-argument construction <c>ExecutedDefinitionHashTests.RunSerialAsync</c> uses, minus the
    /// scheduler: this file drives <see cref="BuildProvenance"/> directly rather than a full run, so
    /// nothing here needs a task in <c>plan.Tasks</c> or a prompt-runner registry.
    /// </summary>
    private TaskExecutor NewExecutor()
    {
        var plan = new PlanDefinition
        {
            PlanDirectory = _root,
            Workspace = _root,
            Config = new RunConfig { Version = 1 },
            Tasks = [],
            Waves = []
        };

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);

        return new TaskExecutor(plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null);
    }

    /// <summary>
    /// Reflective invoke of the PINNED private <c>TaskExecutor.BuildProvenance(TaskNode, WorktreeHandle,
    /// TierResolution?)</c> — see the class doc comment. Resolved fresh per call (not cached) so a rename
    /// fails EVERY test that calls it with a clear reflection exception rather than a confusing null.
    /// </summary>
    private static AttemptProvenance? BuildProvenance(
        TaskExecutor executor, TaskNode task, WorktreeHandle worktree, TierResolution? route)
    {
        MethodInfo? method = typeof(TaskExecutor).GetMethod(
            "BuildProvenance", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(
            method is not null,
            "TaskExecutor no longer declares a private BuildProvenance(TaskNode, WorktreeHandle, " +
            "TierResolution?) — this is the pinned dependency the class doc comment names.");

        return (AttemptProvenance?)method!.Invoke(executor, [task, worktree, route]);
    }
}
