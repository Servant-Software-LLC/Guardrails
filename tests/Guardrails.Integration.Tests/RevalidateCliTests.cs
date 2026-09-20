using System.Text;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
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
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
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

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
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
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
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
    public async Task Revalidate_WorktreeMode_NeverIntegrated_IsRefused_AndSaysWhy()
    {
        // maxParallelism > 1 on a git workspace = worktree mode. This plan has never run, so the plan
        // branch carries no Guardrails-Task: trailer for the task — its work was never integrated, and a
        // hand-fix really would live only in the checkout. Issue #456 KEEPS the refusal here; what it
        // changes is that the message names this situation instead of the blanket "set maxParallelism to 1".
        using var plan = new RevalidatePlan(maxParallelism: 3, makeGitRepo: true);

        (int exit, string output) = await InvokeAsync(
            "run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains("no Guardrails-Task: trailer", output);
        Assert.Contains("never integrated", output);
    }

    [Fact]
    public async Task Revalidate_WorktreeMode_AlreadyIntegrated_VerifiesThePlanBranchTree()
    {
        // The case #456 exists for, and the NON-VACUITY control for this whole change: without it the
        // suite would only prove revalidate still REFUSES, which passes whether or not the allow path
        // works at all.
        using var plan = new RevalidatePlan(
            maxParallelism: 3, makeGitRepo: true, commitWorkspace: true,
            actionWritesMarker: true, withFailingSibling: true);

        // 01 settles GREEN, integrating its work — fixed-marker.txt included — onto guardrails/<plan>.
        // Its DEPENDENT sibling 02 then fails, which is load-bearing twice over: it makes the ordering
        // deterministic (02 cannot run before 01 settles), and it keeps the run RESUMABLE.
        // WorktreeReclaim.ShouldReclaimOnCompletion requires AllSucceeded, so a wholly-green run would
        // have REMOVED the integration worktree — leaving no tree for this verb to verify in.
        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, runExit);
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));

        // POSITIVE CONTROL: 01's work really is on the plan branch...
        string planName = Path.GetFileName(plan.PlanDir);
        Assert.True(
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.PlanDir, planName).ContainsKey("01-fix-manifest"),
            "01's Guardrails-Task: trailer must be on the plan branch for this test to mean anything");

        // ...and the marker it produced exists ONLY there, never in the operator's checkout.
        Assert.False(File.Exists(plan.FixedMarker), "the integrated marker must not be in the checkout");

        // Reproduce the state #456 is about. Scheduler.SettleAsync commits the task trailer BEFORE
        // journaling the settle (5904→5910 FF, 5978→5980 AI-merge, 6015→6017 non-FF union) and no intent
        // marker guards that pair — RewindIntent covers only the rewind path — so a process killed in
        // that window leaves the trailer standing with the journal not succeeded. Reset ONLY the journal:
        // RunReset.Task would route through ScopedReset and REWIND the branch, destroying the very
        // trailer this state is made of.
        RunJournal.LoadOrCreate(new PlanLoader().Load(plan.PlanDir).Plan!).ResetTask("01-fix-manifest");
        Assert.NotEqual(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));

        (int exit, string output) = await InvokeAsync(
            "run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        // NON-VACUITY: the guardrail tests for fixed-marker.txt relative to its cwd, and that file exists
        // ONLY in the plan branch's tree. A pass is therefore possible only if revalidate graded THAT
        // tree; had it graded the operator's checkout — the pre-#456 subject — the guardrail would fail.
        // Carry the CLI's own words into the failure message: each refusal branch prints a DIFFERENT
        // reason, and a bare "expected 0, got 1" cannot tell them apart — which is the #753 shape.
        Assert.True(exit == ExitCodes.Success, $"revalidate exited {exit}. Output:\n{output}");
        Assert.Contains("plan branch's integrated tree", output);
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));
    }

    [Fact]
    public async Task Revalidate_WorktreeMode_Integrated_ButNoWorktreeHoldsTheBranch_MaterializesAndCleansUp()
    {
        // #456's headline ask. The work is provably on the plan branch but NOTHING has that branch
        // checked out — a --fresh teardown, a fresh clone, or the #407 B startup GC having reclaimed an
        // abandoned run's root. There is a correct subject to verify; it just is not materialized yet.
        using var plan = new RevalidatePlan(
            maxParallelism: 3, makeGitRepo: true, commitWorkspace: true,
            actionWritesMarker: true, withFailingSibling: true);

        (int runExit, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, runExit);
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));

        string planName = Path.GetFileName(plan.PlanDir);
        string planBranch = $"guardrails/{planName}";
        var loaded = new PlanLoader().Load(plan.PlanDir).Plan!;

        // The same commit-before-journal crash window as the sibling test above.
        RunJournal.LoadOrCreate(loaded).ResetTask("01-fix-manifest");

        // Reproduce "nothing holds the plan branch" the way the startup GC produces it: reclaim the run's
        // whole worktree root. RemoveWorktreeRoot never deletes a branch, so the integrated work and its
        // trailers survive on guardrails/<plan> — only the checked-out tree goes.
        string worktreeRoot = SchedulerFactory.WorktreeRootFor(loaded);
        GitWorktreeProvider.RemoveWorktreeRoot(plan.PlanDir, worktreeRoot);

        // POSITIVE CONTROLS for the precondition: no worktree holds the branch...
        Assert.Null(GitWorktreeProvider.WorktreeForBranch(plan.PlanDir, planBranch));
        // ...yet the work itself is still there, so refusing would be refusing a verifiable task.
        Assert.True(
            GitWorktreeProvider.ReadPlanBranchTaskHashes(plan.PlanDir, planName).ContainsKey("01-fix-manifest"),
            "reclaiming the worktree root must not remove the integrated work");

        (int exit, string output) = await InvokeAsync(
            "run", plan.PlanDir, "--revalidate-task", "01-fix-manifest");

        // NON-VACUITY, same lever as the sibling test: fixed-marker.txt exists ONLY on the plan branch,
        // never in the checkout, so a pass is possible only if a tree really was materialized at the plan
        // tip and graded there.
        Assert.True(exit == ExitCodes.Success, $"revalidate exited {exit}. Output:\n{output}");
        Assert.Contains("materialized", output);
        Assert.Equal(TaskStatus.Succeeded, StatusOf(plan.PlanDir, "01-fix-manifest"));

        // The temporary tree is this verb's own artifact and must not outlive it: a leaked
        // checkout-sized directory left by a read-only verification verb is exactly the quiet defect
        // this change must not introduce.
        Assert.False(
            Directory.Exists(Path.Combine(worktreeRoot, "_revalidate")),
            "the materialized tree must be torn down after the revalidate");
        Assert.Null(GitWorktreeProvider.WorktreeForBranch(plan.PlanDir, planBranch));
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

        public RevalidatePlan(
            int maxParallelism = 1, bool makeGitRepo = false, bool commitWorkspace = false,
            bool actionWritesMarker = false, bool withFailingSibling = false)
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr-reval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PlanDir);

            if (makeGitRepo)
            {
                // A real git repo so SchedulerFactory.WouldUseWorktreeMode sees worktree mode
                // (maxParallelism > 1 AND a git workspace).
                RunGit("init");

                // The four Windows-safe fixture lessons this repo has been bitten by: line endings must
                // not be translated, signing must not prompt, and a machine-global pre-commit hook must
                // never reach into a throwaway repo (an empty hooksPath inside .git).
                RunGit("config core.autocrlf false");
                RunGit("config commit.gpgsign false");
                Directory.CreateDirectory(Path.Combine(PlanDir, ".git", "no-hooks"));
                RunGit("config core.hooksPath .git/no-hooks");
                RunGit("config user.email fixture@example.invalid");
                RunGit("config user.name Fixture");
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
                  "writeScope": ["action-ran.txt", "fixed-marker.txt"],
                  "dependsOn": []
                }
                """);

            // Action: drop the "I ran" sentinel (relative to cwd = workspace = plan dir). By default it
            // does NOT write the fixed-marker, so the guardrail fails until a human writes it. Under
            // actionWritesMarker the action PRODUCES the marker instead, so the task settles green on its
            // own and the marker becomes part of the work integrated onto the plan branch — which is what
            // lets a worktree-mode test tell the branch's tree apart from the operator's checkout.
            WriteScript(Path.Combine(taskDir, Ps ? "action.ps1" : "action.sh"), (Ps, actionWritesMarker) switch
            {
                (true, false) => "Set-Content -Path 'action-ran.txt' -Value 'ran'\nexit 0\n",
                (true, true) => "Set-Content -Path 'action-ran.txt' -Value 'ran'\n"
                                + "Set-Content -Path 'fixed-marker.txt' -Value 'produced by the action'\nexit 0\n",
                (false, false) => "#!/usr/bin/env bash\necho ran > action-ran.txt\nexit 0\n",
                (false, true) => "#!/usr/bin/env bash\necho ran > action-ran.txt\n"
                                 + "echo produced by the action > fixed-marker.txt\nexit 0\n"
            });

            // Guardrail: pass iff fixed-marker.txt exists in the workspace.
            WriteScript(Path.Combine(taskDir, "guardrails", Ps ? "01-fixed.ps1" : "01-fixed.sh"), Ps
                ? "if (Test-Path 'fixed-marker.txt') { exit 0 } else { Write-Output 'fixed-marker.txt missing'; exit 1 }\n"
                : "#!/usr/bin/env bash\nif [ -f fixed-marker.txt ]; then exit 0; else echo 'fixed-marker.txt missing'; exit 1; fi\n");

            // A sibling that always fails, DEPENDING on 01 so the ordering is deterministic: 01 runs and
            // settles green (integrating its work), then 02 runs and fails. The run therefore ends
            // resumable rather than wholly green, which is what keeps the integration worktree alive.
            if (withFailingSibling)
            {
                string siblingDir = Path.Combine(PlanDir, "tasks", "02-sibling");
                Directory.CreateDirectory(Path.Combine(siblingDir, "guardrails"));
                File.WriteAllText(Path.Combine(siblingDir, "task.json"),
                    """
                    {
                      "description": "Always fails, so the run stays resumable",
                      "writeScope": ["sibling-ran.txt"],
                      "dependsOn": ["01-fix-manifest"]
                    }
                    """);

                WriteScript(Path.Combine(siblingDir, Ps ? "action.ps1" : "action.sh"), Ps
                    ? "Set-Content -Path 'sibling-ran.txt' -Value 'ran'\nexit 0\n"
                    : "#!/usr/bin/env bash\necho ran > sibling-ran.txt\nexit 0\n");

                WriteScript(Path.Combine(siblingDir, "guardrails", Ps ? "01-never.ps1" : "01-never.sh"), Ps
                    ? "Write-Output 'this sibling never passes'; exit 1\n"
                    : "#!/usr/bin/env bash\necho 'this sibling never passes'; exit 1\n");
            }

            // Commit LAST — after every plan file exists. A `git init` with no commits has no HEAD, so
            // `git rev-parse HEAD` fails and worktree mode cannot cut an integration branch at all; the
            // never-integrated test does not need this, but a real worktree-mode run does.
            if (commitWorkspace)
            {
                RunGit("add -A");
                RunGit("commit -m fixture-base");
            }
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
            // git marks loose objects under .git/objects READ-ONLY on Windows, so a plain
            // Directory.Delete throws UnauthorizedAccessException — NOT IOException, which the old
            // catch here covered. SafeDelete strips the attribute first and retries a transient lock;
            // it is the same helper the rest of this repo's git fixtures use. Measured: without it the
            // worktree-mode tests fail in teardown after their assertions have already passed.
            try { SafeDelete.DeleteDirectory(PlanDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort — a leaked temp dir must never fail a green test
            }
        }
    }
}
