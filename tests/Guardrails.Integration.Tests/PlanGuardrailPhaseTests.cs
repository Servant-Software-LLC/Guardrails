using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// RED integration tests for the terminal plan-guardrail phase (preflights-impl deliverable 4). They
/// drive the REAL scheduler / CLI over committed fixture plans that carry a plan-level
/// <c>&lt;plan&gt;/guardrails/</c> folder (loaded by the four-folder loader, deliverable 6) and assert on the
/// observable outcomes: the process exit code, the top-level <c>planGuardrails</c> journal section
/// (deliverable 4/5), the durable plan branch, the terminal-only resume, and the
/// <c>--revalidate-task plan:guardrails</c> behavior.
///
/// <para>
/// The tests COMPILE against the current scheduler + CLI surface + the <c>planGuardrails</c> journal
/// section + the four-folder loader, but FAIL on current code: there is no terminal
/// <c>&lt;plan&gt;/guardrails/</c> phase yet. Today's terminal <c>integrationGate</c> gate only re-runs the
/// per-task <c>scope:"integration"</c> set — none of these fixtures declare one — so a plan with a RED
/// <c>&lt;plan&gt;/guardrails/</c> check drains green and exits 0, and the <c>planGuardrails</c> section is
/// never written. That RED bar is intentional; deliverable 4 turns it green. The tests do NOT implement
/// the phase.
/// </para>
///
/// <para>
/// Behaviours 1–3 are exercised in BOTH serial (<c>maxParallelism = 1</c>, shared workspace) AND worktree
/// (<c>maxParallelism &gt; 1</c>, git repo + plan branch) mode. Behaviour 4 (no per-union regression) is a
/// worktree-only NO-REGRESSION pin — a serial run merges no parallel branches, so there is no merged-HEAD
/// union for the §4.3 re-verify to run against — and it is GREEN on both current and new code by design.
/// </para>
///
/// <para>
/// Reuses the established integration harnesses: the <see cref="CommandFactory.BuildRootCommand"/> +
/// <see cref="StringConsoleIo"/> CLI drive (RevalidateCliTests / MergeOnSuccessTests), the
/// Windows-safe temp git repo, and the hand-wired Scheduler + <c>SpyReVerifier</c> pattern
/// (MergeLockAndSettleTests / TopologyReuseForkSchedulerTests) for the per-union pin.
/// </para>
/// </summary>
[Trait("Category", "Preflights")]
public sealed class PlanGuardrailPhaseTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLI drive + journal helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drive the REAL root command with a per-invocation captured console; return the exit code.</summary>
    private static async Task<int> RunCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>As <see cref="RunCliAsync"/> but also returns the captured console stdout (D4).</summary>
    private static async Task<(int Exit, string Out)> RunCliCapturedAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 1 — plan-guardrail RED → durable terminal halt (exit 2), each in serial + worktree
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serial mode: a plan whose DAG drains green but whose terminal <c>&lt;plan&gt;/guardrails/</c> check is
    /// RED must halt AFTER the DAG settles — process exit 2, <c>planGuardrails.status</c> failed — while the
    /// task work stays DURABLE (every task succeeded and its workspace artifact survives the halt).
    /// RED on current code: no terminal phase, so the run exits 0 and <c>planGuardrails</c> is absent.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_SerialMode_DurableTerminalHalt_ExitsTwo()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);

        int exit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = ReadJournal(plan.PlanDir);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);

        // Durable: the DAG's work survived the terminal halt (tasks green, artifacts on disk).
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["01-a"].Status);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["02-b"].Status);
        Assert.True(File.Exists(Path.Combine(plan.PlanDir, "src", "01-a.cs")),
            "serial durability: 01-a's workspace artifact must survive the terminal halt");
        Assert.True(File.Exists(Path.Combine(plan.PlanDir, "src", "02-b.cs")),
            "serial durability: 02-b's workspace artifact must survive the terminal halt");
    }

    /// <summary>
    /// Worktree mode: same RED terminal gate → exit 2, <c>planGuardrails.status</c> failed, and the plan
    /// branch (<c>guardrails/&lt;plan&gt;</c>) STILL carries every task's committed work (durable). A single
    /// linear chain forms no union, so the worktree-mode terminal-gate content-teeth rule (GR2028) is exempt.
    /// RED on current code: the per-task integration set is empty, so the terminal gate never fires — the run
    /// exits 0 and <c>planGuardrails</c> is absent.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_WorktreeMode_DurableTerminalHalt_ExitsTwo()
    {
        using var plan = new PlanGuardrailPlan(worktree: true);

        int exit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = ReadJournal(plan.PlanDir);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["01-a"].Status);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["02-b"].Status);

        // Durable: the plan branch STILL carries all task work despite the terminal halt.
        string tree = plan.Git("ls-tree", "-r", "--name-only", plan.PlanBranch);
        Assert.Contains("src/01-a.cs", tree, StringComparison.Ordinal);
        Assert.Contains("src/02-b.cs", tree, StringComparison.Ordinal);
    }

    /// <summary>
    /// D4: on a terminal <c>plan-guardrail-failed</c> halt, <c>RunCommand</c> must surface each failed
    /// check's name + reason INLINE in the console (read from the journaled
    /// <c>planGuardrails.failedChecks</c>), not just a bare "see planGuardrails in run.json" pointer — so
    /// a terminal halt is as legible as the legacy per-task gate. The fixture's terminal check echoes
    /// "plan-guardrail terminal gate RED (deliberate)", which becomes the failed check's reason.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_SurfacesFailedCheckNameAndReasonInConsole()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);

        (int exit, string output) = await RunCliCapturedAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);

        // The failed check's NAME (the terminal guardrail's filename stem) is surfaced inline...
        Assert.Contains("01-terminal", output);
        // ...and its actionable REASON (the check's stdout) is surfaced inline, not hidden in run.json.
        Assert.Contains("plan-guardrail terminal gate RED (deliberate)", output);
    }

    /// <summary>
    /// Issue #272 Part 1 (the plan-level analogue of #179): when a terminal <c>&lt;plan&gt;/guardrails/</c>
    /// check emits PREAMBLE noise first (an <c>npm ci</c> line) and re-emits the REAL failure detail at the
    /// END of stdout, the journaled <c>planGuardrails.failedChecks[].reason</c> must carry the TAIL (the
    /// re-emitted detail), NOT the early preamble line. Pre-#272 the reason was the FIRST non-empty line, so
    /// a failing gate reported <c>added 464 packages…</c> and hid the vitest error — zero actionable signal.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_Reason_CarriesReEmittedTail_NotPreambleFirstLine()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);
        // Overwrite the terminal check with one that prints npm-ci preamble noise FIRST, then re-emits the
        // real failure detail at the END (the #179 convention) before exiting non-zero.
        WriteScript(plan.TerminalCheckPath, PreambleThenTailTerminalScript());

        (int exit, string output) = await RunCliCapturedAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument doc = ReadJournal(plan.PlanDir);
        Assert.NotNull(doc.PlanGuardrails);
        FailedGuardrail check = Assert.Single(doc.PlanGuardrails!.FailedChecks);

        // The stored reason carries the re-emitted TAIL — both the specific FAIL line and the summary line...
        Assert.Contains("FAIL  dsl-tools/dfd.test.ts", check.Reason, StringComparison.Ordinal);
        Assert.Contains("vitest suite is not green at the terminal gate", check.Reason, StringComparison.Ordinal);
        // ...and NOT the npm-ci preamble first line (the pre-#272 mis-reported reason).
        Assert.DoesNotContain("added 464 packages", check.Reason, StringComparison.Ordinal);

        // The console halt block surfaces the same tail detail (and not the preamble).
        Assert.Contains("vitest suite is not green at the terminal gate", output, StringComparison.Ordinal);
        Assert.DoesNotContain("added 464 packages", output, StringComparison.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 2 — B2 terminal-only resume: all tasks SKIP (no attempt burned), ONLY the terminal
    // phase re-fires. Method names carry "Resume".
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serial mode B2 terminal-only resume: after a terminal halt, a plain <c>guardrails run</c> (resume)
    /// must SKIP every already-succeeded task (no attempt burned — the task actions do not re-run) and re-fire
    /// ONLY the terminal <c>&lt;plan&gt;/guardrails/</c> phase, which is still RED → exit 2 again.
    /// RED on current code: the very first run exits 0 (no terminal phase), so the exit-2 assertion fails.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_SerialMode_TerminalOnlyResume_SkipsTasks_ReFiresTerminal()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);

        int firstExit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        int actionRunsAfterFirst = plan.ActionRunCount;

        int resumeExit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, resumeExit);

        // No attempt burned: the task actions did NOT re-run on the resume (only the terminal phase re-fired).
        Assert.Equal(actionRunsAfterFirst, plan.ActionRunCount);

        JournalDocument doc = ReadJournal(plan.PlanDir);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["01-a"].Status);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["02-b"].Status);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);
    }

    /// <summary>
    /// Worktree mode B2 terminal-only resume: the resume rule skips every already-integrated task (no attempt
    /// burned) and re-fires ONLY the terminal phase on the current merged HEAD (still RED) → exit 2 again.
    /// RED on current code: the first run exits 0 (no terminal phase).
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRed_WorktreeMode_TerminalOnlyResume_SkipsTasks_ReFiresTerminal()
    {
        using var plan = new PlanGuardrailPlan(worktree: true);

        int firstExit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        int actionRunsAfterFirst = plan.ActionRunCount;

        int resumeExit = await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, resumeExit);

        Assert.Equal(actionRunsAfterFirst, plan.ActionRunCount);

        JournalDocument doc = ReadJournal(plan.PlanDir);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["01-a"].Status);
        Assert.Equal(TaskStatus.Succeeded, doc.Tasks["02-b"].Status);
        Assert.NotNull(doc.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, doc.PlanGuardrails!.Status);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 3 — B2 revalidate via the EXISTING --revalidate-task string arg with value
    // "plan:guardrails". Method names carry "Revalidate". Green settle ⇒ exit 0; still-red ⇒ exit 2.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serial mode B2 revalidate: after a terminal halt, a human hand-fixes the merged HEAD so the terminal
    /// gate now passes, then <c>guardrails run --revalidate-task plan:guardrails</c> re-runs ONLY the
    /// <c>&lt;plan&gt;/guardrails/</c> checks against the current workspace → green settle, exit 0.
    /// The reserved synthetic id <c>plan:guardrails</c> is driven purely as the EXISTING string CLI argument
    /// (no new C# symbol) so this test compiles against the current CLI. RED on current code: the arg resolves
    /// to no task and the CLI reports "Unknown task" (exit 1), so the exit-0 assertion fails.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRevalidate_SerialMode_HandFixGreenSettles_ExitsZero()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);

        // A normal run drains the DAG green then halts on the RED terminal gate (establishes the state).
        await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        // Human hand-fixes the merged HEAD so the terminal gate now passes.
        plan.HandFixTerminalGate();

        int revalExit = await RunCliAsync("run", plan.PlanDir, "--revalidate-task", "plan:guardrails");
        Assert.Equal(ExitCodes.Success, revalExit);
    }

    /// <summary>
    /// Serial mode B2 revalidate, still-failing gate: with NO fix, <c>--revalidate-task plan:guardrails</c>
    /// re-runs the RED <c>&lt;plan&gt;/guardrails/</c> checks → <c>plan-guardrail-failed</c>, exit 2.
    /// RED on current code: the arg resolves to no task → "Unknown task", exit 1.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRevalidate_SerialMode_StillFailing_ExitsTwo()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);

        await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        int revalExit = await RunCliAsync("run", plan.PlanDir, "--revalidate-task", "plan:guardrails");
        Assert.Equal(ExitCodes.TaskFailed, revalExit);
    }

    /// <summary>
    /// Worktree mode B2 revalidate: after a terminal halt, the hand-fix makes the terminal gate pass, then
    /// <c>--revalidate-task plan:guardrails</c> re-runs ONLY the <c>&lt;plan&gt;/guardrails/</c> checks against
    /// the current merged HEAD (the integration worktree the harness owns) → green settle, exit 0.
    /// RED on current code: "Unknown task", exit 1.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRevalidate_WorktreeMode_HandFixGreenSettles_ExitsZero()
    {
        using var plan = new PlanGuardrailPlan(worktree: true);

        await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        plan.HandFixTerminalGate();

        int revalExit = await RunCliAsync("run", plan.PlanDir, "--revalidate-task", "plan:guardrails");
        Assert.Equal(ExitCodes.Success, revalExit);
    }

    /// <summary>
    /// Worktree mode B2 revalidate, still-failing gate: with NO fix, <c>--revalidate-task plan:guardrails</c>
    /// re-runs the RED <c>&lt;plan&gt;/guardrails/</c> checks against the merged HEAD → <c>plan-guardrail-failed</c>,
    /// exit 2. RED on current code: "Unknown task", exit 1.
    /// </summary>
    [Fact]
    public async Task PlanGuardrailRevalidate_WorktreeMode_StillFailing_ExitsTwo()
    {
        using var plan = new PlanGuardrailPlan(worktree: true);

        await RunCliAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        int revalExit = await RunCliAsync("run", plan.PlanDir, "--revalidate-task", "plan:guardrails");
        Assert.Equal(ExitCodes.TaskFailed, revalExit);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 4 — NO per-union regression: the §4.3 per-union scope:"integration" re-verify STILL
    // fires at unions during the run (the terminal folder did NOT absorb the per-union set).
    // Worktree-only by nature; a NO-REGRESSION pin (green on current AND new code).
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The §4.3 per-union integration re-verify must keep firing at unions during the run — the terminal
    /// <c>&lt;plan&gt;/guardrails/</c> folder is terminal-only and must NOT absorb (retire) the per-union set.
    /// Two sibling roots race to the plan branch; the second cannot fast-forward and forms a NON-FF union,
    /// which re-runs the integration set (a <c>scope:"integration"</c> task guardrail). A forced-fail spy
    /// rolls exactly that non-FF sibling back to <c>needs-human</c> — an outcome only the DURING-RUN per-union
    /// re-verify can produce (the end-of-run terminal gate fires only when the whole run is green). The spy
    /// also captures that the re-verify carried the integration set, proving it was not absorbed.
    /// GREEN on both current and new code by design.
    /// </summary>
    [Fact]
    public async Task PerUnionIntegrationReVerify_StillFiresAtUnions_NotAbsorbedByTerminalFolder()
    {
        using var repo = new SiblingUnionRepo();

        var spy = new SpyReVerifier { AlwaysPass = false };
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        RunReport report = await RunSchedulerAsync(repo.PlanDir, provider, spy);

        // The per-union re-verify fired DURING the run...
        Assert.True(spy.CallCount > 0,
            "the §4.3 per-union re-verify must still fire at a non-FF union — SpyReVerifier was never called");

        // ...carrying the scope:"integration" set (not an empty/absorbed set)...
        Assert.Contains(spy.CallGuardrails, set => set.Any(g =>
            string.Equals(g.Scope, "integration", StringComparison.OrdinalIgnoreCase)));

        // ...and forced-failed, rolling exactly the non-FF sibling back to needs-human (one sibling FF's green).
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.NeedsHuman);
        Assert.Single(report.Tasks, t => t.Outcome == TaskOutcome.Succeeded);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 5 (issue #240) — the terminal phase's success path is bracketed with plain console
    // lines, matching Full Flight Checks' treatment (PlanPreflightPhaseTests). Previously totally
    // silent on success: no live-table row (the table's lifetime doesn't even span this phase), no
    // line at all — a real gap surfaced live during the #214 dogfood.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TerminalGate_Green_PrintsRunningThenPassed_ToConsole()
    {
        using var plan = new PlanGuardrailPlan(worktree: false);
        plan.HandFixTerminalGate(); // green from the start — this run should settle wholly green.

        (int exit, string output) = await RunCliCapturedAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Terminal Gate: running...", output);
        Assert.Contains("Terminal Gate: passed.", output);
    }

    [Fact]
    public async Task NoPlanGuardrails_PrintsNothingAboutTerminalGate()
    {
        // A plan with no <plan>/guardrails/ folder at all opts out of this phase entirely (SSOT §7) —
        // printing "Terminal Gate: running..." for a plan that never declared one would be noise, not
        // signal.
        using var plan = new ScriptPlanBuilder().AddTask("01-a");

        (int exit, string output) = await RunCliCapturedAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Terminal Gate", output);
    }

    [Fact]
    public async Task TerminalGate_Red_PrintsRunningButNotPassed()
    {
        // The default PlanGuardrailPlan fixture starts with a RED terminal check — the phase DOES
        // run (the DAG settles green first), so "running..." must print, but it fails, so "passed."
        // must NOT — the existing PrintTerminalGateFailure path (asserted elsewhere) carries the
        // failure detail instead.
        using var plan = new PlanGuardrailPlan(worktree: false);

        (int exit, string output) = await RunCliCapturedAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");

        Assert.Equal(ExitCodes.TaskFailed, exit);
        Assert.Contains("Terminal Gate: running...", output);
        Assert.DoesNotContain("Terminal Gate: passed.", output);
    }

    /// <summary>
    /// Hand-wire the REAL <see cref="Scheduler"/> over the given worktree provider + re-verifier, exactly as
    /// MergeLockAndSettleTests / TopologyReuseForkSchedulerTests do. Load-only (no validate) so the fan-out
    /// plan need not carry a terminal folder for this per-union pin.
    /// </summary>
    private static async Task<RunReport> RunSchedulerAsync(
        string planDir, IWorktreeProvider provider, IReVerifier reVerifier)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();

        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);

        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in the plan-guardrail per-union test."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: reVerifier);

        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // SpyReVerifier — records every re-verify call and the guardrail set it carried.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SpyReVerifier : IReVerifier
    {
        public int CallCount { get; private set; }
        public bool AlwaysPass { get; init; } = true;
        public List<IReadOnlyList<GuardrailDefinition>> CallGuardrails { get; } = [];

        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CallGuardrails.Add(guardrails);
            return Task.FromResult(new ReVerifyResult { Passed = AlwaysPass });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // PlanGuardrailPlan — a linear-chain plan (01-a → 02-b, both green) plus a plan-level
    // <plan>/guardrails/ terminal gate. Serial (workspace ".", maxParallelism 1, no git) or worktree
    // (workspace "..", maxParallelism 2, inside a temp git repo at <repo>/plan).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class PlanGuardrailPlan : IDisposable
    {
        private readonly string _root;

        public string PlanDir { get; }
        public string? RepoPath { get; }

        /// <summary>Absolute log every task action APPENDS its id to (outside any worktree) — proves no re-run.</summary>
        public string ActionRanLogPath { get; }

        /// <summary>The terminal <c>&lt;plan&gt;/guardrails/</c> check file, overwritten by the hand-fix.</summary>
        public string TerminalCheckPath { get; }

        /// <summary>The plan branch a worktree run integrates onto: <c>guardrails/&lt;plan-folder-name&gt;</c>.</summary>
        public string PlanBranch => "guardrails/" + Path.GetFileName(PlanDir);

        /// <summary>How many times a task action has run (append-log line count) — unchanged across a resume.</summary>
        public int ActionRunCount => File.Exists(ActionRanLogPath)
            ? File.ReadAllLines(ActionRanLogPath).Count(l => l.Trim().Length > 0)
            : 0;

        public PlanGuardrailPlan(bool worktree)
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-plangr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ActionRanLogPath = Path.Combine(_root, "action-ran.log");

            if (worktree)
            {
                RepoPath = Path.Combine(_root, "repo");
                Directory.CreateDirectory(RepoPath);
                InitRepo(RepoPath);
                PlanDir = Path.Combine(RepoPath, "plan");
            }
            else
            {
                PlanDir = Path.Combine(_root, "plan");
            }

            Directory.CreateDirectory(PlanDir);
            Directory.CreateDirectory(Path.Combine(PlanDir, "state"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));

            int maxParallelism = worktree ? 2 : 1;
            string workspace = worktree ? ".." : ".";
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                "{\n" +
                "  \"version\": 1,\n" +
                "  \"guardrailMode\": \"failFast\",\n" +
                "  \"workspace\": \"" + workspace + "\",\n" +
                "  \"defaultRetries\": 0,\n" +
                "  \"maxParallelism\": " + maxParallelism + "\n" +
                "}\n");

            // Linear chain: one leaf, no fan-in ⇒ the worktree content-teeth rule (GR2028) is exempt, so a
            // plain exit-1 terminal check validates clean. No task carries scope:"integration", so today's
            // integrationGate terminal gate stays dormant and the run drains green (exit 0) on current code.
            WriteTask("01-a");
            WriteTask("02-b", "01-a");

            string terminalDir = Path.Combine(PlanDir, "guardrails");
            Directory.CreateDirectory(terminalDir);
            TerminalCheckPath = Path.Combine(terminalDir, Ps ? "01-terminal.ps1" : "01-terminal.sh");
            WriteScript(TerminalCheckPath, TerminalCheckScript(passes: false));
        }

        /// <summary>Flip the terminal gate green (the human's hand-fix to the merged HEAD).</summary>
        public void HandFixTerminalGate() => WriteScript(TerminalCheckPath, TerminalCheckScript(passes: true));

        /// <summary>Run git in the (worktree-mode) repo and return stdout.</summary>
        public string Git(params string[] args) => RunGit(RepoPath!, args);

        private void WriteTask(string id, params string[] dependsOn)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string deps = dependsOn.Length == 0
                ? "[]"
                : "[" + string.Join(", ", dependsOn.Select(d => "\"" + d + "\"")) + "]";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                "{ \"description\": \"plan-guardrail task " + id + "\", \"dependsOn\": " + deps + " }");

            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"),
                ActionScriptWithLog(id, ActionRanLogPath));
            WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-check.ps1" : "01-check.sh"),
                GreenGuardrailScript());
        }

        public void Dispose() => SafeDeleteTree(_root);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // SiblingUnionRepo — a temp git repo whose plan has two sibling roots (a NON-FF union) and one
    // scope:"integration" task guardrail, for the per-union re-verify pin.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SiblingUnionRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }
        public string PlanDir { get; }

        public SiblingUnionRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-plangr-union-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            InitRepo(RepoPath);

            PlanDir = Path.Combine(RepoPath, "plan");
            Directory.CreateDirectory(PlanDir);
            Directory.CreateDirectory(Path.Combine(PlanDir, "state"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                "{ \"version\": 1, \"guardrailMode\": \"failFast\", \"workspace\": \"..\", " +
                "\"defaultRetries\": 0, \"maxParallelism\": 2 }");

            // Two sibling roots (no deps): whichever settles second cannot FF and forms a NON-FF union.
            WriteSiblingTask("01-a", integrationGuardrail: true);
            WriteSiblingTask("02-b", integrationGuardrail: false);
        }

        private void WriteSiblingTask(string id, bool integrationGuardrail)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Directory.CreateDirectory(taskDir);
            string grDir = Path.Combine(taskDir, "guardrails");
            Directory.CreateDirectory(grDir);

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                "{ \"description\": \"per-union sibling " + id + "\", \"dependsOn\": [] }");
            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"), SimpleActionScript(id));
            WriteScript(Path.Combine(grDir, Ps ? "01-check.ps1" : "01-check.sh"), GreenGuardrailScript());

            if (integrationGuardrail)
            {
                // A scope:"integration" task guardrail — the §4.3 per-union set the re-verify must re-run.
                WriteScript(Path.Combine(grDir, Ps ? "02-integration.ps1" : "02-integration.sh"), GreenGuardrailScript());
                File.WriteAllText(Path.Combine(grDir, "02-integration.json"), "{ \"scope\": \"integration\" }");
            }
        }

        public void Dispose() => SafeDeleteTree(_root);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Shared script + git + teardown helpers. Paths use '/' inside the workspace (PowerShell accepts
    // it) so nothing needs backslash-escaping; absolute log paths are inserted at runtime (never as a
    // string literal), so they carry OS-native separators harmlessly.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An action that writes <c>src/&lt;id&gt;.cs</c> into the workspace AND appends its id to an absolute log.</summary>
    private static string ActionScriptWithLog(string id, string logPath)
    {
        string safe = id.Replace("-", "_");
        if (Ps)
        {
            return
                "New-Item -ItemType Directory -Force -Path \"$env:GUARDRAILS_WORKSPACE/src\" | Out-Null\n" +
                "Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/src/" + id + ".cs\" -Value 'class " + safe + " {}'\n" +
                "Add-Content -Path '" + logPath + "' -Value '" + id + "'\n" +
                "exit 0\n";
        }
        return
            "#!/usr/bin/env bash\n" +
            "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
            "printf 'class " + safe + " {}' > \"$GUARDRAILS_WORKSPACE/src/" + id + ".cs\"\n" +
            "printf '%s\\n' '" + id + "' >> '" + logPath + "'\n" +
            "exit 0\n";
    }

    /// <summary>An action that just writes <c>src/&lt;id&gt;.cs</c> into the workspace (so the segment commit is non-empty).</summary>
    private static string SimpleActionScript(string id)
    {
        string safe = id.Replace("-", "_");
        if (Ps)
        {
            return
                "New-Item -ItemType Directory -Force -Path \"$env:GUARDRAILS_WORKSPACE/src\" | Out-Null\n" +
                "Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/src/" + id + ".cs\" -Value 'class " + safe + " {}'\n" +
                "exit 0\n";
        }
        return
            "#!/usr/bin/env bash\n" +
            "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
            "printf 'class " + safe + " {}' > \"$GUARDRAILS_WORKSPACE/src/" + id + ".cs\"\n" +
            "exit 0\n";
    }

    private static string GreenGuardrailScript() => Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n";

    /// <summary>
    /// The plan-level <c>&lt;plan&gt;/guardrails/</c> terminal check. Opens with the required <c>catches:</c>
    /// declaration (GR2027, enforced on plan-level folders). RED (exit 1) until hand-fixed to green (exit 0).
    /// </summary>
    private static string TerminalCheckScript(bool passes)
    {
        int code = passes ? 0 : 1;
        string verdict = passes ? "green (hand-fixed)" : "RED (deliberate)";
        if (Ps)
        {
            return
                "# catches: the plan-level <plan>/guardrails/ terminal gate must fire on the merged HEAD and\n" +
                "#          halt the run when a check exits non-zero; a terminal phase that never evaluates\n" +
                "#          <plan>/guardrails/ leaves this file's verdict undetected (the wrong impl this catches).\n" +
                "Write-Output 'plan-guardrail terminal gate " + verdict + "'\n" +
                "exit " + code + "\n";
        }
        return
            "#!/usr/bin/env bash\n" +
            "# catches: the plan-level <plan>/guardrails/ terminal gate must fire on the merged HEAD and halt\n" +
            "#          the run when a check exits non-zero.\n" +
            "echo 'plan-guardrail terminal gate " + verdict + "'\n" +
            "exit " + code + "\n";
    }

    /// <summary>
    /// A terminal <c>&lt;plan&gt;/guardrails/</c> check that emits <c>npm ci</c> preamble noise FIRST, runs a
    /// FULL test suite (many lines, so the preamble is far from the end), then re-emits its real failure
    /// detail at the END (the #179 convention) before failing (exit 1) — the exact #272 Part 1 shape. Opens
    /// with the required <c>catches:</c> declaration (GR2027).
    /// </summary>
    private static string PreambleThenTailTerminalScript()
    {
        string emit(string line) => Ps ? $"Write-Output '{line}'\n" : $"echo '{line}'\n";

        var sb = new System.Text.StringBuilder();
        sb.Append(Ps ? string.Empty : "#!/usr/bin/env bash\n");
        sb.Append("# catches: the plan-level terminal gate must report the RE-EMITTED failure detail (tail),\n");
        sb.Append("#          not the npm-ci preamble first line (#272 Part 1).\n");
        sb.Append(emit("added 464 packages, and audited 465 packages in 24s"));
        for (int i = 1; i <= 20; i++)
        {
            sb.Append(emit($"PASS  dsl-tools/case-{i:00}.test.ts"));
        }
        sb.Append(emit("=== Failure details (re-emitted so they land in the harness feedback tail) ==="));
        sb.Append(emit("FAIL  dsl-tools/dfd.test.ts > round-trips the DSL"));
        sb.Append(emit("vitest suite is not green at the terminal gate"));
        sb.Append("exit 1\n");
        return sb.ToString();
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void InitRepo(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "test@guardrails.local");
        RunGit(repoPath, "config", "user.name", "Guardrails Test");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        RunGit(repoPath, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# plan-guardrail-phase-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>Windows-safe recursive delete (strips the read-only bit git leaves on loose objects).</summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }
}
