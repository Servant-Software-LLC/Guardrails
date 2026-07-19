using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.Review;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #360 Phase 1 — the between-wave breakdown invocation on a REAL git worktree-mode run (a real
/// <see cref="GitWorktreeProvider"/> + plan branch + integration worktree). The breakdown PROMPT is STUBBED
/// (a fake <see cref="IPromptRunner"/> that writes a valid <c>tasks/</c> and returns a canned result — NO
/// real Claude call); the wave's own task runs a real OS-picked script. Proves the invoker receives the REAL
/// integration worktree path and the in-process <c>guardrails validate</c> gate + BreakdownComplete halt work
/// end-to-end against actual git.
/// </summary>
public sealed class WaveBreakdownRunTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

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

    private static void WriteScriptTask(string tasksDir, string folder)
    {
        string taskDir = Path.Combine(tasksDir, folder);
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), $$"""{ "description": "{{folder}}" }""");
        string action = Ps
            ? "Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE/scaffold.txt\" -Value 'x'\nexit 0\n"
            : "#!/usr/bin/env bash\nprintf 'x' > \"$GUARDRAILS_WORKSPACE/scaffold.txt\"\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), action);
        string gate = Ps
            ? "# catches: scaffold.txt missing\nif (-not (Test-Path \"$env:GUARDRAILS_WORKSPACE/scaffold.txt\")) { exit 1 }\nexit 0\n"
            : "#!/usr/bin/env bash\n# catches: scaffold.txt missing\n[ -f \"$GUARDRAILS_WORKSPACE/scaffold.txt\" ] || exit 1\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, "guardrails", Script("01-check")), gate);
    }

    private static void Git(string workingDir, params string[] args)
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
        proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr}");
        }
    }

    /// <summary>A stub breakdown runner that authors a VALID single-task wave-02 and captures the granted integration-worktree path.</summary>
    private sealed class StubBreakdownRunner : IPromptRunner
    {
        public string? GrantedIntegrationDir { get; private set; }
        public int Invocations { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;

            // The invoker grants the integration worktree via a second `--add-dir` (doc 11 §9.3 step 4).
            int addDir = invocation.Settings.ExtraArgs
                .Select((a, i) => (a, i)).Where(t => t.a == "--add-dir").Select(t => t.i).LastOrDefault(-1);
            if (addDir >= 0 && addDir + 1 < invocation.Settings.ExtraArgs.Count)
            {
                GrantedIntegrationDir = invocation.Settings.ExtraArgs[addDir + 1];
            }

            // Simulate the plan-breakdown sub-process authoring wave-02's tasks/ into the plan folder.
            string tasksDir = Path.Combine(invocation.WorkingDirectory, "wave-02-build", "tasks");
            WriteScriptTask(tasksDir, "01-compile");

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored wave-02",
                CostUsd = 0.5m,
                Summary = "breakdown authored wave-02"
            });
        }
    }

    [Fact]
    public async Task AutoPolicy_RealWorktree_BriefPresent_StubAuthorsValid_BreakdownComplete_RealIntegPath_NoMarker()
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-breakdown-it-" + Guid.NewGuid().ToString("N"));
        string repoPath = Path.Combine(root, "repo");
        string worktreeRoot = Path.Combine(root, "worktrees");
        Directory.CreateDirectory(repoPath);
        Directory.CreateDirectory(worktreeRoot);
        try
        {
            Git(repoPath, "init");
            Git(repoPath, "config", "user.email", "test@guardrails.local");
            Git(repoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "# breakdown-e2e");
            Git(repoPath, "add", ".");
            Git(repoPath, "commit", "-m", "Initial commit");

            // A waved plan: wave-01 authored (real script task) + wave-02 an empty JIT stub with a brief.md.
            string planDir = Path.Combine(repoPath, "plan");
            Directory.CreateDirectory(Path.Combine(planDir, "state"));
            File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
                """{ "version": 1, "guardrailMode": "failFast", "workspace": "..", "defaultRetries": 0, "maxParallelism": 2, "autonomyPolicy": "auto" }""");
            WriteScriptTask(Path.Combine(planDir, "wave-01-scaffold", "tasks"), "01-config");
            Directory.CreateDirectory(Path.Combine(planDir, "wave-02-build", "tasks")); // empty JIT stub
            File.WriteAllText(Path.Combine(planDir, "wave-02-build", "brief.md"),
                "# wave-02-build\nCompile the artifact from wave-01's scaffold.\n");

            PlanLoadResult load = new PlanLoader().Load(planDir);
            Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
            PlanDefinition plan = load.Plan!;

            var stateManager = new StateManager(plan.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(plan);
            var registry = PromptRunnerRegistry.Build(plan.Config,
                _ => throw new InvalidOperationException("no real runner in this test"));
            var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
            var executor = new TaskExecutor(
                plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);
            var provider = new GitWorktreeProvider(repoPath, worktreeRoot);
            var reVerifier = new GuardrailReVerifier(new ProcessRunner(), interpreterMap);
            var stub = new StubBreakdownRunner();
            var invoker = new WaveBreakdownInvoker(stub);

            var scheduler = new Scheduler(
                plan, executor, journal, worktreeProvider: provider, reVerifier: reVerifier,
                breakdownInvoker: invoker);

            RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);

            // The breakdown was invoked (auto) against the REAL integration worktree and its output validated.
            Assert.Equal(1, stub.Invocations);
            Assert.NotNull(report.WaveHalt);
            Assert.Equal(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);
            Assert.Equal("wave-02-build", report.WaveHalt.WaveDir);

            // The invoker granted the STUB a REAL, existing integration worktree path (the --add-dir target).
            Assert.NotNull(stub.GrantedIntegrationDir);
            Assert.True(Directory.Exists(stub.GrantedIntegrationDir!),
                $"integration worktree path should exist: {stub.GrantedIntegrationDir}");

            // wave-01 really completed on the plan branch before the checkpoint.
            Assert.Equal(WaveStatus.Completed, journal.WaveEntryOf("wave-01-scaffold")!.Status);

            // auto-applied decision + the transcript under logs/<runId>/wave-02-build/breakdown/.
            Assert.Contains(journal.Document.Decisions ?? [],
                d => d.Boundary == "wave" && d.Decision == "auto-applied" && d.Subject == "wave-02-build");
            string[] composed = Directory.GetFiles(
                Path.Combine(planDir, "logs"), "composed-prompt.md", SearchOption.AllDirectories);
            Assert.Contains(composed, p => p.Replace('\\', '/').Contains("/wave-02-build/breakdown/"));

            // The review gate is never auto-satisfied — no marker written.
            Assert.False(File.Exists(ReviewMarker.PathFor(planDir)));
        }
        finally
        {
            try { SafeDelete.DeleteDirectory(root); } catch { /* best-effort */ }
        }
    }
}
