using Guardrails.Core.Io;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #464 — the worktree-containment hook against a worktree root that has TWO absolute
/// spellings, proven against a REAL link on EVERY platform.
/// <para>
/// <b>Why a separate fixture exists at all.</b> <see cref="WorktreeContainmentHookTests"/> derives the
/// baked root and every candidate path from one string, so both sides of the hook's comparison always
/// agree by construction and the hazard is structurally unreachable there — not under-asserted,
/// unreachable. Here the two sides are built through DIFFERENT spellings of one directory
/// (<see cref="LinkedTree"/>), which is the condition macOS supplies for free: the harness derives a
/// worktree root under <see cref="Path.GetTempPath"/> → <c>/var/folders/…</c>, while <c>/var</c> is a
/// symlink to <c>/private/var</c>, so anything that resolves the path — the OS's own idea of the
/// agent's working directory, <c>git rev-parse --show-toplevel</c>, <c>pwd -P</c> — spells the very
/// same directory <c>/private/var/folders/…</c>. Before #464 the hook baked one literal and compared
/// by pure string normalisation, so a legitimate write inside the agent's own worktree was refused
/// with exit 2 — on every write, of every task, and reading as the hook working correctly.
/// </para>
/// <para>
/// <b>Each test asserts the fixture reproduced the condition</b> (<c>AssertTwoDistinctSpellings</c>
/// plus, sharper, "the candidate is NOT lexically under the baked root"). Without that, a run on a
/// platform where the two paths happened to coincide would pass while proving nothing — which is
/// exactly how this class of bug reaches a green CI.
/// </para>
/// </summary>
public sealed class WorktreeContainmentHookAliasedRootTests : IDisposable
{
    private readonly string _logRoot = Path.Combine(Path.GetTempPath(), "gr-wch-alias-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => SafeDelete.DeleteDirectory(_logRoot);

    private string NewLogDir()
    {
        string dir = Path.Combine(_logRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The assertion that gives this file its teeth: the candidate must NOT be lexically inside the
    /// baked root. That lexical test is EXACTLY the rule both generated scripts applied before #464
    /// (<c>case "$resolved" in "$root_norm"|"$root_norm"/*</c>; <c>StartsWith(rootFull + sep)</c>), so
    /// if it held, an "allowed" verdict below would prove nothing about symlink aliasing at all.
    /// </summary>
    private static void AssertCandidateIsNotLexicallyUnderRoot(string bakedRoot, string candidate)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bakedRoot));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        Assert.False(
            full.Equals(root, RealPath.Comparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, RealPath.Comparison),
            $"the fixture must hand the hook a candidate ('{full}') that is NOT lexically under the "
            + $"baked root ('{root}'), otherwise the pre-#464 single-literal comparison would have "
            + "allowed it too and this test proves nothing.");
    }

    // --- the accepted-roots set itself (no process spawn) ------------------------------------

    [Fact]
    public void AcceptedRoots_AnAliasedRoot_CarriesBothSpellings_AsGivenFirst()
    {
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        IReadOnlyList<string> accepted = WorktreeContainmentHook.AcceptedRoots(fixture.AliasedRoot);

        Assert.Equal(2, accepted.Count);

        // [0] is the PRIMARY: the root exactly as the harness spelled it. The block message names it,
        // and a relative candidate is joined to it, so its position is contractual, not incidental.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.AliasedRoot)),
            accepted[0],
            StringComparer.FromComparison(RealPath.Comparison));

        // [1] is the same directory reached through the link — the spelling git and the OS produce.
        Assert.Equal(
            RealPath.Resolve(fixture.RealRoot),
            accepted[1],
            StringComparer.FromComparison(RealPath.Comparison));
    }

    [Fact]
    public void AcceptedRoots_WithNoLinkInPlay_IsASingleEntry()
    {
        // Dedup, and the "costs nothing when there is nothing to resolve" property: an ALREADY-resolved
        // root resolves to itself, so the script sees exactly the one literal it saw before #464.
        // Deliberately fed through RealPath.Resolve rather than Path.GetTempPath() directly — on macOS
        // a raw temp path legitimately HAS two spellings, and asserting otherwise would be a
        // Windows/Linux-green, macOS-red test of the very kind this issue is about.
        string plain = RealPath.Resolve(Path.Combine(Path.GetTempPath(), "gr-wch-plain-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(new[] { plain }, WorktreeContainmentHook.AcceptedRoots(plain), StringComparer.FromComparison(RealPath.Comparison));
    }

    // --- the real generated script, run standalone -------------------------------------------

    [Fact]
    public async Task AliasedRootBaked_CanonicalCandidateInsideTheWorktree_IsAllowed()
    {
        // THE BUG, exactly: the harness bakes the root it derived (through the link), and the agent
        // supplies a path in the RESOLVED spelling of the same directory. Pre-#464 this exited 2.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        string bakedRoot = fixture.AliasedRoot;
        string candidate = Path.Combine(RealPath.Resolve(fixture.RealSegment), "file.txt");
        AssertCandidateIsNotLexicallyUnderRoot(bakedRoot, candidate);

        string logDir = NewLogDir();
        WorktreeContainmentHook.WriteHookFiles(logDir, bakedRoot);

        (int exitCode, string stderr) = await ContainmentHookScript.RunAsync(
            logDir,
            bakedRoot,
            ContainmentHookScript.ToolCall("Write", $$"""{"file_path":"{{ContainmentHookScript.ForJson(candidate)}}","content":"x"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.Trim());
    }

    [Fact]
    public async Task AliasedRootBaked_CandidateInTheRootsOwnSpelling_IsStillAllowed()
    {
        // The mirror of the case above, and the no-regression half of it: widening the accepted set
        // must not disturb the spelling that already worked. Both directions are covered whenever the
        // baked root is the ALIAS, because both of its spellings are then enumerable.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        string logDir = NewLogDir();
        WorktreeContainmentHook.WriteHookFiles(logDir, fixture.AliasedRoot);

        (int exitCode, _) = await ContainmentHookScript.RunAsync(
            logDir,
            fixture.AliasedRoot,
            ContainmentHookScript.ToolCall(
                "Write",
                $$"""{"file_path":"{{ContainmentHookScript.ForJson(Path.Combine(fixture.AliasedSegment, "file.txt"))}}","content":"x"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AliasedRootBaked_BashRedirectToACanonicalInWorktreePath_IsAllowed()
    {
        // Proves the accepted-roots list reaches the Bash write-ish matchers too, not just the
        // Write/Edit tool paths — the two share resolve_and_check / Resolve-AndCheck, and this is the
        // cheap assertion that keeps them sharing it.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        string candidate = Path.Combine(RealPath.Resolve(fixture.RealSegment), "out.txt");
        AssertCandidateIsNotLexicallyUnderRoot(fixture.AliasedRoot, candidate);

        string logDir = NewLogDir();
        WorktreeContainmentHook.WriteHookFiles(logDir, fixture.AliasedRoot);

        (int exitCode, _) = await ContainmentHookScript.RunAsync(
            logDir,
            fixture.AliasedRoot,
            ContainmentHookScript.ToolCall("Bash", $$"""{"command":"echo hi > {{ContainmentHookScript.ForJson(candidate)}}"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task WithMoreThanOneAcceptedRoot_AGenuineEscapeIsStillBlocked()
    {
        // The regression that would make #464's fix DANGEROUS: adding accepted spellings may only ever
        // turn a wrong block into an allow, never a correct block into an allow. Every case below is
        // outside the worktree in BOTH spellings, and each probes a different part of the rule —
        // directory-boundary (the `-evil` siblings, which share every character of a legitimate root),
        // plain ancestry, and the `..` collapse. Asserted with the multi-entry list actually in force.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        string bakedRoot = fixture.AliasedRoot;
        Assert.Equal(2, WorktreeContainmentHook.AcceptedRoots(bakedRoot).Count);

        string resolvedBase = RealPath.Resolve(fixture.RealBase);
        (string Label, string Path)[] escapes =
        [
            ("sibling sharing the RESOLVED root's prefix", Path.Combine(resolvedBase, "root-evil", "x.txt")),
            ("sibling sharing the ALIASED root's prefix", Path.Combine(fixture.Link, "root-evil", "x.txt")),
            ("the resolved root's own parent", Path.Combine(resolvedBase, "outside.txt")),
            ("a '..' climb out of the worktree", Path.Combine(RealPath.Resolve(fixture.RealRoot), "..", "..", "escape.txt")),
        ];

        string logDir = NewLogDir();
        WorktreeContainmentHook.WriteHookFiles(logDir, bakedRoot);

        foreach ((string label, string escape) in escapes)
        {
            (int exitCode, string stderr) = await ContainmentHookScript.RunAsync(
                logDir,
                bakedRoot,
                ContainmentHookScript.ToolCall("Write", $$"""{"file_path":"{{ContainmentHookScript.ForJson(escape)}}","content":"x"}"""),
                TestContext.Current.CancellationToken);

            Assert.True(
                exitCode == 2,
                $"{label}: '{escape}' escapes the worktree in BOTH accepted spellings and must still be "
                + $"blocked, but the hook exited {exitCode}. stderr: {stderr.Trim()}");
            Assert.Contains("BLOCKED", stderr, StringComparison.Ordinal);

            // The message must still name a worktree root (the primary spelling) — an escape the agent
            // cannot locate is feedback it cannot act on.
            Assert.Contains(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(bakedRoot)),
                stderr,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
