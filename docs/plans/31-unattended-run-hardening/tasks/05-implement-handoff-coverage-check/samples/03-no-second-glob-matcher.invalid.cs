// Sample: the ONE defect 03-no-second-glob-matcher.ps1 exists to catch -> must exit NON-ZERO.
// It is the valid sample with Covers() re-implemented as a local segment-glob matcher. It agrees
// with every fixture in HandoffScopeCoverageTests today, passes all nine pins, and owns a second
// copy of the WriteScope grammar that will silently diverge when WriteScope's rules next move.
using System.Collections.Generic;
using System.Linq;

namespace Guardrails.Core.Loading;

internal static class HandoffScopeCoverage
{
    private static bool IsResolvable(string candidate, IReadOnlyList<string> allScopeEntries)
    {
        string first = candidate.Split('/')[0];
        return allScopeEntries.Any(e => e.Split('/').Contains(first));
    }

    private static bool Covers(string entry, string candidate)
    {
        string[] entrySegs = entry.Split('/');
        string[] candSegs = candidate.Split('/');
        int i = 0, j = 0;
        while (i < entrySegs.Length && j < candSegs.Length)
        {
            if (entrySegs[i] == "**") { return true; }
            if (entrySegs[i] != "*" && entrySegs[i] != candSegs[j]) { return false; }
            i++; j++;
        }
        return i == entrySegs.Length && j == candSegs.Length;
    }
}
