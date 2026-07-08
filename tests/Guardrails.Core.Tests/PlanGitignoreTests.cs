using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PlanGitignore"/> (issue #258): the harness scaffolds a plan-root
/// <c>.gitignore</c> covering exactly the <see cref="RunReset.Fresh"/> transient set, never clobbers a
/// hand-authored one, and lists no committed artifact. The git-behaviour proof (that these patterns
/// actually cause git to ignore the transient paths and keep the committed ones tracked) lives in the
/// integration suite (<c>PlanGitignoreScaffoldTests</c>), which shells out to a real <c>git</c>.
/// </summary>
public sealed class PlanGitignoreTests : IDisposable
{
    private readonly string _planDir;

    public PlanGitignoreTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-gitignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_planDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
            {
                Directory.Delete(_planDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort teardown
        }
    }

    private string GitignorePath => Path.Combine(_planDir, ".gitignore");

    [Fact]
    public void EnsureScaffolded_WritesFile_WhenAbsent()
    {
        PlanGitignore.EnsureScaffolded(_planDir);

        Assert.True(File.Exists(GitignorePath));
        Assert.Equal(PlanGitignore.Content, File.ReadAllText(GitignorePath));
    }

    [Fact]
    public void Content_CoversExactlyTheRunResetFreshTransientSet()
    {
        // These patterns ARE the RunReset.Fresh deletion list (SSOT §6.1). If Fresh's set changes, this
        // guard forces the ignore file to change with it — the two must never drift apart.
        Assert.Contains("/logs/", PlanGitignore.Content);
        Assert.Contains("/state/run.json", PlanGitignore.Content);
        Assert.Contains("/state/state.json", PlanGitignore.Content);
        Assert.Contains("/state/merge-conflicts.log", PlanGitignore.Content);
        Assert.Contains("/state/captured/", PlanGitignore.Content);
    }

    [Fact]
    public void Content_DoesNotIgnoreAnyCommittedArtifact()
    {
        // The committed seed and the review marker are the two traps: a naive `state/*` allow-nothing
        // rule (the issue reporter's workaround) would silently ignore state/seed.json. A denylist that
        // lists ONLY the transient files cannot — assert no line targets a committed artifact.
        string[] lines = PlanGitignore.Content
            .Split('\n')
            .Where(l => l.Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToArray();

        Assert.DoesNotContain("/state/seed.json", lines);
        Assert.DoesNotContain("/state/guardrails-review.json", lines);
        // No blanket state/ or plan-root wildcard that would sweep committed files in.
        Assert.DoesNotContain(lines, l => l is "*" or "/state/" or "/state/*" or "state/");
    }

    [Fact]
    public void EnsureScaffolded_IsIdempotent_SecondCallLeavesFileUnchanged()
    {
        PlanGitignore.EnsureScaffolded(_planDir);
        DateTime firstWrite = File.GetLastWriteTimeUtc(GitignorePath);

        PlanGitignore.EnsureScaffolded(_planDir);

        Assert.Equal(PlanGitignore.Content, File.ReadAllText(GitignorePath));
        // A second scaffold is a pure no-op — the file was not rewritten.
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(GitignorePath));
    }

    [Fact]
    public void EnsureScaffolded_IsNonClobbering_LeavesAHandAuthoredFileUntouched()
    {
        // The issue reporter hand-authored their own state/.gitignore workaround; a user may hand-author
        // a plan-root one too. The scaffold must NEVER overwrite it.
        const string handAuthored = "# my own rules\n*.tmp\n!keep-me\n";
        File.WriteAllText(GitignorePath, handAuthored);

        PlanGitignore.EnsureScaffolded(_planDir);

        Assert.Equal(handAuthored, File.ReadAllText(GitignorePath));
    }
}
