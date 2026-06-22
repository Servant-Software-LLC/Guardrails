using System.Text;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using TaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails run --revalidate-task &lt;id&gt;</c> (issue #102) end-to-end through the real
/// composition root with OS-appropriate scripts (.ps1 on Windows, .sh elsewhere). The fixture models
/// the headline use case: a task whose action writes a "broken" artifact and whose guardrail checks a
/// SEPARATE "fixed marker" file in the workspace. A normal run leaves it needs-human; a human then
/// writes the marker by hand; <c>--revalidate-task</c> must pass the guardrail WITHOUT re-running the
/// action (which would re-break the artifact), mark the task succeeded, and never spawn an agent.
/// </summary>
public sealed class RevalidateCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task Revalidate_AfterHumanFix_PassesWithoutRerunningAction()
    {
        using var plan = new RevalidatePlan();

        // 1. Normal run: action writes the broken artifact (and a sentinel proving it ran); the
        //    guardrail checks for the human's fixed-marker, which is absent → needs-human.
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.TaskFailed, runExit);
        Assert.True(File.Exists(plan.ActionRanSentinel), "the action should have run on the normal run");

        // 2. Human hand-fixes the workspace and removes the sentinel so we can prove the action does
        //    NOT run again during revalidate.
        File.WriteAllText(plan.FixedMarker, "fixed by hand");
        File.Delete(plan.ActionRanSentinel);

        // 3. Revalidate-only: guardrails run against the current workspace, no action.
        (int revalExit, string revalOut) = await InvokeAsync("run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        Assert.Equal(ExitCodes.Success, revalExit);
        Assert.False(File.Exists(plan.ActionRanSentinel), "the action MUST NOT re-run during a revalidate");
        Assert.Contains("revalidate ok", revalOut);

        // The journal records the task succeeded (so the next normal run resumes the rest).
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));
    }

    [Fact]
    public async Task Revalidate_GuardrailStillFails_ReportsFailure_ExitsTwo_NoAction()
    {
        using var plan = new RevalidatePlan();

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.TaskFailed, runExit);
        File.Delete(plan.ActionRanSentinel);

        // No fix applied — the fixed-marker is still missing, so the guardrail still fails.
        (int revalExit, string revalOut) = await InvokeAsync("run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        Assert.Equal(ExitCodes.TaskFailed, revalExit);
        Assert.False(File.Exists(plan.ActionRanSentinel), "no action runs during a revalidate, pass or fail");
        Assert.Contains("still failing", revalOut);
        Assert.NotEqual(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));
    }

    [Fact]
    public async Task Revalidate_UnknownTask_ExitsHarnessError()
    {
        using var plan = new RevalidatePlan();

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--revalidate-task", "99-nope");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("Unknown task", output);
    }

    [Fact]
    public async Task Revalidate_AlreadySucceededTask_IsRefused()
    {
        using var plan = new RevalidatePlan();

        // Fix and run normally so the task genuinely succeeds.
        File.WriteAllText(plan.FixedMarker, "fixed");
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui");
        Assert.Equal(ExitCodes.Success, runExit);
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("already succeeded", output);
    }

    [Fact]
    public async Task Revalidate_CombinedWithFresh_IsRejected()
    {
        using var plan = new RevalidatePlan();

        (int exit, string output) = await InvokeAsync(
            "run", plan.PlanDir, "--revalidate-task", "01-fix-manifest", "--fresh");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("cannot be combined", output);
    }

    [Fact]
    public async Task Revalidate_WorktreeMode_IsRefused()
    {
        // maxParallelism > 1 on a git workspace = worktree mode; an in-place fix in the user's
        // checkout is invisible to an isolated segment worktree, so revalidate must refuse.
        using var plan = new RevalidatePlan(maxParallelism: 3, makeGitRepo: true);

        (int exit, string output) = await InvokeAsync(
            "run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("not supported in worktree mode", output);
    }

    private static TaskStatus StatusOf(string planDir, string taskId)
    {
        JournalDocument doc = JournalReader.Read(RunJournal.PathFor(planDir));
        return doc.Tasks[taskId].Status;
    }

    /// <summary>
    /// A one-task plan whose action writes a broken artifact + a "the action ran" sentinel, and whose
    /// guardrail passes only when a separate fixed-marker file exists in the workspace. OS-appropriate
    /// scripts. The workspace is the plan dir itself (the default <c>"."</c>).
    /// </summary>
    private sealed class RevalidatePlan : IDisposable
    {
        private static readonly bool Ps = OperatingSystem.IsWindows();

        public string PlanDir { get; }

        /// <summary>The "fixed by hand" marker the guardrail checks for (absent ⇒ guardrail fails).</summary>
        public string FixedMarker => Path.Combine(PlanDir, "fixed-marker.txt");

        /// <summary>Written by the action every time it runs — used to prove the action did/did not run.</summary>
        public string ActionRanSentinel => Path.Combine(PlanDir, "action-ran.txt");

        public RevalidatePlan(int maxParallelism = 1, bool makeGitRepo = false)
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr-reval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PlanDir);

            if (makeGitRepo)
            {
                // A real git repo so SchedulerFactory.WouldUseWorktreeMode sees worktree mode
                // (maxParallelism > 1 AND a git workspace).
                RunGit("init");
            }

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": ".",
                  "defaultRetries": 0,
                  "maxParallelism": {{maxParallelism}}
                }
                """);

            string taskDir = Path.Combine(PlanDir, "tasks", "01-fix-manifest");
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                """
                {
                  "description": "Produce the manifest; the guardrail checks the fixed marker",
                  "dependsOn": []
                }
                """);

            // Action: drop the "I ran" sentinel (relative to cwd = workspace = plan dir). It does NOT
            // write the fixed-marker, so the guardrail fails until a human writes it.
            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"), Ps
                ? "Set-Content -Path 'action-ran.txt' -Value 'ran'\nexit 0\n"
                : "#!/usr/bin/env bash\necho ran > action-ran.txt\nexit 0\n");

            // Guardrail: pass iff fixed-marker.txt exists in the workspace.
            WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-fixed.ps1" : "01-fixed.sh"), Ps
                ? "if (Test-Path 'fixed-marker.txt') { exit 0 } else { Write-Output 'fixed-marker.txt missing'; exit 1 }\n"
                : "#!/usr/bin/env bash\nif [ -f fixed-marker.txt ]; then exit 0; else echo 'fixed-marker.txt missing'; exit 1; fi\n");
        }

        private void RunGit(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = PlanDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = System.Diagnostics.Process.Start(psi);
            proc!.WaitForExit();
        }

        private static void WriteScript(string path, string content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(PlanDir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
