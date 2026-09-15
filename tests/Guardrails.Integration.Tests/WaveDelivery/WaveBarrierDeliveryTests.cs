using System.CommandLine;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Red-bar tests for design 39 §1 — deliver at each wave's own barrier instead of once at run end.
/// Drives the REAL <see cref="Scheduler"/> over a REAL <see cref="GitWorktreeProvider"/> (or the real
/// <c>run</c> CLI command in process), never an injected fake of the delivery: every assertion reads git
/// state on the user's real branch and the plan branch, or a log file a fixture script wrote, never a
/// recorded call on a test double. Do NOT wire the delivery here (task 08's job) — this file only pins
/// the contract.
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class WaveBarrierDeliveryTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (WaveExecutionRunTests / TrialDeliveryPrimitiveTests), plus the
    // git-state queries this file's rows need: file-on-branch, ref existence, ancestry.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr-wbd-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            WorktreeRoot = Path.Combine(Root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# wave-barrier-delivery test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public bool BranchHasFile(string branch, string relativePath)
        {
            (_, int exit) = TryGit(RepoPath, "cat-file", "-e", $"{branch}:{relativePath}");
            return exit == 0;
        }

        public bool RefExists(string fullRefName)
        {
            (_, int exit) = TryGit(RepoPath, "show-ref", "--verify", "--quiet", fullRefName);
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
    // Composition-root drivers: the REAL Scheduler over the REAL GitWorktreeProvider, and the REAL
    // `run` CLI command in process (RunOutcomeWiringTests.RunViaCliAsync's pattern).
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
            _ => throw new InvalidOperationException("No prompt runners in wave-barrier-delivery tests."));
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

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("wave-barrier-delivery test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static JournalDocument JournalOf(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir));

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // guardrails.json / brief.md authoring
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string StandardGuardrailsJson(bool? mergeOnSuccess = null)
    {
        string mergeLine = mergeOnSuccess is { } m ? $"\n  \"mergeOnSuccess\": {(m ? "true" : "false")}," : "";
        return $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",{{mergeLine}}
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """;
    }

    private static void WriteDeliversBrief(string waveDir)
    {
        Directory.CreateDirectory(waveDir);
        File.WriteAllText(Path.Combine(waveDir, "brief.md"),
            "---\ndelivers: true\n---\n# wave brief\n\nDelivers at its own barrier.\n");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Script writers — cross-platform (.ps1 / .sh), each guardrail-shaped script opens with a
    // `catches:` declaration (the house convention `enforceCatches: true` expects).
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

    /// <summary>Write a task that writes <paramref name="file"/> (may include subdirectories) into its workspace, and a trivially-green task guardrail.</summary>
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

    private static void WriteAlwaysFailGate(string path, string reason = "always fails by design")
    {
        string body = Ps
            ? $"# catches: {reason}\nexit 1\n"
            : $"#!/usr/bin/env bash\n# catches: {reason}\nexit 1\n";
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

    /// <summary>
    /// Fails iff <c>teammate.txt</c> exists in the tree this gate runs in ($GUARDRAILS_WORKSPACE); appends
    /// one line per run to <paramref name="logPath"/> (outside the repo): "&lt;HEAD sha&gt;\t&lt;existed&gt;".
    /// </summary>
    private static void WriteTeammateSensitiveGate(string path, string logPath)
    {
        string body = Ps
            ? """
              # catches: teammate.txt present in the tree this gate ran against
              $ws = $env:GUARDRAILS_WORKSPACE
              $head = (git -C "$ws" rev-parse HEAD).Trim()
              $exists = Test-Path (Join-Path $ws "teammate.txt")
              Add-Content -Path "__LOG__" -Value "$head`t$exists"
              if ($exists) { exit 1 }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: teammate.txt present in the tree this gate ran against
              ws="$GUARDRAILS_WORKSPACE"
              head=$(git -C "$ws" rev-parse HEAD)
              if [ -f "$ws/teammate.txt" ]; then exists=true; else exists=false; fi
              printf '%s\t%s\n' "$head" "$exists" >> "__LOG__"
              if [ "$exists" = "true" ]; then exit 1; fi
              exit 0
              """;
        WriteExecutable(path, body.Replace("__LOG__", logPath));
    }

    /// <summary>
    /// Always passes; appends "present"/"absent" to <paramref name="logPath"/> saying whether
    /// <paramref name="relFile"/> exists at <paramref name="userBranch"/> in <paramref name="userRepoPath"/>
    /// at the moment this script ran — the mid-run observation point most rows use.
    /// </summary>
    private static void WriteBranchHasFileObserverGate(
        string path, string logPath, string userRepoPath, string userBranch, string relFile)
    {
        string body = Ps
            ? """
              # catches: nothing -- records whether the user's branch already has the file at this checkpoint
              git -C "__REPO__" cat-file -e "__BRANCH__:__FILE__" 2>$null
              if ($LASTEXITCODE -eq 0) {
                Add-Content -Path "__LOG__" -Value 'present'
              } else {
                Add-Content -Path "__LOG__" -Value 'absent'
              }
              exit 0
              """
            : """
              #!/usr/bin/env bash
              # catches: nothing -- records whether the user's branch already has the file at this checkpoint
              if git -C "__REPO__" cat-file -e "__BRANCH__:__FILE__" 2>/dev/null; then
                echo present >> "__LOG__"
              else
                echo absent >> "__LOG__"
              fi
              exit 0
              """;
        body = body.Replace("__REPO__", userRepoPath).Replace("__BRANCH__", userBranch)
            .Replace("__FILE__", relFile).Replace("__LOG__", logPath);
        WriteExecutable(path, body);
    }

    /// <summary>
    /// A task that writes <paramref name="workspaceFile"/> into its own workspace AND, as a teammate would,
    /// commits <paramref name="userCommitFile"/> directly onto the user's real checkout at
    /// <paramref name="userRepoPath"/> — the "mid-run commit" every merged-tree scenario needs. The resulting
    /// commit sha is written to <paramref name="shaFile"/> (outside the repo) so the test can read it back
    /// once the run completes.
    /// </summary>
    private static void WriteTaskWritingFileAndUserCommit(
        string taskDir, string workspaceFile, string userRepoPath, string userCommitFile,
        string commitMessage, string shaFile, bool noVerify = false)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write and commit to the user's repo", "writeScope": ["{{workspaceFile}}"] }""");

        string commitVerb = noVerify ? "commit --no-verify -m" : "commit -m";
        string body = Ps
            ? """
              $p = Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__"
              New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
              Set-Content -NoNewline -Path $p -Value 'x'
              Set-Content -NoNewline -Path (Join-Path "__REPO__" "__COMMITFILE__") -Value 'from a teammate'
              git -C "__REPO__" add "__COMMITFILE__"
              git -C "__REPO__" __COMMITVERB__ "__MSG__"
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
              git -C "__REPO__" __COMMITVERB__ "__MSG__"
              git -C "__REPO__" rev-parse HEAD | tr -d '\n' > "__SHAFILE__"
              exit 0
              """;
        body = body.Replace("__FILE__", workspaceFile).Replace("__REPO__", userRepoPath)
            .Replace("__COMMITFILE__", userCommitFile).Replace("__COMMITVERB__", commitVerb)
            .Replace("__MSG__", commitMessage).Replace("__SHAFILE__", shaFile);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The merged-tree scenario several rows share (§1 correctness point): a delivering wave whose exit
    // gate fails when teammate.txt exists, plus a commit landing on the user's branch mid-run (made by the
    // delivering wave's own task, before its own barrier), plus a later wave whose gate always fails so the
    // run never reaches run-end delivery regardless of what the barrier does.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed record MergedTreeScenario(string PlanDir, string WaveDir, string GateLogPath, string UserCommitShaFile);

    private static MergedTreeScenario CreateMergedTreeScenarioPlan(
        TempGitRepo repo, bool wave1GateFailsOnTeammate, bool wave2AlwaysFails, string planName = "plan")
    {
        string planDir = Path.Combine(repo.RepoPath, planName);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string gateLog = Path.Combine(repo.Root, "gate-log-" + Guid.NewGuid().ToString("N") + ".txt");
        string shaFile = Path.Combine(repo.Root, "user-commit-" + Guid.NewGuid().ToString("N") + ".txt");

        const string waveDir = "wave-01-deliver";
        string w1 = Path.Combine(planDir, waveDir);
        WriteDeliversBrief(w1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile);
        if (wave1GateFailsOnTeammate)
        {
            WriteTeammateSensitiveGate(Path.Combine(w1, "guardrails", Script("01-teammate-check")), gateLog);
        }
        else
        {
            WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");
        }

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        if (wave2AlwaysFails)
        {
            WriteAlwaysFailGate(Path.Combine(w2, "guardrails", Script("01-always-fail")));
        }
        else
        {
            WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");
        }

        return new MergedTreeScenario(planDir, waveDir, gateLog, shaFile);
    }

    private static string ReadUserMidRunCommit(MergedTreeScenario scenario) =>
        File.ReadAllText(scenario.UserCommitShaFile).Trim();

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Divergence fixture (mirrors DivergenceDeliveryGateTests): a task overwrites a dependent task's
    // task.json mid-run.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string TaskJsonText(string description, string file, string dependsOn) =>
        $$"""{ "description": "{{description}}", "writeScope": ["{{file}}"], "dependsOn": ["{{dependsOn}}"] }""";

    private static void WriteTaskThatOverwritesAnotherTasksDefinition(
        string taskDir, string ownFile, string targetTaskJsonPath, string newTargetTaskJson)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "edit", "writeScope": ["{{ownFile}}"] }""");

        string body = Ps
            ? """
              Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__") -Value 'x'
              Set-Content -NoNewline -Path "__TARGET__" -Value '__JSON__'
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              printf '%s' '__JSON__' > "__TARGET__"
              exit 0
              """;
        body = body.Replace("__FILE__", ownFile).Replace("__TARGET__", targetTaskJsonPath)
            .Replace("__JSON__", newTargetTaskJson);
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    private static void WriteTaskDependingOn(string taskDir, string file, string dependsOn)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "target", "writeScope": ["{{file}}"], "dependsOn": ["{{dependsOn}}"] }""");
        string body = Ps
            ? $"Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE \"{file}\") -Value 'x'\nexit 0\n"
            : $"#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/{file}\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), body);
        WriteAlwaysPassGate(Path.Combine(taskDir, "guardrails", Script("01-check")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fake `breakdown` prompt runner (RunOutcomeWiringTests' pattern), adapted so the JIT-authored task
    // actually writes a file into its workspace (the original fixture's authored task is a bare `exit 0`).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string WriteFakeBreakdownCliWritingFile(string dir, string waveDir, string workspaceFile)
    {
        if (Ps)
        {
            string cmdPath = Path.Combine(dir, "breakdown.cmd");
            string ps1Path = Path.Combine(dir, "breakdown.ps1");
            string psBody = """
                $null = [Console]::In.ReadToEnd()
                $planDir = (Get-Location).Path
                $taskDir = Join-Path $planDir '__WAVEDIR__\tasks\01-compile'
                $guardDir = Join-Path $taskDir 'guardrails'
                New-Item -ItemType Directory -Force -Path $guardDir | Out-Null
                Set-Content -NoNewline -Path (Join-Path $taskDir 'task.json') -Value '{ "description": "compile", "writeScope": ["__FILE__"], "dependsOn": [] }'
                Set-Content -Path (Join-Path $taskDir 'action.ps1') -Value 'Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__") -Value "x"', 'exit 0'
                Set-Content -Path (Join-Path $guardDir '01-check.ps1') -Value 'exit 0'
                Write-Output '{"type":"result","is_error":false,"result":"authored wave","num_turns":1}'
                """.Replace("__WAVEDIR__", waveDir).Replace("__FILE__", workspaceFile);
            File.WriteAllText(ps1Path, psBody);
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(dir, "breakdown.sh");
        string bashBody = """
            #!/usr/bin/env bash
            cat > /dev/null
            planDir="$(pwd)"
            taskDir="$planDir/__WAVEDIR__/tasks/01-compile"
            mkdir -p "$taskDir/guardrails"
            printf '%s' '{ "description": "compile", "writeScope": ["__FILE__"], "dependsOn": [] }' > "$taskDir/task.json"
            printf '%s\n' '#!/usr/bin/env bash' 'printf "x" > "$GUARDRAILS_WORKSPACE/__FILE__"' 'exit 0' > "$taskDir/action.sh"
            chmod +x "$taskDir/action.sh"
            printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$taskDir/guardrails/01-check.sh"
            chmod +x "$taskDir/guardrails/01-check.sh"
            printf '%s\n' '{"type":"result","is_error":false,"result":"authored wave","num_turns":1}'
            """.Replace("__WAVEDIR__", waveDir).Replace("__FILE__", workspaceFile);
        WriteExecutable(shPath, bashBody);
        return shPath;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Git hook fixture for the hook-rejection row (GitHookIsolationTests' pattern).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string ToShPath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// A pre-commit hook that checks for an untracked <c>tooling.ok</c> in the directory it runs in:
    /// present → append "passed" to <paramref name="logPath"/> and exit 0; absent → append "rejected" and
    /// exit 1. Logs the verdict only, never the directory (Git for Windows reports it in a different path
    /// format than the test holds).
    /// </summary>
    private static void InstallToolingCheckHook(string repoPath, string logPath)
    {
        string hooksDir = Path.Combine(repoPath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        string hookPath = Path.Combine(hooksDir, "pre-commit");
        string body = """
            #!/bin/sh
            if [ -f tooling.ok ]; then
              echo passed >> "__LOG__"
              exit 0
            else
              echo rejected >> "__LOG__"
              exit 1
            fi
            """.Replace("__LOG__", ToShPath(logPath));
        File.WriteAllText(hookPath, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeliveringWaveMergesAtItsOwnBarrier()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string logPath = Path.Combine(repo.Root, "barrier-check-log.txt");

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteBranchHasFileObserverGate(
            Path.Combine(w2, "preflights", Script("01-check-delivered")), logPath, repo.RepoPath, userBranch, "wave1.txt");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.True(File.Exists(logPath), "wave-02's entry preflight never ran.");
        Assert.Equal("present", File.ReadAllText(logPath).Trim());
    }

    [Fact]
    public async Task ANonDeliveringWaveRidesAlongToTheNextDeliveryPoint()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string logA = Path.Combine(repo.Root, "after-wave1.txt");
        string logB = Path.Combine(repo.Root, "after-wave2.txt");

        string w1 = Path.Combine(planDir, "wave-01-scaffold");
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-deliver");
        WriteBranchHasFileObserverGate(
            Path.Combine(w2, "preflights", Script("01-check-not-yet")), logA, repo.RepoPath, userBranch, "wave1.txt");
        WriteDeliversBrief(w2);
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        string w3 = Path.Combine(planDir, "wave-03-final");
        WriteBranchHasFileObserverGate(
            Path.Combine(w3, "preflights", Script("01-check-wave1")), logB, repo.RepoPath, userBranch, "wave1.txt");
        WriteBranchHasFileObserverGate(
            Path.Combine(w3, "preflights", Script("02-check-wave2")), logB, repo.RepoPath, userBranch, "wave2.txt");
        WriteFileWritingTask(Path.Combine(w3, "tasks", "01-write"), "wave3.txt");
        WriteFileExistsGate(Path.Combine(w3, "guardrails", Script("01-check")), "wave3.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));

        Assert.True(File.Exists(logA));
        Assert.Equal("absent", File.ReadAllText(logA).Trim());

        Assert.True(File.Exists(logB));
        string[] afterWave2 = File.ReadAllLines(logB).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, afterWave2.Length);
        Assert.All(afterWave2, line => Assert.Equal("present", line.Trim()));
    }

    [Fact]
    public async Task AWaveWhoseExitGateFails_DoesNotDeliver()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteAlwaysFailGate(Path.Combine(w1, "guardrails", Script("01-always-fail")));

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.False(report.AllSucceeded);
        Assert.Equal(initialHead, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());
    }

    [Fact]
    public async Task TheGateRunsAgainstTheMergedTree_NotThePlanBranchAlone()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: true, wave2AlwaysFails: true);

        await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        string userMidRunCommit = ReadUserMidRunCommit(scenario);
        string[][] lines = File.Exists(scenario.GateLogPath)
            ? File.ReadAllLines(scenario.GateLogPath).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToArray()
            : [];
        string[]? trueLine = lines.FirstOrDefault(p => p.Length == 2 && bool.Parse(p[1]));

        Assert.NotNull(trueLine);
        Assert.True(repo.IsAncestor(userMidRunCommit, trueLine![0]),
            $"expected the merged-tree gate run's HEAD ({trueLine[0]}) to have the user's mid-run commit ({userMidRunCommit}) as an ancestor.");

        Assert.False(repo.BranchHasFile(userBranch, "wave1.txt"),
            "the user's branch must not carry the delivering wave's commits when the merged-tree gate never ran or never passed.");
    }

    [Fact]
    public async Task AFailedExitGateAfterTheTrialMerge_LeavesTheUsersBranchUnmoved()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: true, wave2AlwaysFails: true);

        (RunReport report, _) = await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.False(report.AllSucceeded);

        string userMidRunCommit = ReadUserMidRunCommit(scenario);
        Assert.Equal(userMidRunCommit, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());
    }

    [Fact]
    public async Task AFailedTrialGate_LeavesThePlanBranchUnmoved()
    {
        using var repo = new TempGitRepo();
        MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: true, wave2AlwaysFails: true);
        string planBranch = $"guardrails/{Path.GetFileName(scenario.PlanDir)}";

        (RunReport report, _) = await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);
        Assert.False(report.AllSucceeded);

        string userMidRunCommit = ReadUserMidRunCommit(scenario);
        Assert.False(repo.IsAncestor(userMidRunCommit, planBranch));
    }

    [Fact]
    public async Task TheTrialRefIsDeleted_AfterEitherOutcome()
    {
        {
            using var repo = new TempGitRepo();
            MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: false, wave2AlwaysFails: false);
            (RunReport report, _) = await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);
            Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
            Assert.False(repo.RefExists($"refs/guardrails/trial/{scenario.WaveDir}"));
        }
        {
            using var repo = new TempGitRepo();
            MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: true, wave2AlwaysFails: true);
            (RunReport report, _) = await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);
            Assert.False(report.AllSucceeded);
            Assert.False(repo.RefExists($"refs/guardrails/trial/{scenario.WaveDir}"));
        }
    }

    [Fact]
    public async Task APlanMarkingNoWave_StillMergesOnceAtRunEnd()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());
        WriteFileWritingTask(Path.Combine(planDir, "tasks", "01-write"), "flat.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.True(repo.BranchHasFile(userBranch, "flat.txt"));
    }

    [Fact]
    public async Task ABarrierDelivery_WithMergeOnSuccessOff_NeverPromotes()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson(mergeOnSuccess: false));

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Equal(initialHead, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());
    }

    [Fact]
    public async Task TheOperatorOverride_LiftsABarrierSuppression()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        const string deliverWaveDir = "wave-02-deliver";
        string breakdownCli = WriteFakeBreakdownCliWritingFile(repo.RepoPath, deliverWaveDir, "wave2.txt");
        string breakdownCmd = breakdownCli.Replace("\\", "\\\\");

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "autonomyPolicy": "auto",
              "autonomy": { "gateThresholds": { "review-gate": "proceed-unreviewed" } },
              "promptRunners": {
                "default": "breakdown",
                "breakdown": {
                  "command": "{{breakdownCmd}}",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Write", "Edit", "Bash"],
                  "maxTurns": 5
                }
              }
            }
            """);

        WriteFileWritingTask(Path.Combine(planDir, "wave-01-scaffold", "tasks", "01-config"), "wave1.txt");
        WriteFileExistsGate(
            Path.Combine(planDir, "wave-01-scaffold", "guardrails", Script("01-check")), "wave1.txt");

        // wave-02-deliver: a JIT stub (tasks/ starts empty; the fake breakdown authors it at the between-wave
        // checkpoint) whose brief.md AND guardrails/ exist from the start, so IsDeliveryPoint is already true
        // at plan load — only the task LIST is JIT-authored.
        string w2 = Path.Combine(planDir, deliverWaveDir);
        Directory.CreateDirectory(Path.Combine(w2, "tasks"));
        File.WriteAllText(Path.Combine(w2, "brief.md"),
            "---\ndelivers: true\n---\n# wave-02-deliver\n\nBuild the artifact.\n");
        WriteAlwaysPassGate(Path.Combine(w2, "guardrails", Script("01-check")));

        string w3 = Path.Combine(planDir, "wave-03-doomed");
        WriteFileWritingTask(Path.Combine(w3, "tasks", "01-write"), "wave3.txt");
        WriteAlwaysFailGate(Path.Combine(w3, "guardrails", Script("01-always-fail")));

        await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--merge-on-success");

        Assert.True(repo.BranchHasFile(userBranch, "wave2.txt"),
            "the operator override must lift the barrier suppression and deliver the delivering wave's commits.");
    }

    [Fact]
    public async Task ADeliversWaveWithNoExitGate_DoesNotDeliverAtItsBarrier()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string w1 = Path.Combine(planDir, "wave-01-deliver");
        WriteDeliversBrief(w1);
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        // Deliberately no guardrails/ folder for wave-01 -- IsDeliveryPoint is false despite Delivers=true.

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteAlwaysFailGate(Path.Combine(w2, "guardrails", Script("01-always-fail")));

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.False(report.AllSucceeded);
        Assert.Equal(initialHead, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());
    }

    [Fact]
    public async Task ADivergedTaskDefinition_BlocksTheBarrierDelivery()
    {
        using var repo = new TempGitRepo();
        string originalBranch = repo.CurrentBranch();
        string initialHead = repo.HeadSha();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string w1 = Path.Combine(planDir, "wave-01-divergent");
        WriteDeliversBrief(w1);
        string targetTaskJson = Path.Combine(w1, "tasks", "02-target", "task.json");
        WriteTaskThatOverwritesAnotherTasksDefinition(
            Path.Combine(w1, "tasks", "01-edit"), "first.txt", targetTaskJson,
            TaskJsonText("edited mid-run", "second.txt", "01-edit"));
        WriteTaskDependingOn(Path.Combine(w1, "tasks", "02-target"), "second.txt", "01-edit");
        WriteAlwaysPassGate(Path.Combine(w1, "guardrails", Script("01-check")));

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteAlwaysFailGate(Path.Combine(w2, "guardrails", Script("01-always-fail")));

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.False(report.AllSucceeded);

        JournalDocument doc = JournalOf(planDir);
        IReadOnlyList<DecisionEntry> decisions = doc.Decisions ?? [];
        Assert.Contains(decisions, d =>
            d.Boundary == "definition-divergence" && d.Subject.Contains("02-target", StringComparison.Ordinal));

        Assert.Equal(initialHead, TempGitRepo.Git(repo.RepoPath, "rev-parse", originalBranch).Trim());
    }

    [Fact]
    public async Task TheFinalWave_DeliversAtRunEnd_NotAtItsBarrier()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string logPath = Path.Combine(repo.Root, "terminal-gate-log.txt");

        string w1 = Path.Combine(planDir, "wave-01-scaffold");
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteDeliversBrief(w2);
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "src/final.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "src/final.txt");

        WriteBranchHasFileObserverGate(
            Path.Combine(planDir, "guardrails", Script("01-terminal")), logPath, repo.RepoPath, userBranch, "src/final.txt");

        (int exit, string output) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        Assert.Equal(0, exit);
        Assert.True(File.Exists(logPath), "the plan-level terminal gate never ran.\n" + output);
        Assert.Equal("absent", File.ReadAllText(logPath).Trim());
        Assert.True(repo.BranchHasFile(userBranch, "src/final.txt"));
    }

    [Fact]
    public async Task TheFinalWave_WithNoPlanLevelGate_StillDeliversAtRunEnd()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string gateLog = Path.Combine(repo.Root, "final-wave-gate-log.txt");
        string shaFile = Path.Combine(repo.Root, "final-wave-user-commit.txt");

        string w1 = Path.Combine(planDir, "wave-01-scaffold");
        WriteFileWritingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt");
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        // No plan-level guardrails/ folder here -- the answer must be unconditional.
        string w2 = Path.Combine(planDir, "wave-02-final");
        WriteDeliversBrief(w2);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w2, "tasks", "01-write"), "src/final.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile);
        WriteTeammateSensitiveGate(Path.Combine(w2, "guardrails", Script("01-teammate-check")), gateLog);

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Null(report.WaveHalt);
        Assert.True(repo.BranchHasFile(userBranch, "src/final.txt"));
    }

    [Fact]
    public async Task AFailedTrialTreeGate_HaltsAsAnExitGateFailure_NamingTheTrialMerge()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        MergedTreeScenario scenario = CreateMergedTreeScenarioPlan(repo, wave1GateFailsOnTeammate: true, wave2AlwaysFails: true);

        (RunReport report, _) = await RunAsync(scenario.PlanDir, repo.RepoPath, repo.WorktreeRoot);

        string userMidRunCommit = ReadUserMidRunCommit(scenario);
        string sha10 = userMidRunCommit[..10];
        string planBranch = $"guardrails/{Path.GetFileName(scenario.PlanDir)}";

        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.ExitGateFailed, report.WaveHalt!.Kind);
        Assert.Equal(scenario.WaveDir, report.WaveHalt.WaveDir);

        string headline = report.WaveHalt.Headline;
        Assert.Contains(sha10, headline, StringComparison.Ordinal);

        Match range = Regex.Match(headline, "([0-9a-f]{10})\\.\\.([0-9a-f]{10})");
        Assert.True(range.Success, $"expected a <sha10>..<sha10> range in the halt headline: {headline}");
        Assert.Equal(sha10, range.Groups[2].Value);

        string leftResolved = TempGitRepo.Git(repo.RepoPath, "rev-parse", range.Groups[1].Value).Trim();
        Assert.True(repo.IsAncestor(leftResolved, planBranch),
            "the range's left side must resolve to a commit on the plan branch.");
        Assert.False(repo.IsAncestor(userMidRunCommit, leftResolved),
            "the range's left side must not already contain the user's mid-run commit.");

        JournalDocument doc = JournalOf(scenario.PlanDir);
        Assert.NotNull(doc.Halt);
        Assert.Equal(RunHaltKind.WaveExitGateFailed, doc.Halt!.Kind);
        Assert.Equal(report.WaveHalt.Headline, doc.Halt.Headline);
        Assert.Contains(sha10, doc.Halt.Headline, StringComparison.Ordinal);
        Assert.Contains($"{range.Groups[1].Value}..{range.Groups[2].Value}", doc.Halt.Headline, StringComparison.Ordinal);

        Assert.Equal(userMidRunCommit, TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim());

        string log = TempGitRepo.Git(repo.RepoPath, "log", "--first-parent", "--format=%B", planBranch);
        Assert.DoesNotContain($"Guardrails-Wave: {scenario.WaveDir}", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHookRejectedTrial_HoldsEveryLaterBarrierDelivery()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();

        string hookLog = Path.Combine(repo.Root, "hook-verdict-log.txt");
        string workLog = Path.Combine(repo.Root, "work-visibility-log.txt");

        // Only the user's own checkout has tooling.ok -- any harness worktree (a trial merge worktree) lacks it.
        File.WriteAllText(Path.Combine(repo.RepoPath, "tooling.ok"), "ok\n");
        File.AppendAllText(Path.Combine(repo.RepoPath, ".git", "info", "exclude"), "tooling.ok\n");
        InstallToolingCheckHook(repo.RepoPath, hookLog);

        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), StandardGuardrailsJson());

        string shaFile = Path.Combine(repo.Root, "hook-test-user-commit.txt");

        string w1 = Path.Combine(planDir, "wave-01-first");
        WriteDeliversBrief(w1);
        WriteTaskWritingFileAndUserCommit(
            Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, "teammate.txt",
            "teammate's mid-run commit", shaFile, noVerify: true);
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-check")), "wave1.txt");

        string w2 = Path.Combine(planDir, "wave-02-second");
        WriteDeliversBrief(w2);
        WriteFileWritingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt");
        WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-check")), "wave2.txt");

        string w3 = Path.Combine(planDir, "wave-03-final");
        WriteBranchHasFileObserverGate(
            Path.Combine(w3, "preflights", Script("01-check-wave1")), workLog, repo.RepoPath, userBranch, "wave1.txt");
        WriteBranchHasFileObserverGate(
            Path.Combine(w3, "preflights", Script("02-check-wave2")), workLog, repo.RepoPath, userBranch, "wave2.txt");
        WriteFileWritingTask(Path.Combine(w3, "tasks", "01-write"), "wave3.txt");
        WriteFileExistsGate(Path.Combine(w3, "guardrails", Script("01-check")), "wave3.txt");

        (RunReport report, _) = await RunAsync(planDir, repo.RepoPath, repo.WorktreeRoot);

        Assert.True(report.AllSucceeded, string.Join("; ", report.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Null(report.WaveHalt);

        string[] hookLines = File.Exists(hookLog)
            ? File.ReadAllLines(hookLog).Where(l => l.Length > 0).Select(l => l.Trim()).ToArray()
            : [];
        Assert.Equal(1, hookLines.Count(l => l == "rejected"));
        Assert.True(hookLines.Count(l => l == "passed") <= 1,
            "the run-end merge in the user's own checkout should be the only place the hook can pass.");

        string[] workLines = File.Exists(workLog)
            ? File.ReadAllLines(workLog).Where(l => l.Length > 0).Select(l => l.Trim()).ToArray()
            : [];
        Assert.Equal(2, workLines.Length);
        Assert.All(workLines, line => Assert.Equal("absent", line));

        string userMidRunCommit = File.ReadAllText(shaFile).Trim();
        Assert.True(repo.BranchHasFile(userBranch, "wave1.txt"));
        Assert.True(repo.BranchHasFile(userBranch, "wave2.txt"));
        Assert.True(repo.BranchHasFile(userBranch, "wave3.txt"));

        string finalTip = TempGitRepo.Git(repo.RepoPath, "rev-parse", userBranch).Trim();
        string mergeFirstParent = TempGitRepo.Git(repo.RepoPath, "rev-parse", $"{finalTip}^1").Trim();
        Assert.Equal(userMidRunCommit, mergeFirstParent);
    }
}
