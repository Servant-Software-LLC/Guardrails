using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Red-bar tests for design 39 §1c/§4 (review round 4, narrowed round 5): a wave-barrier delivery refusal
/// (a moved branch — issue #588, a real conflict, or a dirty working tree — issue #448) HALTS the run at
/// that wave under <see cref="WaveHaltKind.DeliveryRefused"/>, because the condition is not transient —
/// every later wave would hit the identical refusal. A hook-rejected trial is the one exception: it HOLDS
/// this and every later delivery to run end, where the merge runs in the user's own checkout (task 08/29
/// already implement that hold).
///
/// <para>
/// Drives the REAL <see cref="Scheduler"/> over a REAL <see cref="GitWorktreeProvider"/> (the
/// <c>WaveExecutionRunTests</c>/<c>WaveBarrierDeliveryTests</c> construction pattern) — never a provider
/// double: every assertion reads git state on the user's real branch and the plan branch, the run journal,
/// or a log/sentinel file a fixture script wrote, never a recorded call on a test double. Does NOT implement
/// the halt (task 17's job) — this file only pins the contract.
/// </para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class BranchMovedHaltTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (WaveExecutionRunTests / WaveBarrierDeliveryTests).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr-bmh-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            WorktreeRoot = Path.Combine(Root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);
            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# branch-moved-halt test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public bool BranchHasFile(string branch, string relativePath)
        {
            (_, int exit) = TryGit(RepoPath, "cat-file", "-e", $"{branch}:{relativePath}");
            return exit == 0;
        }

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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Composition root: the REAL Scheduler over the REAL GitWorktreeProvider.
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
            _ => throw new InvalidOperationException("No prompt runners in branch-moved-halt tests."));
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

    private static JournalDocument JournalOf(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir));

    private static string PlanBranch(string planDir) => $"guardrails/{Path.GetFileName(planDir)}";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // guardrails.json / brief.md / common script authoring
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string StandardGuardrailsJson() =>
        """
        {
          "version": 1,
          "guardrailMode": "failFast",
          "workspace": "..",
          "defaultRetries": 0,
          "maxParallelism": 2
        }
        """;

    private static void WriteDeliversBrief(string waveDir)
    {
        Directory.CreateDirectory(waveDir);
        File.WriteAllText(Path.Combine(waveDir, "brief.md"),
            "---\ndelivers: true\n---\n# wave brief\n\nDelivers at its own barrier.\n");
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

    private static void WriteFileExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\n" +
              $"if (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/{file}\")) {{ exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n" +
              $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || exit 1\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteAlwaysPassGate(string path)
    {
        string body = Ps
            ? "# catches: nothing -- always green\nexit 0\n"
            : "#!/usr/bin/env bash\n# catches: nothing -- always green\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteFileWritingTask(string taskDir, string file)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}}", "writeScope": ["{{file}}"] }""");

        string body = Ps
            ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/{file}\" -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteFileExistsGate(Path.Combine(taskDir, "guardrails", Script("01-check")), file);
    }

    // ── Group A fixtures: a switched checkout (#588 cause 1) ───────────────────────────────

    private static void WriteTaskSwitchingCheckout(string taskDir, string file, string userRepoPath, string branch)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}} and switch the user's checkout", "writeScope": ["{{file}}"] }""");

        string body = Ps
            ? """
              Set-Content -NoNewline -Path "$env:GUARDRAILS_WORKSPACE/__FILE__" -Value 'x'
              git -C "__REPO__" checkout "__BRANCH__" | Out-Null
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              git -C "__REPO__" checkout "__BRANCH__" > /dev/null 2>&1
              exit 0
              """;
        body = body.Replace("__FILE__", file).Replace("__REPO__", userRepoPath).Replace("__BRANCH__", branch);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteFileExistsGate(Path.Combine(taskDir, "guardrails", Script("01-check")), file);
    }

    private sealed record SwitchedCheckoutScenario(
        string PlanDir, string UserBranch, string OtherBranch, string Wave1Dir, string Wave2Dir, string Wave3Dir);

    /// <summary>
    /// Three waves: wave-01 delivers normally (the baseline for "already-delivered waves stay delivered");
    /// wave-02 delivers too, but its own task switches the user's real checkout onto another branch before
    /// wave-02 reaches its barrier — so <c>PromoteTrialDelivery</c>'s #588 check refuses it as
    /// <see cref="MergeOnSuccessResult.BranchMoved"/>; wave-03 is the plan's final wave (never delivers at
    /// its own barrier), used to prove later waves never start once a delivery is known impossible.
    /// </summary>
    private static SwitchedCheckoutScenario CreateSwitchedCheckoutScenarioPlan(TempGitRepo repo)
    {
        string userBranch = repo.CurrentBranch();
        const string otherBranch = "elsewhere";
        TempGitRepo.Git(repo.RepoPath, "branch", otherBranch);

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        const string w1 = "wave-01-first";
        const string w2 = "wave-02-switch";
        const string w3 = "wave-03-final";

        string wd1 = Path.Combine(planDir, w1);
        WriteDeliversBrief(wd1);
        WriteFileWritingTask(Path.Combine(wd1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(wd1, "guardrails", Script("01-check")), "wave1.txt");

        string wd2 = Path.Combine(planDir, w2);
        WriteDeliversBrief(wd2);
        WriteTaskSwitchingCheckout(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt", repo.RepoPath, otherBranch);
        WriteFileExistsGate(Path.Combine(wd2, "guardrails", Script("01-check")), "wave2.txt");

        string wd3 = Path.Combine(planDir, w3);
        WriteFileWritingTask(Path.Combine(wd3, "tasks", "01-write"), "wave3.txt");
        WriteFileExistsGate(Path.Combine(wd3, "guardrails", Script("01-check")), "wave3.txt");

        return new SwitchedCheckoutScenario(planDir, userBranch, otherBranch, w1, w2, w3);
    }

    // ── Group B fixtures: a real conflict (CreateTrialDelivery's own Refusal route) ─────────

    private static void WriteTaskCreatingConflict(string taskDir, string file, string userRepoPath)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}} and conflict with the user's branch", "writeScope": ["{{file}}"] }""");

        string body = Ps
            ? """
              Set-Content -NoNewline -Path "$env:GUARDRAILS_WORKSPACE/__FILE__" -Value 'plan version'
              Set-Content -NoNewline -Path "__REPO__/__FILE__" -Value 'teammate version'
              git -C "__REPO__" add "__FILE__"
              git -C "__REPO__" commit -m "conflicting teammate commit"
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'plan version' > "$GUARDRAILS_WORKSPACE/__FILE__"
              printf 'teammate version' > "__REPO__/__FILE__"
              git -C "__REPO__" add "__FILE__"
              git -C "__REPO__" commit -m "conflicting teammate commit"
              exit 0
              """;
        body = body.Replace("__FILE__", file).Replace("__REPO__", userRepoPath);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteFileExistsGate(Path.Combine(taskDir, "guardrails", Script("01-check")), file);
    }

    // ── Group C fixtures: the user's branch advances AFTER the trial was built (#588 cause 2) ──

    private static void WriteTaskWritingFileAndUserCommit(
        string taskDir, string workspaceFile, string userRepoPath, string userCommitFile,
        string commitMessage, string shaFile)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write and commit to the user's repo", "writeScope": ["{{workspaceFile}}"] }""");

        string body = Ps
            ? """
              Set-Content -NoNewline -Path "$env:GUARDRAILS_WORKSPACE/__FILE__" -Value 'x'
              Set-Content -NoNewline -Path "__REPO__/__COMMITFILE__" -Value 'from a teammate'
              git -C "__REPO__" add "__COMMITFILE__"
              git -C "__REPO__" commit -m "__MSG__"
              $sha = (git -C "__REPO__" rev-parse HEAD).Trim()
              Set-Content -NoNewline -Path "__SHAFILE__" -Value $sha
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              printf 'from a teammate' > "__REPO__/__COMMITFILE__"
              git -C "__REPO__" add "__COMMITFILE__"
              git -C "__REPO__" commit -m "__MSG__"
              git -C "__REPO__" rev-parse HEAD | tr -d '\n' > "__SHAFILE__"
              exit 0
              """;
        body = body.Replace("__FILE__", workspaceFile).Replace("__REPO__", userRepoPath)
            .Replace("__COMMITFILE__", userCommitFile).Replace("__MSG__", commitMessage)
            .Replace("__SHAFILE__", shaFile);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    /// <summary>
    /// The wave's OWN exit gate: a no-op UNLESS <c>teammate.txt</c> is present in the tree this gate ran
    /// against ($GUARDRAILS_WORKSPACE). Absent against the integration worktree (teammate.txt lives only on
    /// the user's real branch, not the plan branch); present against the TRIAL worktree (the trial merges
    /// the user's tip, which carries it, into the plan tip). When present, commits a NEW file directly onto
    /// the user's real branch — advancing it AFTER <c>CreateTrialDelivery</c> already captured its tip —
    /// and records the resulting sha to <paramref name="shaFile2"/>.
    /// </summary>
    private static void WriteTeammateTriggeredUserAdvanceGate(
        string path, string userRepoPath, string advanceFile, string shaFile2)
    {
        string body = Ps
            ? """
              # catches: nothing -- advances the user's branch once the tree carries teammate.txt
              if (Test-Path "$env:GUARDRAILS_WORKSPACE/teammate.txt") {
                Set-Content -NoNewline -Path "__REPO__/__ADVANCE__" -Value 'advanced after trial'
                git -C "__REPO__" add "__ADVANCE__"
                git -C "__REPO__" commit -m "advance after trial was built"
                $sha = (git -C "__REPO__" rev-parse HEAD).Trim()
                Set-Content -NoNewline -Path "__SHAFILE2__" -Value $sha
              }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: nothing -- advances the user's branch once the tree carries teammate.txt
              if [ -f "$GUARDRAILS_WORKSPACE/teammate.txt" ]; then
                printf 'advanced after trial' > "__REPO__/__ADVANCE__"
                git -C "__REPO__" add "__ADVANCE__"
                git -C "__REPO__" commit -m "advance after trial was built"
                git -C "__REPO__" rev-parse HEAD | tr -d '\n' > "__SHAFILE2__"
              fi
              exit 0
              """;
        body = body.Replace("__REPO__", userRepoPath).Replace("__ADVANCE__", advanceFile)
            .Replace("__SHAFILE2__", shaFile2);
        WriteExecutable(path, body);
    }

    private static (string PlanDir, string Wave1Dir, string ShaFile1, string ShaFile2)
        CreateBranchAdvancedAfterTrialScenarioPlan(TempGitRepo repo)
    {
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile1 = Path.Combine(repo.Root, "user-tip-at-trial.txt");
        string shaFile2 = Path.Combine(repo.Root, "user-tip-after-advance.txt");

        const string w1 = "wave-01-advance";
        string wd1 = Path.Combine(planDir, w1);
        WriteDeliversBrief(wd1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(wd1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile1);
        WriteTeammateTriggeredUserAdvanceGate(
            Path.Combine(wd1, "guardrails", Script("01-check")), repo.RepoPath, "advance.txt", shaFile2);

        const string w2 = "wave-02-final";
        string wd2 = Path.Combine(planDir, w2);
        WriteFileWritingTask(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(wd2, "guardrails", Script("01-check")), "wave2.txt");

        return (planDir, w1, shaFile1, shaFile2);
    }

    // ── Group D fixtures: a hook-rejected trial holds later deliveries but never halts ──────

    /// <summary>
    /// A <c>pre-commit</c> hook that passes iff an UNTRACKED <c>hook-ok.txt</c> exists in the directory it
    /// runs in (present only in the user's real checkout — the harness's own trial/plumbing worktrees never
    /// carry it). Appends its verdict ("passed"/"rejected") to <paramref name="logPath"/>, an absolute path
    /// OUTSIDE the repo. Mirrors <c>GitHookIsolationTests</c>' pattern, including the executable bit.
    /// </summary>
    private static void InstallHookOkGatedHook(string repoPath, string logPath)
    {
        string hooksDir = Path.Combine(repoPath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        string hookPath = Path.Combine(hooksDir, "pre-commit");
        string body = """
            #!/bin/sh
            if [ -f hook-ok.txt ]; then
              echo passed >> "__LOG__"
              exit 0
            else
              echo rejected >> "__LOG__"
              exit 1
            fi
            """.Replace("__LOG__", logPath.Replace('\\', '/'));
        File.WriteAllText(hookPath, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void WriteTaskRecordingUserBranchTip(
        string taskDir, string workspaceFile, string userRepoPath, string userBranch, string sentinelFile)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{workspaceFile}} and record the user's branch tip", "writeScope": ["{{workspaceFile}}"] }""");

        string body = Ps
            ? """
              Set-Content -NoNewline -Path "$env:GUARDRAILS_WORKSPACE/__FILE__" -Value 'x'
              $sha = (git -C "__REPO__" rev-parse __BRANCH__).Trim()
              Set-Content -NoNewline -Path "__SENTINEL__" -Value $sha
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              git -C "__REPO__" rev-parse __BRANCH__ | tr -d '\n' > "__SENTINEL__"
              exit 0
              """;
        body = body.Replace("__FILE__", workspaceFile).Replace("__REPO__", userRepoPath)
            .Replace("__BRANCH__", userBranch).Replace("__SENTINEL__", sentinelFile);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests — Group A: a switched checkout (#588 cause 1, `PromoteTrialDelivery`).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeliveryHittingBranchMoved_HaltsTheRunAtThatWave()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (RunReport report, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.DeliveryRefused, report.WaveHalt!.Kind);
        Assert.Equal(s.Wave2Dir, report.WaveHalt.WaveDir);
    }

    [Fact]
    public async Task LaterWavesDoNotRun_AfterABranchMovedHalt()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (RunReport report, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.WaveHalt);
        Assert.False(repo.BranchHasFile(PlanBranch(s.PlanDir), "wave3.txt"),
            "wave-03 must never start once its delivery is already known to be impossible.");
        Assert.NotEqual(WaveStatus.Completed, journal.WaveEntryOf(s.Wave3Dir)?.Status ?? WaveStatus.Pending);
    }

    [Fact]
    public async Task TheHaltNamesThePinnedTargetAndTheCurrentHead()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (RunReport report, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.WaveHalt);
        Assert.Contains(
            $"run started on '{s.UserBranch}'; HEAD is now '{s.OtherBranch}'",
            report.WaveHalt!.Headline, StringComparison.Ordinal);
        Assert.Contains($"check out '{s.UserBranch}' again", report.WaveHalt.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHaltKindIsDeliveryRefused_NotAGateFailure()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (RunReport report, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.DeliveryRefused, report.WaveHalt!.Kind);
        Assert.NotEqual(WaveHaltKind.ExitGateFailed, report.WaveHalt.Kind);
        Assert.Empty(report.WaveHalt.FailedGates);
    }

    [Fact]
    public async Task TheRefusalIsDurable_OnTheWaveNotInHalt()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (_, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(WaveDeliveryStatus.Refused, journal.WaveEntryOf(s.Wave2Dir)?.Delivered?.Status);
        Assert.Equal(WaveStatus.NeedsHuman, journal.WaveEntryOf(s.Wave2Dir)?.Status);

        JournalDocument doc = JournalOf(s.PlanDir);
        Assert.Null(doc.Halt);
    }

    [Fact]
    public async Task ARefusedDelivery_RecordsAHaltedDecision()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (RunReport report, _) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.NotNull(report.WaveHalt);

        JournalDocument doc = JournalOf(s.PlanDir);
        IReadOnlyList<DecisionEntry> decisions = doc.Decisions ?? [];
        List<DecisionEntry> refusals = decisions.Where(d => d.Gate == "delivery-refused").ToList();

        Assert.Single(refusals);
        DecisionEntry entry = refusals[0];
        Assert.Equal("wave", entry.Boundary);
        Assert.Equal(DecisionTokens.Halted, entry.Decision);
        Assert.Equal(s.Wave2Dir, entry.Subject);
        Assert.Equal(s.Wave2Dir, entry.Wave);
        Assert.Equal(report.WaveHalt!.Headline, entry.Headline);
    }

    [Fact]
    public async Task AResumeAfterARefusedDelivery_ReattemptsItAtThatWave()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        // The operator's remedy: check the original branch back out, then re-run.
        TempGitRepo.Git(repo.RepoPath, "checkout", s.UserBranch);

        (RunReport report2, RunJournal journal2) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(WaveDeliveryStatus.Delivered, journal2.WaveEntryOf(s.Wave2Dir)?.Delivered?.Status);
        Assert.True(repo.BranchHasFile(s.UserBranch, "wave2.txt"));
        Assert.True(report2.AllSucceeded, string.Join("; ", report2.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
    }

    [Fact]
    public async Task AlreadyDeliveredWavesStayDelivered()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        (_, RunJournal journal) = await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(WaveDeliveryStatus.Delivered, journal.WaveEntryOf(s.Wave1Dir)?.Delivered?.Status);
        Assert.True(repo.BranchHasFile(s.UserBranch, "wave1.txt"));
    }

    [Fact]
    public async Task TheUsersCheckoutIsNotModified()
    {
        using var repo = new TempGitRepo();
        SwitchedCheckoutScenario s = CreateSwitchedCheckoutScenarioPlan(repo);

        await RunAsync(s.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(s.OtherBranch, repo.CurrentBranch());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests — Group B: a real conflict, refused when the trial is BUILT (`CreateTrialDelivery`).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConflictingWaveDelivery_AlsoHaltsAtThatWave()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        const string w1 = "wave-01-conflict";
        string wd1 = Path.Combine(planDir, w1);
        WriteDeliversBrief(wd1);
        WriteTaskCreatingConflict(Path.Combine(wd1, "tasks", "01-write"), "conflict.txt", repo.RepoPath);
        WriteFileExistsGate(Path.Combine(wd1, "guardrails", Script("01-check")), "conflict.txt");

        const string w2 = "wave-02-final";
        string wd2 = Path.Combine(planDir, w2);
        WriteFileWritingTask(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(wd2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.DeliveryRefused, report.WaveHalt!.Kind);
        Assert.Equal(w1, report.WaveHalt.WaveDir);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests — Group C: the user's branch advances AFTER the trial was built (#588 cause 2,
    // `PromoteTrialDelivery`).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHaltNamesBothTips_WhenTheUsersBranchAdvancedAfterTheTrial()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        (string planDir, string wave1Dir, string shaFile1, string shaFile2) =
            CreateBranchAdvancedAfterTrialScenarioPlan(repo);

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(File.Exists(shaFile1), "wave-01's mid-run commit never landed.");
        Assert.True(File.Exists(shaFile2), "the trial-tree gate's user-branch advance never fired.");
        string tipAtTrial = File.ReadAllText(shaFile1).Trim();
        string tipAfterAdvance = File.ReadAllText(shaFile2).Trim();
        string sha10AtTrial = tipAtTrial[..10];
        string sha10AfterAdvance = tipAfterAdvance[..10];

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(wave1Dir, report.WaveHalt!.WaveDir);
        Assert.Contains(
            $"'{userBranch}' moved from {sha10AtTrial} to {sha10AfterAdvance} after the trial was built",
            report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains("resume", report.WaveHalt.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check out", report.WaveHalt.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(tipAfterAdvance, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests — Group D: a hook-rejected trial holds later deliveries, but never halts the run.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHookRejectedTrial_DoesNotHaltTheRun_AndHoldsLaterDeliveries()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string hookLog = Path.Combine(repo.Root, "hook-verdict-log.txt");
        File.WriteAllText(Path.Combine(repo.RepoPath, "hook-ok.txt"), "ok\n");
        File.AppendAllText(Path.Combine(repo.RepoPath, ".git", "info", "exclude"), "hook-ok.txt\n");
        InstallHookOkGatedHook(repo.RepoPath, hookLog);

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string sentinel1 = Path.Combine(repo.Root, "sentinel-after-wave1.txt");
        string sentinel2 = Path.Combine(repo.Root, "sentinel-at-wave3.txt");

        const string w1 = "wave-01-first";
        string wd1 = Path.Combine(planDir, w1);
        WriteDeliversBrief(wd1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(wd1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", sentinel1);
        WriteFileExistsGate(Path.Combine(wd1, "guardrails", Script("01-check")), "wave1.txt");

        const string w2 = "wave-02-second";
        string wd2 = Path.Combine(planDir, w2);
        WriteDeliversBrief(wd2);
        WriteFileWritingTask(Path.Combine(wd2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(wd2, "guardrails", Script("01-check")), "wave2.txt");

        const string w3 = "wave-03-final";
        string wd3 = Path.Combine(planDir, w3);
        WriteTaskRecordingUserBranchTip(
            Path.Combine(wd3, "tasks", "01-write"), "wave3.txt", repo.RepoPath, userBranch, sentinel2);
        WriteFileExistsGate(Path.Combine(wd3, "guardrails", Script("01-check")), "wave3.txt");

        (RunReport report, RunJournal journal) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.Equal(WaveDeliveryStatus.Refused, journal.WaveEntryOf(w1)?.Delivered?.Status);
        Assert.Equal(DeliveryOutcome.HookRejected, journal.WaveEntryOf(w1)?.Delivered?.Outcome);
        Assert.Equal(WaveDeliveryStatus.Suppressed, journal.WaveEntryOf(w2)?.Delivered?.Status);

        string[] hookLines = File.Exists(hookLog)
            ? File.ReadAllLines(hookLog).Where(l => l.Length > 0).Select(l => l.Trim()).ToArray()
            : [];
        Assert.Contains("rejected", hookLines);
        Assert.NotEmpty(hookLines);
        Assert.Equal("passed", hookLines[^1]);

        Assert.Null(report.WaveHalt);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.True(repo.BranchHasFile(PlanBranch(planDir), "wave3.txt"), "wave-03 never ran.");

        Assert.True(File.Exists(sentinel1));
        Assert.True(File.Exists(sentinel2));
        Assert.Equal(File.ReadAllText(sentinel1).Trim(), File.ReadAllText(sentinel2).Trim());

        Assert.True(repo.BranchHasFile(userBranch, "wave1.txt"));
        Assert.True(repo.BranchHasFile(userBranch, "wave2.txt"));
        Assert.True(repo.BranchHasFile(userBranch, "wave3.txt"));
    }
}
