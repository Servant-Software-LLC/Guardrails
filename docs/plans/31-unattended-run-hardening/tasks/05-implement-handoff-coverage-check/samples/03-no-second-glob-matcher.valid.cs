// Sample: a CORRECT shape for 03-no-second-glob-matcher.ps1 -> the guardrail must exit 0.
// Stage into a scratch tree at src/Guardrails.Core/Loading/HandoffScopeCoverage.cs.
//
// Built from the trap: the whole-segment ANCHOR TEST legitimately needs Split('/'), and the messages
// legitimately contain '*' and '**'. Both are here. What is absent is the PAIRING - every glob
// decision is routed through WriteScope.IsInScope, so segment splitting never sits beside glob
// handling. A bare Split('/') ban would have red-halted this correct file on arrival.
using System.Collections.Generic;
using System.Linq;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Loading;

internal static class HandoffScopeCoverage
{
    // The anchor test: a candidate is resolvable when its FIRST segment equals a WHOLE segment of
    // some writeScope entry. This is the one place segments are split, and it does no glob work.
    private static bool IsResolvable(string candidate, IReadOnlyList<string> allScopeEntries)
    {
        string first = candidate.Split('/')[0];
        return allScopeEntries.Any(e => e.Split('/').Contains(first));
    }

    // Both arms route through the shared primitive. The glob arm swaps the arguments, because
    // IsInScope globs the SCOPE side and splits the PATH literally.
    private static bool Covers(string entry, string candidate)
    {
        if (candidate.Contains('*'))
        {
            return WriteScope.IsInScope(entry, new[] { candidate })
                || WriteScope.IsInScope(entry, new[] { "**/" + candidate });
        }
        return WriteScope.IsInScope(candidate, new[] { entry })
            || entry == candidate
            || entry.EndsWith("/" + candidate);
    }
}
