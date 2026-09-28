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

    [Fact]
    public void ANonConflictMergeFailure_CarriesGitsExitCodeAndOutput()
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

        // The segment changes shared.txt.
        File.WriteAllText(Path.Combine(seg.WorktreePath, "shared.txt"), "segment\n");

        // The integration branch moves on (so the merge is not a fast-forward) and its worktree holds an
        // uncommitted edit to the same file, which git refuses to overwrite: a failure with no MERGE_HEAD.
        string integPath = integ.IntegrationWorktreePath;
        File.WriteAllText(Path.Combine(integPath, "other.txt"), "sibling\n");
        Git(integPath, "add", "other.txt");
        Git(integPath, "commit", "-m", "sibling landed");
        File.WriteAllText(Path.Combine(integPath, "shared.txt"), "uncommitted local edit\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => provider.Integrate(seg, integ, CancellationToken.None));

        Assert.Contains("failed unexpectedly: it exited ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("left no MERGE_HEAD", ex.Message, StringComparison.Ordinal);

        // git's own explanation, which is what the operator needed and never got.
        Assert.Contains("git merge stderr:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("shared.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overwritten by merge", ex.Message, StringComparison.Ordinal);

        // The sibling probes are no longer silent either.
        Assert.Contains("the MERGE_HEAD probe (exit ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the --ff-only attempt before it (exit ", ex.Message, StringComparison.Ordinal);
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
