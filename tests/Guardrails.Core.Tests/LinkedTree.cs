using System.Diagnostics;
using Guardrails.Core.Io;

namespace Guardrails.Core.Tests;

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
/// <para>
/// <b>Shared, not copied (issue #464).</b> This started life private to <see cref="RealPathTests"/>
/// and was promoted here when <see cref="WorktreeContainmentHookAliasedRootTests"/> needed the same
/// two-spellings-of-one-directory condition. It is deliberately ONE copy: the platform fallback, the
/// link-before-tree teardown order, and above all <see cref="AssertTwoDistinctSpellings"/> are the
/// parts that stop these tests passing vacuously — and a second copy that quietly drifted from this
/// one would recreate exactly the blind spot both issues are about.
/// </para>
/// </summary>
internal sealed class LinkedTree : IDisposable
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
