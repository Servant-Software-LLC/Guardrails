namespace Guardrails.Core.Execution;

/// <summary>
/// An ordered list of workspace-relative glob patterns declaring the paths a task may write.
/// Three meaningful values: empty <c>[]</c> (writes nothing — disjoint from every scope),
/// narrow (e.g. <c>["src/Feature/**"]</c>), and universal <c>["**"]</c> (serializes with all).
/// </summary>
public readonly struct WriteScope
{
    private readonly string[]? _globs;

    private WriteScope(string[] globs) => _globs = globs;

    private string[] Globs => _globs ?? [];
    private bool IsEmpty => Globs.Length == 0;
    private bool IsUniversal => Array.IndexOf(Globs, "**") >= 0;

    /// <summary>
    /// Parses and normalises an ordered list of workspace-relative glob patterns into a
    /// <see cref="WriteScope"/>. Bare paths with no wildcard characters are treated as directory
    /// roots and normalised to <c>dir/**</c>. Rejects <c>?</c>, brace-expansion, and negation.
    /// </summary>
    public static WriteScope Parse(IReadOnlyList<string> globs)
    {
        ArgumentNullException.ThrowIfNull(globs);

        var result = new string[globs.Count];
        for (int i = 0; i < globs.Count; i++)
        {
            Validate(globs[i]);
            result[i] = Normalize(globs[i]);
        }
        return new WriteScope(result);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the two scopes can produce a write to the same path —
    /// i.e. they are NOT disjoint and therefore must not run concurrently. Conservative: anything
    /// the algorithm cannot PROVE disjoint is treated as overlapping.
    /// </summary>
    public static bool Overlaps(WriteScope a, WriteScope b)
    {
        // Step 0: empty short-circuit — [] is disjoint from everything, including ["**"]
        if (a.IsEmpty || b.IsEmpty) return false;

        // Step 1: universal short-circuit — ["**"] overlaps every non-empty scope
        if (a.IsUniversal || b.IsUniversal) return true;

        // Step 2: pairwise glob comparison — any pair that can match a common path → overlap
        foreach (var ga in a.Globs)
        foreach (var gb in b.Globs)
        {
            if (GlobsCanOverlap(ga.Split('/'), gb.Split('/')))
                return true;
        }

        return false;
    }

    private static void Validate(string glob)
    {
        if (glob.Contains('?'))
            throw new ArgumentException(
                $"Single-char wildcard '?' is not supported in write-scope globs: '{glob}'", nameof(glob));
        if (glob.Contains('{'))
            throw new ArgumentException(
                $"Brace expansion is not supported in write-scope globs: '{glob}'", nameof(glob));
        if (glob.StartsWith('!'))
            throw new ArgumentException(
                $"Negation prefix '!' is not supported in write-scope globs: '{glob}'", nameof(glob));
    }

    // A bare path with no wildcard (e.g. "src/Feature" or "src/Feature/") is treated as dir/**
    private static string Normalize(string glob)
    {
        var trimmed = glob.TrimEnd('/');
        return trimmed.Contains('*') ? trimmed : trimmed + "/**";
    }

    // Returns true when two glob patterns can both match at least one common path.
    // Conservative: returns false only when a common match is provably impossible.
    private static bool GlobsCanOverlap(string[] a, string[] b) => Check(a, 0, b, 0);

    private static bool Check(string[] a, int ia, string[] b, int ib)
    {
        // Both patterns exhausted at the same depth: a common path exists
        if (ia == a.Length && ib == b.Length) return true;
        // One exhausted: the other must be able to match "nothing remaining" (only ** can)
        if (ia == a.Length) return CanMatchEmpty(b, ib);
        if (ib == b.Length) return CanMatchEmpty(a, ia);

        string sa = a[ia], sb = b[ib];

        // ** absorbs any number of path segments from the other side (including zero)
        if (sa == "**") return Check(a, ia + 1, b, ib) || Check(a, ia, b, ib + 1);
        if (sb == "**") return Check(a, ia, b, ib + 1) || Check(a, ia + 1, b, ib);

        // A segment containing * is conservatively treated as matching any single segment,
        // UNLESS both have non-empty literal prefixes that are provably incompatible (neither
        // is a prefix of the other in platform comparison — e.g. "left*" vs "right*").
        if (sa.Contains('*') || sb.Contains('*'))
        {
            string prefixA = sa.Contains('*') ? sa[..sa.IndexOf('*')] : sa;
            string prefixB = sb.Contains('*') ? sb[..sb.IndexOf('*')] : sb;
            if (prefixA.Length > 0 && prefixB.Length > 0)
            {
                bool aIsPrefixOfB = prefixB.StartsWith(prefixA, SegmentComparison);
                bool bIsPrefixOfA = prefixA.StartsWith(prefixB, SegmentComparison);
                if (!aIsPrefixOfB && !bIsPrefixOfA)
                    return false;
            }
            return Check(a, ia + 1, b, ib + 1);
        }

        // Literals: overlap only when equal (platform path-comparison semantics)
        if (string.Equals(sa, sb, SegmentComparison)) return Check(a, ia + 1, b, ib + 1);

        return false;
    }

    private static bool CanMatchEmpty(string[] segs, int from)
    {
        for (int i = from; i < segs.Length; i++)
            if (segs[i] != "**") return false;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="relPath"/> falls within this scope.
    /// Empty scope → <see langword="false"/>; universal scope (<c>**</c>) → <see langword="true"/>;
    /// otherwise the path is matched segment-by-segment against each normalized glob.
    /// </summary>
    public bool IsInScope(string relPath)
    {
        if (IsEmpty) return false;
        if (IsUniversal) return true;
        string normalized = relPath.Replace('\\', '/').TrimStart('/');
        string[] pathSegs = normalized.Split('/');
        foreach (var glob in Globs)
        {
            if (PathMatchesGlob(pathSegs, glob.Split('/')))
                return true;
        }
        return false;
    }

    private static bool PathMatchesGlob(string[] path, string[] pattern)
        => MatchPath(path, 0, pattern, 0);

    private static bool MatchPath(string[] path, int si, string[] pattern, int pi)
    {
        if (pi == pattern.Length && si == path.Length) return true;
        if (pi == pattern.Length) return false;
        string seg = pattern[pi];
        if (seg == "**")
        {
            for (int n = si; n <= path.Length; n++)
                if (MatchPath(path, n, pattern, pi + 1))
                    return true;
            return false;
        }
        if (si == path.Length) return false;
        if (seg.Contains('*')) return MatchPath(path, si + 1, pattern, pi + 1);
        if (!string.Equals(seg, path[si], SegmentComparison)) return false;
        return MatchPath(path, si + 1, pattern, pi + 1);
    }

    private static StringComparison SegmentComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static implicit operator WriteScope(string[] globs) => Parse(globs);
}
