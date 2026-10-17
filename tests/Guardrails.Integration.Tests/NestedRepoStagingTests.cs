using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #827: an untracked, non-ignored nested git repository with NO commit (a fresh <c>git init</c>) makes a plain
/// <c>git add -A</c> fail ("does not have a commit checked out"), which took down the serial write-scope snapshot and
/// worktree-mode staging. Real git on temp repos: every harness staging site now leaves such a directory out, REPORTS
/// it, and never reads its files as an out-of-scope write; a nested repo WITH a commit still stages as a gitlink.
/// </summary>
public sealed class NestedRepoStagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-nest-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# Test repo");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string Dir(string relative) => Path.Combine(RepoPath, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relativePath, string content)
        {
            string full = Dir(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>A nested repo under <paramref name="relative"/>: <c>git init</c>, one file, optionally one commit.</summary>
        public void NestedRepo(string relative, bool withCommit, string root = "")
        {
            string path = root.Length == 0 ? Dir(relative) : Path.Combine(root, relative);
            Directory.CreateDirectory(path);
            Git(path, "init");
            Git(path, "config", "user.email", "n@guardrails.local");
            Git(path, "config", "user.name", "Nested");
            File.WriteAllText(Path.Combine(path, "inner.txt"), "inner");
            if (withCommit)
            {
                Git(path, "add", ".");
                Git(path, "commit", "-m", "inner commit");
            }
        }

        public static (int Exit, string Stdout, string Stderr) TryGit(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi)!;
            Task<string> err = proc.StandardError.ReadToEndAsync();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, err.GetAwaiter().GetResult());
        }

        public static string Git(string workingDir, params string[] args)
        {
            (int exit, string stdout, string stderr) = TryGit(workingDir, args);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {exit}: {stderr}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown of a temp dir */ }
        }
    }

    [Fact]
    public void NegativeControl_PlainAddAll_FailsOnACommitlessNestedRepo()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);

        (int exit, _, string stderr) = TempGitRepo.TryGit(repo.RepoPath, "add", "-A");

        Assert.NotEqual(0, exit);
        Assert.Contains("does not have a commit checked out", stderr);
    }

    [Fact]
    public void FindCommitlessNestedRepos_ReturnsOnlyRepoWithoutHead_IncludingSpacesAndNonAscii()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);
        repo.NestedRepo("vendor/has commit", withCommit: true);
        repo.NestedRepo("tools/café sub/fresh one", withCommit: false); // inside an untracked directory
        repo.Write("src/plain.cs", "// plain untracked file");

        IReadOnlyList<string> found = SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, cancellationToken: Ct);

        Assert.Equal(
            ["tools/café sub/fresh one", "vendor/new"],
            found.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void FindCommitlessNestedRepos_OnANonGitDirectory_FailsToTheOldBehaviorAndExcludesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-nest-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(dir, cancellationToken: Ct));
        }
        finally
        {
            SafeDelete.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void StageAllArguments_ExcludeEachSkippedRepoByLiteralPath()
    {
        IReadOnlyList<string> args = SegmentStaging.StageAllArguments(["vendor/new", "a b/c"]);

        Assert.Contains(":(exclude,literal)vendor/new", args);
        Assert.Contains(":(exclude,literal)a b/c", args);
        Assert.DoesNotContain(args, a => a.Contains("literal") && a.EndsWith('/'));
    }

    [Fact]
    public void StageAll_SkipsAndReportsACommitlessRepo_ButStagesEverythingElseAndAGitlinkForACommittedRepo()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);
        repo.NestedRepo("vendor/old", withCommit: true);
        repo.Write("src/real.cs", "// real");

        IReadOnlyList<string> skipped = SegmentStaging.StageAll(repo.RepoPath, cancellationToken: Ct);

        Assert.Equal(["vendor/new"], skipped);
        string staged = TempGitRepo.Git(repo.RepoPath, "ls-files", "-s").Replace('\\', '/');
        Assert.Contains("src/real.cs", staged);
        Assert.Matches(@"160000 [0-9a-f]{40} 0\tvendor/old", staged); // gitlink, exactly as before
        Assert.DoesNotContain("vendor/new", staged);
    }

    [Fact]
    public void StageAll_WithNoNestedRepo_ReportsNothing()
    {
        using var repo = new TempGitRepo();
        repo.Write("src/real.cs", "// real");

        Assert.Empty(SegmentStaging.StageAll(repo.RepoPath, cancellationToken: Ct));
    }

    [Fact]
    public void SerialSnapshot_WithAFreshNestedRepo_Succeeds_TheCheckRuns_AndTheRepoIsNeitherAnOffenseNorADiff()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false); // exists BEFORE the attempt, as in the issue

        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException($"snapshot failed: {why}");
        Assert.Null(why);
        Assert.Equal(["vendor/new"], snapshot.SkippedNestedRepos);

        // The agent edits its own repo's file and writes an in-scope file; the nested repo's bytes change too.
        File.WriteAllText(repo.Dir("vendor/new/inner.txt"), "edited by the attempt");
        repo.Write("src/ok.cs", "// in scope");

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);

        Assert.True(check.Passed, string.Join(", ", check.OffendingPaths.Select(o => o.Path)));
        Assert.DoesNotContain(check.OffendingPaths, o => o.Status == '?'); // the check RAN, not "not run"
        Assert.Equal(["src/ok.cs"], check.InScopePaths);
        Assert.Equal(["vendor/new"], check.SkippedNestedRepos);
    }

    [Fact]
    public void SerialSnapshot_WithAFreshNestedRepo_StillFlagsARealOutOfScopeWrite()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException($"snapshot failed: {why}");

        repo.Write("docs/oops.md", "out of scope");

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);

        Assert.False(check.Passed);
        Assert.Equal(["docs/oops.md"], check.OffendingPaths.Select(o => o.Path).ToArray());
    }

    [Fact]
    public void SerialSnapshot_WhenTheAttemptCreatesAFreshNestedRepoInScope_ItIsAllowed_AndReported()
    {
        using var repo = new TempGitRepo();
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException($"snapshot failed: {why}");
        Assert.Empty(snapshot.SkippedNestedRepos);

        repo.NestedRepo("vendor/new", withCommit: false); // created DURING the attempt

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["vendor/**"], Ct);

        Assert.True(check.Passed, string.Join(", ", check.OffendingPaths.Select(o => o.Path)));
        Assert.Equal(["vendor/new"], check.InScopePaths);
        Assert.Equal(["vendor/new"], check.SkippedNestedRepos);
    }

    [Fact]
    public void SerialSnapshot_WhenTheAttemptCreatesAFreshNestedRepoOutOfScope_ItIsAnOffense_NotABypass()
    {
        using var repo = new TempGitRepo();
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException($"snapshot failed: {why}");

        repo.NestedRepo("vendor/new", withCommit: false); // created DURING the attempt, outside src/**

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);

        Assert.False(check.Passed);
        WriteScopeOffense offense = Assert.Single(check.OffendingPaths);
        Assert.Equal("vendor/new", offense.Path);
        Assert.Equal(WriteScopeOffense.EmbeddedRepoStatus, offense.Status);

        // It is not in the index, so the revert must neither throw nor delete the directory.
        WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, Ct);
        Assert.True(File.Exists(repo.Dir("vendor/new/inner.txt")));
    }

    [Fact]
    public void WorktreeBase_AFreshNestedRepoOutOfScope_IsAnOffense()
    {
        using var repo = new TempGitRepo();
        string taskBase = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();
        repo.NestedRepo("vendor/new", withCommit: false);

        WriteScopeCheckResult check = WriteScopeCheck.Check(repo.RepoPath, taskBase, ["src/**"]);

        Assert.Equal(["vendor/new"], check.OffendingPaths.Select(o => o.Path).ToArray());
    }

    [Fact]
    public void SerialSnapshot_WithACommittedNestedRepo_BehavesAsBefore_AGitlinkAppearsAsAnAddedPath()
    {
        using var repo = new TempGitRepo();
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException($"snapshot failed: {why}");

        repo.NestedRepo("vendor/old", withCommit: true);

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);

        Assert.Empty(check.SkippedNestedRepos);
        Assert.Equal(["vendor/old"], check.OffendingPaths.Select(o => o.Path).ToArray());
    }

    [Fact]
    public void Integrate_WithAFreshNestedRepoInTheSegment_Succeeds_LeavesItOutOfTheCommit_AndRecordsIt()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("nest-plan", "run-827", Ct);
        WorktreeHandle seg = provider.CreateSegment("01-task", 1, integ, Ct);

        File.WriteAllText(Path.Combine(seg.WorktreePath, "real.txt"), "real output");
        repo.NestedRepo("vendor/new", withCommit: false, root: seg.WorktreePath);

        IntegrationResult result = provider.Integrate(seg, integ, Ct);

        Assert.Equal(IntegrationResult.FastForward, result);
        string committed = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", seg.RecordedCommitSha)
            .Replace('\\', '/');
        Assert.Contains("real.txt", committed);
        Assert.DoesNotContain("vendor/new", committed);
        Assert.Equal(["vendor/new"], seg.SkippedNestedRepos);
        Assert.True(File.Exists(Path.Combine(seg.WorktreePath, "vendor", "new", "inner.txt")),
            "the skipped repo stays on disk (stage-exclusion, not deletion)");
    }

    [Fact]
    public void Integrate_WithACommittedNestedRepoInTheSegment_StillCommitsAGitlink()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("nest-plan2", "run-827b", Ct);
        WorktreeHandle seg = provider.CreateSegment("01-task", 1, integ, Ct);

        repo.NestedRepo("vendor/old", withCommit: true, root: seg.WorktreePath);

        provider.Integrate(seg, integ, Ct);

        string tree = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", seg.RecordedCommitSha).Replace('\\', '/');
        Assert.Matches(@"160000 commit [0-9a-f]{40}\tvendor/old", tree);
        Assert.Empty(seg.SkippedNestedRepos);
    }

    [Fact]
    public void PreserveAttemptToRef_WithAFreshNestedRepo_Succeeds()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);
        repo.Write("src/wip.cs", "// work in progress");

        GitWorktreeProvider.PreserveAttemptToRef(repo.RepoPath, "refs/guardrails/salvage/test-827/attempt-1");

        string tree = TempGitRepo.Git(
            repo.RepoPath, "ls-tree", "-r", "--name-only", "refs/guardrails/salvage/test-827/attempt-1").Replace('\\', '/');
        Assert.Contains("src/wip.cs", tree);
        Assert.DoesNotContain("vendor/new", tree);
    }

    [Fact]
    public void FindCommitlessNestedRepos_FailsClosed_OnlyAnExitOfExactlyOneExcludes()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("vendor/new", withCommit: false);

        Assert.Equal(["vendor/new"], SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, null, _ => 1, Ct));
        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, null, _ => 128, Ct)); // unsafe owner / corrupt
        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, null, _ => 0, Ct));   // has a HEAD
        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(
            repo.RepoPath, null, _ => throw new InvalidOperationException("timed out"), Ct));      // timeout / spawn failure
    }

    [Fact]
    public void FindCommitlessNestedRepos_NeverTreatsAnOrdinaryUntrackedDirectoryAsARepo()
    {
        using var repo = new TempGitRepo();
        repo.Write("plain/a.txt", "a");
        repo.Write("plain/deep/b.txt", "b");

        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, cancellationToken: Ct));
    }

    [Fact]
    public void FindCommitlessNestedRepos_FindsARepoUnderATrackedDirectory()
    {
        using var repo = new TempGitRepo();
        repo.Write("tracked/keep.txt", "k");
        TempGitRepo.Git(repo.RepoPath, "add", ".");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "tracked dir");
        repo.NestedRepo("tracked/inner repo", withCommit: false);

        Assert.Equal(["tracked/inner repo"], SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, cancellationToken: Ct));
        Assert.Equal(["tracked/inner repo"], SegmentStaging.StageAll(repo.RepoPath, cancellationToken: Ct));
    }

    [Fact]
    public void FindCommitlessNestedRepos_NeverListsAnIgnoredRepo_AndStageAllLeavesItAlone()
    {
        using var repo = new TempGitRepo();
        repo.Write(".gitignore", "ignored-repo/\n");
        TempGitRepo.Git(repo.RepoPath, "add", ".gitignore");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "ignore");
        repo.NestedRepo("ignored-repo", withCommit: false);

        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, cancellationToken: Ct));
        Assert.Empty(SegmentStaging.StageAll(repo.RepoPath, cancellationToken: Ct));
    }

    [Fact]
    public void FindCommitlessNestedRepos_DoesNotWalkReconstructableDirs()
    {
        using var repo = new TempGitRepo();
        repo.NestedRepo("node_modules/pkg", withCommit: false); // inside the staging exclusion set

        Assert.Empty(SegmentStaging.FindCommitlessNestedRepos(repo.RepoPath, cancellationToken: Ct));
        Assert.Empty(SegmentStaging.StageAll(repo.RepoPath, cancellationToken: Ct)); // and the add still succeeds
    }

    [Fact]
    public void WriteNestedReposSkippedNote_NamesEveryRepo_AndIsANoOpWhenNone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr-nest-note-" + Guid.NewGuid().ToString("N"));
        try
        {
            AttemptArtifacts.WriteNestedReposSkippedNote(dir, []);
            Assert.False(Directory.Exists(dir));

            AttemptArtifacts.WriteNestedReposSkippedNote(dir, ["vendor/new", "a b/c"]);

            string text = File.ReadAllText(Path.Combine(dir, "write-scope-check.log"));
            Assert.Contains("#827", text);
            Assert.Contains("vendor/new", text);
            Assert.Contains("a b/c", text);
        }
        finally
        {
            SafeDelete.DeleteDirectory(dir);
        }
    }
}
