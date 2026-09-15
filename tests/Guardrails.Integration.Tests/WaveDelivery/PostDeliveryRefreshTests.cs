using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Red-bar tests for design 39 §1c "Does a delivering wave let later waves pick up the user's branch?",
/// and its "How a refresh is recorded (post-plan-40 refinement)" subsection: once a wave's barrier
/// delivery is a NON-fast-forward (the user's branch moved during the run), the plan branch must be
/// refreshed with the user's new tip before the next wave's gates run over it, and that refresh must be
/// named, provenanced and ordered correctly.
/// <para>
/// Drives the REAL <see cref="Scheduler"/> over a REAL <see cref="GitWorktreeProvider"/>
/// (<see cref="WaveBarrierDeliveryTests"/>' / <see cref="TrialDeliveryPrimitiveTests"/>' composition-root
/// pattern) against a temp git repo — never a provider double. Every "mid-run" change to the user's real
/// branch is made by a task or gate SCRIPT the fixture writes, never by a fake or recording
/// <c>IWorktreeProvider</c>: the house fake (<c>FakeWorktreeProvider.MergePlanBranchIntoUserBranch</c>)
/// hardcodes <c>MergeOnSuccessResult.FastForwarded</c> and makes no commits, so it cannot express any of
/// this file's non-fast-forward behaviour at all.
/// </para>
/// <para>
/// <b>TDD red.</b> The Scheduler does not yet refresh the plan branch after a non-fast-forward barrier
/// delivery, so every test below except <see cref="AFastForwardDelivery_DoesNotRefresh"/> is expected to
/// FAIL against this tree (task 15 implements the refresh). <see cref="AFastForwardDelivery_DoesNotRefresh"/>
/// is declared exempt from the red census: in the quiet fast-forward case nothing ever needs to refresh, so
/// today's code — which never refreshes anything — already leaves a correct test of it green.
/// </para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class PostDeliveryRefreshTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (WaveBarrierDeliveryTests / TrialDeliveryPrimitiveTests).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr-pdr-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            WorktreeRoot = Path.Combine(Root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# post-delivery refresh test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public bool BranchHasFile(string branch, string relativePath)
        {
            (_, int exit) = TryGit(RepoPath, "cat-file", "-e", $"{branch}:{relativePath}");
            return exit == 0;
        }

        public bool IsAncestor(string ancestor, string descendant)
        {
            (_, int exit) = TryGit(RepoPath, "merge-base", "--is-ancestor", ancestor, descendant);
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
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort teardown
            }
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
            _ => throw new InvalidOperationException("No prompt runners in post-delivery-refresh tests."));
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

    private static WaveJournalEntry? WaveEntryOf(JournalDocument doc, string waveDir) =>
        doc.Waves is not null && doc.Waves.TryGetValue(waveDir, out WaveJournalEntry? entry) ? entry : null;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // guardrails.json / brief.md authoring
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

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Script writers — cross-platform (.ps1 / .sh).
    // ─────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Write a task that writes <paramref name="file"/> into its workspace, with a trivially-green task guardrail.</summary>
    private static void WriteFileWritingTask(string taskDir, string file)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}}", "writeScope": ["{{file}}"] }""");

        string body = Ps
            ? $"$p = Join-Path $env:GUARDRAILS_WORKSPACE \"{file}\"\n" +
              "New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null\n" +
              "Set-Content -NoNewline -Path $p -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nmkdir -p \"$(dirname \"$GUARDRAILS_WORKSPACE/{file}\")\"\n" +
              $"printf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    private static void WriteAlwaysPassGate(string path)
    {
        string body = Ps
            ? "# catches: nothing -- always green\nexit 0\n"
            : "#!/usr/bin/env bash\n# catches: nothing -- always green\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteFileExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\n" +
              $"if (-not (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE \"{file}\"))) {{ exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n" +
              $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || exit 1\nexit 0\n";
        WriteExecutable(path, body);
    }

    /// <summary>Fails iff <c>teammate.txt</c> exists in the tree this gate runs in ($GUARDRAILS_WORKSPACE). Used as both an entry preflight and an exit gate.</summary>
    private static void WriteFailsIfTeammatePresentGate(string path)
    {
        string body = Ps
            ? """
              # catches: teammate.txt present in the tree this gate ran against
              if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE "teammate.txt")) { exit 1 }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: teammate.txt present in the tree this gate ran against
              [ -f "$GUARDRAILS_WORKSPACE/teammate.txt" ] && exit 1
              exit 0
              """;
        WriteExecutable(path, body);
    }

    /// <summary>
    /// Always passes. Leaves an UNTRACKED <c>teammate.txt</c> behind in $GUARDRAILS_WORKSPACE the first time
    /// it runs against a tree that lacks the file — used to make the later refresh merge collide with an
    /// untracked file already sitting in the integration worktree.
    /// </summary>
    private static void WriteCreateUntrackedTeammateIfAbsentGate(string path)
    {
        string body = Ps
            ? """
              # catches: nothing -- always green; leaves an untracked teammate.txt behind when absent
              $p = Join-Path $env:GUARDRAILS_WORKSPACE "teammate.txt"
              if (-not (Test-Path $p)) { Set-Content -NoNewline -Path $p -Value 'untracked from exit gate' }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: nothing -- always green; leaves an untracked teammate.txt behind when absent
              p="$GUARDRAILS_WORKSPACE/teammate.txt"
              [ -f "$p" ] || printf 'untracked from exit gate' > "$p"
              exit 0
              """;
        WriteExecutable(path, body);
    }

    /// <summary>
    /// A task that writes <paramref name="workspaceFile"/> into its own workspace AND, as a teammate would,
    /// commits <paramref name="userCommitFile"/> directly onto the user's real checkout at
    /// <paramref name="userRepoPath"/> — the "mid-run commit" that makes a barrier delivery non-fast-forward.
    /// The resulting user-branch commit sha is written to <paramref name="shaFile"/> (outside the repo).
    /// </summary>
    private static void WriteTaskWritingFileAndUserCommit(
        string taskDir, string workspaceFile, string userRepoPath, string userCommitFile,
        string commitMessage, string shaFile)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write and commit to the user's repo", "writeScope": ["{{workspaceFile}}"] }""");

        string body = Ps
            ? """
              $p = Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__"
              New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
              Set-Content -NoNewline -Path $p -Value 'x'
              Set-Content -NoNewline -Path (Join-Path "__REPO__" "__COMMITFILE__") -Value 'from a teammate'
              git -C "__REPO__" add "__COMMITFILE__"
              git -C "__REPO__" commit -m "__MSG__"
              $sha = (git -C "__REPO__" rev-parse HEAD).Trim()
              Set-Content -NoNewline -Path "__SHAFILE__" -Value $sha
              exit 0
              """
            : """
              #!/usr/bin/env bash
              mkdir -p "$(dirname "$GUARDRAILS_WORKSPACE/__FILE__")"
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

    /// <summary>A task that writes <paramref name="workspaceFile"/> AND a sentinel file at an absolute path OUTSIDE the repo — proof the task ran.</summary>
    private static void WriteSentinelWritingTask(string taskDir, string workspaceFile, string sentinelPath)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write sentinel", "writeScope": ["{{workspaceFile}}"] }""");
        string body = Ps
            ? """
              Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__") -Value 'x'
              Set-Content -NoNewline -Path "__SENTINEL__" -Value 'ran'
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              printf 'ran' > "__SENTINEL__"
              exit 0
              """;
        body = body.Replace("__FILE__", workspaceFile).Replace("__SENTINEL__", sentinelPath);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    /// <summary>
    /// A task that writes <paramref name="workspaceFile"/> AND records, into a sentinel file OUTSIDE the
    /// repo, whether <c>teammate.txt</c> exists in its OWN workspace (a segment forked from the plan-branch
    /// tip) — proof of whether the tree this wave builds on already carries a refresh.
    /// </summary>
    private static void WriteTeammatePresenceRecordingTask(string taskDir, string workspaceFile, string sentinelPath)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "record teammate presence", "writeScope": ["{{workspaceFile}}"] }""");
        string body = Ps
            ? """
              Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__") -Value 'x'
              if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE "teammate.txt")) {
                Set-Content -NoNewline -Path "__SENTINEL__" -Value 'present'
              } else {
                Set-Content -NoNewline -Path "__SENTINEL__" -Value 'absent'
              }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              if [ -f "$GUARDRAILS_WORKSPACE/teammate.txt" ]; then
                printf 'present' > "__SENTINEL__"
              else
                printf 'absent' > "__SENTINEL__"
              fi
              exit 0
              """;
        body = body.Replace("__FILE__", workspaceFile).Replace("__SENTINEL__", sentinelPath);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    /// <summary>
    /// A wave exit gate that merges <paramref name="planBranch"/> directly INTO the user's own checkout at
    /// <paramref name="userRepoPath"/> (as if the user pulled the plan branch themselves), then writes the
    /// resulting HEAD sha to <paramref name="shaSentinelPath"/> — the <c>AlreadyDelivered</c> trigger.
    /// </summary>
    private static void WriteMergePlanIntoUserRepoExitGate(
        string path, string userRepoPath, string planBranch, string shaSentinelPath)
    {
        string body = Ps
            ? """
              # catches: the plan branch failing to merge into the user's own checkout
              git -C "__REPO__" merge --no-edit "__BRANCH__"
              if ($LASTEXITCODE -ne 0) { exit 1 }
              $sha = (git -C "__REPO__" rev-parse HEAD).Trim()
              Set-Content -NoNewline -Path "__SHAFILE__" -Value $sha
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: the plan branch failing to merge into the user's own checkout
              git -C "__REPO__" merge --no-edit "__BRANCH__" || exit 1
              git -C "__REPO__" rev-parse HEAD | tr -d '\n' > "__SHAFILE__"
              exit 0
              """;
        body = body.Replace("__REPO__", userRepoPath).Replace("__BRANCH__", planBranch)
            .Replace("__SHAFILE__", shaSentinelPath);
        WriteExecutable(path, body);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The shared non-fast-forward fixture: wave-01 delivers, and one of its own tasks also commits
    // teammate.txt directly onto the user's real branch mid-run, so wave-01's barrier delivery cannot be a
    // fast-forward (§1c's refresh trigger). wave-01's own exit gate is unrelated to teammate.txt, so it
    // passes regardless. Every plan built from this fixture needs a wave-02 after it (the final wave never
    // barrier-delivers).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string SetUpDeliveringWaveOneWithMidRunCommit(string planDir, TempGitRepo repo, string shaFile)
    {
        const string waveDir = "wave-01-deliver";
        string w1 = Path.Combine(planDir, waveDir);
        WriteDeliversBrief(w1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile);
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");
        return waveDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Declared exempt from the red census. In the quiet fast-forward case (no mid-run commit) nothing
    /// ever needs to refresh, so today's code — which never refreshes anything — already leaves this test
    /// green. Rejects: a test that merely checks <c>MergeOnSuccessResult.FastForwarded</c>, which is true
    /// on EVERY delivery under §1's trial merge and therefore discriminates nothing.
    /// </summary>
    [Fact]
    public async Task AFastForwardDelivery_DoesNotRefresh()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());
        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        string planBranchLog = TempGitRepo.Git(repo.RepoPath, "log", planBranch, "--format=%B");
        Assert.DoesNotContain("Refreshed-From:", planBranchLog, StringComparison.Ordinal);

        JournalDocument doc = JournalOf(planDir);
        Assert.Null(doc.Refreshed);
    }

    /// <summary>The main happy-path pin: a non-fast-forward barrier delivery must land the user's mid-run commit back onto the plan branch.</summary>
    [Fact]
    public async Task ANonFastForwardDelivery_RefreshesThePlanBranch()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());
        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        _ = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.True(repo.BranchHasFile(planBranch, "teammate.txt"),
            "a non-fast-forward delivery must refresh the plan branch with the user's mid-run commit.");

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Refreshed);
        Assert.Single(doc.Refreshed!);
    }

    /// <summary>After a refresh, the next wave's own task workspace (forked from the plan-branch tip) must already carry the user's new commit.</summary>
    [Fact]
    public async Task AfterARefresh_TheNextWaveBuildsOnTheUsersNewCommits()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        _ = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        string presenceSentinel = Path.Combine(repo.Root, "wave2-presence-" + Guid.NewGuid().ToString("N") + ".txt");
        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteTeammatePresenceRecordingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt", presenceSentinel);
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.True(File.Exists(presenceSentinel), "wave-02's task never ran.");
        Assert.Equal("present", File.ReadAllText(presenceSentinel).Trim());
    }

    /// <summary>
    /// The full provenance pin (design 39 §1c "How a refresh is recorded"): the record's six fields, the
    /// refresh commit's parent order (plan side first, user side second), the trailer, and the plan
    /// branch's <c>--first-parent</c> spine.
    /// </summary>
    [Fact]
    public async Task TheRefreshIsRecordedAsProvenance()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());
        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        string waveDir = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Refreshed);
        RefreshedRecord refreshed = Assert.Single(doc.Refreshed!);
        Assert.Null(doc.Supplied);

        Assert.Equal(userBranch, refreshed.From);
        Assert.Equal(waveDir, refreshed.DeliveredWave);

        // "The user's branch tip after delivery" is wave-01's OWN delivered commit, read from the journal
        // rather than a live git query — wave-02 is not itself a delivery point, so the plan's UNCONDITIONAL
        // run-end delivery (Finalize) legitimately advances the user's branch again before RunAsync returns.
        WaveDeliveredRecord? delivered = WaveEntryOf(doc, waveDir)?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(delivered!.Commit, refreshed.Upstream);

        string parent1 = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{refreshed.Commit}^1").Trim();
        string parent2 = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{refreshed.Commit}^2").Trim();
        Assert.Equal(refreshed.Upstream, parent2);
        Assert.NotEqual(refreshed.Upstream, parent1);

        string[] planFirstParents = TempGitRepo.Git(repo.RepoPath, "log", "--first-parent", "--format=%H", planBranch)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        Assert.Contains(refreshed.Commit, planFirstParents);
        Assert.Contains(parent1, planFirstParents);

        Assert.False(repo.BranchHasFile(parent1, "teammate.txt"),
            "the refresh commit's first parent must be the plan side, which never authored teammate.txt.");

        Assert.Contains("teammate.txt", refreshed.Paths);

        string commitMessage = TempGitRepo.Git(repo.RepoPath, "log", "-1", "--format=%B", refreshed.Commit);
        Assert.Contains($"Refreshed-From: {userBranch}", commitMessage, StringComparison.Ordinal);
        Assert.Contains($"Guardrails-Run: {doc.RunId}", commitMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Supplied-By:", commitMessage, StringComparison.Ordinal);
    }

    /// <summary>A wave ENTRY preflight failing over a refreshed tree must name the refresh, not blame the wave.</summary>
    [Fact]
    public async Task AnEntryGateFailureOverARefreshedTree_NamesTheRefresh()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        _ = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        const string w2Dir = "wave-02-final";
        string w2 = Path.Combine(planDir, w2Dir);
        WriteFailsIfTeammatePresentGate(Path.Combine(w2, "preflights", Script("01-check-teammate")));
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        string userTipAfterRun = TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim();
        string upstream10 = userTipAfterRun[..10];

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.EntryGateFailed, report.WaveHalt!.Kind);
        Assert.StartsWith(
            $"Wave '{w2Dir}' entry preflight FAILED: 01-check-teammate", report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains(userBranch, report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains(upstream10, report.WaveHalt.Headline, StringComparison.Ordinal);

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Halt);
        Assert.Equal(RunHaltKind.WaveEntryGateFailed, doc.Halt!.Kind);
        Assert.Equal(report.WaveHalt.Headline, doc.Halt.Headline);
    }

    /// <summary>A wave EXIT gate failing over a refreshed tree must also name the refresh.</summary>
    [Fact]
    public async Task AnExitGateFailureOverARefreshedTree_NamesTheRefresh()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        _ = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        const string w2Dir = "wave-02-final";
        string w2 = Path.Combine(planDir, w2Dir);
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFailsIfTeammatePresentGate(Path.Combine(w2, "guardrails", Script("01-check-teammate")));

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        string userTipAfterRun = TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim();
        string upstream10 = userTipAfterRun[..10];

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.ExitGateFailed, report.WaveHalt!.Kind);
        Assert.StartsWith(
            $"Wave '{w2Dir}' exit gate FAILED: 01-check-teammate", report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains(userBranch, report.WaveHalt.Headline, StringComparison.Ordinal);
        Assert.Contains(upstream10, report.WaveHalt.Headline, StringComparison.Ordinal);

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Halt);
        Assert.Equal(RunHaltKind.WaveExitGateFailed, doc.Halt!.Kind);
        Assert.Equal(report.WaveHalt.Headline, doc.Halt.Headline);
    }

    /// <summary>The refresh commit must land on the plan branch BEFORE the delivering wave's own completion marker commit.</summary>
    [Fact]
    public async Task TheRefreshLandsBeforeTheWaveMarker()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        string waveDir = SetUpDeliveringWaveOneWithMidRunCommit(planDir, repo, shaFile);

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        JournalDocument doc = JournalOf(planDir);
        Assert.NotNull(doc.Refreshed);
        RefreshedRecord refreshed = Assert.Single(doc.Refreshed!);

        string? markerSha = WaveEntryOf(doc, waveDir)?.MarkerSha;
        Assert.False(string.IsNullOrEmpty(markerSha), "wave-01's completion marker was never recorded.");

        Assert.True(repo.IsAncestor(refreshed.Commit, markerSha!),
            "the refresh commit must be an ancestor of the wave-completion marker commit.");
    }

    /// <summary>
    /// A refresh that cannot land (an untracked file in the way) must ABORT honestly (#150) — never
    /// continue on the stale base, never fabricate a record with no commit, and never run the next wave.
    /// </summary>
    [Fact]
    public async Task AFailedRefresh_AbortsWithNoRecord_AndNoLaterWaveRuns()
    {
        using var repo = new TempGitRepo();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "midrun-commit-" + Guid.NewGuid().ToString("N") + ".txt");
        const string waveDir = "wave-01-deliver";
        string w1 = Path.Combine(planDir, waveDir);
        WriteDeliversBrief(w1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile);
        WriteCreateUntrackedTeammateIfAbsentGate(Path.Combine(w1, "guardrails", Script("01-check")));

        string sentinelPath = Path.Combine(repo.Root, "wave2-ran-" + Guid.NewGuid().ToString("N") + ".txt");
        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteSentinelWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt", sentinelPath);
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.NotNull(report.Abort);
        Assert.Contains("teammate.txt", report.Abort!.Headline, StringComparison.Ordinal);

        JournalDocument doc = JournalOf(planDir);
        Assert.Null(doc.Refreshed);

        WaveDeliveredRecord? delivered = WaveEntryOf(doc, waveDir)?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);

        Assert.False(File.Exists(sentinelPath), "wave-02 must never run after an aborted refresh.");
    }

    /// <summary>
    /// The trigger must be read from the trial's ancestry, not from <c>PromoteTrialDelivery</c>'s result:
    /// when the user merges the plan branch into their OWN checkout before the barrier promotes it, the
    /// trial reports <c>AlreadyDelivered</c> with <c>UserTipWasAncestor</c> false and promotion never runs
    /// at all — the refresh is still owed.
    /// </summary>
    [Fact]
    public async Task AnAlreadyDeliveredTrial_StillRefreshesThePlanBranch()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());
        string planBranch = $"guardrails/{Path.GetFileName(planDir)}";

        string throwawaySha = Path.Combine(repo.Root, "throwaway-" + Guid.NewGuid().ToString("N") + ".txt");
        const string waveDir = "wave-01-deliver";
        string w1 = Path.Combine(planDir, waveDir);
        WriteDeliversBrief(w1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", throwawaySha);

        string mSentinel = Path.Combine(repo.Root, "already-delivered-sha-" + Guid.NewGuid().ToString("N") + ".txt");
        WriteMergePlanIntoUserRepoExitGate(
            Path.Combine(w1, "guardrails", Script("01-merge-into-user-repo")), repo.RepoPath, planBranch, mSentinel);

        string presenceSentinel = Path.Combine(repo.Root, "wave2-presence-" + Guid.NewGuid().ToString("N") + ".txt");
        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteTeammatePresenceRecordingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt", presenceSentinel);
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.True(File.Exists(mSentinel), "wave-01's exit gate never ran the self-merge.");
        string m = File.ReadAllText(mSentinel).Trim();

        JournalDocument doc = JournalOf(planDir);
        WaveDeliveredRecord? delivered = WaveEntryOf(doc, waveDir)?.Delivered;
        Assert.NotNull(delivered);
        Assert.Equal(WaveDeliveryStatus.Delivered, delivered!.Status);
        Assert.Equal(m, delivered.Commit);

        // M must still be in the user's history (nothing rewrote or bypassed it) — checked by ancestry,
        // never equality, because wave-02 is not itself a delivery point, so the plan's UNCONDITIONAL
        // run-end delivery (Finalize; "APlanMarkingNoWave_StillMergesOnceAtRunEnd") legitimately advances
        // the user's branch again after wave-01's barrier, exactly as it always has.
        Assert.True(repo.IsAncestor(m, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim()),
            "wave-01's already-delivered commit M must remain on the user's branch.");

        Assert.NotNull(doc.Refreshed);
        RefreshedRecord refreshed = Assert.Single(doc.Refreshed!);
        Assert.Equal(m, refreshed.Upstream);

        Assert.True(File.Exists(presenceSentinel));
        Assert.Equal("present", File.ReadAllText(presenceSentinel).Trim());
    }
}
