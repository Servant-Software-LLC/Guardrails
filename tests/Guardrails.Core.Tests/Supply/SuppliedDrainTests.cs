using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §2 — the boundary drain: copies whatever <c>guardrails supply</c> staged
/// (<see cref="SuppliedStagingTree"/>'s subject) onto the run's base and commits it with the §4
/// provenance trailer. Unlike <see cref="SuppliedStagingTree"/>, which is pure path logic,
/// <see cref="SuppliedDrain"/>'s whole job is committing onto a real base — so these tests drive an
/// ACTUAL git repository rather than faking the seam. A faked git would prove nothing about the thing
/// this class exists to do.
/// <para>
/// TDD red: <see cref="SuppliedDrain.Drain"/> currently throws <see cref="NotImplementedException"/>, so
/// every test below is expected to FAIL against the stub. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedDrainTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly string _planDirectory =
        Path.Combine(Path.GetTempPath(), "gr-supply-drain-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _repo.Dispose();
        SafeDelete.DeleteDirectory(_planDirectory);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Write one file directly at the location <c>guardrails supply</c> would have staged it.</summary>
    private void Stage(string runId, string workspaceRelativePath, string content)
    {
        string staged = Path.Combine(
            _planDirectory, "logs", runId, "supplied",
            workspaceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, content);
    }

    private string SuppliedRoot(string runId) => Path.Combine(_planDirectory, "logs", runId, "supplied");

    // ── Drain ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drain_OnAnEmptyStagingTree_DoesNothingAndMakesNoCommit()
    {
        // §2 step 1, the never-weaker requirement: a run that never called `supply` has no
        // logs/<runId>/supplied/ directory at all, and the drain must be completely inert for it.
        string before = _repo.HeadSha();

        SuppliedDrainResult result = SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", "operator");

        Assert.Equal(before, _repo.HeadSha());
        Assert.Empty(result.CommittedPaths);
        Assert.Equal(0L, result.TotalBytes);
        Assert.Null(result.CommitSha);
    }

    [Fact]
    public void Drain_CopiesEveryStagedFileToItsWorkspacePath()
    {
        // The staged tree lands at the workspace paths §1 says — no separate destination argument.
        Stage("R", "vendor/x.js", "export const ok = true;");
        Stage("R", "docs/nested/notes.md", "# notes");

        SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", "operator");

        Assert.Equal(
            "export const ok = true;",
            File.ReadAllText(Path.Combine(_repo.RepoPath, "vendor", "x.js")));
        Assert.Equal(
            "# notes",
            File.ReadAllText(Path.Combine(_repo.RepoPath, "docs", "nested", "notes.md")));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("overwatcher")]
    [InlineData("task:03-author-tests-drain")]
    public void Drain_CommitsWithTheSuppliedByTrailer(string by)
    {
        // The trailer's VALUE is whatever `by` names — the record's `by` field (§3 auto-resolve names
        // "overwatcher", a §5a agent-callable supply names "task:<folder>"). The trailer must never be
        // the fixed string "Supplied-By-Operator": pinning that constant would make it a false statement
        // on both of those callers.
        Stage("R", "vendor/x.js", "content");

        SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", by);

        string message = _repo.LastCommitMessage();
        Assert.Contains($"Supplied-By: {by}", message);
        Assert.Contains("Guardrails-Run: R", message);
        Assert.DoesNotContain("Supplied-By-Operator", message);
    }

    [Fact]
    public void Drain_DeletesTheStagingTreeAfterCommitting()
    {
        Stage("R", "vendor/x.js", "content");

        SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", "operator");

        Assert.False(Directory.Exists(SuppliedRoot("R")));

        // The tree is harness-owned and drained: a second drain against the same (now-empty) run must
        // find nothing staged and must NOT re-commit the same files.
        string afterFirstDrain = _repo.HeadSha();
        SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", "operator");
        Assert.Equal(afterFirstDrain, _repo.HeadSha());
    }

    [Fact]
    public void Drain_ReturnsTheCommittedPathsAndByteCount()
    {
        // The caller needs these for the §4 provenance record (paths, bytes, commit) and the §2
        // step-3 announcement.
        Stage("R", "vendor/x.js", "12345");   // 5 bytes
        Stage("R", "docs/notes.md", "123");   // 3 bytes

        SuppliedDrainResult result = SuppliedDrain.Drain(_repo.RepoPath, _planDirectory, "R", "operator");

        Assert.Equal(2, result.CommittedPaths.Count);
        Assert.Contains("vendor/x.js", result.CommittedPaths);
        Assert.Contains("docs/notes.md", result.CommittedPaths);
        Assert.Equal(8L, result.TotalBytes);
        Assert.Equal(_repo.HeadSha(), result.CommitSha);
    }

    // ── a real git repository, not a fake of one ─────────────────────────────────────────────────────

    /// <summary>
    /// A throwaway single-use git repository in a temp directory (mirroring the shared idiom already
    /// duplicated across <c>Guardrails.Integration.Tests</c> and <c>ProducerCoverageTests</c> — there is
    /// no shared fixture project this test project references).
    /// <para>
    /// Two Windows behaviours it must handle — this repo has been bitten by both before. Git marks loose
    /// objects under <c>.git/objects</c> READ-ONLY, so a plain recursive delete throws
    /// <see cref="UnauthorizedAccessException"/> unless the attributes are cleared first — handled by
    /// <see cref="SafeDelete.DeleteDirectory"/>. And <c>core.autocrlf</c> is forced OFF so committed
    /// content is byte-stable regardless of the host's global git config, which is what makes
    /// <see cref="Drain_ReturnsTheCommittedPathsAndByteCount"/>'s byte count deterministic. Hooks are
    /// pointed at an empty directory inside <c>.git</c> so a machine-global hook (e.g. a pre-commit
    /// scanner) can never reach in and gate this throwaway repo.
    /// </para>
    /// </summary>
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

        internal string LastCommitMessage() => Git("log", "-1", "--format=%B");

        private string Git(params string[] arguments)
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
