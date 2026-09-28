using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #750: when the non-fast-forward merge in <see cref="GitWorktreeProvider.Integrate"/> failed without
/// leaving a MERGE_HEAD (so: not a conflict), the harness threw "failed unexpectedly" and discarded everything git
/// had said. The message is the abort headline and the exception is abort.log's detail, so both were blind.
/// </summary>
public sealed class IntegrateGitFailureDetailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-750-" + Guid.NewGuid().ToString("N"));

    /// <summary>Repo + integration + a segment that changes <c>shared.txt</c>, with the integration branch moved on.</summary>
    private (GitWorktreeProvider Provider, IntegrationHandle Integ, WorktreeHandle Seg) Diverged(string segmentFile, string segmentContent)
    {
        string repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init");
        Git(repo, "config", "user.email", "test@guardrails.local");
        Git(repo, "config", "user.name", "Guardrails Test");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "base\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-m", "base");

        var provider = new GitWorktreeProvider(repo, Path.Combine(_root, "worktrees"));
        IntegrationHandle integ = provider.CreateIntegration("p750", "run-750", CancellationToken.None);
        WorktreeHandle seg = provider.CreateSegment("01-task", 1, integ, CancellationToken.None);
        File.WriteAllText(Path.Combine(seg.WorktreePath, segmentFile), segmentContent);

        // The integration branch moves on, so the segment's merge is not a fast-forward.
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "other.txt"), "sibling\n");
        Git(integ.IntegrationWorktreePath, "add", "other.txt");
        Git(integ.IntegrationWorktreePath, "commit", "-m", "sibling landed");
        return (provider, integ, seg);
    }

    [Fact]
    public void AMergeRefusedOnce_IsRetriedFromTheCleanPreMergeHead_AndSucceeds()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ, WorktreeHandle seg) = Diverged("shared.txt", "segment\n");

        // A stray uncommitted edit in the integration worktree: git refuses to overwrite it (no MERGE_HEAD, so not a
        // conflict). The reset to the pre-merge head clears it, which is exactly what a transient state looks like.
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "shared.txt"), "stray local edit\n");

        Assert.Equal(IntegrationResult.Merged, provider.Integrate(seg, integ, CancellationToken.None));
        Assert.Equal(1, provider.MergeRetries);
    }

    [Fact]
    public void AMergeRefusedTwice_ThrowsWithGitsWords_AndLeavesTheIntegrationWorktreeAtItsPreMergeHead()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ, WorktreeHandle seg) = Diverged("added.txt", "segment adds this\n");
        string preMergeHead = Git(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim();

        // An UNTRACKED file where the segment adds one: git refuses to overwrite it, and a hard reset does not remove
        // untracked files, so the retry is refused the same way. A persistent, non-conflict failure.
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "added.txt"), "untracked in the way\n");

        IntegrationMergeFailedException ex = Assert.Throws<IntegrationMergeFailedException>(
            () => provider.Integrate(seg, integ, CancellationToken.None));

        Assert.Equal(1, provider.MergeRetries);
        Assert.Contains("failed twice without a conflict", ex.Message, StringComparison.Ordinal);
        Assert.Contains("added.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("would be overwritten by merge", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the retry stderr:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("git merge stderr:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the MERGE_HEAD probe (exit ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the --ff-only attempt before it (exit ", ex.Message, StringComparison.Ordinal);

        // Left at the pre-merge head with no merge in progress, for the tasks integrating after this one.
        Assert.Equal(preMergeHead, Git(integ.IntegrationWorktreePath, "rev-parse", "HEAD").Trim());
        Assert.Throws<InvalidOperationException>(
            () => Git(integ.IntegrationWorktreePath, "rev-parse", "-q", "--verify", "MERGE_HEAD"));
    }

    [Fact]
    public void ARealConflict_StillTakesTheConflictPath_WithoutARetry()
    {
        (GitWorktreeProvider provider, IntegrationHandle integ, WorktreeHandle seg) = Diverged("shared.txt", "segment side\n");
        File.WriteAllText(Path.Combine(integ.IntegrationWorktreePath, "shared.txt"), "integration side\n");
        Git(integ.IntegrationWorktreePath, "commit", "-am", "integration changes the same line");

        Assert.Equal(IntegrationResult.Conflict, provider.Integrate(seg, integ, CancellationToken.None));
        Assert.Equal(0, provider.MergeRetries);
    }

    [Fact]
    public void GitOutputDetail_MarksEmptyStreams_AndCapsLongOnes()
    {
        string detail = GitWorktreeProvider.GitOutputDetail("git merge", stdout: "", stderr: new string('x', 5000));

        Assert.Contains("git merge stdout: (none)", detail, StringComparison.Ordinal);
        Assert.Contains("(1000 more characters)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 4001), detail, StringComparison.Ordinal);
    }

    private static string Git(string workingDir, params string[] args)
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
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            SafeDelete.DeleteDirectory(_root);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
