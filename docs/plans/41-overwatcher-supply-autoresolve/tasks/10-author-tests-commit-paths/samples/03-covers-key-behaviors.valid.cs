using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// The CORRECT shape: the class carries the plan trait, it exercises CommitPaths, and the pathspec
/// tests really do put something ELSE in the index (unrelated-staged.txt) before asserting that only
/// the named path was committed.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedDrainTests : IDisposable
{
    private readonly TempGitRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void CommitPaths_CommitsOnlyItsPathspec_WhenAnUnrelatedFileIsStaged()
    {
        _repo.WriteFile("unrelated-staged.txt", "an operator's half-finished work");
        _repo.Git("add", "--", "unrelated-staged.txt");
        _repo.WriteFile("vendor/x.js", "export const ok = true;");

        SuppliedDrainResult result = SuppliedDrain.CommitPaths(
            _repo.RepoPath, "R", "overwatcher", ["vendor/x.js"]);

        Assert.Equal(["vendor/x.js"], _repo.FilesInCommit(result.CommitSha!));
        Assert.Contains("unrelated-staged.txt", _repo.Status());
    }

    [Fact]
    public void CommitPaths_OnAnEmptyPathList_MakesNoCommit()
    {
        string before = _repo.HeadSha();

        SuppliedDrainResult result = SuppliedDrain.CommitPaths(_repo.RepoPath, "R", "operator", []);

        Assert.Equal(before, _repo.HeadSha());
        Assert.Null(result.CommitSha);
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; } =
            Path.Combine(Path.GetTempPath(), "gr-supply-drain-repo-" + Guid.NewGuid().ToString("N"));

        internal TempGitRepo()
        {
            Directory.CreateDirectory(RepoPath);
            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# fixture repo");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        internal string HeadSha() => Git("rev-parse", "HEAD").Trim();

        internal string Status() => Git("status", "--porcelain");

        internal string[] FilesInCommit(string sha) =>
            Git("show", "--name-only", "--format=", sha)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Write a file, recreating its parent if git pruned it (Git-for-Windows empty-dir prune).</summary>
        internal void WriteFile(string relativePath, string content)
        {
            string full = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        internal string Git(params string[] arguments)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} (in {RepoPath}) exited {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose() => SafeDelete.DeleteDirectory(RepoPath);
    }
}
