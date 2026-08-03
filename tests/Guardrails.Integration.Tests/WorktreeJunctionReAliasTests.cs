using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #419 change #4 — the reused-integration RE-ALIAS on resume, against the REAL
/// <see cref="GitWorktreeProvider"/> on a temp git repo. git canonicalizes the short junction back to the
/// real path in its own registrations, so on resume <c>WorktreeForBranch</c> returns the REAL (long) path;
/// the provider must RE-ALIAS it onto THIS run's junction so the terminal-gate / union-reverify cwd stays
/// short — matching a fresh run. The Windows junction assertions are gated on
/// <see cref="OperatingSystem.IsWindows"/>; a cross-OS test pins the no-junction identity (the Linux/macOS
/// root pass). Everything lives under a UNIQUE TEMP tree (never the real <c>C:\</c> drive root).
/// </summary>
public sealed class WorktreeJunctionReAliasTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoPath;
    private readonly string _realRoot;
    private readonly List<string> _links = [];

    public WorktreeJunctionReAliasTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-realias-" + Guid.NewGuid().ToString("N"));
        _repoPath = Path.Combine(_root, "repo");
        _realRoot = Path.Combine(_root, "real"); // the git-canonical worktree root the junctions point at
        Directory.CreateDirectory(_repoPath);
        Directory.CreateDirectory(_realRoot);

        Git(_repoPath, "init");
        Git(_repoPath, "config", "user.email", "test@guardrails.local");
        Git(_repoPath, "config", "user.name", "Guardrails Test");
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# realias-test");
        Git(_repoPath, "add", ".");
        Git(_repoPath, "commit", "-m", "Initial commit");
    }

    [Fact]
    public void ResumedIntegration_IsReAliasedToTheFreshJunction_ShortCwd()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Two DIFFERENT junction letters, both → the real root (the original run's .j, the resume's .k).
        string junctionJ = Track(Path.Combine(_root, "base", ".j"));
        string junctionK = Track(Path.Combine(_root, "base", ".k"));
        Assert.True(WorktreeJunction.TryCreateJunction(junctionJ, _realRoot));
        Assert.True(WorktreeJunction.TryCreateJunction(junctionK, _realRoot));

        // Original run: build the integration UNDER .j (fresh AddIntegrationWorktree).
        var run1 = new GitWorktreeProvider(_repoPath, junctionJ, _realRoot);
        IntegrationHandle integ1 = run1.CreateIntegration("my-plan", "run-1", CancellationToken.None);
        Assert.StartsWith(junctionJ, integ1.IntegrationWorktreePath, StringComparison.OrdinalIgnoreCase);

        // The feasibility premise: git canonicalized the junction away — its own registration is the REAL path.
        string registered = RegisteredWorktreeFor("guardrails/my-plan");
        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_realRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered)),
            StringComparison.OrdinalIgnoreCase);

        // Resume with a DIFFERENT letter .k: CreateIntegration ADOPTS the existing worktree (real path) and
        // must RE-ALIAS it to .k — a SHORT cwd, NOT the raw real root.
        var run2 = new GitWorktreeProvider(_repoPath, junctionK, _realRoot);
        IntegrationHandle integ2 = run2.CreateIntegration("my-plan", "run-2", CancellationToken.None);
        Assert.StartsWith(junctionK, integ2.IntegrationWorktreePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            integ2.IntegrationWorktreePath.StartsWith(_realRoot, StringComparison.OrdinalIgnoreCase),
            "the resumed integration cwd must be the short junction, not the raw long real root (#419).");

        // And it is USABLE through the alias (git resolves .k → the real integration worktree).
        Assert.Equal(
            "guardrails/my-plan",
            Git(integ2.IntegrationWorktreePath, "rev-parse", "--abbrev-ref", "HEAD").Trim());

        // A task SEGMENT created on the resume also lands under the short junction (the other #419 cwd).
        WorktreeHandle seg = run2.CreateSegment("01-task", attempt: 1, integ2, CancellationToken.None);
        Assert.StartsWith(junctionK, seg.WorktreePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoJunction_AliasIsIdentity_TheRealRootPass_CrossOS()
    {
        // The Linux/macOS root pass (and the Windows lazy-skip / fallback): with no junction the effective
        // root IS the real root, so AliasForLaunch is the identity — a resume adopts the real integration
        // path unchanged. Cross-OS.
        var run1 = new GitWorktreeProvider(_repoPath, _realRoot); // realRoot == worktreeRoot ⇒ no junction
        IntegrationHandle integ1 = run1.CreateIntegration("plan-b", "run-1", CancellationToken.None);
        AssertUnderRealRoot(integ1.IntegrationWorktreePath);

        var run2 = new GitWorktreeProvider(_repoPath, _realRoot);
        IntegrationHandle integ2 = run2.CreateIntegration("plan-b", "run-2", CancellationToken.None);
        AssertUnderRealRoot(integ2.IntegrationWorktreePath);
    }

    /// <summary>
    /// Assert an integration path is under the real root, tolerating the macOS temp-dir symlink: git
    /// canonicalizes <c>/var/folders/…</c> to <c>/private/var/folders/…</c>, so normalize a leading
    /// <c>/private</c> on both sides before the prefix check. Linux/Windows are unaffected (no such prefix).
    /// </summary>
    private void AssertUnderRealRoot(string integrationPath) =>
        Assert.StartsWith(
            StripMacPrivate(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_realRoot))),
            StripMacPrivate(Path.TrimEndingDirectorySeparator(Path.GetFullPath(integrationPath))),
            StringComparison.OrdinalIgnoreCase);

    private static string StripMacPrivate(string p) =>
        p.StartsWith("/private/", StringComparison.Ordinal) ? p["/private".Length..] : p;

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The absolute path git has registered for the worktree on <paramref name="branch"/> (git-canonical).</summary>
    private string RegisteredWorktreeFor(string branch)
    {
        string listing = Git(_repoPath, "worktree", "list", "--porcelain");
        string? current = null;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                current = line["worktree ".Length..].Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal)
                     && line["branch ".Length..].Trim() == $"refs/heads/{branch}" && current is not null)
                return current;
        }

        throw new InvalidOperationException($"no worktree registered for {branch}");
    }

    private string Track(string link)
    {
        _links.Add(link);
        return link;
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
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        foreach (string link in _links)
        {
            WorktreeJunction.RemoveJunctionLink(link); // link-only, so the tree delete never follows a reparse point
        }

        try { SafeDelete.DeleteDirectory(_root); } catch { /* best-effort temp cleanup */ }
    }
}
