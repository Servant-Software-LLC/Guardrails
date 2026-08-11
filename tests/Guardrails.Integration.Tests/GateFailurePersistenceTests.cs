using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #432 — a FAILING gate must leave evidence on disk, not only on the operator's terminal.
///
/// <para>
/// The reported defect: a wave ENTRY gate failed, the console printed a full diagnosis, and the plan
/// folder recorded NOTHING usable — <c>logs/&lt;runId&gt;/</c> held only viewer HTML, and <c>run.json</c>
/// showed 14 silent <c>pending</c> tasks with no captured output and no recorded cause. The run had
/// already promised "Logs (post-mortem any task — pass or fail)"; the one thing that failed was the one
/// thing with no log.
/// </para>
///
/// <para>
/// All four gate folders of the four-folder model run their checks through the SAME
/// <see cref="Guardrails.Core.Execution.IReVerifier"/> seam, so the capture is one fix; the journaling
/// is per-path, so each is asserted separately here. Every test drives the REAL <c>run</c> command (which
/// builds its scheduler through the production
/// <see cref="Guardrails.Core.Execution.SchedulerFactory"/> and evaluates the plan-level phases through
/// the real <see cref="PlanPreflightPhase"/>/<see cref="PlanGuardrailPhase"/>) over a real git repo with
/// OS-picked <c>.ps1</c>/<c>.sh</c> scripts — a fake re-verifier would satisfy the assertions while the
/// composition root stayed broken.
/// </para>
///
/// <para>
/// The load-bearing assertion in every case: after a FAILED gate, the recorded state NAMES the failing
/// check AND that check's captured stdout is on disk at the path the journal points to.
/// </para>
/// </summary>
public sealed class GateFailurePersistenceTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    /// <summary>A sentinel a check prints so the assertion proves the CAPTURED BYTES, not just the file's existence.</summary>
    private const string FailSentinel = "GATE-FAILURE-DETAIL-SENTINEL";

    private const string PassSentinel = "GATE-PASS-DETAIL-SENTINEL";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. Plan-level Full Flight Checks  —  <plan>/preflights/
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlanPreflightFails_NamesFailingCheck_CapturesItsOutput_AndRecordsHaltReason()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteGate(planDir, "preflights", "01-environment-ready", passes: false);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit); // semantics unchanged: the gate still halts, exit 2.

        JournalDocument journal = ReadJournal(planDir);

        // (a) The state record names WHICH check failed, and why.
        Assert.NotNull(journal.PlanPreflights);
        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, journal.PlanPreflights!.Status);
        PlanPreflightCheck failed = Assert.Single(journal.PlanPreflights.Checks, c => !c.Passed);
        Assert.Equal("01-environment-ready", failed.Name);
        Assert.Contains(FailSentinel, failed.Reason);

        // (b) The failing check's captured output is on disk, at the path the journal advertises.
        Assert.Equal($"logs/{journal.RunId}/preflights", journal.PlanPreflights.LogDir);
        AssertCapturedOutput(planDir, journal.PlanPreflights.LogDir, "01-environment-ready", FailSentinel, passed: false);

        // (c) A machine-readable halt reason on the run record.
        Assert.NotNull(journal.Halt);
        Assert.Equal(RunHaltKind.PlanPreflightFailed, journal.Halt!.Kind);
        Assert.Null(journal.Halt.WaveDir);
        Assert.Equal("01-environment-ready", Assert.Single(journal.Halt.FailedChecks).Name);
        Assert.Equal(journal.PlanPreflights.LogDir, journal.Halt.LogDir);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. Wave ENTRY gate  —  <plan>/<wave>/preflights/   (the reported case)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaveEntryGateFails_NamesFailingCheck_CapturesItsOutput_AndRecordsHaltReason()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateWavedPlan(repo.RepoPath, entryPasses: false, exitPasses: true);

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument journal = ReadJournal(planDir);

        // (a) The wave's entry marker exists and names the failing check — the shape the PASSING path
        // already wrote, now written on failure too.
        WaveJournalEntry wave = journal.Waves!["wave-01-scaffold"];
        Assert.NotNull(wave.Entry);
        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, wave.Entry!.Status);
        PlanPreflightCheck failed = Assert.Single(wave.Entry.Checks, c => !c.Passed);
        Assert.Equal("01-upstream-materialized", failed.Name);
        Assert.Contains(FailSentinel, failed.Reason);
        Assert.NotEqual(default, wave.Entry.EvaluatedAt);

        // (b) Captured output at logs/<runId>/<wave>/preflights/<check>/.
        Assert.Equal($"logs/{journal.RunId}/wave-01-scaffold/preflights", wave.Entry.LogDir);
        AssertCapturedOutput(planDir, wave.Entry.LogDir, "01-upstream-materialized", FailSentinel, passed: false);

        // (c) The halt reason, so a post-mortem never sees only silent pending tasks.
        Assert.NotNull(journal.Halt);
        Assert.Equal(RunHaltKind.WaveEntryGateFailed, journal.Halt!.Kind);
        Assert.Equal("wave-01-scaffold", journal.Halt.WaveDir);
        Assert.Equal("01-upstream-materialized", Assert.Single(journal.Halt.FailedChecks).Name);
        Assert.Contains(FailSentinel, Assert.Single(journal.Halt.FailedChecks).Reason);
        Assert.Equal(wave.Entry.LogDir, journal.Halt.LogDir);

        // The defect's fingerprint, asserted directly: every task is still pending, so WITHOUT the halt
        // record above the journal would say nothing at all about why the run stopped.
        Assert.All(journal.Tasks.Values, t => Assert.Empty(t.Attempts));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. Wave EXIT gate  —  <plan>/<wave>/guardrails/
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaveExitGateFails_NamesFailingCheck_CapturesItsOutput_AndRecordsHaltReason()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateWavedPlan(repo.RepoPath, entryPasses: true, exitPasses: false);

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument journal = ReadJournal(planDir);

        WaveJournalEntry wave = journal.Waves!["wave-01-scaffold"];
        Assert.NotNull(wave.Exit);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, wave.Exit!.Status);

        // (a) The failure is named in the pre-existing failedChecks AND in the new per-check list, which
        // is what distinguishes "one check ran and failed" from "three ran and the third failed".
        Assert.Equal("01-wave-sound", Assert.Single(wave.Exit.FailedChecks).Name);
        PlanPreflightCheck failed = Assert.Single(wave.Exit.Checks, c => !c.Passed);
        Assert.Equal("01-wave-sound", failed.Name);
        Assert.Contains(FailSentinel, failed.Reason);
        Assert.NotNull(wave.Exit.EvaluatedAt);

        // (b) Captured output at logs/<runId>/<wave>/guardrails/<check>/.
        Assert.Equal($"logs/{journal.RunId}/wave-01-scaffold/guardrails", wave.Exit.LogDir);
        AssertCapturedOutput(planDir, wave.Exit.LogDir, "01-wave-sound", FailSentinel, passed: false);

        // (c) The halt reason.
        Assert.NotNull(journal.Halt);
        Assert.Equal(RunHaltKind.WaveExitGateFailed, journal.Halt!.Kind);
        Assert.Equal("wave-01-scaffold", journal.Halt.WaveDir);
        Assert.Equal("01-wave-sound", Assert.Single(journal.Halt.FailedChecks).Name);
        Assert.Equal(wave.Exit.LogDir, journal.Halt.LogDir);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. Terminal plan gate  —  <plan>/guardrails/
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TerminalPlanGateFails_NamesFailingCheck_CapturesItsOutput_AndRecordsHaltReason()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");
        WriteGate(planDir, "guardrails", "01-whole-repo-build", passes: false);

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.TaskFailed, exit);

        JournalDocument journal = ReadJournal(planDir);

        // (a) The state record. `checks` + `evaluatedAt` are the #432 additions; `failedChecks` is
        // preserved verbatim for existing readers.
        Assert.NotNull(journal.PlanGuardrails);
        Assert.Equal(PlanPhaseStatus.PlanGuardrailFailed, journal.PlanGuardrails!.Status);
        Assert.Equal("01-whole-repo-build", Assert.Single(journal.PlanGuardrails.FailedChecks).Name);
        PlanPreflightCheck failed = Assert.Single(journal.PlanGuardrails.Checks, c => !c.Passed);
        Assert.Equal("01-whole-repo-build", failed.Name);
        Assert.Contains(FailSentinel, failed.Reason);
        Assert.NotNull(journal.PlanGuardrails.EvaluatedAt);

        // (b) Captured output at logs/<runId>/guardrails/<check>/.
        Assert.Equal($"logs/{journal.RunId}/guardrails", journal.PlanGuardrails.LogDir);
        AssertCapturedOutput(planDir, journal.PlanGuardrails.LogDir, "01-whole-repo-build", FailSentinel, passed: false);

        // (c) The halt reason.
        Assert.NotNull(journal.Halt);
        Assert.Equal(RunHaltKind.PlanGuardrailFailed, journal.Halt!.Kind);
        Assert.Equal("01-whole-repo-build", Assert.Single(journal.Halt.FailedChecks).Name);
        Assert.Equal(journal.PlanGuardrails.LogDir, journal.Halt.LogDir);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. The green path — passing checks are captured too, and a run that did NOT halt records no halt.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GreenRun_CapturesPassingGateOutput_AndRecordsNoHalt()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFlatPlan(repo.RepoPath);
        WriteGate(planDir, "preflights", "01-environment-ready", passes: true);
        WriteTask(Path.Combine(planDir, "tasks"), "01-only", "only.txt");
        WriteGate(planDir, "guardrails", "01-whole-repo-build", passes: true);

        int exit = await RunCliAsync(planDir);

        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument journal = ReadJournal(planDir);
        Assert.Null(journal.Halt); // nothing halted, so nothing claims it did.

        // A passing gate's output is captured too — the evidence that a gate ran at all and was not vacuous.
        AssertCapturedOutput(planDir, journal.PlanPreflights!.LogDir, "01-environment-ready", PassSentinel, passed: true);
        AssertCapturedOutput(planDir, journal.PlanGuardrails!.LogDir, "01-whole-repo-build", PassSentinel, passed: true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6. The halt record describes THIS run — a resume that gets past the gate must not leave a stale one.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_AfterTheGateIsFixed_ClearsTheStaleHaltRecord()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateWavedPlan(repo.RepoPath, entryPasses: false, exitPasses: true);

        Assert.Equal(ExitCodes.TaskFailed, await RunCliAsync(planDir));
        Assert.NotNull(ReadJournal(planDir).Halt);

        // Fix the entry gate and resume: the run now gets past it, so the previous run's halt must be gone.
        WriteGate(Path.Combine(planDir, "wave-01-scaffold"), "preflights", "01-upstream-materialized", passes: true);

        Assert.Equal(ExitCodes.Success, await RunCliAsync(planDir));
        Assert.Null(ReadJournal(planDir).Halt);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Assertions
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assert the gate check's captured artifacts exist under the journal-advertised
    /// <paramref name="relativeLogDir"/> and that <c>stdout.log</c> really carries the bytes the check
    /// printed (existence alone would pass over an empty file).
    /// </summary>
    private static void AssertCapturedOutput(
        string planDir, string? relativeLogDir, string checkName, string sentinel, bool passed)
    {
        Assert.NotNull(relativeLogDir);
        string dir = Path.Combine(planDir, Path.Combine(relativeLogDir!.Split('/')), checkName);
        Assert.True(Directory.Exists(dir), $"expected captured gate output under '{dir}'");

        string stdout = File.ReadAllText(Path.Combine(dir, "stdout.log"));
        Assert.Contains(sentinel, stdout);
        Assert.True(File.Exists(Path.Combine(dir, "stderr.log")));

        string resultJson = File.ReadAllText(Path.Combine(dir, "result.json"));
        Assert.Contains($"\"name\": \"{checkName}\"", resultJson);
        Assert.Contains(passed ? "\"passed\": true" : "\"passed\": false", resultJson);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A flat single-task plan in worktree mode (the mode the reported run used).</summary>
    private static string CreateFlatPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);
        return planDir;
    }

    /// <summary>
    /// A single-wave plan whose ENTRY and EXIT gates are independently switchable. One task per wave keeps
    /// the topology union-free, so the GR2028 terminal-integration obligation does not apply.
    /// </summary>
    private static string CreateWavedPlan(string repoPath, bool entryPasses, bool exitPasses)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            { "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2 }
            """);

        string wave = Path.Combine(planDir, "wave-01-scaffold");
        WriteGate(wave, "preflights", "01-upstream-materialized", entryPasses);
        WriteTask(Path.Combine(wave, "tasks"), "01-config", "config.txt");
        WriteGate(wave, "guardrails", "01-wave-sound", exitPasses);

        return planDir;
    }

    /// <summary>
    /// Write one gate check into <c>&lt;root&gt;/&lt;folder&gt;/</c>. Both variants PRINT a sentinel, so a
    /// test can prove the captured stdout is the check's real output rather than an empty placeholder.
    /// </summary>
    private static void WriteGate(string root, string folder, string name, bool passes)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        string catches = $"# catches: {name} — a gate whose failure leaves no evidence on disk (issue #432)";
        string sentinel = passes ? PassSentinel : FailSentinel;
        string code = passes ? "exit 0" : "exit 1";
        WriteScript(
            Path.Combine(dir, Script(name)),
            $"{catches}\nWrite-Output '{sentinel}'\n{code}",
            $"{catches}\necho '{sentinel}'\n{code}");
    }

    /// <summary>A task that writes one file and whose single guardrail asserts that file exists.</summary>
    private static void WriteTask(string tasksDir, string id, string file)
    {
        string taskDir = Path.Combine(tasksDir, id);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "gate-persistence fixture {{id}}", "writeScope": ["{{file}}"] }""");

        WriteScript(
            Path.Combine(taskDir, Script("action")),
            $"Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE '{file}') -Value 'x'\nexit 0",
            $"printf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0");

        WriteScript(
            Path.Combine(taskDir, "guardrails", Script("01-check")),
            $"# catches: {file} missing\nif (-not (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '{file}'))) "
            + $"{{ Write-Output '{file} missing'; exit 1 }}\nexit 0",
            $"# catches: {file} missing\n[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo '{file} missing'; exit 1; }}\nexit 0");
    }

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static void WriteScript(string path, string psBody, string bashBody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Ps ? psBody + "\n" : "#!/usr/bin/env bash\n" + bashBody + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Harness
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the REAL <c>run</c> command — the composition root that wires the scheduler, the re-verifier
    /// and the two plan-level phases. Output goes to a discarded <see cref="StringConsoleIo"/> so nothing
    /// touches the process-global console (parallel-safe).
    /// </summary>
    private static async Task<int> RunCliAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("gate-failure persistence test root");
        root.Add(RunCommand.Create(io));
        return await root
            .Parse(["run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success"])
            .InvokeAsync();
    }

    private static JournalDocument ReadJournal(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-gate-persist-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# gate-failure-persistence");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        private void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr.Trim()}");
            }
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }
}
