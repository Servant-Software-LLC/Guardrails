using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Opt-in end-to-end proof of the prompt-output staging fix (SSOT §9.5, issue #266) against the
/// REAL Claude Code CLI. Skipped unless <c>GUARDRAILS_REAL_CLAUDE=1</c> (CI and default local runs
/// never spend tokens) — mirrors <see cref="RealClaudeSmokeTests"/>'s exact gating convention.
///
/// <para>
/// This is the test the PRIOR (dead-end) investigation would have needed: a fake <see cref="Prompts.IPromptRunner"/>
/// cannot reproduce Claude Code's own hardcoded <c>.claude/</c> sensitive-path block — only a real
/// sub-agent process enforces it. The plan folder here is physically rooted at
/// <c>&lt;tmpRepo&gt;/.claude/plans/probe/</c> inside a throwaway git repo with <c>maxParallelism: 2</c>,
/// so <see cref="SchedulerFactory.Create"/> wires REAL worktree mode (segment worktree + the
/// worktree-containment hook, SSOT §9.4) — the exact combination the design had to reconcile: the
/// sub-agent's own <c>GUARDRAILS_STATE_OUT</c> write must land OUTSIDE both the `.claude/` block AND
/// the containment hook's worktree boundary.
/// </para>
/// </summary>
public sealed class RealClaudeClaudeNestedPlanTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("GUARDRAILS_REAL_CLAUDE") == "1";

    // ── temp git repo (Windows-safe teardown; also prunes the harness-owned worktree root) ────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        private PlanDefinition? _plan;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-realclaude-claudenested-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# real-claude .claude-nested-plan test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Set once the plan is loaded, so <see cref="Dispose"/> can prune its worktree root.</summary>
        public void TrackPlan(PlanDefinition plan) => _plan = plan;

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

        public void Dispose()
        {
            ForceDelete(_root);
            // SchedulerFactory places segment worktrees under a global temp root keyed by the plan
            // dir's own path — remove it too so a worktree-mode run leaves nothing behind.
            if (_plan is not null)
            {
                ForceDelete(SchedulerFactory.WorktreeRootFor(_plan));
            }
        }

        private static void ForceDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    [Fact]
    public async Task PromptActionTask_ClaudeNestedPlan_WorktreeMode_RunsGreen_AgainstRealClaude()
    {
        Assert.SkipUnless(Enabled, "Set GUARDRAILS_REAL_CLAUDE=1 to run the real-claude .claude-nested-plan test.");

        using var repo = new TempGitRepo();
        string planDir = BuildClaudeNestedPromptPlan(repo.RepoPath);

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        repo.TrackPlan(load.Plan!);

        // maxParallelism: 2 + a real git repo => SchedulerFactory wires REAL worktree mode (segment
        // worktree + the worktree-containment hook, SSOT §9.4) — the exact scenario issue #266 was
        // filed against (a `.claude/`-nested plan folder run for real, not just validated).
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        RunReport report = await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded, string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Summary}")));

        // The DOCUMENTED final path (which DOES sit under .claude/) exists and carries the real
        // agent's fragment — proof the sub-agent's write landed at the STAGING path (never the
        // `.claude/`-nested one) and the harness promoted it here.
        string runId = JournalReader.Read(RunJournal.PathFor(planDir)).RunId;
        string finalFragmentPath = Path.Combine(planDir, "logs", runId, "01-probe", "attempt-1", "action-out-fragment.json");
        Assert.True(File.Exists(finalFragmentPath),
            $"expected the harness to have promoted the staged fragment to {finalFragmentPath}");
        Assert.Contains("probed", File.ReadAllText(finalFragmentPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildClaudeNestedPromptPlan(string repoRoot)
    {
        string planDir = Path.Combine(repoRoot, ".claude", "plans", "probe");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "../../..",
              "defaultRetries": 0,
              "defaultTimeoutSeconds": 300,
              "maxParallelism": 2,
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "claude",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Write"],
                  "maxTurns": 5
                }
              }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-probe");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "probe .claude-nested staging against real Claude", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"),
            "Write a state fragment containing exactly one top-level key equal to your own task id, " +
            "whose value is the object `{\"probed\": true}`. Do not write any other files. Then stop.\n");

        // Deterministic guardrail: verify the recorded action's fragment via GUARDRAILS_STATE_FRAGMENT
        // (SSOT §5.1) — no second real-Claude call needed to check the first one's output.
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-verify.ps1"),
                """
                if (-not $env:GUARDRAILS_STATE_FRAGMENT -or -not (Test-Path $env:GUARDRAILS_STATE_FRAGMENT)) {
                    Write-Output 'no state fragment was produced'
                    exit 1
                }
                $content = Get-Content -Raw $env:GUARDRAILS_STATE_FRAGMENT
                if ($content -notmatch 'probed') {
                    Write-Output "fragment missing 'probed': $content"
                    exit 1
                }
                exit 0
                """);
        }
        else
        {
            string sh = Path.Combine(taskDir, "guardrails", "01-verify.sh");
            File.WriteAllText(sh,
                """
                #!/usr/bin/env bash
                if [ -z "$GUARDRAILS_STATE_FRAGMENT" ] || [ ! -f "$GUARDRAILS_STATE_FRAGMENT" ]; then
                  echo 'no state fragment was produced'
                  exit 1
                fi
                if ! grep -qi 'probed' "$GUARDRAILS_STATE_FRAGMENT"; then
                  echo "fragment missing 'probed': $(cat "$GUARDRAILS_STATE_FRAGMENT")"
                  exit 1
                fi
                exit 0
                """);
            File.SetUnixFileMode(sh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }
}
