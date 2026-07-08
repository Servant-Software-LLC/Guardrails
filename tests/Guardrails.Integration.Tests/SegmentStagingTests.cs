using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #280 (SSOT §5.3(D)): pins the reconstructable-dependency staging exclusion at the git level
/// (<see cref="SegmentStaging"/>) and at the provider's segment-commit site
/// (<see cref="GitWorktreeProvider.Integrate"/>). The load-bearing behavior these tests nail:
/// <c>node_modules</c> at any depth (plus the two harness scaffolding dirs) is NEVER STAGED, yet an
/// in-scope DELETION is still staged; and the excluded dirs remain ON DISK (stage-exclusion, not
/// worktree deletion — the #255 warm-cache constraint).
/// </summary>
public sealed class SegmentStagingTests
{
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-seg-" + Guid.NewGuid().ToString("N"));
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

        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public void Write(string relativePath, string content)
        {
            string full = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string StagedNameStatus() =>
            Git(RepoPath, "diff", "--cached", "--name-status", "--no-renames", "HEAD");

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
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_root); }
            catch { /* best-effort teardown of a temp dir */ }
        }
    }

    // -------------------------------------------------------------------------
    // The load-bearing (B) git incantation pin.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The single most important assertion for #280: <see cref="SegmentStaging.StageAll"/> must EXCLUDE
    /// <c>node_modules</c> at the repo root AND nested at any depth (plus the two harness scaffolding
    /// dirs), while STILL staging an in-scope add, an in-scope modify, and — crucially — an in-scope
    /// DELETION. A file merely CONTAINING "node_modules" as a name substring is NOT excluded.
    /// </summary>
    [Fact]
    public void StageAll_ExcludesReconstructablesAtAnyDepth_ButStagesInScopeDeletion()
    {
        using var repo = new TempGitRepo();
        // Baseline: two tracked files so a modify and a delete have something to act on.
        repo.Write("src/keep.cs", "// keep base");
        repo.Write("src/gone.cs", "// will be deleted");
        TempGitRepo.Git(repo.RepoPath, "add", ".");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "baseline");

        // The attempt's changes: in-scope add + modify + delete, PLUS reconstructable cruft at
        // root and nested depth, PLUS a substring-only decoy that must stay staged.
        repo.Write("src/new.cs", "// new in-scope file");
        repo.Write("src/keep.cs", "// keep MODIFIED");
        File.Delete(Path.Combine(repo.RepoPath, "src", "gone.cs"));
        repo.Write("src/node_modules_helper.cs", "// substring, NOT a path component — must stage");
        repo.Write("node_modules/foo/index.js", "root dep cruft");
        repo.Write("dsl/validator/node_modules/ajv/dist/ajv.js", "nested dep cruft");
        repo.Write(".guardrails-staging/01-task/scaffold.txt", "staging scaffolding");
        repo.Write(".guardrails-agent-io/01-task/attempt-1/out.json", "agent-io residue");

        SegmentStaging.StageAll(repo.RepoPath);

        string staged = repo.StagedNameStatus().Replace('\\', '/');
        IReadOnlyList<string> lines = staged
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        // In-scope add / modify / DELETE all staged.
        Assert.Contains(lines, l => l == "A\tsrc/new.cs");
        Assert.Contains(lines, l => l == "M\tsrc/keep.cs");
        Assert.Contains(lines, l => l == "D\tsrc/gone.cs");
        // Substring decoy staged (node_modules only matched as a whole path component).
        Assert.Contains(lines, l => l == "A\tsrc/node_modules_helper.cs");

        // NONE of the reconstructable set is staged, at root or nested depth.
        Assert.DoesNotContain("node_modules/foo/index.js", staged);
        Assert.DoesNotContain("dsl/validator/node_modules/ajv/dist/ajv.js", staged);
        Assert.DoesNotContain(".guardrails-staging/", staged);
        Assert.DoesNotContain(".guardrails-agent-io/", staged);
    }

    // -------------------------------------------------------------------------
    // Provider-level: Integrate excludes the dep dir from the commit, keeps it on disk (#255).
    // -------------------------------------------------------------------------

    /// <summary>
    /// #280 tests #1 + #7: a nested <c>node_modules</c> present in the segment worktree (as a guardrail's
    /// <c>npm ci</c> side effect would leave it) must NOT appear in the segment commit tree, while the
    /// in-scope action output DOES — and the <c>node_modules</c> dir must REMAIN ON DISK afterward
    /// (stage-exclusion, not worktree deletion; the #255 warm-cache constraint). This is the RED-BAR
    /// test: against pre-fix <see cref="GitWorktreeProvider.Integrate"/> (plain <c>git add -A</c>) the
    /// committed tree contains <c>node_modules</c> and the first assertion fails.
    /// </summary>
    [Fact]
    public void Integrate_DoesNotCommitGuardrailCreatedNodeModules_ButLeavesItOnDisk()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("dep-plan", "run-280", CancellationToken.None);
        WorktreeHandle seg = provider.CreateSegment("01-vendor", 1, integ, CancellationToken.None);

        // The in-scope action output.
        string inScope = Path.Combine(seg.WorktreePath, "dsl", "src.js");
        Directory.CreateDirectory(Path.GetDirectoryName(inScope)!);
        File.WriteAllText(inScope, "export const ok = true;");

        // The guardrail side effect: a nested node_modules (mirrors the #280 incident's
        // dsl-tools/threagile-validator/node_modules).
        string nestedDep = Path.Combine(seg.WorktreePath, "dsl", "node_modules", "ajv", "dist", "ajv.js");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedDep)!);
        File.WriteAllText(nestedDep, "module.exports = {};");

        provider.Integrate(seg, integ, CancellationToken.None);

        string committed = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", seg.RecordedCommitSha)
            .Replace('\\', '/');

        Assert.Contains("dsl/src.js", committed);
        Assert.DoesNotContain("node_modules", committed);

        // #255: the dir is only excluded from the INDEX/commit — it stays on disk in the worktree.
        Assert.True(File.Exists(nestedDep),
            "node_modules must remain on disk in the segment (stage-exclusion, NOT worktree deletion — #255).");
    }

    /// <summary>
    /// #280 test #8: a <c>.guardrails-agent-io/</c> residue (§9.5) left in the segment by a cleanup
    /// no-op (#266) must never be captured into the segment commit — the harness's own scaffolding is
    /// on the §5.3(D) exclusion set.
    /// </summary>
    [Fact]
    public void Integrate_DoesNotCommitAgentIoResidue()
    {
        using var repo = new TempGitRepo();
        var provider = new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot);
        IntegrationHandle integ = provider.CreateIntegration("io-plan", "run-266", CancellationToken.None);
        WorktreeHandle seg = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);

        File.WriteAllText(Path.Combine(seg.WorktreePath, "real.txt"), "real output");

        string residue = Path.Combine(
            seg.WorktreePath, ".guardrails-agent-io", "01-task", "attempt-1", "state-out.json");
        Directory.CreateDirectory(Path.GetDirectoryName(residue)!);
        File.WriteAllText(residue, "{\"leftover\":true}");

        provider.Integrate(seg, integ, CancellationToken.None);

        string committed = TempGitRepo.Git(repo.RepoPath, "ls-tree", "-r", "--name-only", seg.RecordedCommitSha)
            .Replace('\\', '/');

        Assert.Contains("real.txt", committed);
        Assert.DoesNotContain(".guardrails-agent-io", committed);
    }
}
