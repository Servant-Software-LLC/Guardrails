using System.Diagnostics;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Issue #726 — the delivery target is RECORDED when the first delivery lands, and a resume that would
/// deliver somewhere else is refused before anything lands.
///
/// <para><b>The defect.</b> Design 39 delivers a waved plan's earlier waves at their own barriers, but the
/// delivery target is re-pinned from <c>HEAD</c> on every process start
/// (<c>GitWorktreeProvider.CreateIntegration</c>) and nothing recorded where the earlier deliveries actually
/// went. Run on <c>master</c>, wave 1 delivers, a wave-2 task needs a human, <c>git switch -c spike</c>, fix,
/// resume — and wave 2's barrier delivery lands on <c>spike</c>. The #588 branch-moved check compares against
/// the NEW pin, so it refuses nothing. The plan's work ends up split across two branches and nothing says so.
/// Before design 39 all delivery happened at run end, so a resume elsewhere at least kept the work together.</para>
///
/// <para><b>How the split is observed.</b> Git, after the run: which branch carries <c>wave2.txt</c>, and
/// whether the branch wave 1 landed on still points where it did. Never a recorded call on a double — these
/// drive the REAL <see cref="Scheduler"/> over the REAL <see cref="GitWorktreeProvider"/>, the
/// <c>BranchMovedHaltTests</c>/<c>WaveBarrierDeliveryTests</c> construction.</para>
///
/// <para><b>Each row is two runs over ONE journal</b>, which is what makes the target's durability the
/// subject: run 1 delivers wave 1 and halts at a wave-2 task whose guardrail is waiting on a sentinel file;
/// the sentinel is then created (no plan edit, so no definition drift) and the run is resumed from wherever
/// the row puts the checkout. <see cref="AResumeOnTheSameBranch_StillDelivers"/> is the control: without it
/// an implementation that refuses every resume would pass every other row here.</para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class DeliveryTargetTests
{
    private const string Wave1 = "wave-01-first";
    private const string Wave2 = "wave-02-gated";
    private const string Wave3 = "wave-03-final";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Row 1 — the measured defect: a resume on another branch.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResumeOnAnotherBranch_IsRefused_AndTheWorkStaysTogether()
    {
        using var repo = new TempGitRepo();
        Scenario s = CreateGatedThreeWavePlan(repo);

        (RunReport first, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        RequireWaveOneDelivered(repo, s, first);
        string tipAfterWaveOne = repo.TipOf(s.UserBranch);

        File.WriteAllText(s.SentinelPath, "unblocked\n");
        repo.Switch("-c", "spike");

        (RunReport second, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        // THE CLAIM, asked of git before anything is asked of the harness's own report: nothing landed on
        // the branch the run was standing on, and nothing landed a second time on the branch wave 1 went to.
        // The plan's work is not split.
        Assert.False(repo.BranchHasFile("spike", "wave2.txt"),
            $"wave 2 delivered to 'spike' — the plan's work is now split across two branches.\n{Describe(second)}");
        Assert.False(repo.BranchHasFile(s.UserBranch, "wave2.txt"),
            $"wave 2 reached '{s.UserBranch}' although its delivery was refused.\n{Describe(second)}");
        Assert.Equal(tipAfterWaveOne, repo.TipOf(s.UserBranch));
        Assert.Equal(tipAfterWaveOne, repo.TipOf("spike"));

        // A refusal is not transient, so no later wave starts.
        Assert.False(repo.BranchHasFile(PlanBranch(s.PlanDir), "wave3.txt"),
            "wave 3 ran although wave 2's delivery was already known to be impossible.");

        // The refusal itself, in design 39's own shape.
        Assert.NotNull(second.WaveHalt);
        Assert.Equal(WaveHaltKind.DeliveryRefused, second.WaveHalt!.Kind);
        Assert.Equal(Wave2, second.WaveHalt.WaveDir);
        Assert.Contains(
            $"an earlier delivery landed on '{s.UserBranch}'; HEAD is now 'spike'",
            second.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains($"check out '{s.UserBranch}' again", second.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.Equal(WaveDeliveryStatus.Refused, journal.WaveEntryOf(Wave2)?.Delivered?.Status);
        Assert.Equal(DeliveryOutcome.BranchMoved, journal.WaveEntryOf(Wave2)?.Delivered?.Outcome);

        // The report names the branch the work IS on — the recorded target, never this process's pin.
        Assert.Equal(s.UserBranch, second.DeliveredToBranch);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Row 2 — a detached HEAD. The #588 check already refused this one, but it named the pin: on a
    // detached run start the pin is the literal "HEAD", so the halt said "run started on a detached HEAD"
    // and the remedy told the operator to `check out 'HEAD' again` — a branch that does not exist.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResumeOnADetachedHead_IsRefused_AndNamesTheBranchTheWorkIsOn()
    {
        using var repo = new TempGitRepo();
        Scenario s = CreateGatedThreeWavePlan(repo);

        (RunReport first, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        RequireWaveOneDelivered(repo, s, first);
        string tipAfterWaveOne = repo.TipOf(s.UserBranch);

        File.WriteAllText(s.SentinelPath, "unblocked\n");
        repo.Switch("--detach");

        (RunReport second, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(second.WaveHalt);
        Assert.Equal(WaveHaltKind.DeliveryRefused, second.WaveHalt!.Kind);
        Assert.Contains(
            $"an earlier delivery landed on '{s.UserBranch}'; HEAD is now detached",
            second.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains($"check out '{s.UserBranch}' again", second.WaveHalt.Detail, StringComparison.Ordinal);
        Assert.Equal(WaveDeliveryStatus.Refused, journal.WaveEntryOf(Wave2)?.Delivered?.Status);

        Assert.False(repo.BranchHasFile(s.UserBranch, "wave2.txt"),
            $"wave 2 reached '{s.UserBranch}' although its delivery was refused.\n{Describe(second)}");
        Assert.Equal(tipAfterWaveOne, repo.TipOf(s.UserBranch));
        Assert.Equal(s.UserBranch, second.DeliveredToBranch);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Row 3 — THE CONTROL. Same two runs, same journal, checkout never moved: the delivery still happens.
    // Without this row, "refuse everything" passes rows 1, 2 and 4.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResumeOnTheSameBranch_StillDelivers()
    {
        using var repo = new TempGitRepo();
        Scenario s = CreateGatedThreeWavePlan(repo);

        (RunReport first, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        RequireWaveOneDelivered(repo, s, first);

        File.WriteAllText(s.SentinelPath, "unblocked\n");

        (RunReport second, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Null(second.WaveHalt);
        Assert.True(second.AllSucceeded, Describe(second));

        // At the BARRIER, not merely by run end: only a barrier delivery writes this record.
        Assert.Equal(WaveDeliveryStatus.Delivered, journal.WaveEntryOf(Wave2)?.Delivered?.Status);
        Assert.True(repo.BranchHasFile(s.UserBranch, "wave2.txt"),
            $"wave 2's work never reached '{s.UserBranch}'.\n{Describe(second)}");
        Assert.True(repo.BranchHasFile(s.UserBranch, "wave3.txt"),
            $"the final wave never reached '{s.UserBranch}' at run end.\n{Describe(second)}");
        Assert.Equal(s.UserBranch, second.DeliveredToBranch);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Row 4 — the recorded target itself, on the wire.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheTargetIsRecordedWhenTheFirstDeliveryLands_AndARefusedResumeNeverRepinsIt()
    {
        using var repo = new TempGitRepo();
        Scenario s = CreateGatedThreeWavePlan(repo);

        // A fixture PRECONDITION, not evidence about the field: no run has happened, so run.json does not
        // exist and RecordedTarget would answer null whatever the harness does. Asserting the absent FILE
        // says what is actually true here. The real "not stamped at run start" control is
        // ARunThatDeliversNothing_RecordsNoTarget below, which asks the question of a run.json that EXISTS.
        Assert.False(File.Exists(RunJournal.PathFor(s.PlanDir)));

        (RunReport first, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        RequireWaveOneDelivered(repo, s, first);
        Assert.Equal(s.UserBranch, RecordedTarget(s.PlanDir));

        File.WriteAllText(s.SentinelPath, "unblocked\n");
        repo.Switch("-c", "spike");
        await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(s.UserBranch, RecordedTarget(s.PlanDir));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Row 5 — THE OTHER CONTROL: a run that delivers NOTHING records no target.
    //
    // Row 4's pre-run check cannot see this. Before the first run there is no run.json at all, so it reads
    // null whatever the harness does — which left an implementation that stamps the target at RUN START,
    // from the live pin, passing every other row in this file, both journal-level suites, and the run.json
    // key-set pin. That implementation is not harmless: with a target recorded before anything lands, the
    // refusal fires when NO delivery has landed, so a first run halted before any barrier and resumed from
    // another branch would be refused — neither #726's requirement nor the behaviour before it.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunThatDeliversNothing_RecordsNoTarget()
    {
        using var repo = new TempGitRepo();
        Scenario s = CreateGatedThreeWavePlan(repo, gateWaveOneInstead: true);
        string tipBefore = repo.TipOf(s.UserBranch);

        // Wave 1's OWN task is the blocked one, so the run halts at the first barrier: no wave ever reaches
        // a delivery point, and the run-end merge is never attempted either.
        (RunReport only, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.False(only.AllSucceeded, Describe(only));
        Assert.False(repo.BranchHasFile(s.UserBranch, "wave1.txt"),
            $"nothing should have reached '{s.UserBranch}' — no wave reached a barrier.\n{Describe(only)}");
        Assert.Equal(tipBefore, repo.TipOf(s.UserBranch));
        Assert.Null(only.DeliveredToBranch);

        // THE LOAD-BEARING HALF: run.json EXISTS, so a null below is the FIELD being absent rather than the
        // file being missing — the distinction row 4 could not draw. A target stamped at run start from
        // this process's pin fails exactly here, and nowhere else in the suite.
        string journalPath = RunJournal.PathFor(s.PlanDir);
        Assert.True(File.Exists(journalPath), $"the run never wrote {journalPath}.\n{Describe(only)}");
        Assert.Null(RecordedTarget(s.PlanDir));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Shared assertions and helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Run 1's preconditions, asserted in every row: wave 1 really delivered (so "a delivery has already
    /// landed" is true) and wave 2 really halted (so run 2 has a barrier left to reach). A row whose run 1
    /// silently did neither would make every later assertion vacuous.
    /// </summary>
    private static void RequireWaveOneDelivered(TempGitRepo repo, Scenario s, RunReport first)
    {
        Assert.True(repo.BranchHasFile(s.UserBranch, "wave1.txt"),
            $"run 1: wave 1 did not deliver at its barrier.\n{Describe(first)}");
        Assert.False(repo.BranchHasFile(s.UserBranch, "wave2.txt"),
            $"run 1: wave 2 delivered although its task's guardrail was still blocked.\n{Describe(first)}");
        Assert.False(first.AllSucceeded, $"run 1 was expected to halt at wave 2.\n{Describe(first)}");
    }

    private static string Describe(RunReport report) =>
        $"halt: {report.WaveHalt?.Kind.ToString() ?? "<none>"} ({report.WaveHalt?.Headline ?? "-"}); "
        + $"deliveredToBranch: {report.DeliveredToBranch ?? "<null>"}; tasks: "
        + string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}"));

    /// <summary>The wire value of <c>run.json</c>'s top-level <c>deliveryTarget</c>, read as JSON so the key's own spelling is pinned.</summary>
    private static string? RecordedTarget(string planDir)
    {
        string path = RunJournal.PathFor(planDir);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("deliveryTarget", out JsonElement value)
            ? value.GetString()
            : null;
    }

    private static string PlanBranch(string planDir) => $"guardrails/{Path.GetFileName(planDir)}";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Composition root: the REAL Scheduler over the REAL GitWorktreeProvider (BranchMovedHaltTests).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(RunReport Report, RunJournal Journal)> RunAsync(
        string planDir, string repoPath, string worktreeRoot)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in delivery-target tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
        var provider = new GitWorktreeProvider(repoPath, worktreeRoot);
        var reVerifier = new GuardrailReVerifier(new ProcessRunner(), interpreterMap);
        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: reVerifier);

        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
        return (report, journal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The plan: three waves, the first two delivery points. Wave 2's TASK guardrail waits on a sentinel
    // file OUTSIDE the repository, so run 1 halts there and run 2 gets past it with no plan edit — an
    // edit would move the wave's definition hash and turn this into a drift test.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(string PlanDir, string UserBranch, string SentinelPath);

    /// <param name="gateWaveOneInstead">
    /// Put the sentinel gate on WAVE 1's task rather than wave 2's, so the run halts at the FIRST barrier
    /// and no delivery is ever attempted — the "this run delivered nothing" shape
    /// <see cref="ARunThatDeliversNothing_RecordsNoTarget"/> needs.
    /// </param>
    private static Scenario CreateGatedThreeWavePlan(TempGitRepo repo, bool gateWaveOneInstead = false)
    {
        string userBranch = repo.CurrentBranch();
        string sentinel = Path.Combine(repo.Root, "unblock-wave-2.txt");

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        string wd1 = Path.Combine(planDir, Wave1);
        WriteDeliversBrief(wd1);
        WriteFileWritingTask(Path.Combine(wd1, "tasks", "01-write"), "wave1.txt",
            gateWaveOneInstead ? sentinel : null);
        WriteFileExistsGate(Path.Combine(wd1, "guardrails", Script("01-check")), "wave1.txt");

        string wd2 = Path.Combine(planDir, Wave2);
        WriteDeliversBrief(wd2);
        WriteFileWritingTask(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt",
            gateWaveOneInstead ? null : sentinel);
        WriteFileExistsGate(Path.Combine(wd2, "guardrails", Script("01-check")), "wave2.txt");

        string wd3 = Path.Combine(planDir, Wave3);
        WriteFileWritingTask(Path.Combine(wd3, "tasks", "01-write"), "wave3.txt", sentinelPath: null);
        WriteFileExistsGate(Path.Combine(wd3, "guardrails", Script("01-check")), "wave3.txt");

        return new Scenario(planDir, userBranch, sentinel);
    }

    private static void WriteDeliversBrief(string waveDir)
    {
        Directory.CreateDirectory(waveDir);
        File.WriteAllText(Path.Combine(waveDir, "brief.md"),
            "---\ndelivers: true\n---\n# wave brief\n\nDelivers at its own barrier.\n");
    }

    /// <summary>
    /// A task that writes <paramref name="file"/> into its workspace. When <paramref name="sentinelPath"/>
    /// is set its guardrail ALSO requires that absolute path to exist, so the task settles needs-human until
    /// the fixture creates it — the "a wave-2 task needs a human" half of the scenario.
    /// </summary>
    private static void WriteFileWritingTask(string taskDir, string file, string? sentinelPath)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}}", "writeScope": ["{{file}}"] }""");

        string action = Ps
            ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/{file}\" -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), action);

        string guardrail = Path.Combine(taskDir, "guardrails", Script("01-check"));
        if (sentinelPath is null)
        {
            WriteFileExistsGate(guardrail, file);
        }
        else
        {
            WriteSentinelGate(guardrail, file, sentinelPath);
        }
    }

    private static void WriteFileExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\n"
              + $"if (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/{file}\")) {{ Write-Output \"{file} is missing\"; exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n"
              + $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo \"{file} is missing\"; exit 1; }}\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteSentinelGate(string path, string file, string sentinelPath)
    {
        string sentinel = sentinelPath.Replace('\\', '/');
        string body = Ps
            ? $"# catches: {file} not present, or the human decision this task waits on not yet made\n"
              + $"if (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/{file}\")) {{ Write-Output \"{file} is missing\"; exit 1 }}\n"
              + $"if (-not (Test-Path \"{sentinel}\")) {{ Write-Output 'still blocked: this task needs a human'; exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present, or the human decision this task waits on not yet made\n"
              + $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo \"{file} is missing\"; exit 1; }}\n"
              + $"[ -f \"{sentinel}\" ] || {{ echo 'still blocked: this task needs a human'; exit 1; }}\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (BranchMovedHaltTests), plus the checkout moves these rows need.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr726-dt-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            WorktreeRoot = Path.Combine(Root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# delivery-target test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string TipOf(string branch) => Git(RepoPath, "rev-parse", "refs/heads/" + branch).Trim();

        public bool BranchHasFile(string branch, string relativePath)
        {
            (_, int exit) = TryGit(RepoPath, "cat-file", "-e", $"{branch}:{relativePath}");
            return exit == 0;
        }

        /// <summary><c>git switch</c> in the user's checkout, e.g. <c>-c spike</c> or <c>--detach</c>.</summary>
        public void Switch(params string[] args) => _ = Git(RepoPath, ["switch", .. args]);

        public static string Git(string workingDir, params string[] args)
        {
            (string stdout, string stderr, int exit) = RunGit(workingDir, args);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {exit}: {stderr.Trim()}");
            }

            return stdout;
        }

        public static (string Output, int ExitCode) TryGit(string workingDir, params string[] args)
        {
            (string stdout, _, int exit) = RunGit(workingDir, args);
            return (stdout, exit);
        }

        private static (string Stdout, string Stderr, int ExitCode) RunGit(string workingDir, string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (stdout, stderr, proc.ExitCode);
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(Root); } catch { /* best-effort teardown */ }
        }
    }
}
