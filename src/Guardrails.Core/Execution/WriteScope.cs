using System;
using System.Collections.Generic;
using System.Linq;

namespace Guardrails.Core.Execution;

/// <summary>
/// Write-scope glob matcher — plan 08 §2.1/§2.2.
/// </summary>
public static class WriteScope
{
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>Returns true if <paramref name="path"/> is claimed by at least one glob in <paramref name="scope"/>.</summary>
    public static bool IsInScope(string path, IReadOnlyList<string> scope)
    {
        if (scope.Count == 0) return false;
        string[] pathSegs = path.Split('/');
        foreach (string glob in scope)
        {
            if (MatchPath(Normalize(glob).Split('/'), 0, pathSegs, 0))
                return true;
        }
        return false;
    }

    /// <summary>Returns true if any path exists that is claimed by both <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static bool Overlaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (string gA in a)
        {
            string[] pA = Normalize(gA).Split('/');
            foreach (string gB in b)
            {
                string[] pB = Normalize(gB).Split('/');
                if (CanOverlap(pA, 0, pB, 0, new Dictionary<(int, int), bool>()))
                    return true;
            }
        }
        return false;
    }

    // A bare entry with no glob metacharacter is normalised file-vs-directory by whether the
    // last segment looks like a FILE (carries an extension: a dot that is neither leading nor
    // trailing) or a DIRECTORY (no dot, or a leading-dot dotfile dir like '.github', or a
    // trailing dot). A directory normalises to '<dir>/**' (plan 08 §2.1(a), truth-table rows
    // 9/10); a file literal is left unchanged and matches segment-for-segment exactly.
    //
    // WS_1 defect was keying on lastSeg.Contains('.'), which mis-classified '.github' (a
    // dotfile DIRECTORY whose only dot is leading) as a file, so '.github/workflows/ci.yml'
    // fell out of scope. The extension test below classifies '.github' as a directory while
    // keeping 'src/Thing.cs' (a real extension) a file literal — preserving the membership-
    // implies-overlap property's literal-path singletons.
    private static string Normalize(string glob)
    {
        if (glob.Contains('*')) return glob;
        int lastSlash = glob.LastIndexOf('/');
        string lastSeg = lastSlash >= 0 ? glob[(lastSlash + 1)..] : glob;
        return HasFileExtension(lastSeg) ? glob : glob + "/**";
    }

    // A segment carries a file extension when it has a '.' that is neither the first nor the
    // last character (so 'Thing.cs' is a file, but '.github' and 'name.' are directories).
    private static bool HasFileExtension(string segment)
    {
        int dot = segment.LastIndexOf('.');
        return dot > 0 && dot < segment.Length - 1;
    }

    // Single-segment wildcard match. Enforces prefix, suffix, and all interior literals between *s.
    // This is the shared primitive used by both IsInScope (via MatchPath) and Overlaps (via CanSegmentsIntersect).
    private static bool MatchSegment(string pattern, string value)
    {
        if (!pattern.Contains('*'))
            return string.Equals(pattern, value, Cmp);

        string[] parts = pattern.Split('*');
        string prefix = parts[0];
        string suffix = parts[^1];

        if (!value.StartsWith(prefix, Cmp)) return false;
        if (!value.EndsWith(suffix, Cmp)) return false;
        if (prefix.Length + suffix.Length > value.Length) return false;

        // All interior literals between consecutive * must appear in order within the middle section.
        string middle = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
        int pos = 0;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            int idx = middle.IndexOf(parts[i], pos, Cmp);
            if (idx < 0) return false;
            pos = idx + parts[i].Length;
        }
        return true;
    }

    // Match normalized glob segments against concrete path segments.
    // ** matches 1+ whole segments; * within a segment matches 0+ chars.
    private static bool MatchPath(string[] pattern, int pi, string[] path, int vi)
    {
        while (true)
        {
            if (pi == pattern.Length && vi == path.Length) return true;
            if (pi == pattern.Length) return false;

            if (pattern[pi] == "**")
            {
                // ** = 1+: skip to after ** and try each position at least 1 segment ahead.
                pi++;
                for (int k = vi + 1; k <= path.Length; k++)
                    if (MatchPath(pattern, pi, path, k))
                        return true;
                return false;
            }

            if (vi == path.Length) return false;
            if (!MatchSegment(pattern[pi], path[vi])) return false;
            pi++;
            vi++;
        }
    }

    // Returns true if there is a concrete string that satisfies both single-segment patterns.
    private static bool CanSegmentsIntersect(string s1, string s2)
    {
        bool s1Star = s1.Contains('*');
        bool s2Star = s2.Contains('*');

        if (!s1Star && !s2Star) return string.Equals(s1, s2, Cmp);
        if (!s1Star) return MatchSegment(s2, s1);
        if (!s2Star) return MatchSegment(s1, s2);

        // Both have wildcards: check prefix/suffix compatibility, build a candidate, verify.
        string[] p1 = s1.Split('*'), p2 = s2.Split('*');
        string pre1 = p1[0], suf1 = p1[^1];
        string pre2 = p2[0], suf2 = p2[^1];

        // Prefix check: one must be a prefix of the other.
        string combinedPre;
        if (pre1.Length >= pre2.Length)
        {
            if (!pre1.StartsWith(pre2, Cmp)) return false;
            combinedPre = pre1;
        }
        else
        {
            if (!pre2.StartsWith(pre1, Cmp)) return false;
            combinedPre = pre2;
        }

        // Suffix check: one must be a suffix of the other.
        string combinedSuf;
        if (suf1.Length >= suf2.Length)
        {
            if (!suf1.EndsWith(suf2, Cmp)) return false;
            combinedSuf = suf1;
        }
        else
        {
            if (!suf2.EndsWith(suf1, Cmp)) return false;
            combinedSuf = suf2;
        }

        // Build candidate: prefix + all interior parts from both + suffix, then verify both patterns match.
        string interior = string.Concat(p1.Skip(1).SkipLast(1).Concat(p2.Skip(1).SkipLast(1)));
        string candidate = combinedPre + interior + combinedSuf;
        return MatchSegment(s1, candidate) && MatchSegment(s2, candidate);
    }

    // Returns true if two normalized glob arrays share at least one common concrete path.
    // Uses memoization; cycles (** staying at same position in both) resolve to false since
    // any real witness is finite and found through non-cyclic transitions.
    private static bool CanOverlap(string[] A, int iA, string[] B, int iB, Dictionary<(int, int), bool> memo)
    {
        if (memo.TryGetValue((iA, iB), out bool cached)) return cached;

        // Mark as false before recursing to break cycles.
        memo[(iA, iB)] = false;

        bool result;
        if (iA == A.Length && iB == B.Length)
            result = true;
        else if (iA == A.Length || iB == B.Length)
            // ** = 1+: remaining ** or segments cannot match the empty suffix required by the exhausted side.
            result = false;
        else
        {
            string sA = A[iA], sB = B[iB];
            if (sA == "**" && sB == "**")
            {
                // Consume 1 shared segment; each ** can independently finish (advance) or continue (stay).
                // The (iA, iB) stay-both case is the cycle already set to false above.
                result = CanOverlap(A, iA + 1, B, iB + 1, memo)  // both done
                      || CanOverlap(A, iA,     B, iB + 1, memo)  // A continues, B done
                      || CanOverlap(A, iA + 1, B, iB,     memo); // A done, B continues
            }
            else if (sA == "**")
            {
                // A's ** absorbs a segment satisfying B's sB; A stays or finishes.
                result = CanOverlap(A, iA,     B, iB + 1, memo)
                      || CanOverlap(A, iA + 1, B, iB + 1, memo);
            }
            else if (sB == "**")
            {
                // B's ** absorbs a segment satisfying A's sA; B stays or finishes.
                result = CanOverlap(A, iA + 1, B, iB,     memo)
                      || CanOverlap(A, iA + 1, B, iB + 1, memo);
            }
            else
            {
                // Both regular segments: must share a common value.
                result = CanSegmentsIntersect(sA, sB)
                      && CanOverlap(A, iA + 1, B, iB + 1, memo);
            }
        }

        memo[(iA, iB)] = result;
        return result;
    }
}
