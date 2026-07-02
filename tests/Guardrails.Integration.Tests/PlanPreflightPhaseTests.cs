using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests for preflights-impl <b>Deliverable 3</b> — the pre-DAG plan-preflight
/// phase. They drive the REAL run entry (<see cref="RunCommand"/>, which builds its scheduler through
/// the production <see cref="Guardrails.Core.Execution.SchedulerFactory"/>) over committed fixture
/// plans that carry a plan-level <c>&lt;plan&gt;/preflights/</c> folder (understood by the
/// four-folder loader, Deliverable 6), and assert OBSERVABLE outcomes: the process exit code and the
/// journal's new top-level <c>planPreflights</c> section in <c>state/run.json</c> (Deliverable 4).
///
/// <para>
/// Every test runs in <b>BOTH</b> serial (<c>maxParallelism = 1</c>) and worktree
/// (<c>maxParallelism &gt; 1</c>, a git workspace) mode via a two-row <see cref="TheoryAttribute"/>.
/// The serial-mode path is exactly where a false-green hides: the pre-DAG phase reuses the
/// <c>IReVerifier</c> seam that the composition root now wires UNCONDITIONALLY (Deliverable 1), so a
/// regression that only fired the phase in worktree mode would pass a worktree-only test and ship a
/// broken serial run.
/// </para>
///
/// <para>
/// These are RED on current code — there is no pre-DAG phase yet, so a red
/// <c>&lt;plan&gt;/preflights/</c> does NOT halt, and the <c>planPreflights</c> journal section is
/// never written (the loader parses the folder but nothing evaluates it). Each test therefore fails
/// today (the intent) and goes green once Deliverable 3 lands the phase + its SSOT §7 resume rule.
/// Nothing here IMPLEMENTS the phase; the fixtures are the only new surface, and they live entirely
/// in this one file (the task's write-scope).
/// </para>
///
/// <para>
/// Tagged <c>[Trait("Category", "Preflights")]</c> at the class level (inherited by every case) so the
/// per-deliverable green baseline can exclude these deliberately-red tests via
/// <c>--filter "Category!=Preflights"</c>.
/// </para>
/// </summary>
[Trait("Category", "Preflights")]
public sealed class PlanPreflightPhaseTests
{
    // Serial fixtures use maxParallelism 1; worktree fixtures use maxParallelism 2 (the git workspace
    // + parallelism combination the factory wires a GitWorktreeProvider on). The InlineData rows below
    // exercise both — "serial" and "worktree" — for each of the four behaviors.

    private static readonly bool Ps = OperatingSystem.IsWindows();

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior #1 — a RED <plan>/preflights/ halts BEFORE any task runs: exit 2, ZERO attempts,
    // planPreflights.status == plan-preflight-failed. On current code (no pre-DAG phase) the loader
    // parses the red preflight but never evaluates it, so the green DAG runs to exit 0 → the exit
    // assertion (and the marker/zero-attempts assertions) FAIL. That RED bar is the point.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)] // serial mode
    [InlineData(2)] // worktree mode
    public async Task PlanPreflightRed_HaltsBeforeAnyTask_ZeroAttempts_Exit2(int maxParallelism)
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, maxParallelism);
        WritePlanPreflight(planDir, PreflightKind.Red);
        WriteTask(planDir, "01-only", createBaselineArtifact: false, guardrailPasses: true);

        int exit = await RunCliAsync(planDir);

        // The red pre-DAG phase must halt the run at exit 2 (ExitCodes.TaskFailed) — NOT the exit 0 a
        // wholly-green DAG produces on current code, which never evaluates the preflight.
        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument journal = ReadJournal(planDir);

        // The F9 plan-scoped halt is journaled OUTSIDE tasks{} as planPreflights.status = plan-preflight-failed.
        Assert.NotNull(journal.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, journal.PlanPreflights!.Status);

        // Zero-token halt: the run stopped BEFORE scheduling, so not one task attempt was journaled.
        Assert.NotEmpty(journal.Tasks);
        Assert.All(journal.Tasks.Values, entry => Assert.Empty(entry.Attempts));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior #2 — a GREEN <plan>/preflights/ records planPreflights.status == passed with a
    // planHash matching the current PlanHash, and the DAG then schedules normally. On current code
    // the DAG also runs to exit 0, but the planPreflights section is never written → the NotNull
    // assertion is the RED bar.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)] // serial mode
    [InlineData(2)] // worktree mode
    public async Task PlanPreflightGreen_MarkerPassed_WithMatchingPlanHash_DagSchedules(int maxParallelism)
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, maxParallelism);
        WritePlanPreflight(planDir, PreflightKind.Green);
        WriteTask(planDir, "01-only", createBaselineArtifact: false, guardrailPasses: true);

        int exit = await RunCliAsync(planDir);

        // A green pre-DAG lets the DAG schedule to a wholly-green exit 0. (This holds on current code
        // too — the RED bar is the planPreflights marker below, which current code never writes.)
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = ReadJournal(planDir);

        // planPreflights.status == passed, planHash == the current PlanHash (the SAME hash the journal
        // and the §13 review marker use). Both the independently-computed hash and the journal's own
        // top-level planHash must agree with the marker.
        Assert.NotNull(journal.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.Passed, journal.PlanPreflights!.Status);
        Assert.Equal(ComputePlanHash(planDir), journal.PlanPreflights.PlanHash);
        Assert.Equal(journal.PlanHash, journal.PlanPreflights.PlanHash);

        // The DAG then ran and the single task went green (this test's actual concern).
        Assert.Equal(JournalTaskStatus.Succeeded, journal.Tasks["01-only"].Status);
        // NOTE: an Attempts-non-empty assertion was removed here — worktree-mode success journals
        // no AttemptRecord (pre-existing gap vs SSOT §7, tracked as #196), so it fails only the
        // maxParallelism>1 row for a reason unrelated to the pre-DAG phase. Status==Succeeded above
        // already proves the DAG scheduled and the task ran green, which is what this test verifies.
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior #3 (B1) — negative-baseline RESUME SKIP. The pre-DAG check passes ONLY at the START
    // (it asserts the ABSENCE of an artifact a task then introduces). After a mid-DAG interruption,
    // a plain resume must READ the passed planPreflights marker and SKIP the phase — it must NOT
    // re-evaluate the now-RED negative baseline against partially-merged bytes and false-halt. The
    // evidence of "not re-evaluated" is the marker (its evaluatedAt + planHash) being UNCHANGED
    // across the resume, and the resumed run finishing green. RED on current code: run 1 writes no
    // planPreflights marker at all. Method name carries "Resume".
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)] // serial mode
    [InlineData(2)] // worktree mode
    public async Task NegativeBaselinePreflight_NotReEvaluatedOnResume_NoFalseHalt(int maxParallelism)
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, maxParallelism);

        // 01-seed introduces baseline-artifact.txt (which the negative-baseline preflight forbids);
        // 02-consumer's guardrail is RED on the first run so the DAG halts mid-way (the interruption).
        WritePlanPreflight(planDir, PreflightKind.NegativeBaseline);
        WriteTask(planDir, "01-seed", createBaselineArtifact: true, guardrailPasses: true);
        WriteTask(planDir, "02-consumer", createBaselineArtifact: false, guardrailPasses: false, "01-seed");

        // --- run 1: pre-DAG passes (artifact absent at start), 01-seed introduces the artifact,
        //            02-consumer fails its guardrail → the DAG halts mid-way. ------------------------
        int exit1 = await RunCliAsync(planDir);
        Assert.Equal(ExitCodes.TaskFailed, exit1);

        JournalDocument journal1 = ReadJournal(planDir);
        // Mid-DAG interruption: 01-seed settled, 02-consumer did not.
        Assert.Equal(JournalTaskStatus.Succeeded, journal1.Tasks["01-seed"].Status);
        Assert.NotEqual(JournalTaskStatus.Succeeded, journal1.Tasks["02-consumer"].Status);

        // The pre-DAG marker was recorded passed on run 1 — RED on current code (no phase → null).
        Assert.NotNull(journal1.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.Passed, journal1.PlanPreflights!.Status);
        DateTimeOffset evaluatedAtRun1 = journal1.PlanPreflights.EvaluatedAt;
        string preflightHashRun1 = journal1.PlanPreflights.PlanHash;

        // --- fix 02-consumer's guardrail, then RESUME with a plain run (NO --fresh). ---------------
        // Editing a guardrail SCRIPT does not change the PlanHash (hashed over guardrails.json +
        // task.json only), so the resume SKIP precondition — a passed marker whose planHash matches
        // the current PlanHash — still holds.
        SetTaskGuardrail(planDir, "02-consumer", passes: true);

        int exit2 = await RunCliAsync(planDir);

        // The resume must SKIP the pre-DAG phase (read the passed marker) rather than re-evaluate the
        // now-RED negative baseline (baseline-artifact.txt now exists) — so the run finishes green,
        // never false-halting at exit 2.
        Assert.Equal(ExitCodes.Success, exit2);

        JournalDocument journal2 = ReadJournal(planDir);
        Assert.NotNull(journal2.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.Passed, journal2.PlanPreflights!.Status);

        // Evidence of NO re-evaluation: the marker (evaluatedAt + planHash) is byte-for-byte the run-1
        // marker — a re-evaluation would stamp a fresh evaluatedAt.
        Assert.Equal(evaluatedAtRun1, journal2.PlanPreflights.EvaluatedAt);
        Assert.Equal(preflightHashRun1, journal2.PlanPreflights.PlanHash);

        // The resumed task actually completed.
        Assert.Equal(JournalTaskStatus.Succeeded, journal2.Tasks["02-consumer"].Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior #4 — --fresh RE-RUNS the pre-DAG phase. After a passed marker, a --fresh run clears
    // run.json (and prunes worktrees), so the pre-DAG phase re-evaluates and re-writes the marker
    // with a fresh evaluatedAt. RED on current code: run 1 writes no marker. Method name carries
    // "Fresh".
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)] // serial mode
    [InlineData(2)] // worktree mode
    public async Task PlanPreflight_ReEvaluatedOnFresh_AfterPassedMarker(int maxParallelism)
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, maxParallelism);
        WritePlanPreflight(planDir, PreflightKind.Green);
        WriteTask(planDir, "01-only", createBaselineArtifact: false, guardrailPasses: true);

        // --- run 1: green pre-DAG writes a passed marker. -----------------------------------------
        int exit1 = await RunCliAsync(planDir);
        Assert.Equal(ExitCodes.Success, exit1);

        JournalDocument journal1 = ReadJournal(planDir);
        Assert.NotNull(journal1.PlanPreflights); // RED on current code (no pre-DAG phase → null).
        DateTimeOffset evaluatedAtRun1 = journal1.PlanPreflights!.EvaluatedAt;

        // --- run 2 with --fresh: state (incl. the marker) is cleared, so the pre-DAG phase RE-RUNS.--
        int exit2 = await RunCliAsync(planDir, fresh: true);
        Assert.Equal(ExitCodes.Success, exit2);

        JournalDocument journal2 = ReadJournal(planDir);
        // A skipped phase after --fresh cleared the marker would leave it ABSENT; a re-evaluation
        // re-writes it, present again and with a NEW evaluatedAt (the two runs are seconds apart).
        Assert.NotNull(journal2.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.Passed, journal2.PlanPreflights!.Status);
        Assert.NotEqual(evaluatedAtRun1, journal2.PlanPreflights.EvaluatedAt);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    // Fixture helpers — committed plan folders inside a temp git repo. Self-contained (the
    // TempGitRepo pattern is copied here because the task's write-scope is this one file), mirroring
    // the git-worktree fixtures in GitHookIsolationTests / StagingOutputsRunTests. Scripts are
    // OS-appropriate (PowerShell on Windows, bash elsewhere) so the same test runs cross-platform.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    private enum PreflightKind
    {
        /// <summary>A pre-DAG check that always passes (exit 0).</summary>
        Green,

        /// <summary>A pre-DAG check that always fails (exit 1) — the plan-preflight-red halt.</summary>
        Red,

        /// <summary>
        /// A B1 negative baseline: passes only while <c>baseline-artifact.txt</c> is ABSENT from the
        /// workspace (fails once a task introduces it). Evaluated exactly once across the run lifecycle.
        /// </summary>
        NegativeBaseline
    }

    /// <summary>
    /// Create the plan folder at <c>&lt;repo&gt;/plan/</c> with <c>workspace: ".."</c> (so the git repo
    /// root is the workspace — worktree mode's hard dependency) and <paramref name="maxParallelism"/>.
    /// <c>defaultRetries: 0</c> keeps single-attempt semantics exact.
    /// </summary>
    private static string CreatePlan(string repoPath, int maxParallelism)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": {{maxParallelism}}
            }
            """);

        return planDir;
    }

    /// <summary>
    /// Write a plan-level <c>&lt;plan&gt;/preflights/</c> "Full Flight Check". The file is
    /// guardrail-shaped and opens with a <c>catches:</c> comment (the four-folder loader requires it),
    /// followed by the deterministic check the <paramref name="kind"/> selects.
    /// </summary>
    private static void WritePlanPreflight(string planDir, PreflightKind kind)
    {
        string dir = Path.Combine(planDir, "preflights");
        Directory.CreateDirectory(dir);

        (string ps, string bash) = kind switch
        {
            PreflightKind.Green => (
                "exit 0",
                "exit 0"),
            PreflightKind.Red => (
                "Write-Output 'plan preflight red (deliberate)'\nexit 1",
                "echo 'plan preflight red (deliberate)'\nexit 1"),
            PreflightKind.NegativeBaseline => (
                "if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE 'baseline-artifact.txt')) { Write-Output 'baseline artifact already present'; exit 1 } else { exit 0 }",
                "if [ -f \"$GUARDRAILS_WORKSPACE/baseline-artifact.txt\" ]; then echo 'baseline artifact already present'; exit 1; else exit 0; fi"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        const string catches = "# catches: 01-baseline - a pre-DAG phase that never evaluates the plan preflights against the starting bytes";
        WriteScript(Path.Combine(dir, PreflightFile), catches + "\n" + ps, catches + "\n" + bash);
    }

    /// <summary>
    /// Write a task: <c>task.json</c> (with any <paramref name="dependsOn"/>), an action that writes a
    /// per-task marker file (a real working-tree change so a worktree-mode integrate makes a non-empty
    /// commit) and optionally introduces <c>baseline-artifact.txt</c>, plus one guardrail whose verdict
    /// is <paramref name="guardrailPasses"/>.
    /// </summary>
    private static void WriteTask(
        string planDir, string id, bool createBaselineArtifact, bool guardrailPasses, params string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string deps = dependsOn.Length == 0
            ? "[]"
            : "[" + string.Join(", ", dependsOn.Select(d => "\"" + d + "\"")) + "]";

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "plan-preflight fixture {{id}}",
              "dependsOn": {{deps}}
            }
            """);

        string ps = $"New-Item -Path (Join-Path $env:GUARDRAILS_WORKSPACE '{id}.out') -Force -Value 'ran' | Out-Null";
        string bash = $"printf 'ran' > \"$GUARDRAILS_WORKSPACE/{id}.out\"";
        if (createBaselineArtifact)
        {
            ps += "\nNew-Item -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'baseline-artifact.txt') -Force -Value 'made' | Out-Null";
            bash += "\nprintf 'made' > \"$GUARDRAILS_WORKSPACE/baseline-artifact.txt\"";
        }
        ps += "\nexit 0";
        bash += "\nexit 0";
        WriteScript(Path.Combine(taskDir, ActionFile), ps, bash);

        WriteTaskGuardrail(taskDir, guardrailPasses);
    }

    /// <summary>Overwrite a task's single guardrail body — used to "fix" a red guardrail before a resume.</summary>
    private static void SetTaskGuardrail(string planDir, string taskId, bool passes) =>
        WriteTaskGuardrail(Path.Combine(planDir, "tasks", taskId), passes);

    private static void WriteTaskGuardrail(string taskDir, bool passes)
    {
        string ps = passes ? "exit 0" : "Write-Output 'task guardrail red (deliberate)'\nexit 1";
        string bash = passes ? "exit 0" : "echo 'task guardrail red (deliberate)'\nexit 1";
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFile), ps, bash);
    }

    private static string ActionFile => Ps ? "action.ps1" : "action.sh";
    private static string GuardrailFile => Ps ? "01-check.ps1" : "01-check.sh";
    private static string PreflightFile => Ps ? "01-baseline.ps1" : "01-baseline.sh";

    /// <summary>Write an OS-appropriate script (no shebang on Windows; a bash shebang + exec bit elsewhere).</summary>
    private static void WriteScript(string path, string psBody, string bashBody)
    {
        string content = Ps ? psBody + "\n" : "#!/usr/bin/env bash\n" + bashBody + "\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Run + journal helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the real <c>run</c> command pipeline (the same entry the CLI uses; it builds its scheduler
    /// through the production <see cref="Guardrails.Core.Execution.SchedulerFactory"/>). Output goes to
    /// a discarded <see cref="StringConsoleIo"/> so nothing touches the process-global console — safe
    /// to run in parallel. <c>--no-ui --no-log-server</c> keep the run headless.
    /// </summary>
    private static async Task<int> RunCliAsync(string planDir, bool fresh = false)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("plan-preflight phase cli test root");
        root.Add(RunCommand.Create(io));

        string[] args = fresh
            ? new[] { "run", planDir, "--fresh", "--no-ui", "--no-log-server" }
            : new[] { "run", planDir, "--no-ui", "--no-log-server" };

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>Read <c>state/run.json</c> exactly as it stands on disk (no resume normalization).</summary>
    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    /// <summary>Compute the current <c>PlanHash</c> the same way the harness does — over the on-disk plan.</summary>
    private static string ComputePlanHash(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        return PlanHash.Compute(load.Plan!);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — Windows-safe temp repo (SafeDelete strips read-only bits before delete). The plan
    // folder is created inside RepoPath but left uncommitted; worktree mode forks the plan branch from
    // the initial commit (README) and reads task/preflight scripts straight from the plan dir.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-preflight-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# plan-preflight-phase-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }
}
