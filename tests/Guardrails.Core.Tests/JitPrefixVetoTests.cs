using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// The <b>#501 regression, one diagnostic code over</b> — GR2060 (<c>UnproducibleGateRequirement</c>)
/// vetoing a JIT partial prefix that it cannot fairly veto.
///
/// <para><b>The defect these pin.</b> <c>Scheduler.ValidatePlanAfterBreakdown</c> excuses the errors a
/// knowingly-INCOMPLETE wave prefix cannot satisfy by construction, and the allow-list
/// (<c>Scheduler.UnsatisfiableWhileIncomplete</c>) is a single-code comparison against GR2028. GR2060 is
/// not in it. But a JIT partial prefix — five authored task folders of an intended twelve — has an
/// INCOMPLETE <c>writeScope</c> union by construction, so a gate requiring content one of the not-yet-
/// authored tasks would have produced looks to GR2060 exactly like a gate nothing in the plan can
/// produce. GR2060 is ERROR severity as of the task that shipped it, so that finding lands in
/// <c>blocking</c>, the gate rejects, and the authored prefix is reverted wholesale — which is verbatim
/// the shape #501 was opened for, and reverted JIT authoring is the most expensive thing this harness
/// can throw away.</para>
///
/// <para><b>Why <c>PlanIsClosed</c> is not the answer, and driving it would test the trap.</b>
/// <c>PlanValidator.PlanIsClosed</c> is <c>plan.Waves.All(w =&gt; w.Tasks.Count &gt; 0)</c>. It detects an
/// EMPTY STUB wave and returns <b>true</b> for an authored prefix, because 5 &gt; 0. The two suppressions
/// are complementary, never alternatives. So the prefix here is built the way the harness really builds
/// one: a breakdown session that writes a <c>state/breakdown-intent.json</c> declaring three folders and
/// is cut off after one, leaving <c>wavePrefixIsIncomplete</c> true from the manifest's own knowledge
/// rather than inferred from the wave's shape.</para>
///
/// <para><b>What "excused" has to mean, and the way a mitigation can be worse than the bug.</b> Excusing
/// a finding means it stops casting a veto it cannot fairly cast. It does NOT mean the finding vanishes:
/// an operator reading <c>gate-decision.txt</c> must still see the GR2060 finding, its witness and its
/// path. A fix that made the error disappear would satisfy
/// <see cref="PartialPrefix_TrippingGr2060_IsNotReverted"/> and hide a real impossibility from the only
/// person who can act on it — hence
/// <see cref="PartialPrefix_TrippingGr2060_StillReportsTheFinding"/>.</para>
///
/// <para><b>The two boundary controls.</b>
/// <see cref="CompletePlan_TrippingGr2060_IsStillBlocked"/> holds the line against over-correction: a
/// plan whose manifest owes nothing genuinely cannot produce its own gate's requirement, and must still
/// be blocked, or GR2060 stops meaning anything. <see cref="PlainValidate_OnAPartialPrefix_StillErrors"/>
/// holds the other line: the suppression belongs to the JIT breakdown GATE, not to <c>validate</c>. A
/// human running <c>guardrails validate</c> on a partial prefix is asking a different question — "is this
/// plan sound to run as it stands" — and must still get the error.</para>
///
/// <para><b>Real seams, not fakes, for everything GR2060 reads.</b> The gate builds its own
/// <c>new PlanValidator()</c> in-process, so the git-tracked-file probe and the interpreter probe are the
/// production ones and cannot be injected. Rather than fight that, the fixture satisfies them honestly:
/// the plan's workspace IS this repository, and the file the gate requires a literal in is this
/// repository's own tracked <c>README.md</c>, which really is tracked and really does not contain the
/// witness. Nothing here writes to the repository — the plan folder lives in a temp directory and the
/// workspace is only ever read. Where those seams cannot answer (no git, no PowerShell interpreter) the
/// tests SKIP with a reason rather than assert something they have not measured.</para>
/// </summary>
public sealed class JitPrefixVetoTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    /// <summary>The plan-root terminal gate carrying the unproducible requirement.</summary>
    private const string GateFileName = "01-readme-must-document-the-veto.ps1";

    /// <summary>
    /// The workspace file the gate requires a literal in: tracked by git, outside the plan folder, and
    /// declared by no task in the plan — GR2060's conditions 6, 7 and 8, satisfied by a real file rather
    /// than by a fixture that merely claims them.
    /// </summary>
    private const string RequiredPath = "README.md";

    /// <summary>
    /// The exact literal the gate requires. De-regexable to one witness (condition 4) and absent from
    /// <see cref="RequiredPath"/>'s bytes (condition 5) — asserted, not assumed.
    /// </summary>
    private const string Witness = "JitPrefixVetoWitness";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── the real seams this fixture depends on ───────────────────────────────────────────────────────

    /// <summary>
    /// The repository this test file was compiled from — two levels above the test project, the same
    /// anchor <see cref="ProducerCoverageTests"/> uses. It is the plan's WORKSPACE here, which is what
    /// makes the two halves of condition 6 agree: <c>GitLsFilesProbe</c> asks git about a path anchored to
    /// the repository the process is running in, and condition 5 reads that same path's bytes under
    /// <c>plan.Workspace</c>.
    /// </summary>
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));

    /// <summary>
    /// Does the PRODUCTION probe — the one the gate's own <c>new PlanValidator()</c> builds — report
    /// <see cref="RequiredPath"/> as tracked from here? Anything other than a <c>true</c> answer (no git,
    /// a test host launched outside the checkout) means condition 6 cannot be satisfied honestly, and
    /// these tests skip rather than assert over a probe that never answered.
    /// </summary>
    private static readonly bool RequiredPathIsTracked =
        new GitLsFilesProbe().AreTracked([RequiredPath]) is { } answers
        && answers.TryGetValue(RequiredPath, out bool? tracked)
        && tracked == true;

    /// <summary>
    /// Can a <c>.ps1</c> guardrail resolve an interpreter here? The gate's validator uses the real PATH
    /// probe, so on a machine with no PowerShell the fixture's plan carries an UnresolvableInterpreter
    /// error and every verdict below would be about that instead of about GR2060.
    /// </summary>
    private static readonly bool PowerShellGuardrailsResolve =
        new InterpreterMap(new PathExecutableProbe()).Resolve("gate.ps1", []).Status
            == InterpreterMap.Status.Resolved;

    private static void SkipUnlessTheRealSeamsAnswer()
    {
        Assert.SkipUnless(RequiredPathIsTracked,
            $"git cannot confirm '{RequiredPath}' is tracked from this test host, so GR2060's condition 6 "
            + "cannot be satisfied without faking the probe the gate itself builds. Run the suite from "
            + "inside a git checkout of this repository.");
        Assert.SkipUnless(PowerShellGuardrailsResolve,
            "no interpreter for '.ps1' resolves on PATH, so the fixture plan would carry an interpreter "
            + "error and the gate's verdict would not be about GR2060.");

        // A fixture invariant, not an environment question: if the repository's README ever gained this
        // witness the requirement would be SATISFIED and every silence below would be vacuous.
        string readme = Path.Combine(RepositoryRoot, RequiredPath);
        Assert.True(File.Exists(readme), $"the fixture requires '{readme}' to exist.");
        Assert.DoesNotContain(Witness, File.ReadAllText(readme), StringComparison.Ordinal);
    }

    // ── 1. the prefix survives the gate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The regression.</b> A breakdown session declares three task folders, authors one completely and
    /// is cut off. The manifest still owes two, so the wave on disk is a KNOWINGLY partial prefix and
    /// <c>wavePrefixIsIncomplete</c> is true. The plan-root gate requires a literal in a tracked workspace
    /// file no task declares — which is exactly what an unfinished wave looks like, because the tasks that
    /// would have owned that file have not been authored yet — so GR2060 fires.
    ///
    /// <para>That finding must not cast a veto. The prefix is the thing the manifest exists to preserve;
    /// discarding it means re-paying for authoring the harness already bought, and the JIT checkpoint
    /// re-opens on the next run to finish what is owed.</para>
    /// </summary>
    [Fact]
    public async Task PartialPrefix_TrippingGr2060_IsNotReverted()
    {
        SkipUnlessTheRealSeamsAnswer();

        (WavePlanBuilder b, PlanDefinition plan) = UnproduciblePlan();
        using WavePlanBuilder _ = b;

        // Declares three, authors one, is killed — and the second segment makes no further progress, so
        // the bounded-resume rule stops the loop rather than a cap.
        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;
            }

            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout);

        RunReport report = await NewScheduler(plan, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        // Fixture integrity first: if GR2060 never fired, everything below would pass for the wrong
        // reason and this file would pin nothing at all.
        string decision = GateDecision(b.PlanDir);
        AssertGr2060Fired(decision);

        // THE assertion: the authored prefix is still on disk.
        Assert.True(
            Directory.Exists(Path.Combine(b.PlanDir, Wave2, "tasks", "01-compile")),
            "a GR2060 finding a partial prefix cannot satisfy by construction must not revert the prefix");

        // …and the run settled on the salvage path, not the quarantine one.
        Assert.Equal(WaveHaltKind.BreakdownIncomplete, report.WaveHalt!.Kind);

        // The manifest survives, so the JIT checkpoint really can resume what is still owed. Without it
        // the "preserved" prefix would read as a finished wave on the next run.
        Assert.True(File.Exists(BreakdownIntent.PathFor(Path.Combine(b.PlanDir, Wave2))));
    }

    // ── 2. excused is not vanished ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The half that is easy to get wrong.</b> Suppressing the veto must not suppress the FINDING. The
    /// impossibility GR2060 spotted may well be real — a requirement that no task will ever own, which a
    /// human has to resolve by giving some task the file or by dropping a requirement that does not belong
    /// — and the gate decision is the only place an operator reads it. A mitigation that dropped the
    /// diagnostic would pass <see cref="PartialPrefix_TrippingGr2060_IsNotReverted"/> and be a worse bug
    /// than the one it fixed: it would convert a loud wrong revert into a silent wrong pass, which is this
    /// project's most frequently recurring defect shape.
    ///
    /// <para>So three things are pinned together: the verdict is PASS, GR2060 is named as EXCUSED rather
    /// than as blocking, and the finding's own text — witness and path — is still in the record.</para>
    /// </summary>
    [Fact]
    public async Task PartialPrefix_TrippingGr2060_StillReportsTheFinding()
    {
        SkipUnlessTheRealSeamsAnswer();

        (WavePlanBuilder b, PlanDefinition plan) = UnproduciblePlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;
            }

            DeclareIntent(inv.WorkingDirectory, "01-compile", "02-package", "03-publish");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        }, PromptFailureKind.Timeout);

        await NewScheduler(plan, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        string decision = GateDecision(b.PlanDir);
        AssertGr2060Fired(decision);

        // The gate let the prefix through…
        Assert.Contains("gate verdict : PASS", decision, StringComparison.Ordinal);
        Assert.Contains("prefix state : KNOWINGLY INCOMPLETE", decision, StringComparison.Ordinal);

        // …and said WHY: GR2060 was excused, not blocking. The two lines are mutually exclusive by
        // construction, so asserting both directions is what tells a suppression that FIRED from one that
        // never ran.
        Assert.Contains(
            "excused (#501): " + DiagnosticCodes.UnproducibleGateRequirement, decision, StringComparison.Ordinal);
        Assert.DoesNotContain("blocking     :", decision, StringComparison.Ordinal);

        // THE assertion: the finding itself is still there for a human to act on. Excused means "casts no
        // veto", never "vanishes".
        Assert.Contains(Witness, decision, StringComparison.Ordinal);
        Assert.Contains(RequiredPath, decision, StringComparison.Ordinal);
    }

    // ── 3. the anti-over-correction control ──────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The excuse is scoped to an incomplete prefix, not a licence.</b> Same plan, same gate, same
    /// unproducible requirement — but the session ends cleanly and its manifest declares exactly what it
    /// authored, so the wave is FINISHED and <c>wavePrefixIsIncomplete</c> is false. Here GR2060's verdict
    /// is the true one: this plan genuinely cannot produce its own gate's requirement, and running it
    /// would spend the whole DAG before finding out.
    ///
    /// <para>If the mitigation leaked to complete plans, GR2060 would stop meaning anything and the
    /// $115.32 lesson it encodes would have to be paid again. Note that <c>PlanIsClosed</c> is TRUE in
    /// both this test and the two above — every declared wave holds a task in both — which is precisely
    /// why it cannot be the discriminator.</para>
    /// </summary>
    [Fact]
    public async Task CompletePlan_TrippingGr2060_IsStillBlocked()
    {
        SkipUnlessTheRealSeamsAnswer();

        (WavePlanBuilder b, PlanDefinition plan) = UnproduciblePlan();
        using WavePlanBuilder _ = b;

        // Declares one, authors one, terminates cleanly: nothing is owed, so nothing is unsatisfiable
        // "because the wave is unfinished".
        var runner = new StubBreakdownRunner((inv, _) =>
        {
            DeclareIntent(inv.WorkingDirectory, "01-compile");
            AuthorTask(inv.WorkingDirectory, "01-compile");
        });

        RunReport report = await NewScheduler(plan, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        string decision = GateDecision(b.PlanDir);
        AssertGr2060Fired(decision);
        Assert.Contains("prefix state : not flagged incomplete", decision, StringComparison.Ordinal);
        Assert.Contains(
            "blocking     : " + DiagnosticCodes.UnproducibleGateRequirement, decision, StringComparison.Ordinal);
        Assert.DoesNotContain("excused (#501)", decision, StringComparison.Ordinal);
        Assert.Contains("gate verdict : REJECT", decision, StringComparison.Ordinal);

        // The wave is quarantined and the halt carries the report, so the operator sees the impossibility
        // rather than a bare rejection.
        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.Contains(DiagnosticCodes.UnproducibleGateRequirement, report.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(b.PlanDir, Wave2, "tasks", "01-compile")));
    }

    // ── 4. the other boundary: validate is a different question ──────────────────────────────────────

    /// <summary>
    /// <b>The suppression belongs to the gate, not to <c>validate</c>.</b> This builds the same partial
    /// prefix directly on disk — one authored folder, a manifest still owing two — and asks it the
    /// question <c>guardrails validate</c> asks, through the same <c>new PlanValidator()</c> the CLI's
    /// <c>PlanProbe</c> builds. A human running validate on a partial prefix wants to know whether the
    /// plan as it stands is sound to run, and the answer is no: the gate on disk today cannot be
    /// satisfied by anything on disk today.
    ///
    /// <para>Teaching <c>ProducerCoverage</c> itself about incomplete prefixes — rather than the gate that
    /// knows about them — would silence this error for every caller, including the operator who asked. The
    /// GR2063 warning is asserted alongside it as the fixture's own proof that this really is a partial
    /// prefix and not merely a plan with a missing task.</para>
    /// </summary>
    [Fact]
    public void PlainValidate_OnAPartialPrefix_StillErrors()
    {
        SkipUnlessTheRealSeamsAnswer();

        (WavePlanBuilder b, PlanDefinition _) = UnproduciblePlan();
        using WavePlanBuilder builder = b;

        DeclareIntent(b.PlanDir, "01-compile", "02-package", "03-publish");
        AuthorTask(b.PlanDir, "01-compile");

        PlanLoadResult reload = new PlanLoader().Load(b.PlanDir);
        Assert.False(reload.HasErrors);
        IReadOnlyList<Diagnostic> diagnostics = new PlanValidator().Validate(reload.Plan!);

        // The fixture really is a PARTIAL prefix: the manifest still owes two folders, and every declared
        // wave holds a task, so PlanIsClosed is true and condition 10 does not suppress.
        Diagnostic incomplete = Assert.Single(
            diagnostics, d => d.Code == DiagnosticCodes.WaveBreakdownIncomplete);
        Assert.Equal(DiagnosticSeverity.Warning, incomplete.Severity);
        Assert.True(PlanValidator.PlanIsClosed(reload.Plan!));

        // THE assertion: validate still ERRORS, naming the witness and the path.
        Diagnostic unproducible = Assert.Single(
            diagnostics, d => d.Code == DiagnosticCodes.UnproducibleGateRequirement);
        Assert.Equal(DiagnosticSeverity.Error, unproducible.Severity);
        Assert.Contains(Witness, unproducible.Message, StringComparison.Ordinal);
        Assert.Contains(RequiredPath, unproducible.Message, StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The gate that cannot pass: a plan-root terminal guardrail requiring <see cref="Witness"/> in
    /// <see cref="RequiredPath"/>. Written in the MEASURED instance's own one-hop shape —
    /// <c>$v = if (Test-Path 'X') { Get-Content -Raw 'X' } else { "" }</c> — rather than the direct
    /// <c>Get-Content</c> form, because that is the shape GR2060's extractor was built to read.
    /// </summary>
    private static readonly string GateBody =
        "$ErrorActionPreference = 'Continue'\n"
        + "$failures = @()\n"
        + $"$readme = if (Test-Path '{RequiredPath}') {{ Get-Content -Raw '{RequiredPath}' }} else {{ \"\" }}\n"
        + $"if ($readme -cnotmatch '{Witness}') {{\n"
        + $"    $failures += \"{RequiredPath} does not carry the marker this gate requires\"\n"
        + "}\n"
        + "if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }\n"
        + "exit 0\n";

    /// <summary>
    /// wave-01 authored, wave-02 a JIT stub with a brief, and a plan-root gate carrying the unproducible
    /// requirement. <c>autonomyPolicy: auto</c> so the between-wave checkpoint fires without a human.
    ///
    /// <para>The workspace is set to <see cref="RepositoryRoot"/> — read only, never written — so the two
    /// facts GR2060 reads about <see cref="RequiredPath"/> come from the same real tree: git's index says
    /// it is tracked, and its bytes say the witness is absent. <c>maxParallelism: 1</c> keeps GR2015 and
    /// GR2028 out of the fixture's diagnostic list, so the gate's verdict turns on GR2060 alone.</para>
    /// </summary>
    private static (WavePlanBuilder Builder, PlanDefinition Plan) UnproduciblePlan()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n- package\n- publish\n");
        b.PlanGuardrail(GateFileName, GateBody);
        b.EditConfig($$"""
            { "version": 1, "maxParallelism": 1, "workspace": {{JsonSerializer.Serialize(RepositoryRoot)}} }
            """);

        PlanLoadResult loaded = b.Load();
        Assert.DoesNotContain(loaded.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        PlanDefinition plan = loaded.Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    /// <summary>
    /// A COMPLETE task folder under wave-02, with a <c>writeScope</c> naming a file it genuinely could
    /// own. Non-empty on purpose: condition 8's answer then has to be a real "no task declares
    /// <see cref="RequiredPath"/>" rather than the degenerate one an all-empty union would give.
    /// </summary>
    private static void AuthorTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": ["src/Guardrails.Core/Execution/Scheduler.cs"] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    /// <summary>
    /// Write the breakdown's declared decomposition, as <c>plan-breakdown</c>'s first act would. This —
    /// not the wave's shape — is what makes the prefix KNOWINGLY incomplete: the harness reads it before
    /// the gate runs, so <c>wavePrefixIsIncomplete</c> comes from the session's own declaration.
    /// </summary>
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
              "declaredAt": "2026-09-02T05:00:00Z",
              "tasks": [
                {{entries}}
              ]
            }
            """);
    }

    /// <summary>The gate's teed reasoning — the artifact #501 added so a wrong verdict is readable afterwards.</summary>
    private static string GateDecision(string planDir) =>
        File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(planDir, "logs"), "gate-decision.txt", SearchOption.AllDirectories)));

    /// <summary>
    /// The fixture's own precondition, asserted before any verdict is read: GR2060 really did fire. Every
    /// assertion in this file is about what the gate DOES with that finding, so a fixture that quietly
    /// stopped producing it would let all four tests pass while pinning nothing.
    ///
    /// <para>The one way this can fail on a healthy tree is an environment race: the tracked-file probe the
    /// gate builds is anchored to whatever repository the process is pointed at, and a concurrently running
    /// test that sets <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> (see <see cref="ProducerCoverageTests"/>) would
    /// redirect it mid-run, so condition 6 answers "not tracked" and GR2060 goes silent. That is a flake in
    /// the fixture, not a finding about the gate — hence the message says so rather than leaving a reader
    /// to rediscover it.</para>
    /// </summary>
    private static void AssertGr2060Fired(string decision) =>
        Assert.True(
            decision.Contains(DiagnosticCodes.UnproducibleGateRequirement, StringComparison.Ordinal),
            $"the fixture did not trip {DiagnosticCodes.UnproducibleGateRequirement}, so nothing below is "
            + "evidence about the gate. The likeliest cause is that the production tracked-file probe was "
            + "pointed at another repository mid-run by a concurrent test's GIT_DIR/GIT_WORK_TREE window. "
            + "Gate decision was:\n" + decision);

    private static Scheduler NewScheduler(PlanDefinition plan, WaveBreakdownInvoker invoker) =>
        new(plan, new GreenExecutor(), RunJournal.LoadOrCreate(plan),
            worktreeProvider: new RecordingWorktreeProvider(), observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null);

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────────

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
    /// A stub breakdown runner whose per-segment behaviour the test scripts, mirroring
    /// <see cref="SchedulerBreakdownDurabilityTests"/>: <c>Completed:false</c> plus a non-<c>None</c>
    /// failure kind is the shape of a real truncation, and the default (<c>None</c>) is a session that
    /// ended cleanly.
    /// </summary>
    private sealed class StubBreakdownRunner(
        Action<PromptInvocation, int> author,
        PromptFailureKind failureKind = PromptFailureKind.None) : IPromptRunner
    {
        private int _invocations;

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            author(invocation, ++_invocations);
            bool clean = failureKind == PromptFailureKind.None;
            return Task.FromResult(new PromptResult
            {
                Completed = clean,
                IsError = false,
                ResultText = clean ? "authored the wave" : null,
                CostUsd = 0.10m,
                FailureKind = failureKind,
                Summary = clean ? "breakdown authored the wave" : "breakdown was cut off"
            });
        }
    }
}
