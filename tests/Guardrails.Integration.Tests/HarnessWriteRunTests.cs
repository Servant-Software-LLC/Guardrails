using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end runs of the <c>needsHarnessWrite</c> escape hatch (issue #191, SSOT §9) through the real
/// <see cref="TaskExecutor"/> attempt loop, using script actions (OS-picked .ps1/.sh) that write the
/// fragment key directly — no real Claude CLI needed, mirroring the <c>needsHuman</c>/staging-outputs
/// test conventions elsewhere in this project. Proves:
/// <list type="bullet">
///   <item>an in-scope request results in the harness process itself writing the file, and the task's
///     guardrails subsequently run and observe it (task goes green);</item>
///   <item>an out-of-scope request (outside the declared <c>writeScope</c>) is rejected with actionable
///     retry feedback naming the path, and the attempt fails — same shape as an existing
///     write-scope violation, eventual needs-human on budget exhaustion;</item>
///   <item>a path attempting to escape the workspace entirely is rejected regardless of writeScope.</item>
/// </list>
/// The #445 tests at the end prove the multi-file ARRAY form end-to-end: three <c>.claude/</c> files
/// corrected in ONE attempt (the case a singular request could never converge, because a guardrail
/// failure rolls the segment back and discards the previous attempt's write), cross-file atomicity, the
/// duplicate-path and empty-array rejections, and that the #321 carve-out denies a whole batch when any
/// single entry targets a settings file.
/// The #321 tests use a fake <see cref="IPromptRunner"/> instead — the ordering bug only
/// reproduces with a PROMPT action that reports <c>BlockedWritePaths</c> (a direct-write probe the
/// permission scanner captured); a script action never populates them. They prove the permission-wall
/// early halt now YIELDS to the escape hatch (#321), that an un-escaped <c>.claude/</c> wall still
/// halts, that a non-<c>.claude/</c> repeated wall still halts even with a hatch present (#86 intact),
/// and that a hatch to <c>.claude/settings.json</c> is denied with an actionable reason (carve-out).
/// </summary>
public sealed class HarnessWriteRunTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-hwrun-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            // Pin line endings so a segment worktree checks out exactly the bytes committed — the #437
            // anchored-edit tests assert on file content, and an autocrlf-driven CRLF rewrite on the way
            // into the worktree would make them machine-dependent. (The CRLF↔LF anchor tolerance itself
            // is unit-tested directly in HarnessWriteTests.)
            Git(RepoPath, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# needsHarnessWrite test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Commit <paramref name="content"/> at <paramref name="relativePath"/> so a segment worktree checks it out.</summary>
        public void Commit(string relativePath, string content)
        {
            string full = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Git(RepoPath, "add", "--", relativePath);
            Git(RepoPath, "commit", "-m", $"Seed {relativePath}");
        }

        /// <summary>The committed content of <paramref name="relativePath"/> on <paramref name="planBranch"/>.</summary>
        public string PlanBranchContent(string planBranch, string relativePath)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add($"{planBranch}:{relativePath}");
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0
                ? stdout
                : throw new InvalidOperationException($"git show {planBranch}:{relativePath} failed: {stderr.Trim()}");
        }

        public static void Git(string workingDir, params string[] args)
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
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        public bool PlanBranchHasPath(string planBranch, string path)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("cat-file");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"{planBranch}:{path}");
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<Core.Model.GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    /// <summary>
    /// A single-task plan whose SCRIPT action writes a <c>needsHarnessWrite</c> fragment requesting
    /// <paramref name="requestedPath"/> with fixed content; the guardrail checks that
    /// <paramref name="guardrailChecksPath"/> exists in the workspace (defaults to the requested path,
    /// so a successful write is what makes the guardrail pass).
    /// </summary>
    private static string WriteHarnessWritePlan(
        string repoPath,
        string requestedPath,
        string? writeScope,
        string? guardrailChecksPath = null)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks", "01-write", "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-write");
        string writeScopeJson = writeScope is null ? "" : $", \"writeScope\": [{writeScope}]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "request a harness write",
              "dependsOn": []{{writeScopeJson}}
            }
            """);

        string checkedPath = guardrailChecksPath ?? requestedPath;

        // The fragment JSON embeds the requested path/content — write it via the STATE_OUT env var
        // directly (a script action IS allowed to write needsHarnessWrite, same fragment file
        // needsHuman uses).
        if (Ps)
        {
            string ps = "$json = '{ \"needsHarnessWrite\": { \"path\": \"" + requestedPath.Replace("\\", "\\\\") +
                        "\", \"content\": \"WRITTEN-BY-HARNESS\", \"reason\": \"runtime blocks this path\" } }'\n" +
                        "Set-Content -Path $env:GUARDRAILS_STATE_OUT -Value $json -NoNewline\n" +
                        "exit 0\n";
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), ps);

            string checkedPs = checkedPath.Replace("/", "\\");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-exists.ps1"),
                "if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '" + checkedPs + "')) { exit 0 } else { Write-Output 'missing'; exit 1 }\n");
        }
        else
        {
            string sh = "#!/usr/bin/env bash\n" +
                        "printf '%s' '{ \"needsHarnessWrite\": { \"path\": \"" + requestedPath +
                        "\", \"content\": \"WRITTEN-BY-HARNESS\", \"reason\": \"runtime blocks this path\" } }' > \"$GUARDRAILS_STATE_OUT\"\n" +
                        "exit 0\n";
            WriteSh(Path.Combine(taskDir, "action.sh"), sh);

            WriteSh(Path.Combine(taskDir, "guardrails", "01-exists.sh"),
                "#!/usr/bin/env bash\n" +
                $"if [ -f \"$GUARDRAILS_WORKSPACE/{checkedPath}\" ]; then exit 0; else echo missing; exit 1; fi\n");
        }

        return planDir;
    }

    private static void WriteSh(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static async Task<(RunReport report, string planBranch)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = Core.Prompts.PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("no prompt runners"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(load.Plan!, executor, journal,
            worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier());

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, "guardrails/plan");
    }

    [Fact]
    public async Task Worktree_InScopeRequest_HarnessWritesFile_GuardrailsSeeIt_TaskSucceeds()
    {
        using var repo = new TempGitRepo();
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: ".claude/skills/foo/SKILL.md", writeScope: "\".claude/**\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        // The committed plan-branch carries the harness-written file — proof it reached the real
        // segment workspace (not just some scratch location) and survived integration.
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/foo/SKILL.md"),
            "the harness-written .claude/ file must be committed on the plan branch");
    }

    [Fact]
    public async Task Worktree_OutOfScopeRequest_Rejected_ActionableFeedback_EventualNeedsHuman()
    {
        using var repo = new TempGitRepo();
        // writeScope authorizes ONLY .claude/** — the requested path is deliberately outside it.
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: "src/Sneaky.cs", writeScope: "\".claude/**\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        // defaultRetries: 0 -> budget of 1 -> the single rejected attempt exhausts the budget. The
        // reported per-attempt TaskOutcome keeps the GuardrailFailed shape (same as any other
        // exhausted write-scope violation); the JOURNAL settles needs-human (asserted below) — the
        // task is non-green either way, so dependents correctly block.
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite", task.Summary);
        Assert.False(repo.PlanBranchHasPath(planBranch, "src/Sneaky.cs"),
            "a rejected needsHarnessWrite must never reach a commit");

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The retry feedback names the offending path (actionable, same tone as a normal write-scope
        // violation).
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));
        AttemptRecord attempt = Assert.Single(doc.Tasks["01-write"].Attempts);
        string feedbackPath = Path.Combine(planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md");
        Assert.True(File.Exists(feedbackPath));
        string feedback = File.ReadAllText(feedbackPath);
        Assert.Contains("src/Sneaky.cs", feedback);
    }

    [Fact]
    public async Task Worktree_WorkspaceEscapingRequest_Rejected_RegardlessOfWriteScope()
    {
        using var repo = new TempGitRepo();
        // An extremely permissive writeScope ("**") must still not let a workspace-escaping path
        // through — the escape check is independent of writeScope (issue #191).
        string escaping = OperatingSystem.IsWindows() ? "..\\..\\outside.txt" : "../../outside.txt";
        string planDir = WriteHarnessWritePlan(repo.RepoPath, requestedPath: escaping, writeScope: "\"**\"");

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // Nothing landed outside the repo tree.
        string outsidePath = Path.Combine(Path.GetDirectoryName(repo.RepoPath)!, "outside.txt");
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task Worktree_HarnessWriteDestinationInScope_Succeeds()
    {
        using var repo = new TempGitRepo();
        // #389: every task declares a writeScope. A needsHarnessWrite destination must be IN that scope —
        // HarnessWrite.Validate allows it (in-scope) AND the post-action phase-1 write-scope check (which
        // now runs for every real segment, fail-closed on absent) sees the harness-written file in-scope.
        string planDir = WriteHarnessWritePlan(
            repo.RepoPath, requestedPath: ".claude/skills/bar/SKILL.md", writeScope: "\".claude/skills/bar/SKILL.md\"");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.True(repo.PlanBranchHasPath(planBranch, ".claude/skills/bar/SKILL.md"));
    }

    // ── #321: prompt-action probe-then-hatch ordering (fake IPromptRunner) ───────────────────────

    /// <summary>
    /// A fake prompt runner that reproduces the #321 probe-then-hatch flow with no real Claude CLI: it
    /// writes a caller-chosen fragment to whatever <c>GUARDRAILS_STATE_OUT</c> it is handed (a
    /// <c>needsHarnessWrite</c> request, or <c>{}</c> = no hatch) AND reports a caller-chosen set of
    /// <see cref="PromptResult.BlockedWritePaths"/> — the permission-wall paths a real agent's refused
    /// direct-write PROBE would have populated. <c>Completed</c> + <c>!IsError</c> (a runtime refusal
    /// under <c>acceptEdits</c> does not make the agent report <c>is_error</c> — the #86/#104/#321
    /// condition). Only prompt actions trigger the ordering bug; a script action never populates
    /// <c>BlockedWritePaths</c>, which is why the tests above cannot catch it.
    /// </summary>
    private sealed class ProbeThenHatchRunner : IPromptRunner
    {
        private readonly string? _harnessWritePath;
        private readonly string _content;
        private readonly IReadOnlyList<string> _blockedWritePaths;
        private readonly string? _deliverableToWrite;
        private readonly bool _actionSucceeds;

        public ProbeThenHatchRunner(
            string? harnessWritePath,
            IReadOnlyList<string> blockedWritePaths,
            string content = "WRITTEN-BY-HARNESS",
            string? deliverableToWrite = null,
            bool actionSucceeds = true)
        {
            _harnessWritePath = harnessWritePath;
            _blockedWritePaths = blockedWritePaths;
            _content = content;
            _deliverableToWrite = deliverableToWrite;
            _actionSucceeds = actionSucceeds;
        }

        public int Invocations { get; private set; }
        public string Name => "fake";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken ct)
        {
            Invocations++;

            // #325: model the agent RECOVERING from a .claude/ Bash-classifier refusal in the SAME
            // attempt — it wrote the deliverable to an in-scope workspace path (via the Read tool /
            // staging), so the attempt CONVERGES even though a .claude/ path was reported blocked. The
            // write lands in the segment worktree (cwd == the effective workspace) so the write-scope
            // check and the guardrail both observe it.
            if (_deliverableToWrite is not null)
            {
                string dest = Path.Combine(
                    invocation.WorkingDirectory, _deliverableToWrite.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, _content);
            }

            string stateOut = invocation.Environment["GUARDRAILS_STATE_OUT"];
            string json = _harnessWritePath is null
                ? "{}"
                : "{ \"needsHarnessWrite\": { \"path\": \"" + _harnessWritePath +
                  "\", \"content\": \"" + _content + "\", \"reason\": \"the direct .claude/ write was refused\" } }";
            File.WriteAllText(stateOut, json);
            // #329: actionSucceeds=false models the ACTION itself failing (the agent could not complete)
            // while a .claude/ wall was reported — NO guardrail then runs, the pure #104 permission-wall.
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = !_actionSucceeds,
                FailureKind = _actionSucceeds ? PromptFailureKind.None : PromptFailureKind.Error,
                Summary = "fake: probed then optionally requested a harness write",
                BlockedWritePaths = _blockedWritePaths
            });
        }
    }

    /// <summary>
    /// A single-task plan whose action is a PROMPT (routed to the fake runner) and whose guardrail is a
    /// deterministic script asserting <paramref name="guardrailChecksPath"/> exists in the workspace.
    /// </summary>
    private static string WriteHarnessWritePromptPlan(
        string repoPath, string? writeScope, string guardrailChecksPath, int defaultRetries = 0,
        string? requiredToken = null, bool guardrailTimesOut = false)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks", "01-write", "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": 1,
              "promptRunners": {
                "default": "fake",
                "fake": { "command": "fake-claude", "maxTurns": 3 }
              }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-write");
        string writeScopeJson = writeScope is null ? "" : $", \"writeScope\": [{writeScope}]";
        // #339 N1: a whole-attempt timeout so the sleeping 02-timeout guardrail below is KILLED
        // (guardrails.TimedOut) rather than run to completion. The fast fake action and the trivial
        // 01-exists check must finish well under it; only the deliberate sleeper trips it.
        //
        // WAS 3s, AND THAT MADE THIS TEST LOAD-SENSITIVE — do not tighten it back. The budget is
        // WHOLE-ATTEMPT: it covers the fake action AND 01-exists AND the sleeper, not the sleeper
        // alone. Under full-suite load on Windows, spawning a shell for the trivial 01-exists check
        // can itself consume 3s, so the budget was exhausted BEFORE 02-timeout ever started; 01-exists
        // was killed and the summary named IT instead of the sleeper. Observed as a plan-preflight
        // halt: "Failed: 1, Passed: 954" with `Not found: "02-timeout"` against a summary reading
        // "guardrail(s) failed: 01-exists - needs human", while the same test passed 3/3 in isolation
        // on the same commit.
        //
        // 20s is chosen to be loose at BOTH ends and asserts nothing about timing itself: ~6x headroom
        // over the observed 7s worst case for the work that must COMPLETE, and still 3x under the
        // sleeper's 60s so the kill remains certain. Every assertion is unchanged - the attempt outcome
        // is still Timeout, failedGuardrails is still exactly [02-timeout] - so this widens the room
        // the NON-target work runs in without loosening what the test proves.
        string timeoutJson = guardrailTimesOut ? ", \"timeoutSeconds\": 20" : "";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "probe .claude/ then request a harness write",
              "dependsOn": []{{writeScopeJson}}{{timeoutJson}}
            }
            """);

        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"),
            "Author the deliverable under .claude/. Emit needsHarnessWrite for it.\n");

        if (Ps)
        {
            string checkedPs = guardrailChecksPath.Replace("/", "\\");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-exists.ps1"),
                "if (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE '" + checkedPs + "')) { exit 0 } else { Write-Output 'missing'; exit 1 }\n");
        }
        else
        {
            WriteSh(Path.Combine(taskDir, "guardrails", "01-exists.sh"),
                "#!/usr/bin/env bash\n" +
                $"if [ -f \"$GUARDRAILS_WORKSPACE/{guardrailChecksPath}\" ]; then exit 0; else echo missing; exit 1; fi\n");
        }

        // #329: an OPTIONAL second, CONTENT guardrail (ordinal 02, runs after 01-exists passes) — models
        // the real #329 case where the .claude/ deliverable LANDED (the agent recovered from the wall) but
        // a required fence/heading was dropped, so a guardrail UNRELATED to the wall genuinely fails.
        if (requiredToken is not null)
        {
            if (Ps)
            {
                string checkedPs = guardrailChecksPath.Replace("/", "\\");
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-content.ps1"),
                    "$p = Join-Path $env:GUARDRAILS_WORKSPACE '" + checkedPs + "'\n" +
                    "if ((Test-Path $p) -and ((Get-Content $p -Raw) -match '" + requiredToken + "')) { exit 0 } " +
                    "else { Write-Output 'missing required content: " + requiredToken + "'; exit 1 }\n");
            }
            else
            {
                WriteSh(Path.Combine(taskDir, "guardrails", "02-content.sh"),
                    "#!/usr/bin/env bash\n" +
                    $"if grep -q '{requiredToken}' \"$GUARDRAILS_WORKSPACE/{guardrailChecksPath}\" 2>/dev/null; " +
                    $"then exit 0; else echo 'missing required content: {requiredToken}'; exit 1; fi\n");
            }
        }

        // #339 N1: an OPTIONAL second guardrail (ordinal 02, runs after 01-exists passes) that SLEEPS past
        // the short task timeout so it is KILLED — models a guardrail that TIMES OUT while a recovered
        // .claude/ wall coincided in the same attempt. It exits 0 if it ever finished, so the ONLY way it
        // fails is the timeout kill (guardrails.TimedOut).
        if (guardrailTimesOut)
        {
            if (Ps)
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "02-timeout.ps1"),
                    "Start-Sleep -Seconds 60\nexit 0\n");
            }
            else
            {
                WriteSh(Path.Combine(taskDir, "guardrails", "02-timeout.sh"),
                    "#!/usr/bin/env bash\nsleep 60\nexit 0\n");
            }
        }

        return planDir;
    }

    private static async Task<(RunReport report, string planBranch)> RunWorktreePromptAsync(
        string planDir, TempGitRepo repo, IPromptRunner runner, CancellationToken ct)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config, _ => runner);
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);

        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(load.Plan!, executor, journal,
            worktreeProvider: provider, reVerifier: new AlwaysPassReVerifier());

        RunReport report = await scheduler.RunAsync(load.Plan!, ct);
        return (report, "guardrails/plan");
    }

    [Fact]
    public async Task Worktree_PromptProbesClaudeThenHatches_HarnessWritesFile_TaskGoesGreen()
    {
        // #321 RED-BAR regression: a PROMPT action probes a direct .claude/ write (captured into
        // BlockedWritePaths), then emits needsHarnessWrite for that same path. Before the fix the
        // permission-wall early halt fired on the structural .claude/ path BEFORE the needsHarnessWrite
        // handler ran, pre-empting the escape-hatch write and dead-ending the task at needs-human. After
        // the fix the halt YIELDS to the hatch (drops .claude/ walls when a needsHarnessWrite is
        // present) and the task completes green.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/foo.md";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable);

        var runner = new ProbeThenHatchRunner(harnessWritePath: deliverable, blockedWritePaths: [deliverable]);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.True(repo.PlanBranchHasPath(planBranch, deliverable),
            "the harness-written .claude/ file must be committed on the plan branch");
    }

    // ── #325: outcome-aware structural .claude/ halt ─────────────────────────────────────────────

    [Fact]
    public async Task Worktree_PromptBashReadsClaude_RecoversStagingOnly_NoHatch_TaskGoesGreen()
    {
        // #325 RED-BAR: a task extending an EXISTING .claude/ file runs `cp ".claude/…" <staging>` — the
        // .claude/ path only as a READ SOURCE. Claude Code's Bash classifier phrases ANY .claude/
        // reference as a WRITE and refuses it (captured into BlockedWritePaths), but the agent RECOVERS
        // in the SAME attempt (Read tool → the deliverable lands in-scope) and there is NO
        // needsHarnessWrite hatch. The deliverable is present, so the guardrail PASSES and the attempt
        // CONVERGES. Before the fix the early structural halt fired the instant the .claude/ wall was
        // observed — BEFORE the outcome was known — and dead-ended the converged task at needs-human.
        // After the fix the structural halt is consulted only on a NON-converged attempt, so a converged
        // one goes GREEN. Must FAIL today (early halt), pass after.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/x.md";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable);

        // No hatch (harnessWritePath: null); a .claude/ path reported blocked; the deliverable is written
        // to that same in-scope .claude/ path (what the staging move would have produced).
        var runner = new ProbeThenHatchRunner(
            harnessWritePath: null, blockedWritePaths: [deliverable], deliverableToWrite: deliverable);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
        Assert.True(repo.PlanBranchHasPath(planBranch, deliverable),
            "the converged .claude/ deliverable must be committed on the plan branch");
    }

    [Fact]
    public async Task Worktree_UnrecoverableClaudeDeliverable_HaltsNeedsHuman_ReportsGuardrailFailed()
    {
        // #325 must NOT weaken the #104 fast-halt: a .claude/ wall with NO hatch whose deliverable never
        // lands (the runner writes NOTHING) makes the guardrail FAIL, so the attempt does NOT converge —
        // the structural .claude/ halt fires at the guardrail-failed site, settling needs-human. Crucially
        // it halts on the SINGLE recorded attempt even though the budget is larger (defaultRetries: 2 →
        // budget 3): a further retry cannot clear a .claude/ wall, so the remaining budget is never burned
        // (the #104 fast-halt survives).
        //
        // #329: the REPORTED outcome must now name the TRUE primary cause — a guardrail genuinely ran and
        // FAILED — not `permission-denied` with an empty failedGuardrails[]. So the attempt outcome is
        // `guardrail-failed`, failedGuardrails[] names the failing guardrail (01-exists), and the summary
        // LEADS with the guardrail failure while still disclosing the .claude/ wall as secondary context.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/x.md";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable, defaultRetries: 2);

        var runner = new ProbeThenHatchRunner(
            harnessWritePath: null, blockedWritePaths: [deliverable], deliverableToWrite: null);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.False(task.IsGreen);
        // Summary leads with the guardrail failure and still mentions the .claude/ wall (secondary).
        Assert.Contains("guardrail(s) failed", task.Summary);
        Assert.Contains("01-exists", task.Summary);
        Assert.Contains(deliverable, task.Summary);
        Assert.DoesNotContain("structural", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);
        Assert.False(repo.PlanBranchHasPath(planBranch, deliverable));

        // #329: the single recorded attempt reports `guardrail-failed` (NOT `permission-denied`) with a
        // NON-EMPTY failedGuardrails[] naming the guardrail that ran and failed.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);
        FailedGuardrail failed = Assert.Single(attempt.FailedGuardrails);
        Assert.Equal("01-exists", failed.Name);

        // Fast-halt proof: exactly ONE attempt and the runner ran ONCE, even though the budget allowed
        // three — the guardrail-failed structural halt did not burn the retries (#104 preserved).
        Assert.Equal(1, runner.Invocations);
    }

    [Fact]
    public async Task Worktree_RecoversClaudeWall_UnrelatedGuardrailFails_ReportsGuardrailFailed()
    {
        // #329 RED-BAR (faithful to the reported case, task 03b): a task extending an EXISTING .claude/
        // file hits the Bash-classifier .claude/ wall on a READ-source `cp`, RECOVERS in the same attempt
        // (writes the deliverable via the Read tool → 01-exists PASSES), but the transcribed content DROPS
        // a required fence, so a DIFFERENT, unrelated CONTENT guardrail (02-content) genuinely FAILS. The
        // wall is incidental — the real bug is the guardrail failure. Before #329 the harness reported
        // `permission-denied` + failedGuardrails: [] (hiding that 02-content ran and failed, misdirecting
        // triage into "the #325 fix didn't ship"). After #329 the attempt reports `guardrail-failed`,
        // failedGuardrails[] names 02-content, and the summary leads with it. Must FAIL today (would report
        // permission-denied / empty failedGuardrails), pass after.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/traverse-repo.md";
        const string requiredToken = "REQUIRED-FENCE";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable, defaultRetries: 2,
            requiredToken: requiredToken);

        // The deliverable LANDS (recovers from the wall) but its content LACKS the required fence, so
        // 01-exists passes and 02-content fails. The .claude/ path is reported blocked (the read-source
        // detour). content default "WRITTEN-BY-HARNESS" does not contain REQUIRED-FENCE.
        var runner = new ProbeThenHatchRunner(
            harnessWritePath: null, blockedWritePaths: [deliverable], deliverableToWrite: deliverable);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("guardrail(s) failed", task.Summary);
        Assert.Contains("02-content", task.Summary);
        Assert.Contains(deliverable, task.Summary);            // the .claude/ wall path, secondary context
        Assert.DoesNotContain("permission", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        Assert.Equal(AttemptOutcome.GuardrailFailed, attempt.Outcome);       // NOT PermissionDenied
        FailedGuardrail failed = Assert.Single(attempt.FailedGuardrails);
        Assert.Equal("02-content", failed.Name);                             // the UNRELATED guardrail
        Assert.Contains(requiredToken, failed.Reason);

        // Fast-halt preserved: one attempt, runner ran once (the .claude/ wall is unclearable).
        Assert.Equal(1, runner.Invocations);
    }

    [Fact]
    public async Task Worktree_RecoversClaudeWall_GuardrailTimesOut_ReportsTimeout_NotGuardrailFailed()
    {
        // #339 N1: a guardrail that TIMES OUT while a .claude/ wall was hit + RECOVERED in the SAME attempt
        // must record the attempt outcome `timeout`, not `guardrail-failed`. The #329 structural-wall halt
        // site previously HARD-CODED GuardrailFailed, dropping the `guardrails.TimedOut ? Timeout :
        // GuardrailFailed` distinction its canonical guardrail-failed sibling keeps. The halt DECISION is
        // unchanged — needs-human on ONE attempt (fast-halt), the .claude/ wall disclosed as secondary
        // context — only the RECORDED outcome differs (guardrail-failed → timeout). Must FAIL before the
        // N1 fix (would report guardrail-failed), pass after.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/traverse-repo.md";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable, defaultRetries: 2,
            guardrailTimesOut: true);

        // The deliverable LANDS (recovers from the wall → 01-exists passes); the .claude/ path is reported
        // blocked (the read-source detour); then 02-timeout sleeps past the 3s task timeout and is killed.
        var runner = new ProbeThenHatchRunner(
            harnessWritePath: null, blockedWritePaths: [deliverable], deliverableToWrite: deliverable);

        var (report, _) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("guardrail(s) failed", task.Summary);
        Assert.Contains("02-timeout", task.Summary);
        Assert.Contains(deliverable, task.Summary);            // the .claude/ wall path, secondary context

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // #339 N1: the recorded attempt outcome is `timeout` (NOT guardrail-failed) — a timed-out guardrail
        // keeps its timeout classification even at the structural-wall halt site.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        Assert.Equal(AttemptOutcome.Timeout, attempt.Outcome);
        FailedGuardrail failed = Assert.Single(attempt.FailedGuardrails);
        Assert.Equal("02-timeout", failed.Name);

        // Fast-halt preserved: one attempt, runner ran once even with budget 3 (a wall no retry clears).
        Assert.Equal(1, runner.Invocations);
    }

    [Fact]
    public async Task Worktree_ActionFailsWithClaudeWall_NoGuardrailRuns_StillReportsPermissionDenied()
    {
        // #329 companion (proves NO over-correction): the PURE permission-wall case must STAY
        // `permission-denied`. Here the ACTION itself FAILS (the agent could not complete) while a .claude/
        // wall was reported — so NO guardrail runs (guardrails are skipped on action failure). There is no
        // guardrail failure being hidden, and the .claude/ wall IS the honest primary cause (the classic
        // #104 first-attempt wall). The attempt must therefore report `permission-denied` with an EMPTY
        // failedGuardrails[], exactly as #326 did — #329 changed only the guardrail-failed site.
        using var repo = new TempGitRepo();
        const string deliverable = ".claude/commands/foo.md";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: deliverable, defaultRetries: 2);

        var runner = new ProbeThenHatchRunner(
            harnessWritePath: null, blockedWritePaths: [deliverable], deliverableToWrite: null,
            actionSucceeds: false);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains(deliverable, task.Summary);
        Assert.Contains("structural", task.Summary);          // permission-wall wording, unchanged

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);
        Assert.False(repo.PlanBranchHasPath(planBranch, deliverable));

        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        Assert.Equal(AttemptOutcome.PermissionDenied, attempt.Outcome);     // unchanged: pure wall
        Assert.Empty(attempt.FailedGuardrails);                             // no guardrail ran

        // Fast-halt preserved: one attempt, runner ran once even with budget 3.
        Assert.Equal(1, runner.Invocations);
    }

    [Fact]
    public async Task Worktree_NonClaudeRepeatedWall_WithHatchPresent_StillHalts_Issue86Intact()
    {
        // #321 drops ONLY .claude/ structural walls from the tracker when a needsHarnessWrite is
        // present — every NON-.claude/ path is still observed, so the #86 repeated-path protection is
        // intact even with a hatch present. The hatch is present every attempt (an OUT-OF-SCOPE target,
        // so the attempt fails past the wall check); a non-.claude/ wall (src/Sneaky.cs) repeats and
        // must halt on the SECOND attempt.
        using var repo = new TempGitRepo();
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: ".claude/commands/foo.md",
            defaultRetries: 2);

        var runner = new ProbeThenHatchRunner(
            harnessWritePath: "src/OutOfScope.cs", blockedWritePaths: ["src/Sneaky.cs"]);

        var (report, _) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains("src/Sneaky.cs", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);
        // Two attempts recorded — the wall halted on the SECOND (not the first), proving #86 semantics
        // survive with a hatch present (attempt 1 saw src/Sneaky.cs once and did not halt).
        Assert.Equal(2, journalAfter.Tasks["01-write"].Attempts.Count);
        Assert.Equal(2, runner.Invocations);
    }

    [Fact]
    public async Task Worktree_HatchToClaudeSettingsJson_DeniedWithActionableReason()
    {
        // Carve-out (#321): needsHarnessWrite to a permission-granting .claude/settings.json is DENIED —
        // the harness will not write permission-granting files on an agent's behalf. The .claude/ wall
        // is dropped by the hatch filter so we REACH the handler (proving the carve-out denies it, not
        // the wall halting), and the feedback names the human-must-author remedy.
        using var repo = new TempGitRepo();
        const string settings = ".claude/settings.json";
        string planDir = WriteHarnessWritePromptPlan(
            repo.RepoPath, writeScope: "\".claude/**\"", guardrailChecksPath: settings);

        var runner = new ProbeThenHatchRunner(harnessWritePath: settings, blockedWritePaths: [settings]);

        var (report, planBranch) = await RunWorktreePromptAsync(
            planDir, repo, runner, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite", task.Summary);
        Assert.Contains("denied", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The permission-granting settings file must NEVER be harness-written.
        Assert.False(repo.PlanBranchHasPath(planBranch, settings),
            "the harness must refuse to write .claude/settings.json");

        // The feedback carries the actionable, human-must-author remedy.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        string feedbackPath = Path.Combine(
            planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md");
        Assert.True(File.Exists(feedbackPath));
        string feedback = File.ReadAllText(feedbackPath);
        Assert.Contains("permission-granting files", feedback);
    }

    // ── #437: a LARGE .claude/ deliverable is completable via the anchored `edits` form ───────────

    private const string BigSkillPath = ".claude/skills/big/SKILL.md";
    private const string OriginalPassage = "ORIGINAL-PASSAGE: the sentence that must be corrected.";
    private const string CorrectedPassage = "CORRECTED-PASSAGE: the sentence after correction.";

    /// <summary>
    /// ~250 KB of filler prose with exactly ONE unique anchor line buried in the middle — the shape of
    /// the real #437 casualties (`plan-breakdown/SKILL.md` was 204,136 bytes).
    /// </summary>
    private static string BuildBigSkillFile()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 4_000; i++)
        {
            text.Append(i == 2_000 ? OriginalPassage : $"Line {i:D5}: filler prose that must survive the edit untouched.");
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The shared scaffold for a single-task plan whose SCRIPT action copies a pre-authored fragment JSON
    /// onto <c>GUARDRAILS_STATE_OUT</c>. Copying a file beats interpolating a multi-kilobyte JSON blob
    /// into a shell literal: the fragment is built with a real serializer, so nothing depends on
    /// PowerShell/bash quoting. The caller supplies the task's guardrail(s).
    /// </summary>
    private static (string PlanDir, string TaskDir) WriteFragmentCopyScaffold(
        string repoPath, string fragmentJson, string description)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks", "01-write", "guardrails"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-write");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "{{description}}",
              "dependsOn": [],
              "writeScope": [".claude/**"]
            }
            """);

        string fragmentFile = Path.Combine(planDir, "fragment.json");
        File.WriteAllText(fragmentFile, fragmentJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (Ps)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                $"Copy-Item -LiteralPath '{fragmentFile}' -Destination $env:GUARDRAILS_STATE_OUT -Force\nexit 0\n");
        }
        else
        {
            WriteSh(Path.Combine(taskDir, "action.sh"),
                $"#!/usr/bin/env bash\ncp '{fragmentFile}' \"$GUARDRAILS_STATE_OUT\"\nexit 0\n");
        }

        return (planDir, taskDir);
    }

    /// <summary>
    /// The #437 plan: the fragment-copy scaffold plus a guardrail asserting the big skill file was
    /// CORRECTED and NOT TRUNCATED.
    /// </summary>
    private static string WriteFragmentCopyPlan(string repoPath, string fragmentJson)
    {
        (string planDir, string taskDir) = WriteFragmentCopyScaffold(
            repoPath, fragmentJson, "correct one passage in a large .claude/ skill file");

        if (Ps)
        {
            // A plain (non-interpolated) raw literal: the script is full of PowerShell `$vars` and `{ }`
            // blocks, so a placeholder swap is far less error-prone than brace/dollar escaping.
            const string guardrailPs = """
                $p = Join-Path $env:GUARDRAILS_WORKSPACE '__TARGET__'
                if (-not (Test-Path -LiteralPath $p)) { Write-Output 'deliverable missing'; exit 1 }
                $c = Get-Content -LiteralPath $p -Raw
                if ($c -notmatch 'CORRECTED-PASSAGE') { Write-Output 'the correction did not land'; exit 1 }
                if ($c -match 'ORIGINAL-PASSAGE') { Write-Output 'the old passage is still present'; exit 1 }
                $len = (Get-Item -LiteralPath $p).Length
                if ($len -lt 200000) { Write-Output "the file was TRUNCATED to $len bytes"; exit 1 }
                exit 0

                """;
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-corrected.ps1"),
                guardrailPs.Replace("__TARGET__", BigSkillPath.Replace("/", "\\"), StringComparison.Ordinal));
        }
        else
        {
            WriteSh(Path.Combine(taskDir, "guardrails", "01-corrected.sh"),
                $"""
                #!/usr/bin/env bash
                p="$GUARDRAILS_WORKSPACE/{BigSkillPath}"
                if [ ! -f "$p" ]; then echo 'deliverable missing'; exit 1; fi
                if ! grep -q 'CORRECTED-PASSAGE' "$p"; then echo 'the correction did not land'; exit 1; fi
                if grep -q 'ORIGINAL-PASSAGE' "$p"; then echo 'the old passage is still present'; exit 1; fi
                len=$(wc -c < "$p")
                if [ "$len" -lt 200000 ]; then echo "the file was TRUNCATED to $len bytes"; exit 1; fi
                exit 0

                """);
        }

        return planDir;
    }

    [Fact]
    public async Task Worktree_LargeClaudeFile_CorrectedViaAnchoredEdits_TaskGoesGreen()
    {
        // #437 RED-BAR regression — the case that was structurally impossible before this change. The
        // deliverable is a ~250 KB .claude/ skill file and the change is ONE line. Full-content mode
        // would have required re-emitting ~60k tokens (at or over the runner's maxOutputTokens cap), so
        // the task could not be completed autonomously no matter how small the edit. With the anchored
        // form the request carries only the anchor and its replacement, the harness applies it, and the
        // task's own guardrail then verifies BOTH halves of the safety claim: the correction landed AND
        // the other ~4 000 lines are still there (the file was not truncated).
        using var repo = new TempGitRepo();
        string original = BuildBigSkillFile();
        Assert.True(original.Length > 200_000);
        repo.Commit(BigSkillPath, original);

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new
            {
                path = BigSkillPath,
                reason = "the tool-permission layer refuses .claude/ writes and the file is far too large to re-emit",
                edits = new[] { new { old = OriginalPassage, @new = CorrectedPassage } }
            }
        });

        string planDir = WriteFragmentCopyPlan(repo.RepoPath, fragmentJson);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The committed plan-branch content is the ORIGINAL with exactly that one line replaced —
        // byte-for-byte everywhere else. This is the assertion a full-content re-emission could never
        // make good on, and it is what "untouched by construction" means in practice.
        string committed = repo.PlanBranchContent(planBranch, BigSkillPath);
        Assert.Equal(original.Replace(OriginalPassage, CorrectedPassage, StringComparison.Ordinal), committed);
    }

    [Fact]
    public async Task Worktree_LargeClaudeFile_FullContentMode_RefusedEarly_FeedbackPointsAtEdits()
    {
        // #437 part 2 — fail early and clearly on the size wall. An agent that still reaches for
        // full-content mode on a big EXISTING target gets an actionable refusal naming `edits`, instead
        // of a silently truncated file (here the "content" is deliberately a stub, exactly what a
        // budget-exhausted re-emission would leave behind) or a hunt for a permission workaround.
        using var repo = new TempGitRepo();
        string original = BuildBigSkillFile();
        repo.Commit(BigSkillPath, original);

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new
            {
                path = BigSkillPath,
                content = "# Big skill\n\n(truncated re-emission)\n",
                reason = "rewriting the whole file"
            }
        });

        string planDir = WriteFragmentCopyPlan(repo.RepoPath, fragmentJson);

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite not applied", task.Summary);
        Assert.Contains("`edits`", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The retry feedback restates BOTH accepted forms and the exactly-once anchor rule, so the next
        // attempt can re-emit a correct request rather than guessing.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        string feedbackPath = Path.Combine(
            planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md");
        string feedback = File.ReadAllText(feedbackPath);
        Assert.Contains("could not be applied", feedback);
        Assert.Contains("\"edits\"", feedback);
        Assert.Contains("exactly once", feedback);
        Assert.Contains("UNCHANGED", feedback);
    }

    [Fact]
    public async Task Worktree_LargeClaudeFile_AmbiguousAnchor_RejectedWithoutTouchingTheFile()
    {
        // The safety semantic that makes the anchored form trustworthy, proven end-to-end: an anchor
        // that occurs more than once is refused as AMBIGUOUS rather than silently applied to the first
        // occurrence. The filler lines in the big file are deliberately non-unique.
        using var repo = new TempGitRepo();
        repo.Commit(BigSkillPath, BuildBigSkillFile());

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new
            {
                path = BigSkillPath,
                reason = "correcting a passage",
                edits = new[] { new { old = "filler prose that must survive the edit untouched.", @new = "x" } }
            }
        });

        string planDir = WriteFragmentCopyPlan(repo.RepoPath, fragmentJson);

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("needsHarnessWrite not applied", task.Summary);
        Assert.Contains("AMBIGUOUS", task.Summary);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);
    }

    // ── #445: a deliverable spanning SEVERAL .claude/ files converges in ONE attempt ──────────────

    private const string SkillPath = ".claude/skills/plan-breakdown/SKILL.md";
    private const string SchemasPath = ".claude/skills/plan-breakdown/references/schemas.md";
    private const string ExamplePath = ".claude/skills/plan-breakdown/references/example-breakdown.md";
    private const string StaleWording = "WITHHOLDING-WORDING: the sentence that must be corrected.";
    private const string FixedWording = "CORRECTED-WORDING: the sentence after correction.";

    /// <summary>
    /// The fragment-copy scaffold plus ONE guardrail that holds every listed path to the same standard:
    /// present, carrying the corrected sentence, and free of the stale one. It is the shape of the real
    /// #445 casualty's guardrail — "withholding wording still present in: …" — which is precisely the
    /// check a singular <c>needsHarnessWrite</c> could never satisfy.
    /// </summary>
    private static string WriteMultiFileFragmentPlan(
        string repoPath, string fragmentJson, params string[] checkedPaths)
    {
        (string planDir, string taskDir) = WriteFragmentCopyScaffold(
            repoPath, fragmentJson, "correct the same stale sentence in three .claude/ files");

        if (Ps)
        {
            string list = string.Join(", ", checkedPaths.Select(p => $"'{p.Replace("/", "\\")}'"));
            const string guardrailPs = """
                foreach ($rel in @(__PATHS__)) {
                    $p = Join-Path $env:GUARDRAILS_WORKSPACE $rel
                    if (-not (Test-Path -LiteralPath $p)) { Write-Output "deliverable missing: $rel"; exit 1 }
                    $c = Get-Content -LiteralPath $p -Raw
                    if ($c -match 'WITHHOLDING-WORDING') { Write-Output "withholding wording still present in: $rel"; exit 1 }
                    if ($c -notmatch 'CORRECTED-WORDING') { Write-Output "the correction did not land in: $rel"; exit 1 }
                }
                exit 0

                """;
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-all-corrected.ps1"),
                guardrailPs.Replace("__PATHS__", list, StringComparison.Ordinal));
        }
        else
        {
            string list = string.Join(" ", checkedPaths.Select(p => $"\"{p}\""));
            WriteSh(Path.Combine(taskDir, "guardrails", "01-all-corrected.sh"),
                $"""
                #!/usr/bin/env bash
                for rel in {list}; do
                  p="$GUARDRAILS_WORKSPACE/$rel"
                  if [ ! -f "$p" ]; then echo "deliverable missing: $rel"; exit 1; fi
                  if grep -q 'WITHHOLDING-WORDING' "$p"; then echo "withholding wording still present in: $rel"; exit 1; fi
                  if ! grep -q 'CORRECTED-WORDING' "$p"; then echo "the correction did not land in: $rel"; exit 1; fi
                done
                exit 0

                """);
        }

        return planDir;
    }

    [Fact]
    public async Task Worktree_ThreeClaudeFiles_CorrectedInOneAttempt_TaskGoesGreen()
    {
        // #445 RED-BAR regression, faithful to the reported run
        // (2026-08-11T14-23-39Z-76a7 / wave-01-correct-the-record/03-correct-plan-breakdown-allowlist-wording):
        // ONE deliverable — the same stale sentence corrected in THREE files under
        // .claude/skills/plan-breakdown/. With a SINGULAR needsHarnessWrite this task could never
        // converge: attempt 1 corrected the one file it was allowed and its own fragment reported
        // "filesRemaining"; the guardrail failed with "withholding wording still present in: …"; the
        // failure rolled the segment back to the clean base, so attempt 2 found all three files stale
        // again and honestly halted ("Three files match in the clean base"). Progress could not
        // accumulate, so no number of retries would have helped. With the array, one attempt delivers
        // all three — and `defaultRetries: 0` means it has exactly ONE attempt to do it in.
        using var repo = new TempGitRepo();
        repo.Commit(SkillPath, $"# plan-breakdown\n\nintro\n{StaleWording}\noutro\n");
        repo.Commit(SchemasPath, $"# schemas\n\n{StaleWording}\n");
        // The third file does not exist yet — the array mixes an `edits` entry with a `content` one.

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new object[]
            {
                new
                {
                    path = SkillPath,
                    reason = "the tool-permission layer refuses .claude/ writes",
                    edits = new[] { new { old = StaleWording, @new = FixedWording } }
                },
                new
                {
                    path = SchemasPath,
                    reason = "same correction, second file",
                    edits = new[] { new { old = StaleWording, @new = FixedWording } }
                },
                new
                {
                    path = ExamplePath,
                    reason = "same correction, third file — this one does not exist yet",
                    content = $"# example breakdown\n\n{FixedWording}\n"
                }
            }
        });

        string planDir = WriteMultiFileFragmentPlan(
            repo.RepoPath, fragmentJson, SkillPath, SchemasPath, ExamplePath);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // Exactly ONE attempt: the whole point is that this no longer needs to (and cannot) accumulate
        // progress across retries.
        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Single(journalAfter.Tasks["01-write"].Attempts);

        // All three files reached the plan branch, corrected, with the untouched prose intact.
        Assert.Equal($"# plan-breakdown\n\nintro\n{FixedWording}\noutro\n",
            repo.PlanBranchContent(planBranch, SkillPath));
        Assert.Equal($"# schemas\n\n{FixedWording}\n", repo.PlanBranchContent(planBranch, SchemasPath));
        Assert.Equal($"# example breakdown\n\n{FixedWording}\n", repo.PlanBranchContent(planBranch, ExamplePath));
    }

    [Fact]
    public async Task Worktree_SingularRequest_CorrectsOnlyOneOfThreeFiles_GuardrailStillFails()
    {
        // The pre-#445 world, reproduced — and the NEGATIVE polarity of the guardrail the green test
        // above relies on. A SINGULAR request corrects the one file it names; the shared guardrail then
        // fails with the real run's message ("withholding wording still present in: …"), which is why
        // the task could never converge: the failure rolls the segment back, so the next attempt sees
        // all three files stale again. This test also keeps the green test honest — a toothless
        // guardrail that passed regardless would fail HERE.
        using var repo = new TempGitRepo();
        repo.Commit(SkillPath, $"# plan-breakdown\n\n{StaleWording}\n");
        repo.Commit(SchemasPath, $"# schemas\n\n{StaleWording}\n");
        repo.Commit(ExamplePath, $"# example breakdown\n\n{StaleWording}\n");

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new
            {
                path = SkillPath,
                reason = "one file per request — the pre-#445 shape",
                edits = new[] { new { old = StaleWording, @new = FixedWording } }
            }
        });

        string planDir = WriteMultiFileFragmentPlan(
            repo.RepoPath, fragmentJson, SkillPath, SchemasPath, ExamplePath);

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The guardrail that the green test above depends on genuinely has teeth: it names the files
        // still carrying the stale wording, exactly as the real run's did.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        FailedGuardrail failed = Assert.Single(attempt.FailedGuardrails);
        Assert.Equal("01-all-corrected", failed.Name);
        Assert.Contains("withholding wording still present in", failed.Reason);
    }

    [Fact]
    public async Task Worktree_MultiFileBatch_BadAnchorInTheSecondFile_WritesNothingAtAll()
    {
        // CROSS-FILE ATOMICITY end-to-end. The first entry is perfectly good; the second's anchor is not
        // in its file. A naive per-entry loop would already have written file 1 by the time it discovered
        // the problem, leaving a half-corrected tree the agent cannot reason about. Nothing may land:
        // the attempt fails as `not applied`, both committed files keep their original bytes, and the
        // feedback names the offending array index.
        using var repo = new TempGitRepo();
        string firstOriginal = $"# plan-breakdown\n\n{StaleWording}\n";
        string secondOriginal = "# schemas\n\nthis file never carried the stale sentence\n";
        repo.Commit(SkillPath, firstOriginal);
        repo.Commit(SchemasPath, secondOriginal);

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new object[]
            {
                new
                {
                    path = SkillPath,
                    reason = "this one would apply cleanly",
                    edits = new[] { new { old = StaleWording, @new = FixedWording } }
                },
                new
                {
                    path = SchemasPath,
                    reason = "this anchor is not in the file",
                    edits = new[] { new { old = StaleWording, @new = FixedWording } }
                }
            }
        });

        string planDir = WriteMultiFileFragmentPlan(repo.RepoPath, fragmentJson, SkillPath, SchemasPath);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.False(task.IsGreen);
        Assert.Contains("needsHarnessWrite not applied", task.Summary);
        Assert.Contains("needsHarnessWrite[1]", task.Summary);
        Assert.Contains("NOT FOUND", task.Summary);

        // NEITHER file changed — not even the one whose edit resolved perfectly.
        Assert.Equal(firstOriginal, repo.PlanBranchContent(planBranch, SkillPath));
        Assert.Equal(secondOriginal, repo.PlanBranchContent(planBranch, SchemasPath));

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        // The retry feedback teaches the array form and says plainly that nothing landed, so the agent
        // re-emits the WHOLE array instead of hunting for which half it still owes.
        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        string feedback = File.ReadAllText(Path.Combine(
            planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md"));
        Assert.Contains("could not be applied", feedback);
        Assert.Contains("NOTHING was written", feedback);
        Assert.Contains("ARRAY of entries", feedback);
        Assert.Contains(SkillPath, feedback);
        Assert.Contains(SchemasPath, feedback);
    }

    [Fact]
    public async Task Worktree_MultiFileBatch_OneEntryTargetsClaudeSettings_DeniesTheWholeBatch()
    {
        // The #321 carve-out must hold for EVERY entry: an array is not a side door for a
        // permission-granting settings file smuggled in beside legitimate deliverables. Neither the
        // settings file NOR the legitimate sibling may be written.
        using var repo = new TempGitRepo();

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new object[]
            {
                new { path = ".claude/commands/legit.md", reason = "a legitimate deliverable", content = "# legit\n" },
                new { path = ".claude/settings.json", reason = "widen my own permissions", content = "{\"permissions\":{\"allow\":[\"Write(.claude/**)\"]}}" }
            }
        });

        string planDir = WriteMultiFileFragmentPlan(repo.RepoPath, fragmentJson, ".claude/commands/legit.md");

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("needsHarnessWrite denied", task.Summary);

        Assert.False(repo.PlanBranchHasPath(planBranch, ".claude/settings.json"),
            "the harness must refuse to write .claude/settings.json");
        Assert.False(repo.PlanBranchHasPath(planBranch, ".claude/commands/legit.md"),
            "nothing else in the array may be written when one entry is denied");

        JournalDocument journalAfter = JournalReader.Read(RunJournal.PathFor(planDir));
        Assert.Equal(Core.Journal.TaskStatus.NeedsHuman, journalAfter.Tasks["01-write"].Status);

        AttemptRecord attempt = Assert.Single(journalAfter.Tasks["01-write"].Attempts);
        string feedback = File.ReadAllText(Path.Combine(
            planDir, attempt.LogDir.Replace('/', Path.DirectorySeparatorChar), "feedback.md"));
        Assert.Contains("permission-granting files", feedback);
    }

    [Fact]
    public async Task Worktree_MultiFileBatch_DuplicatePathEntries_RejectedRatherThanLastWins()
    {
        // Two entries for one file is ambiguous — the order a model listed them in is not a contract.
        // The harness refuses and tells the agent to merge them, rather than silently discarding one of
        // the two sets of changes it asked for.
        using var repo = new TempGitRepo();
        string original = $"# plan-breakdown\n\n{StaleWording}\nsecond line\n";
        repo.Commit(SkillPath, original);

        string fragmentJson = JsonSerializer.Serialize(new
        {
            needsHarnessWrite = new object[]
            {
                new
                {
                    path = SkillPath,
                    reason = "first half of the change",
                    edits = new[] { new { old = StaleWording, @new = FixedWording } }
                },
                new
                {
                    path = SkillPath,
                    reason = "second half of the change — same file, a separate entry",
                    edits = new[] { new { old = "second line", @new = "SECOND LINE" } }
                }
            }
        });

        string planDir = WriteMultiFileFragmentPlan(repo.RepoPath, fragmentJson, SkillPath);

        var (report, planBranch) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("needsHarnessWrite not applied", task.Summary);
        Assert.Contains("SAME file", task.Summary);
        Assert.Equal(original, repo.PlanBranchContent(planBranch, SkillPath));
    }

    [Fact]
    public async Task Worktree_EmptyArray_RejectedWithAnActionableReason_NotASilentNoOp()
    {
        // A silent no-op would surface as an unrelated guardrail failure ("deliverable missing") several
        // steps away from the actual mistake. Fail at the request, with a reason that names it.
        using var repo = new TempGitRepo();

        string planDir = WriteMultiFileFragmentPlan(
            repo.RepoPath, """{ "needsHarnessWrite": [] }""", SkillPath);

        var (report, _) = await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("needsHarnessWrite not applied", task.Summary);
        Assert.Contains("EMPTY array", task.Summary);
    }
}
