using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// JIT wave-breakdown DURABILITY (design of record <c>docs/plans/20-jit-breakdown-durability.md</c>, SSOT
/// §14.11 — issues #385, #402, #471, #489). Same shape as <see cref="SchedulerWaveBreakdownTests"/>: a real
/// on-disk waved plan, a real journal, a stub <see cref="IPromptRunner"/> that SIMULATES the plan-breakdown
/// sub-process. No real Claude call is ever made.
///
/// <para>What these pin, in the order the design argues them:</para>
/// <list type="number">
///   <item>the runner's <c>FailureKind</c> reaches the halt, so an operator is told WHICH bound was hit;</item>
///   <item>a CUT-OFF session can never be reported <c>BreakdownComplete</c>, whatever <c>validate</c> says;</item>
///   <item>an 11-of-14-shaped truncation survives as a valid PREFIX instead of being discarded whole;</item>
///   <item><b>the #471 regression</b> — a quarantine reverts exactly what the attempt wrote, leaves a human's
///     hand-authored wave gate byte-identical, and restores <c>PlanDefinitionHash</c> exactly;</item>
///   <item><b>the #489 regression</b> — Ctrl+C mid-breakdown leaves the plan folder LOADABLE.</item>
/// </list>
/// </summary>
public sealed class SchedulerBreakdownDurabilityTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- fakes -------------------------------------------------------------------------------------

    private sealed class GreenExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct) =>
            Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
    }

    /// <summary>
    /// A stub breakdown runner whose per-segment behaviour the test scripts: <paramref name="author"/> runs
    /// the simulated authoring, and <paramref name="failureKind"/> is the runner-agnostic classification the
    /// harness must now carry into the halt. <c>Completed:false</c> + a non-<c>None</c> kind is exactly the
    /// shape of the two measured truncations (a kill, with no terminal result).
    /// </summary>
    private sealed class StubBreakdownRunner(
        Action<PromptInvocation, int> author,
        PromptFailureKind failureKind = PromptFailureKind.None,
        int? numTurns = null) : IPromptRunner
    {
        public int Invocations { get; private set; }

        public List<string> Prompts { get; } = [];

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            Prompts.Add(invocation.ComposedPrompt);
            author(invocation, Invocations);
            bool clean = failureKind == PromptFailureKind.None;
            return Task.FromResult(new PromptResult
            {
                Completed = clean,
                IsError = false,
                ResultText = clean ? "authored the wave" : null,
                CostUsd = 0.10m,
                NumTurns = numTurns,
                FailureKind = failureKind,
                Summary = clean ? "breakdown authored the wave" : "breakdown was cut off"
            });
        }
    }

    /// <summary>A runner that authors a partial wave and then CANCELS the run — the operator's Ctrl+C (#489).</summary>
    private sealed class CancellingBreakdownRunner(Action<PromptInvocation> author, CancellationTokenSource cts)
        : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            author(invocation);
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static Scheduler NewScheduler(
        PlanDefinition plan, RunJournal journal, IWorktreeProvider provider, WaveBreakdownInvoker invoker) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: provider, observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null);

    // --- plan fixtures -----------------------------------------------------------------------------

    /// <summary>wave-01 authored; wave-02 a JIT stub with a brief; autonomyPolicy auto so the checkpoint fires.</summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) WavedPlan(bool withHandAuthoredGate = false)
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n- package\n- publish\n");
        if (withHandAuthoredGate)
        {
            // The pattern §5.1 protects: a HUMAN writes the wave's exit gate BEFORE the breakdown runs
            // ("define the postconditions, let the breakdown fill the tasks").
            b.WaveGuardrail(Wave2, "00-hand-authored-exit.sh", "#!/bin/sh\nexit 0\n");
        }

        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    /// <summary>Author a COMPLETE task folder (task.json + action + a guardrail) under wave-02.</summary>
    private static void AuthorTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    /// <summary>Author a HALF-WRITTEN task folder: a <c>task.json</c> and a <c>guardrails/</c>, no action file — the #385 artifact.</summary>
    private static void AuthorTruncatedTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    /// <summary>Write the breakdown's declared decomposition, as <c>plan-breakdown</c>'s first act would.</summary>
    private static void DeclareIntent(string planDir, params string[] folders)
    {
        string stateDir = Path.Combine(planDir, Wave2, "state");
        Directory.CreateDirectory(stateDir);
        string entries = string.Join(",\n    ",
            folders.Select(f => $$"""{ "folder": "{{f}}", "purpose": "author {{f}}" }"""));
        File.WriteAllText(Path.Combine(stateDir, BreakdownIntent.FileName),
            $$"""
            {
              "version": 1,
              "declaredAt": "2026-08-20T05:00:00Z",
              "tasks": [
                {{entries}}
              ]
            }
            """);
    }

    // --- 1. milestone 1: the runner's FailureKind reaches the halt ---------------------------------

    [Fact]
    public async Task CutOffSession_HaltDetail_NamesTheBoundThatWasHit_NotJustDidNotCompleteCleanly()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // A timeout kill that authored NOTHING usable: the halt must still say WHICH bound stopped it.
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            (_, _) => { }, PromptFailureKind.Timeout, numTurns: 35));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        // #504 renamed this bound: the 30-minute ceiling became a 4h BACKSTOP behind the stall bound, so
        // calling it "the breakdown timeout" would now misdescribe which budget stopped the session. The
        // property under test is unchanged — the halt must name the bound, not shrug.
        Assert.Contains("CUT OFF by the breakdown BACKSTOP", report.WaveHalt.Detail);
        Assert.Contains("used 35 of", report.WaveHalt.Detail); // the turn evidence §3.1 had to reconstruct
        Assert.DoesNotContain("did not complete cleanly", report.WaveHalt.Detail);
    }

    [Fact]
    public async Task TurnExhaustion_AndTimeout_AreNamedDifferently_BecauseOnlyOneRemedyIsABudget()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            (_, _) => { }, PromptFailureKind.MaxTurns, numTurns: 400));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        Assert.Contains("ran out of TURNS", report.WaveHalt!.Detail);
        Assert.DoesNotContain("CUT OFF by the breakdown timeout", report.WaveHalt.Detail);
    }

    // --- 2. milestone 2: a cut-off session can NEVER be reported complete --------------------------

    [Fact]
    public async Task CleanValidateAfterACutOffSession_IsStillIncomplete_NeverBreakdownComplete()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // The session authors THREE perfectly valid task folders and declares three — `guardrails validate`
        // is clean and the manifest is satisfied — but the runner reports it was killed at the timeout.
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner((inv, _) =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
            AuthorTask(inv.WorkingDirectory, "03-publish");
        }, PromptFailureKind.Timeout));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        // THE point of the rule: validate says the wave is fine, and the harness still refuses to call it
        // complete — a valid prefix that reads as a finished wave is worse than a loud quarantine.
        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);
        Assert.NotEqual(WaveHaltKind.BreakdownComplete, report.WaveHalt.Kind);
        Assert.Contains("never reported completion", report.WaveHalt.Detail);

        // The prefix is preserved, not quarantined.
        Assert.True(Directory.Exists(Path.Combine(b.PlanDir, Wave2, "tasks", "03-publish")));
    }

    // --- 3. milestone 3: an 11-of-14-shaped truncation survives as a valid PREFIX ------------------

    [Fact]
    public async Task TruncatedPrefix_IsSweptAndPreserved_BreakdownIncomplete_Gr2063NamesTheMissingFolders()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // Declares four, authors two completely, is killed halfway through the third, never starts the fourth
        // — the shape of the August truncation, scaled down.
        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return; // the resume segment makes no further progress → the no-progress rule stops the loop
            }

            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish", "04-announce");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "02-package");
            AuthorTruncatedTask(inv.WorkingDirectory, "03-publish");
        }, PromptFailureKind.Timeout);
        var invoker = new WaveBreakdownInvoker(runner);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        // The resume segment named only what was owed, and the complete folders as DONE.
        Assert.Equal(2, runner.Invocations);
        Assert.Contains("## RESUME — segment 2 of at most 3", runner.Prompts[1]);
        Assert.Contains("03-publish", runner.Prompts[1]);
        Assert.Contains("01-compile", runner.Prompts[1]); // named as ALREADY COMPLETE, not re-authored

        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);
        Assert.Contains("2 of 4 declared", report.WaveHalt.Headline);

        // The valid 2-task prefix is on disk; the half-written folder was SWEPT to rejected/, not left to
        // wedge the load, and not taken as grounds to discard the whole wave.
        string tasks = Path.Combine(b.PlanDir, Wave2, "tasks");
        Assert.True(Directory.Exists(Path.Combine(tasks, "01-compile")));
        Assert.True(Directory.Exists(Path.Combine(tasks, "02-package")));
        Assert.False(Directory.Exists(Path.Combine(tasks, "03-publish")));
        Assert.Contains(
            Directory.GetDirectories(Path.Combine(b.PlanDir, "logs"), "rejected", SearchOption.AllDirectories),
            r => Directory.Exists(Path.Combine(r, "tasks", "03-publish")));

        // The plan is LOADABLE and GR2063 names exactly what is still owed.
        PlanLoadResult reload = new PlanLoader().Load(b.PlanDir);
        Assert.False(reload.HasErrors);
        Diagnostic gr2063 = Assert.Single(
            new PlanValidator(FakeExecutableProbe.All).Validate(reload.Plan!),
            d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
        Assert.Equal(DiagnosticSeverity.Warning, gr2063.Severity);
        Assert.Contains("03-publish", gr2063.Message);
        Assert.Contains("04-announce", gr2063.Message);
        Assert.DoesNotContain("01-compile", gr2063.Message);
    }

    [Fact]
    public async Task PreservedPrefix_ReOpensTheJitCheckpointOnTheNextRun_InsteadOfRunningAsAFinishedWave()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner((inv, _) =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout));
        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        // The next run LOADS a wave that has one task. Without the manifest re-opening the checkpoint it would
        // simply RUN it — the "a truncated wave reads as finished" hazard, one run boundary later. The second
        // segment finishes the declaration, and only then is the wave complete.
        PlanDefinition plan2 = b.Load().Plan!;
        plan2 = plan2 with { Config = plan2.Config with { AutonomyPolicy = AutonomyPolicy.Auto } };
        Assert.Single(plan2.Waves.Single(w => w.Dir == Wave2).Tasks);

        var runner2 = new StubBreakdownRunner((inv, _) => AuthorTask(inv.WorkingDirectory, "02-package"));
        RunReport report2 = await NewScheduler(
                plan2, RunJournal.LoadOrCreate(plan2), new RecordingWorktreeProvider(),
                new WaveBreakdownInvoker(runner2))
            .RunAsync(plan2, Ct);

        Assert.Equal(1, runner2.Invocations);
        Assert.Equal(WaveHaltKind.BreakdownComplete, report2.WaveHalt!.Kind);

        // The manifest's lifetime is ONE attempt: it is gone once the wave settles complete, so GR2063 is
        // silent and the checkpoint stays closed thereafter.
        Assert.False(File.Exists(BreakdownIntent.PathFor(Path.Combine(b.PlanDir, Wave2))));
    }

    [Fact]
    public async Task NoManifest_CutOffSession_IsQuarantined_BecauseAPrefixWithNoDeclarationCannotBeToldFromAFinishedWave()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // A session cut off BEFORE it wrote the manifest. The prefix validates, but nothing durable would stop
        // the next run reading it as authored — so today's loud quarantine is the honest outcome.
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            (inv, _) => AuthorTask(inv.WorkingDirectory, "01-compile"), PromptFailureKind.Timeout));
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(plan, journal, new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.Contains(BreakdownIntent.FileName, report.WaveHalt.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(b.PlanDir, Wave2, "tasks")));
    }

    [Fact]
    public async Task AManifestThatDeclaresNothing_IsQuarantinedToo_ButTheHaltNeverClaimsThereIsNoManifest()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // A session that wrote a manifest whose every entry is unusable. Salvage is genuinely impossible —
        // there is nothing declared to resume — so the quarantine is right. What must NOT happen is the halt
        // asserting "the wave carries no manifest" while the file sits on disk (#471's lesson about text).
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            (inv, _) =>
            {
                string stateDir = Path.Combine(inv.WorkingDirectory, Wave2, "state");
                Directory.CreateDirectory(stateDir);
                File.WriteAllText(Path.Combine(stateDir, BreakdownIntent.FileName),
                    """{ "version": 1, "tasks": [ { "folder": "tasks/01-compile" } ] }""");
                AuthorTask(inv.WorkingDirectory, "01-compile");
            },
            PromptFailureKind.Timeout));

        RunReport report = await NewScheduler(plan, RunJournal.LoadOrCreate(plan),
            new RecordingWorktreeProvider(), invoker).RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.DoesNotContain("carries no", report.WaveHalt.Detail);
        Assert.Contains("GR2064", report.WaveHalt.Detail);
        Assert.Contains("tasks/01-compile", report.WaveHalt.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(b.PlanDir, Wave2, "tasks")));
    }

    // --- 4. bounded resume ------------------------------------------------------------------------

    [Fact]
    public async Task ResumeIsBounded_ASegmentThatAddsNoCompleteTaskFolder_HaltsRatherThanRetrying()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // Segment 1 authors one of two; segment 2 adds nothing. There must be no third segment.
        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;
            }

            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout);
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        RunReport report = await NewScheduler(
                plan, journal, new RecordingWorktreeProvider(), new WaveBreakdownInvoker(runner))
            .RunAsync(plan, Ct);

        Assert.Equal(2, runner.Invocations);
        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);
        Assert.Contains("added no complete task folder", report.WaveHalt.Detail);
    }

    [Fact]
    public async Task ResumeIsBounded_AtMostThreeSegmentsPerWavePerRun()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // Each segment makes real progress but never finishes: the cap, not the progress rule, must stop it.
        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call == 1)
            {
                DeclareIntent(inv.WorkingDirectory, "01-a", "02-b", "03-c", "04-d", "05-e");
            }

            AuthorTask(inv.WorkingDirectory, $"0{call}-{(char)('a' + call - 1)}");
        }, PromptFailureKind.Timeout);

        RunReport report = await NewScheduler(
                plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(),
                new WaveBreakdownInvoker(runner))
            .RunAsync(plan, Ct);

        Assert.Equal(3, runner.Invocations);
        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);
        Assert.Contains("3-segment cap", report.WaveHalt.Detail);
    }

    // --- 5. #471: the load-bearing quarantine-scope regression -------------------------------------

    [Fact]
    public async Task Quarantine_RevertsExactlyWhatTheAttemptWrote_LeavesAHandAuthoredGateUntouched_AndRestoresPlanDefinitionHash()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan(withHandAuthoredGate: true);
        using WavePlanBuilder _ = b;

        string handGate = Path.Combine(b.PlanDir, Wave2, "guardrails", "00-hand-authored-exit.sh");
        string handGateBytes = File.ReadAllText(handGate);
        string hashBefore = PlanDefinitionHash.Compute(b.Load().Plan!);

        // The attempt writes tasks AND its own wave gate AND a preflight, then fails validation. #471 measured
        // the shipped behaviour leaving EIGHT files behind while claiming "the wave reverted to its empty stub".
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner((inv, _) =>
        {
            AuthorTask(inv.WorkingDirectory, "01-compile");
            string gates = Path.Combine(inv.WorkingDirectory, Wave2, "guardrails");
            Directory.CreateDirectory(gates);
            File.WriteAllText(Path.Combine(gates, "10-attempt-exit.sh"),
                "# catches: a wrong implementation\n#!/bin/sh\nexit 0\n");
            string preflights = Path.Combine(inv.WorkingDirectory, Wave2, "preflights");
            Directory.CreateDirectory(preflights);
            File.WriteAllText(Path.Combine(preflights, "10-attempt-entry.sh"),
                "# catches: a missing dependency\n#!/bin/sh\nexit 0\n");

            // …and one task folder with no guardrails at all → the wave FAILS validate.
            string bad = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "02-bad");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "task.json"), """{ "description": "bad", "writeScope": [] }""");
            File.WriteAllText(Path.Combine(bad, "action.sh"), "#!/bin/sh\necho hi\n");
        }, PromptFailureKind.Timeout));

        RunReport report = await NewScheduler(
            plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker).RunAsync(plan, Ct);

        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);

        // (a) the HUMAN's gate is untouched — not moved, not rewritten
        Assert.True(File.Exists(handGate));
        Assert.Equal(handGateBytes, File.ReadAllText(handGate));

        // (b) everything the ATTEMPT wrote is under rejected/, preserving relative paths
        string rejected = Directory
            .GetDirectories(Path.Combine(b.PlanDir, "logs"), "rejected", SearchOption.AllDirectories)
            .Single(r => r.Replace('\\', '/').Contains($"/{Wave2}/breakdown/rejected"));
        Assert.True(File.Exists(Path.Combine(rejected, "guardrails", "10-attempt-exit.sh")));
        Assert.True(File.Exists(Path.Combine(rejected, "preflights", "10-attempt-entry.sh")));
        Assert.True(File.Exists(Path.Combine(rejected, "tasks", "01-compile", "task.json")));
        Assert.True(File.Exists(Path.Combine(rejected, "tasks", "02-bad", "task.json")));
        Assert.False(File.Exists(Path.Combine(rejected, "guardrails", "00-hand-authored-exit.sh")));

        // (c) THE assertion: a quarantine never spends the operator's review attestation
        Assert.Equal(hashBefore, PlanDefinitionHash.Compute(b.Load().Plan!));

        // (d) the message no longer lies — it states what moved and what was kept
        Assert.Contains("nothing that pre-dated it was touched", report.WaveHalt.Detail);
        Assert.Contains("00-hand-authored-exit.sh", report.WaveHalt.Detail);
        Assert.Contains("PlanDefinitionHash is unchanged", report.WaveHalt.Detail);
    }

    [Fact]
    public async Task Quarantine_RestoresAPreExistingGateTheAttemptOVERWROTE_ByteForByte()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan(withHandAuthoredGate: true);
        using WavePlanBuilder _ = b;

        string handGate = Path.Combine(b.PlanDir, Wave2, "guardrails", "00-hand-authored-exit.sh");
        string original = File.ReadAllText(handGate);
        string hashBefore = PlanDefinitionHash.Compute(b.Load().Plan!);

        var invoker = new WaveBreakdownInvoker(new WaveBreakdownInvokerFixtureRunner(inv =>
        {
            // The attempt OVERWRITES the human's gate and authors an invalid task.
            File.WriteAllText(
                Path.Combine(inv.WorkingDirectory, Wave2, "guardrails", "00-hand-authored-exit.sh"),
                "# catches: something else entirely\n#!/bin/sh\nexit 1\n");
            string bad = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "01-bad");
            Directory.CreateDirectory(bad);
            File.WriteAllText(Path.Combine(bad, "task.json"), """{ "description": "bad", "writeScope": [] }""");
            File.WriteAllText(Path.Combine(bad, "action.sh"), "#!/bin/sh\necho hi\n");
        }));

        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        // Classifying the overwrite is not enough — the human's bytes have to come BACK, or the "hash is
        // byte-identical after a quarantine" property fails in exactly the case the inventory exists for.
        Assert.Equal(original, File.ReadAllText(handGate));
        Assert.Equal(hashBefore, PlanDefinitionHash.Compute(b.Load().Plan!));
    }

    /// <summary>A minimal always-cut-off runner for fixtures that only care about what was written.</summary>
    private sealed class WaveBreakdownInvokerFixtureRunner(Action<PromptInvocation> author) : IPromptRunner
    {
        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            author(invocation);
            return Task.FromResult(new PromptResult
            {
                Completed = false,
                IsError = false,
                CostUsd = 0.1m,
                FailureKind = PromptFailureKind.Timeout,
                Summary = "cut off"
            });
        }
    }

    // --- 6. #489: Ctrl+C mid-breakdown must leave the plan folder LOADABLE -------------------------

    [Fact]
    public async Task CtrlC_MidBreakdown_LeavesThePlanFolderLoadable_AndDoesNotSpendTheAttestation()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan(withHandAuthoredGate: true);
        using WavePlanBuilder _ = b;

        string hashBefore = PlanDefinitionHash.Compute(b.Load().Plan!);
        string handGate = Path.Combine(b.PlanDir, Wave2, "guardrails", "00-hand-authored-exit.sh");
        string handGateBytes = File.ReadAllText(handGate);

        using var cts = new CancellationTokenSource();
        var invoker = new WaveBreakdownInvoker(new CancellingBreakdownRunner(inv =>
        {
            // Exactly the #385 artifact: a complete task, then a folder with a guardrails/ and no task.json.
            AuthorTask(inv.WorkingDirectory, "01-compile");
            string half = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "02-package");
            Directory.CreateDirectory(Path.Combine(half, "guardrails"));
            File.WriteAllText(Path.Combine(half, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
        }, cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
                .RunAsync(plan, cts.Token));

        // THE assertion (#489): the operator's own escape hatch must not manufacture the #385 artifact.
        // Before the fix, cancellation propagated PAST the quarantine and this load reported GR1004/GR1001.
        PlanLoadResult reload = new PlanLoader().Load(b.PlanDir);
        Assert.False(reload.HasErrors);
        Assert.DoesNotContain(new PlanValidator(FakeExecutableProbe.All).Validate(reload.Plan!),
            d => d.Severity == DiagnosticSeverity.Error);

        // And the cleanup is inventory-scoped like every other revert: the human's gate survives and the
        // attestation is not spent by a Ctrl+C.
        Assert.Equal(handGateBytes, File.ReadAllText(handGate));
        Assert.Equal(hashBefore, PlanDefinitionHash.Compute(reload.Plan!));
    }

    [Fact]
    public async Task CtrlC_MidBreakdown_KeepsAResumableDeclaredPrefix_RatherThanDiscardingIt()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        using var cts = new CancellationTokenSource();
        var invoker = new WaveBreakdownInvoker(new CancellingBreakdownRunner(inv =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
            AuthorTruncatedTask(inv.WorkingDirectory, "02-package");
        }, cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
                .RunAsync(plan, cts.Token));

        // Loadable (the half-written folder was swept) AND the declared prefix survived for the next run.
        PlanLoadResult reload = new PlanLoader().Load(b.PlanDir);
        Assert.False(reload.HasErrors);
        Assert.True(Directory.Exists(Path.Combine(b.PlanDir, Wave2, "tasks", "01-compile")));
        Assert.False(Directory.Exists(Path.Combine(b.PlanDir, Wave2, "tasks", "02-package")));
        Assert.True(File.Exists(BreakdownIntent.PathFor(Path.Combine(b.PlanDir, Wave2))));
    }

    [Fact]
    public async Task AStalledResume_HonestHalts_AndTheHeadlineDoesNotClaimTheWaveHasNoAuthoredTasks()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // Leave a resumable prefix, then make the checkpoint unable to invoke (halt policy). The halt must not
        // say "has no authored tasks" — it must name the prefix and the shortfall (#471's lesson about text).
        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner((inv, _) =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout));
        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        PlanDefinition halted = b.Load().Plan!;
        halted = halted with
        {
            Config = halted.Config with { AutonomyPolicy = AutonomyPolicy.Halt, AutoBreakdown = false }
        };

        RunReport report = await NewScheduler(
                halted, RunJournal.LoadOrCreate(halted), new RecordingWorktreeProvider(),
                new WaveBreakdownInvoker(new StubBreakdownRunner((_, _) => { })))
            .RunAsync(halted, Ct);

        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.DoesNotContain("has no authored tasks", report.WaveHalt.Headline);
        Assert.Contains("INCOMPLETE", report.WaveHalt.Headline);
        Assert.Contains("02-package", report.WaveHalt.Detail);
        Assert.Contains(BreakdownIntent.FileName, report.WaveHalt.Detail);
    }

    // --- 7. the forensic artifact ------------------------------------------------------------------

    [Fact]
    public async Task PreInvocationInventory_IsWrittenBesideTheTranscript_AsTheForensicRecord()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan(withHandAuthoredGate: true);
        using WavePlanBuilder _ = b;

        var invoker = new WaveBreakdownInvoker(new StubBreakdownRunner(
            (inv, _) => AuthorTask(inv.WorkingDirectory, "01-compile")));
        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), invoker)
            .RunAsync(plan, Ct);

        string[] manifests = Directory.GetFiles(
            Path.Combine(b.PlanDir, "logs"), "pre-invocation.json", SearchOption.AllDirectories);
        string manifest = Assert.Single(manifests);
        Assert.Contains($"/{Wave2}/breakdown/", manifest.Replace('\\', '/'));
        Assert.Contains("00-hand-authored-exit.sh", File.ReadAllText(manifest));
    }

    // --- 8. #501: the gate must judge SOUNDNESS, not COMPLETENESS ----------------------------------

    /// <summary>
    /// #501 — the gate records its reasoning on EVERY path, not only on rejection. The bug this closes was
    /// invisible in the logs: a prefix that should have been kept was reverted while GR2063 announced a
    /// resume, and the successful-salvage path printed no report at all, so there was nothing to read
    /// afterwards. A defect that resisted unit reproduction has to at least explain itself next time.
    /// </summary>
    [Fact]
    public async Task TheGateTeesItsDecision_OnASalvagePath_SoASilentWrongVerdictIsReadableAfterwards()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1) { return; }
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout);

        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), new WaveBreakdownInvoker(runner))
            .RunAsync(plan, Ct);

        string decision = Assert.Single(
            Directory.GetFiles(Path.Combine(b.PlanDir, "logs"), "gate-decision.txt", SearchOption.AllDirectories));
        string text = File.ReadAllText(decision);

        // The four facts that would have made the original contradiction obvious in one read.
        Assert.Contains("gate verdict :", text, StringComparison.Ordinal);
        Assert.Contains("prefix state :", text, StringComparison.Ordinal);
        Assert.Contains("intent manifest : usable", text, StringComparison.Ordinal);
        Assert.Contains("02-package", text, StringComparison.Ordinal);   // named among what is still owed

        // …and the verdict must be the COMPOSITE one (#512): this prefix is non-empty and valid, so PASS.
        Assert.Contains("gate verdict : PASS", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// #512 — a session that authored NOTHING is REJECTED, and the record must say so. The gate's real
    /// decision is <c>!valid || authoredTaskCount == 0</c>, but the tee printed only the <c>valid</c> half,
    /// so an empty prefix — which validates trivially — was recorded as <b>PASS</b>.
    /// <para>This is the shape a provider outage produces, and it is exactly what shipped: a wave-3
    /// breakdown killed by a 429 at turn 1 wrote "gate verdict : PASS (blocking=0, excused=0,
    /// authoredTasks=0)". Every case #501 was written against had authoredTasks &gt; 0, so the two verdicts
    /// agreed and the gap never showed.</para>
    /// </summary>
    [Fact]
    public async Task AnEmptyBreakdownIsRecordedAsREJECT_NotPassOnAVacuouslyValidPrefix()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        // Authors nothing at all and dies on a transient — the 429-at-turn-1 shape.
        var runner = new StubBreakdownRunner((_, _) => { }, PromptFailureKind.Transient);

        await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), new WaveBreakdownInvoker(runner))
            .RunAsync(plan, Ct);

        string decision = Assert.Single(
            Directory.GetFiles(Path.Combine(b.PlanDir, "logs"), "gate-decision.txt", SearchOption.AllDirectories));
        string text = File.ReadAllText(decision);

        Assert.Contains("gate verdict : REJECT", text, StringComparison.Ordinal);
        Assert.Contains("authoredTasks=0", text, StringComparison.Ordinal);
        Assert.Contains("nothing was authored", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gate verdict : PASS", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of #501's contract, and what keeps the fix from becoming a rubber stamp: only errors
    /// unsatisfiable BECAUSE the wave is unfinished are excused. A malformed task folder says the authored
    /// CONTENT is wrong, and resuming onto wrong content is worse than re-authoring.
    /// </summary>
    [Fact]
    public async Task APrefixWithAMalformedTask_IsStillRevertedWholesale_TheOverrideIsOneNamedCodeNotABlanketPass()
    {
        (WavePlanBuilder b, PlanDefinition plan) = WavedPlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1) { return; }
            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");

            // GR2041: a task.json with NO writeScope. An error about the CONTENT, not about being unfinished.
            string bad = Path.Combine(inv.WorkingDirectory, Wave2, "tasks", "02-package");
            Directory.CreateDirectory(Path.Combine(bad, "guardrails"));
            File.WriteAllText(Path.Combine(bad, "task.json"), """{ "description": "no writeScope" }""");
            File.WriteAllText(Path.Combine(bad, "action.sh"), "#!/bin/sh\necho hi\n");
            File.WriteAllText(Path.Combine(bad, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
        }, PromptFailureKind.Timeout);

        RunReport report = await NewScheduler(plan, RunJournal.LoadOrCreate(plan), new RecordingWorktreeProvider(), new WaveBreakdownInvoker(runner))
            .RunAsync(plan, Ct);

        string tasks = Path.Combine(b.PlanDir, Wave2, "tasks");
        Assert.False(Directory.Exists(Path.Combine(tasks, "01-compile")),
            "a prefix carrying a malformed task must be reverted wholesale, never resumed onto");
        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
    }
}
