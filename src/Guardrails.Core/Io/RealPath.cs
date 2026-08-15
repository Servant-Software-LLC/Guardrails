namespace Guardrails.Core.Io;

/// <summary>
/// Symlink-resolving path canonicalisation, for the ONE comparison shape a lexical comparison
/// cannot get right: a path the harness derived itself (<see cref="Path.GetTempPath"/>,
/// <see cref="Path.Combine(string, string)"/>, config) compared against a path an EXTERNAL tool
/// reported back — above all <c>git worktree list --porcelain</c>, which stores and prints the
/// symlink-RESOLVED real path.
/// <para>
/// <b>Why this is needed (issue #452).</b> <see cref="Path.GetFullPath(string)"/> normalises
/// separators and collapses <c>..</c>, but it does <b>not</b> resolve symlinks — it is pure string
/// arithmetic over the path. On macOS <c>Path.GetTempPath()</c> returns the UNRESOLVED
/// <c>/var/folders/…</c> while <c>/var</c> is a symlink to <c>/private/var</c>, so the same
/// directory reaches a comparison as <c>/var/folders/…/root</c> from the harness and
/// <c>/private/var/folders/…/root</c> from git. Every lexical test between them — equality,
/// ancestry, <see cref="Path.GetRelativePath"/> — is then silently wrong, on macOS only. (The
/// same shape exists on Linux with a symlinked <c>$TMPDIR</c> and on Windows with a junctioned
/// temp dir; macOS is merely where it is the default.)
/// </para>
/// <para>
/// <b>The resolution has to walk the whole path.</b> <see cref="Directory.ResolveLinkTarget"/>
/// resolves the FINAL segment only and returns null when that segment is not itself a link — so
/// pointing it at <c>/var/folders/…/gr-wt/abc123</c> resolves nothing, because the link is
/// <c>/var</c>, five segments up. <see cref="Resolve"/> therefore walks root → leaf and resolves
/// each segment in turn.
/// </para>
/// <para>
/// <b>Best-effort by contract.</b> Every step degrades to the literal spelling: a path that does
/// not exist cannot be resolved (and the callers here routinely ask about directories that were
/// just deleted), an unreadable one must not throw, and <see cref="IsUnder"/> tries the cheap
/// LEXICAL comparison first — so a filesystem with no symlinks in play behaves exactly as it did
/// before this type existed, with zero extra IO, and a resolution failure can only ever cost the
/// resolved second chance, never a previously-working match.
/// </para>
/// </summary>
public static class RealPath
{
    /// <summary>
    /// Path comparison for the current OS: case-insensitive on Windows, ordinal elsewhere.
    /// </summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The fully symlink-resolved absolute form of <paramref name="path"/> (no trailing separator):
    /// <see cref="Path.GetFullPath(string)"/>, then every segment from the root down replaced by its
    /// final link target where one exists. Never throws — any segment that cannot be resolved (it does
    /// not exist, it is not a link, it is unreadable, or the link chain is circular) is kept literally,
    /// so the result degrades gracefully toward the plain <see cref="Path.GetFullPath(string)"/> form.
    /// </summary>
    public static string Resolve(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }

        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length == 0 || root.Length > full.Length)
        {
            return full; // no rooted prefix to walk down from — nothing further to resolve
        }

        // A root ("/", "C:\", "\\server\share") keeps its own trailing separator — Path.Combine handles
        // both spellings — so the walk starts at the root and appends one segment at a time.
        string current = root;
        foreach (string segment in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            // Re-root on each resolved target: once an ANCESTOR resolves (macOS /var → /private/var),
            // every remaining segment is appended to the REAL ancestor. This is also what makes a
            // path whose LEAF no longer exists behave correctly — the deepest existing ancestor
            // resolves, the vanished remainder is carried literally onto it — which matters because
            // the sweep this serves asks about directories it has just deleted.
            current = ResolveSegment(Path.Combine(current, segment));
        }

        return current;
    }

    /// <summary>
    /// True when <paramref name="path"/> equals, or is nested under, <paramref name="ancestor"/> —
    /// compared LEXICALLY first (no IO, the pre-symlink behaviour), and only on a miss again over the
    /// <see cref="Resolve"/>d spelling of both sides. A match is therefore never LOST by adding
    /// resolution; it can only be gained where the two sides were different spellings of one directory.
    /// Nesting is tested on a directory boundary, so a sibling such as <c>…/root-evil</c> is never
    /// under <c>…/root</c>.
    /// </summary>
    public static bool IsUnder(string path, string ancestor) =>
        IsUnderLexical(path, ancestor) || IsUnderLexical(Resolve(path), Resolve(ancestor));

    /// <summary>
    /// True when both paths name the SAME filesystem entry — <see cref="IsUnder"/>'s equality case:
    /// lexically equal, or equal once <see cref="Resolve"/>d.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <c>WorktreeJunction.SamePath</c>, which must stay LEXICAL: the issue
    /// #383 short junction <c>C:\.a</c> and its target <c>&lt;temp&gt;\gr-wt\&lt;hash&gt;</c> resolve to
    /// the same directory, yet the whole point of that code is to tell the two spellings APART (the run
    /// launches under the short alias). Resolving there would collapse the alias and disable the
    /// junction. Use this one where two spellings of one directory must COMPARE EQUAL; use that one
    /// where an alias must remain distinguishable from its target.
    /// </remarks>
    public static bool SamePath(string a, string b) =>
        EqualLexical(a, b) || EqualLexical(Resolve(a), Resolve(b));

    /// <summary>Lexical (<see cref="Path.GetFullPath(string)"/>-only) form of <see cref="IsUnder"/>.</summary>
    private static bool IsUnderLexical(string path, string ancestor)
    {
        try
        {
            string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
            return p.Equals(a, Comparison) || p.StartsWith(a + Path.DirectorySeparatorChar, Comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Lexical (<see cref="Path.GetFullPath(string)"/>-only) path equality.</summary>
    private static bool EqualLexical(string a, string b)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
                .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), Comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// One step of the <see cref="Resolve"/> walk: <paramref name="path"/> replaced by its final link
    /// target when it is a link, else unchanged. Returns <paramref name="path"/> on every failure —
    /// a missing parent, an unreadable entry, or a circular link chain (which
    /// <see cref="Directory.ResolveLinkTarget"/> reports as an <see cref="IOException"/>).
    /// </summary>
    private static string ResolveSegment(string path)
    {
        FileSystemInfo? target;
        try
        {
            target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
        }
        catch (IOException)
        {
            // A MISSING entry lands here: .NET raises DirectoryNotFoundException (an IOException) for a
            // path that does not exist rather than returning null — measured, and load-bearing, because
            // the sweep that drives this asks about directories it has already deleted. The other case
            // is a FILE link, which the directory overload refuses; retry as a file before giving up.
            try { target = File.ResolveLinkTarget(path, returnFinalTarget: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }

        if (target is null)
        {
            return path; // not a link — the overwhelmingly common case
        }

        try
        {
            // A link target may be RELATIVE, and .NET resolves a relative FileSystemInfo.FullName
            // against the CURRENT directory rather than the LINK's directory — re-base it explicitly
            // so a relative symlink cannot produce a path pointing at the process's cwd.
            string linkParent = Path.GetDirectoryName(path) ?? path;
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(StripDevicePrefix(target.FullName), linkParent));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>
    /// Strip a Windows device prefix (<c>\??\</c> or <c>\\?\</c>) that a resolved reparse point can
    /// surface — an <c>mklink /J</c> junction stores its target literally as <c>\??\C:\…</c>. A prefixed
    /// path would compare unequal to every ordinary spelling of the same directory, which is exactly the
    /// failure this type exists to prevent. Stripped ONLY ahead of a drive-letter root, so the
    /// <c>\\?\UNC\server\share</c> form (where the prefix is load-bearing) is left intact.
    /// </summary>
    private static string StripDevicePrefix(string path) =>
        (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        && path.Length >= 6 && char.IsLetter(path[4]) && path[5] == ':'
            ? path[4..]
            : path;
}
