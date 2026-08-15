using System.Diagnostics;
using Guardrails.Core.Io;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #452 — the symlink-resolving path comparison, proven against a REAL link on EVERY platform.
/// <para>
/// The bug these guard was macOS-only in the wild, but it is not macOS-specific in nature: git stores
/// and prints the symlink-RESOLVED real path of a worktree, the harness derives its own roots from
/// <see cref="Path.GetTempPath"/>, and <see cref="Path.GetFullPath(string)"/> does not resolve symlinks
/// — so on macOS, where <c>/var</c> is a symlink to <c>/private/var</c> and <c>$TMPDIR</c> lives under
/// it, the two sides of every containment test were different spellings of one directory and never
/// matched. macOS just supplies that link by default; here it is constructed explicitly, so the
/// comparison is exercised for real on Windows and Linux too.
/// </para>
/// <para>
/// The link is deliberately an <b>intermediate</b> segment (a link to the parent, with real directories
/// beneath it) rather than the leaf, because that is the shape of the real failure — and the shape a
/// leaf-only <see cref="Directory.ResolveLinkTarget"/> silently fails to resolve.
/// </para>
/// </summary>
public sealed class RealPathTests
{
    [Fact]
    public void Resolve_FollowsAnIntermediateLink_SoAnAliasedAndARealSpellingAgree()
    {
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        // <base>/link/root/segment and <base>/real/root/segment are the same directory reached two ways.
        Assert.Equal(
            RealPath.Resolve(fixture.RealSegment),
            RealPath.Resolve(fixture.AliasedSegment),
            StringComparer.FromComparison(RealPath.Comparison));

        // And the resolution really did follow the link rather than leaving the alias in place — a
        // leaf-only Directory.ResolveLinkTarget (what this replaced, and the tempting one-line "fix"
        // that would change nothing) returns null here, because the leaf 'segment' is an ordinary
        // directory and the link is an ANCESTOR. Asserted as a path PREFIX on a directory boundary
        // rather than a substring search, so no accidental spelling inside the temp path can satisfy it.
        string resolvedAlias = RealPath.Resolve(fixture.AliasedSegment);
        Assert.StartsWith(
            RealPath.Resolve(fixture.RealBase) + Path.DirectorySeparatorChar, resolvedAlias, RealPath.Comparison);
        Assert.False(
            resolvedAlias.StartsWith(
                Path.GetFullPath(fixture.Link) + Path.DirectorySeparatorChar, RealPath.Comparison),
            "Resolve must replace the intermediate link segment with its target, not keep the alias.");
    }

    [Fact]
    public void IsUnder_MatchesAGitStyleResolvedPath_AgainstAHarnessStyleAliasedRoot()
    {
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        // THE BUG, exactly: git reports the resolved real path of a registered worktree, while the
        // harness's own root descends from Path.GetTempPath() and is still spelled through the link.
        // Lexically these never match, so nothing was unregistered and the batched prune never ran.
        Assert.True(
            RealPath.IsUnder(fixture.RealSegment, fixture.AliasedRoot),
            "a git-reported (resolved) worktree path must be recognised as under the harness's own "
            + "(unresolved) worktree root — this is the macOS /var vs /private/var failure.");

        // The reverse spelling too, and the root's own identity.
        Assert.True(RealPath.IsUnder(fixture.AliasedSegment, fixture.RealRoot));
        Assert.True(RealPath.IsUnder(fixture.RealRoot, fixture.AliasedRoot));
        Assert.True(RealPath.SamePath(fixture.RealRoot, fixture.AliasedRoot));
    }

    [Fact]
    public void Resolve_ReCanonicalisesAnAdoptedTarget_WhenTheLinkTargetIsWrittenThroughAnotherLink()
    {
        // The defect this pins, found by the tests above on real macOS and INVISIBLE to them on Windows
        // and Linux. A link stores whatever spelling was handed to ln/mklink, so its target can be
        // absolute and yet NOT canonical — it can run through another link. Adopting such a target
        // verbatim mid-walk throws away every resolution already accumulated: the walk follows the link
        // correctly and still lands on an aliased path.
        //
        // On macOS the outer link is supplied for free — the harness builds under Path.GetTempPath(),
        // which lives under /var → /private/var, so a link created there records "/var/…" and the walk
        // jumped back out of "/private/var" the moment it adopted one. Here BOTH links are built
        // explicitly, rather than borrowing a platform's ambient symlink.
        //
        // HONEST SCOPE — this test is INERT ON WINDOWS: it passes with or without the fix there, and
        // that was MEASURED, not assumed. Windows resolves a link with GetFinalPathNameByHandle, which
        // returns an already-canonical final target (probed: immediate target "…\link\root", final
        // target "…\real\root"), so no Windows link arrangement can hand the walk an aliased target. On
        // Unix .NET follows the link CHAIN but leaves the components of the path it lands on untouched,
        // which is where the defect lives. The platform-independent proof of this same logic is
        // Walk_ReCanonicalisesAnAdoptedTarget_…, which drives the walk through an injected resolver.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.LinkedViaAlias, LinkedTree.SkipReason);

        // <base>/via → <base>/link/root, and <base>/link → <base>/real. So 'via's stored target is
        // ABSOLUTE but ALIASED, and resolving it must still land on the fully canonical real root.
        Assert.Equal(
            RealPath.Resolve(fixture.RealRoot),
            RealPath.Resolve(fixture.Via),
            StringComparer.FromComparison(RealPath.Comparison));

        // Stated as a path prefix as well, so the failure mode is named rather than inferred: the result
        // may not still be expressed through the alias.
        Assert.False(
            RealPath.Resolve(fixture.Via).StartsWith(
                Path.GetFullPath(fixture.Link) + Path.DirectorySeparatorChar, RealPath.Comparison),
            "an adopted link target must be re-canonicalised, not carried through in the alias's spelling.");

        Assert.True(RealPath.IsUnder(fixture.Via, fixture.RealRoot));
        Assert.True(RealPath.IsUnder(Path.Combine(fixture.Via, "segment"), fixture.RealSegment));
    }

    [Fact]
    public void Walk_ReCanonicalisesAnAdoptedTarget_ProvenWithoutDependingOnPlatformLinkSemantics()
    {
        // The SAME defect as the test above, pinned through the injectable segment resolver so it is
        // genuinely exercised on EVERY OS — including the ones where no real link arrangement can
        // reproduce it (Windows hands back an already-canonical final target, so the hazard is
        // unreachable there through the filesystem). Fails against an implementation that adopts a
        // link target verbatim; passes only when the adopted target is re-canonicalised.
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        string aliasDir = Path.Combine(root, "alias");
        string realDir = Path.Combine(root, "real");
        string entry = Path.Combine(root, "entry");

        // 'entry' is a link whose target is ABSOLUTE but written through 'alias'; 'alias' is itself a
        // link to 'real'. Resolving 'entry' must land on 'real', not on 'alias'.
        string Fake(string p) =>
            string.Equals(p, entry, RealPath.Comparison) ? Path.Combine(aliasDir, "inner")
            : string.Equals(p, aliasDir, RealPath.Comparison) ? realDir
            : p;

        Assert.Equal(
            Path.Combine(realDir, "inner"),
            RealPath.ResolveWith(entry, Fake),
            StringComparer.FromComparison(RealPath.Comparison));

        // And the segments that follow the adopted target ride on the CANONICAL form, not the alias.
        Assert.Equal(
            Path.Combine(realDir, "inner", "leaf"),
            RealPath.ResolveWith(Path.Combine(entry, "leaf"), Fake),
            StringComparer.FromComparison(RealPath.Comparison));
    }

    [Fact]
    public void Walk_Terminates_OnLinksThatWouldOtherwiseReCanonicaliseForever()
    {
        // Re-canonicalising every adopted target introduces a runaway that following a link chain does
        // not: '/x/a → /y/a' plus '/y → /x' makes each target's re-canonicalisation rediscover the other
        // link, forever, though NEITHER link is individually circular — so the runtime's own SYMLOOP
        // detection never fires and cannot save us. The depth guard must stop it, and per the best-effort
        // contract it must degrade to a literal rather than throw or hang. If this regresses, this test
        // does not fail politely — it hangs or overflows the stack, which is itself the signal.
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        string x = Path.Combine(root, "x");
        string y = Path.Combine(root, "y");

        string Fake(string p) =>
            string.Equals(p, Path.Combine(x, "a"), RealPath.Comparison) ? Path.Combine(y, "a")
            : string.Equals(p, y, RealPath.Comparison) ? x
            : p;

        string result = RealPath.ResolveWith(Path.Combine(x, "a"), Fake);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.True(Path.IsPathRooted(result));
    }

    [Fact]
    public void IsUnder_DoesNotMatchASiblingThatMerelySharesAPrefix()
    {
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);

        // 'root-evil' shares every character of 'root' and resolves through the same link — the
        // directory-boundary test, not a substring test, is what keeps it out. (The bug report this
        // fix came from failed on an assertion that WAS a substring test, and whose companion passed
        // only because "/private/var/…" happens to contain "/var/…".)
        string sibling = Path.Combine(fixture.RealBase, "root-evil");
        Directory.CreateDirectory(sibling);

        Assert.False(RealPath.IsUnder(sibling, fixture.AliasedRoot));
        Assert.False(RealPath.IsUnder(Path.Combine(sibling, "segment"), fixture.AliasedRoot));
        Assert.False(RealPath.SamePath(sibling, fixture.AliasedRoot));
    }

    [Fact]
    public void UnresolvablePaths_DegradeToTheLiteralComparison_AndNeverThrow()
    {
        // Best-effort by contract: these helpers are called about directories that were just deleted
        // (the GC sweep's whole job), so a path that cannot be resolved must still compare literally —
        // nothing that worked before resolution existed may start failing because of it.
        string gone = Path.Combine(Path.GetTempPath(), "gr-absent-" + Guid.NewGuid().ToString("N"));
        string goneChild = Path.Combine(gone, "a", "b");

        Assert.True(RealPath.IsUnder(goneChild, gone));
        Assert.True(RealPath.SamePath(gone, gone + Path.DirectorySeparatorChar));
        Assert.False(RealPath.IsUnder(gone, goneChild));

        // Resolving a path that does not exist must not throw: .NET raises DirectoryNotFoundException for
        // a missing entry rather than returning null. The vanished remainder is carried through verbatim.
        //
        // Deliberately NOT asserted equal to Path.GetFullPath(goneChild): on macOS the EXISTING ancestors
        // of a missing temp path DO resolve (/var → /private/var), so the two legitimately differ there.
        // An equality assertion here would be green on Windows and Linux and red on macOS — precisely the
        // platform-blind shape that let this bug reach master in the first place.
        string resolvedGoneChild = RealPath.Resolve(goneChild);
        Assert.EndsWith(Path.Combine("a", "b"), resolvedGoneChild, RealPath.Comparison);
        Assert.True(RealPath.IsUnder(resolvedGoneChild, gone));

        // A dangling link (target deleted after the link was made) is the same story: no resolution is
        // possible, so the literal spelling has to stand rather than throw.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        SafeDelete.DeleteDirectory(fixture.RealBase);
        Assert.True(RealPath.IsUnder(fixture.AliasedSegment, fixture.AliasedRoot));

        // Nothing here may throw, including on shapes that are not paths at all.
        Assert.False(RealPath.IsUnder("", gone));
        Assert.False(RealPath.SamePath("", gone));
        Assert.Equal("", RealPath.Resolve(""));
    }

    [Fact]
    public void IsUnder_StillMatches_WhenTheLeafHasALREADYBeenDeleted()
    {
        // The sweep's own situation, and the reason resolution cannot simply demand an existing path:
        // GitWorktreeProvider.RemoveWorktreeRoot compares git's registered worktree paths against a root
        // it is in the middle of DELETING, so it is routinely handed paths whose leaf is already gone.
        // Resolution must therefore resolve the deepest EXISTING ancestor and carry the vanished
        // remainder onto it — which is what makes the containment answer survive the deletion — and it
        // must do so without throwing, because a GC hiccup may never abort a run.
        using var fixture = new LinkedTree();
        Assert.SkipUnless(fixture.Linked, LinkedTree.SkipReason);
        fixture.AssertTwoDistinctSpellings();

        Directory.Delete(fixture.RealSegment); // the leaf vanishes; its ancestors (and the link) remain

        Assert.False(Directory.Exists(fixture.RealSegment));
        Assert.True(
            RealPath.IsUnder(fixture.RealSegment, fixture.AliasedRoot),
            "a deleted leaf must still be recognised as under its root — the deepest EXISTING ancestor "
            + "is what resolves, and the missing remainder rides along.");
        Assert.True(RealPath.IsUnder(fixture.AliasedSegment, fixture.RealRoot));
    }

    /// <summary>
    /// A temp tree containing an INTERMEDIATE directory link, so one directory has two spellings:
    /// <c>&lt;base&gt;/real/root/segment</c> (git's) and <c>&lt;base&gt;/link/root/segment</c> (the harness's).
    /// <para>
    /// Creating the link is the one platform-dependent step. A POSIX symlink needs no privilege; on
    /// Windows <see cref="Directory.CreateSymbolicLink"/> needs Developer Mode or elevation, so an
    /// <c>mklink /J</c> DIRECTORY JUNCTION — which needs neither, and which is the very link type the
    /// issue #383 short-root machinery creates — is the fallback. Only if BOTH are unavailable does the
    /// test skip, and it says so rather than passing vacuously.
    /// </para>
    /// </summary>
    private sealed class LinkedTree : IDisposable
    {
        internal const string SkipReason =
            "could not create a directory symlink or junction on this machine (Windows needs Developer "
            + "Mode, elevation, or mklink /J) — the symlink-resolution assertions cannot be exercised here.";

        private readonly string _base;

        internal LinkedTree()
        {
            _base = Path.Combine(Path.GetTempPath(), "gr-realpath-" + Guid.NewGuid().ToString("N"));
            RealBase = Path.Combine(_base, "real");
            Link = Path.Combine(_base, "link");
            Via = Path.Combine(_base, "via");
            Directory.CreateDirectory(Path.Combine(RealBase, "root", "segment"));
            Linked = TryLink(Link, RealBase);

            // A SECOND link whose stored target is written THROUGH the first — absolute, but aliased.
            // Created after Link so the target it records is a path that itself needs resolving.
            LinkedViaAlias = Linked && TryLink(Via, AliasedRoot);
        }

        /// <summary>The real directory the link points at — <c>&lt;base&gt;/real</c>.</summary>
        internal string RealBase { get; }

        /// <summary>The link — <c>&lt;base&gt;/link</c> → <see cref="RealBase"/>.</summary>
        internal string Link { get; }

        /// <summary>
        /// A link to <see cref="AliasedRoot"/> — <c>&lt;base&gt;/via</c> → <c>&lt;base&gt;/link/root</c>.
        /// Its recorded target is ABSOLUTE but runs through <see cref="Link"/>, which is the shape macOS
        /// hands the harness for free via <c>/var</c> → <c>/private/var</c>.
        /// </summary>
        internal string Via { get; }

        /// <summary>False when neither a symlink nor a junction could be created here.</summary>
        internal bool Linked { get; }

        /// <summary>True when BOTH links exist, so the aliased-target hazard can be exercised.</summary>
        internal bool LinkedViaAlias { get; }

        /// <summary>The root as GIT would report it (resolved).</summary>
        internal string RealRoot => Path.Combine(RealBase, "root");

        /// <summary>The root as the HARNESS derives it (through the unresolved link).</summary>
        internal string AliasedRoot => Path.Combine(Link, "root");

        internal string RealSegment => Path.Combine(RealRoot, "segment");

        internal string AliasedSegment => Path.Combine(AliasedRoot, "segment");

        /// <summary>
        /// Proves the fixture reproduces the CONDITION under test — one directory, two spellings that
        /// <see cref="Path.GetFullPath(string)"/> alone does NOT reconcile. Without this the resolution
        /// assertions could pass on a platform where the two paths were already lexically identical,
        /// which is precisely how a symlink bug reaches a green CI on the OSes that lack the symlink.
        /// </summary>
        internal void AssertTwoDistinctSpellings() =>
            Assert.False(
                Path.GetFullPath(RealSegment).Equals(Path.GetFullPath(AliasedSegment), RealPath.Comparison),
                "the fixture must produce two lexically DIFFERENT spellings of one directory, otherwise "
                + "the symlink-resolution assertions prove nothing.");

        public void Dispose()
        {
            // Remove the LINKS first and by themselves, so a recursive delete can never walk through one
            // into the target (and, on a junction, delete the real tree twice over). 'via' goes before
            // 'link' — it points through it.
            DeleteLink(Via);
            DeleteLink(Link);
            SafeDelete.DeleteDirectory(_base);
        }

        private static void DeleteLink(string link)
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (IOException) { /* best-effort */ }
            catch (UnauthorizedAccessException) { /* best-effort */ }
        }

        private static bool TryLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.Exists(link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Windows without symlink privilege: fall back to a junction, which needs none.
                return OperatingSystem.IsWindows() && TryJunction(link, target);
            }
        }

        private static bool TryJunction(string link, string target)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(link);
                psi.ArgumentList.Add(target);

                using Process? proc = Process.Start(psi);
                if (proc is null) return false;
                proc.WaitForExit(30_000);
                return Directory.Exists(link);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
