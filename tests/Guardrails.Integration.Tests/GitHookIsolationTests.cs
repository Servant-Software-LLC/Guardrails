using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Regression tests for issues #149/#150 — a global user git hook (the incident was GitGuardian's
/// <c>ggshield</c> <c>pre-commit</c> hook, which fired offline and crashed the run with an unhandled
/// exception).
///
/// <para>#149 — hook isolation: the harness's INTERNAL plumbing commits (machine bookkeeping in
/// throwaway worktrees) must BYPASS user git hooks (<c>--no-verify</c>), while the USER-FACING
/// merge-back into the user's real branch KEEPS them. A failing <c>pre-commit</c> hook installed in
/// the repo applies to every worktree (hooks live in the shared common <c>.git/hooks</c>), so these
/// tests install one that <c>exit 1</c> and assert the internal commits STILL succeed.</para>
///
/// <para>#150 — graceful halt: when the user's hook rejects the user-facing merge commit, the harness
/// runs <c>git merge --abort</c> (user branch left CLEAN), leaves the plan branch intact, and returns
/// <see cref="MergeOnSuccessResult.HookRejected"/> carrying the hook's stderr — never an unhandled
/// crash.</para>
/// </summary>
public sealed class GitHookIsolationTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — proven-safe Windows teardown (strips read-only loose objects before delete).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-hook-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# hook isolation test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public bool BranchExists(string branch)
        {
            try { Git(RepoPath, "rev-parse", "--verify", $"refs/heads/{branch}"); return true; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>
        /// Install a <c>pre-commit</c> hook that exits 1 (printing <paramref name="message"/> to
        /// stderr) into this repo's shared common hooks dir, so it fires on commits in ANY worktree —
        /// reproducing the incident (an offline GitGuardian hook). Cross-platform: git runs the hook
        /// via its bundled sh on Windows too, so a <c>#!/bin/sh</c> script works everywhere.
        /// </summary>
        public void InstallFailingPreCommitHook(string message)
        {
            string hooksDir = Path.Combine(RepoPath, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            string hookPath = Path.Combine(hooksDir, "pre-commit");
            File.WriteAllText(hookPath, "#!/bin/sh\necho '" + message + "' 1>&2\nexit 1\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(hookPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
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
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #149 — Integrate's INTERNAL commit bypasses a failing user pre-commit hook.
    // This is the DIRECT regression for the incident: the state-only marker commit that failed.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With a failing <c>pre-commit</c> hook installed, <see cref="GitWorktreeProvider.Integrate"/>'s
    /// internal segment commit (the <c>--allow-empty</c> state marker that died in the incident) STILL
    /// succeeds — it runs with <c>--no-verify</c>, so the hook never gates harness bookkeeping. The
    /// task's work fast-forwards onto the plan branch as normal.
    /// </summary>
    [Fact]
    public void Integrate_InternalCommit_BypassesFailingPreCommitHook()
    {
        using var repo = new TempGitRepo();
        repo.InstallFailingPreCommitHook("ggshield: offline, refusing commit");

        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("hook-plan", "run-149a", CancellationToken.None);

        WorktreeHandle seg = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);
        File.WriteAllText(Path.Combine(seg.WorktreePath, "out.txt"), "task output");

        // Would THROW (git commit exits non-zero on the failing hook) if Integrate omitted --no-verify.
        IntegrationResult result = provider.Integrate(seg, integ, CancellationToken.None);

        Assert.Equal(IntegrationResult.FastForward, result);
        Assert.False(string.IsNullOrEmpty(seg.RecordedCommitSha));

        // The work is on the plan branch: its log carries the task's internal trailer.
        string log = TempGitRepo.Git(integ.IntegrationWorktreePath, "log", "--format=%B");
        Assert.Contains("Guardrails-Task: 01-task", log);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #149 — CommitStagedMerge's INTERNAL union merge commit bypasses the failing hook.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A non-FF union forces <see cref="GitWorktreeProvider.Integrate"/> down the <c>merge --no-commit</c>
    /// path and <see cref="GitWorktreeProvider.CommitStagedMerge"/> to create the merge commit. With a
    /// failing <c>pre-commit</c> hook installed, that INTERNAL merge commit STILL succeeds
    /// (<c>--no-verify</c>) and stamps the task trailer onto the plan branch.
    /// </summary>
    [Fact]
    public void CommitStagedMerge_InternalUnionCommit_BypassesFailingPreCommitHook()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("union-plan", "run-149b", CancellationToken.None);

        // Segment forks off the ORIGINAL plan tip and edits its own file.
        WorktreeHandle seg = provider.CreateSegment("01-union", 1, integ, CancellationToken.None);
        File.WriteAllText(Path.Combine(seg.WorktreePath, "seg.txt"), "seg work");

        // Now advance the plan branch on a DIFFERENT file AFTER the segment was forked, so the segment
        // diverges from the plan tip — Integrate cannot fast-forward and must do a real union merge.
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "integ.txt"), "integ advance");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "add", "-A");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "commit", "-m", "advance plan branch");

        // Install the failing hook AFTER the setup commits above so only the harness paths face it.
        repo.InstallFailingPreCommitHook("ggshield: offline, refusing commit");

        IntegrationResult result = provider.Integrate(seg, integ, CancellationToken.None);
        Assert.Equal(IntegrationResult.Merged, result);

        // Would THROW if CommitStagedMerge omitted --no-verify (the hook fails the merge commit).
        string mergeSha = provider.CommitStagedMerge(integ, "01-union", CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(mergeSha));
        string log = TempGitRepo.Git(integ.IntegrationWorktreePath, "log", "--format=%B");
        Assert.Contains("Guardrails-Task: 01-union", log);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #150 — the USER-FACING merge KEEPS hooks; a hook rejection is a graceful HookRejected halt.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issue #150 (part 2): the user-facing merge-back KEEPS the user's git hooks. In a NON-FF merge
    /// scenario (the user's branch advanced), a failing <c>pre-commit</c> hook rejects the merge
    /// commit. The harness must:
    /// <list type="bullet">
    ///   <item>return <see cref="MergeOnSuccessResult.HookRejected"/> (NOT throw),</item>
    ///   <item>carry the hook's stderr in <see cref="GitWorktreeProvider.LastMergeOnSuccessDetail"/>,</item>
    ///   <item>leave the user's branch HEAD UNCHANGED (merge aborted / clean), and</item>
    ///   <item>leave the plan branch intact.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void MergePlanBranchIntoUserBranch_NonFf_HookRejection_IsGracefulHalt_UserBranchClean()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        string originalBranch = repo.CurrentBranch();

        // Plan branch gets a commit on the plan side.
        IntegrationHandle integ = provider.CreateIntegration("merge-plan", "run-150", CancellationToken.None);
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "plan.txt"), "plan work");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "add", "-A");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "commit", "--no-verify", "-m", "plan commit");

        // Advance the USER's branch on a DIFFERENT file so the merge is NON-FF but conflict-free —
        // it reaches the real merge commit, which the hook then rejects.
        File.WriteAllText(Path.Combine(repo.RepoPath, "user.txt"), "user work");
        TempGitRepo.Git(repo.RepoPath, "add", "-A");
        TempGitRepo.Git(repo.RepoPath, "commit", "--no-verify", "-m", "user advance");
        string userHeadBeforeMerge = repo.HeadSha();

        // Now install the failing hook: it must gate the USER-FACING merge commit (no --no-verify there).
        const string hookMessage = "ggshield: secret detected, blocking merge";
        repo.InstallFailingPreCommitHook(hookMessage);

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        // Graceful halt — NOT a throw, NOT a silent success.
        Assert.Equal(MergeOnSuccessResult.HookRejected, result);

        // The hook's stderr is surfaced for the CLI to show the user.
        Assert.NotNull(provider.LastMergeOnSuccessDetail);
        Assert.Contains(hookMessage, provider.LastMergeOnSuccessDetail!);

        // The user's branch is CLEAN at its pre-merge HEAD (merge --abort restored it) ...
        Assert.Equal(userHeadBeforeMerge, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
        // ... with no in-progress merge state lingering.
        Assert.Equal(0, TempGitRepo.Git(repo.RepoPath, "status", "--porcelain").Trim().Length);

        // The verified plan branch is intact for a manual merge.
        Assert.True(repo.BranchExists("guardrails/merge-plan"));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #149 — the user-facing merge KEEPS hooks: a NON-FF merge with a PASSING hook still commits.
    // (Proves the merge path actually invokes the hook — the complement to the rejection test.)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The user-facing non-FF merge KEEPS hooks (issue #149): with a <c>pre-commit</c> hook that
    /// PASSES (exit 0), the merge commit is created and the user's branch advances — confirming the
    /// merge path runs the hook (rather than silently bypassing it) and proceeds when the hook is happy.
    /// </summary>
    [Fact]
    public void MergePlanBranchIntoUserBranch_NonFf_PassingHook_Merges()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);

        IntegrationHandle integ = provider.CreateIntegration("merge-plan-ok", "run-149c", CancellationToken.None);
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "plan.txt"), "plan work");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "add", "-A");
        TempGitRepo.Git(integ.IntegrationWorktreePath, "commit", "--no-verify", "-m", "plan commit");

        File.WriteAllText(Path.Combine(repo.RepoPath, "user.txt"), "user work");
        TempGitRepo.Git(repo.RepoPath, "add", "-A");
        TempGitRepo.Git(repo.RepoPath, "commit", "--no-verify", "-m", "user advance");

        // Passing pre-commit hook.
        string hooksDir = Path.Combine(repo.RepoPath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        string hookPath = Path.Combine(hooksDir, "pre-commit");
        File.WriteAllText(hookPath, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        MergeOnSuccessResult result = provider.MergePlanBranchIntoUserBranch(integ, CancellationToken.None);

        Assert.Equal(MergeOnSuccessResult.Merged, result);
        Assert.Null(provider.LastMergeOnSuccessDetail);
        // A real merge commit now exists on the user's branch.
        Assert.NotEmpty(TempGitRepo.Git(repo.RepoPath, "log", "--merges", "--format=%H").Trim());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #149 — END-TO-END through the real `run` command: a failing user pre-commit hook no longer
    // crashes the run. This is the full-pipeline regression for the incident (the run died on the
    // INTERNAL state-marker commit). With --merge-on-success and a clean FF delivery, the run goes
    // GREEN and exits 0 despite the hook installed in the repo, because every internal commit uses
    // --no-verify and the FF merge-back creates no commit (so the hook never fires there either).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cli_Run_WithFailingPreCommitHook_StillGoesGreen_InternalCommitsBypassHook()
    {
        using var repo = new TempGitRepo();
        repo.InstallFailingPreCommitHook("ggshield: offline, refusing commit");

        string initialHead = repo.HeadSha();
        string planDir = CreateSingleTaskPlan(repo.RepoPath);

        int exitCode = await InvokeCliAsync(
            "run", planDir, "--merge-on-success", "--no-ui", "--no-log-server");

        // The run completed cleanly (exit 0) — the incident's unhandled crash is gone, and the
        // internal commits succeeded under the failing hook.
        Assert.Equal(ExitCodes.Success, exitCode);
        // FF delivery advanced the user's branch (the work landed).
        Assert.NotEqual(initialHead, repo.HeadSha());
    }

    /// <summary>
    /// Build a single-task plan inside <paramref name="repoPath"/> at <c>&lt;repoPath&gt;/plan/</c>
    /// with <c>workspace: ".."</c> and <c>maxParallelism: 2</c> (activates worktree mode). The task
    /// writes a file (a real working-tree change so Integrate makes a non-empty commit) and exits 0.
    /// </summary>
    private static string CreateSingleTaskPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

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

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{"description": "hook isolation task", "dependsOn": []}""");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"),
                "New-Item -Path \"$env:GUARDRAILS_WORKSPACE\\out.txt\" -Force -Value 'work' | Out-Null\nexit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string ap = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(ap,
                "#!/usr/bin/env bash\nprintf 'work' > \"$GUARDRAILS_WORKSPACE/out.txt\"\nexit 0\n");
            File.SetUnixFileMode(ap,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string gp = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(gp, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(gp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return planDir;
    }

    /// <summary>
    /// Drive the real <c>run</c> command pipeline. Output goes to a discarded
    /// <see cref="StringConsoleIo"/> so nothing touches the process-global console (parallel-safe).
    /// </summary>
    private static async Task<int> InvokeCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("hook isolation cli test root");
        root.Add(RunCommand.Create(io));
        return await root.Parse(args).InvokeAsync();
    }
}
