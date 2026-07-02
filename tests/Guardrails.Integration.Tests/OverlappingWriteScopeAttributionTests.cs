using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #175 — merge-collision ATTRIBUTION on the LEGACY <c>integrationGate</c> path. When the terminal
/// integration gate fails on the final merged HEAD and two tasks have OVERLAPPING <c>writeScope</c> on a
/// shared file, the harness enriches the gate-failure diagnosis to name those task pairs + the shared path
/// — so a human immediately sees "this looks like a merge collision on <c>Launcher.cs</c>" instead of a
/// bare build error (the real plan-0009 CS0101 duplicate-class break a 3-way merge could not catch). The
/// harness does NOT detect the semantic duplicate itself (that is the build guardrail's job); it surfaces
/// the structural suspects.
///
/// <para>
/// <b>⚠ DELIBERATE LEGACY-PATH REGRESSION PIN — this is NOT a validation contract.</b> The fixture
/// intentionally builds a plan with a terminal <c>integrationGate: true</c> sink task and loads it via
/// <see cref="PlanLoader"/> ONLY, deliberately BYPASSING <see cref="PlanValidator"/> — so
/// <c>GR2029</c> (<see cref="DiagnosticCodes.RetiredIntegrationGateKey"/>, the four-folder loader's hard
/// rejection of the retired <c>integrationGate</c> key) never fires. This pins the #175 attribution logic
/// on the LEGACY per-task <c>integrationGate</c> / <c>Scheduler.WithTerminalGateFailure</c> C1 path, which
/// is still live for plans on the retired sink-task modelling. Do NOT read this as "<c>integrationGate:
/// true</c> still validates" — it does not (a real <c>run</c>/<c>validate</c> rejects it via GR2029). The
/// #175 attribution has NOT yet been ported to the new plan-level <c>&lt;plan&gt;/guardrails/</c> terminal
/// gate; that port is tracked as <b>#205</b>, at which point this fixture should migrate to the four-folder
/// shape and validate. Until then this test MUST keep bypassing validate — do not "fix" it to validate,
/// and do not migrate it (that work is blocked on #205).
/// </para>
///
/// Driven through a real git repo + a forced terminal-gate failure (<see cref="FailingReVerifier"/>),
/// exercising the production <c>Scheduler.WithTerminalGateFailure</c> path. The linear chain FF-settles
/// (no union re-verify), so the re-verifier fires ONLY at the terminal C1 gate.
/// </summary>
public sealed class OverlappingWriteScopeAttributionTests
{
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-175-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# overlap-attribution-test");
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

    /// <summary>A re-verifier that always fails — forces the terminal C1 gate to fail.</summary>
    private sealed class FailingReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath, IReadOnlyList<GuardrailDefinition> guardrails, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult
            {
                Passed = false,
                FailedGuardrails = [new GuardrailResult
                {
                    Name = "01-solution-builds",
                    Passed = false,
                    Reason = "build failed"
                }]
            });
    }

    private static void WriteScript(string path, string body)
    {
        string content = OperatingSystem.IsWindows() ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>An action that writes (overwrites) a single workspace-relative file in its writeScope.</summary>
    private static string WriteFileAction(string relPath, string content) => OperatingSystem.IsWindows()
        ? $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE\\{relPath}\" -Value '{content}'; exit 0"
        : $"printf '%s' '{content}' > \"$GUARDRAILS_WORKSPACE/{relPath}\"; exit 0";

    /// <summary>
    /// Build a 3-task plan in <paramref name="repoPath"/>: two implementation tasks (a linear chain so
    /// both FF-settle) each declaring <paramref name="scopeA"/>/<paramref name="scopeB"/> + writing a
    /// file in scope, then a terminal <c>integrationGate</c> no-op sink carrying one
    /// <c>scope:"integration"</c> guardrail (so the C1 gate runs). The gate's re-verify is forced to
    /// fail by the test's <see cref="FailingReVerifier"/>.
    /// </summary>
    private static string CreatePlan(string repoPath, string scopeA, string fileA, string scopeB, string fileB)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(planDir);
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

        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        // 01-impl-a and 02-impl-b: a linear chain (B depends on A) so both settle via FF, each writing
        // exactly one in-scope file. The chain avoids a union so the FailingReVerifier fires ONLY at C1.
        WriteImplTask(planDir, "01-impl-a", dependsOn: [], scope: scopeA, file: fileA, content: "class CommanderRestImporter {}");
        WriteImplTask(planDir, "02-impl-b", dependsOn: ["01-impl-a"], scope: scopeB, file: fileB, content: "class CommanderRestImporter { void X() {} }");
        WriteTerminalGate(planDir, "10-terminal-gate", dependsOn: ["02-impl-b"]);
        return planDir;
    }

    private static void WriteImplTask(string planDir, string taskId, string[] dependsOn, string scope, string file, string content)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "impl {{taskId}}",
              "dependsOn": {{dependsJson}},
              "writeScope": ["{{scope}}"]
            }
            """);

        WriteScript(Path.Combine(taskDir, ActionFileName), WriteFileAction(file, content));
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), "exit 0");
    }

    private static void WriteTerminalGate(string planDir, string taskId, string[] dependsOn)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        string dependsJson = "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "terminal integration gate {{taskId}}",
              "dependsOn": {{dependsJson}},
              "integrationGate": true
            }
            """);

        // A pure no-op action — the gate verifies the merged HEAD, it does not produce work.
        WriteScript(Path.Combine(taskDir, ActionFileName), "exit 0");
        // One integration-scoped guardrail so the run has a non-empty integration set (the C1 gate).
        WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), "exit 0");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", GuardrailSidecarName),
            """{ "scope": "integration" }""");
    }

    private static string ActionFileName => OperatingSystem.IsWindows() ? "action.ps1" : "action.sh";
    private static string GuardrailFileName => OperatingSystem.IsWindows() ? "01-solution-builds.ps1" : "01-solution-builds.sh";
    private static string GuardrailSidecarName => "01-solution-builds.json";

    private static async Task<RunReport> RunAsync(TempGitRepo repo, string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        var stateManager = new StateManager(load.Plan!.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(load.Plan!);
        var registry = PromptRunnerRegistry.Build(load.Plan!.Config,
            _ => throw new InvalidOperationException("No prompt runners in #175 tests."));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), load.Plan!.Config.Interpreters);
        var executor = new TaskExecutor(
            load.Plan!, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        var scheduler = new Scheduler(
            load.Plan!, executor, journal, worktreeProvider: provider, reVerifier: new FailingReVerifier());

        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TerminalGateFails_OverlappingWriteScopes_DiagnosisNamesBothTasksAndSharedFile()
    {
        using var repo = new TempGitRepo();
        // Both impl tasks write Launcher.cs (overlapping writeScope) — the #175 collision shape.
        string planDir = CreatePlan(repo.RepoPath,
            scopeA: "Launcher.cs", fileA: "Launcher.cs",
            scopeB: "Launcher.cs", fileB: "Launcher.cs");

        RunReport report = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "10-terminal-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);

        // The diagnosis names the merge-collision suspects: both tasks AND the shared file.
        Assert.Contains("merge collision", gate.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("01-impl-a", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("02-impl-b", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("Launcher.cs", gate.Summary, StringComparison.Ordinal);
        // The bare gate-failure detail is still present (the hint is additive).
        Assert.Contains("terminal integration gate failed", gate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalGateFails_DisjointWriteScopes_NoCollisionAnnotation()
    {
        using var repo = new TempGitRepo();
        // Disjoint writeScopes — no overlap, so NO collision hint must be appended.
        string planDir = CreatePlan(repo.RepoPath,
            scopeA: "A.cs", fileA: "A.cs",
            scopeB: "B.cs", fileB: "B.cs");

        RunReport report = await RunAsync(repo, planDir);

        TaskResult gate = report.Tasks.Single(t => t.TaskId == "10-terminal-gate");
        Assert.Equal(TaskOutcome.NeedsHuman, gate.Outcome);

        // Still a gate failure, but with NO merge-collision attribution (scopes don't overlap).
        Assert.Contains("terminal integration gate failed", gate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("merge collision", gate.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
