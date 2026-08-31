# catches: a private inline path matcher inside HandoffScopeCoverage.cs - a second copy of the glob
#          grammar. This is plan 31 section 4.9 pin 8, and the plan is explicit that it is a
#          GUARDRAIL and not a unit test, for a reason worth restating: an inline matcher that happens
#          to agree with every fixture in HandoffScopeCoverageTests passes all nine pins and still
#          owns a duplicate of WriteScope's rules. The nine pins cannot see it; only the source can.
#          When WriteScope's grammar next moves - the #262 dotfile arm is the precedent - one copy
#          moves and the other silently does not.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the ideal instrument is
#          an AGREEMENT property test - enumerate an input domain and assert the two sides agree for
#          every input. It cannot be written here, and not for a scoping reason: there IS no second
#          side to compare against when the implementation is correct. The property is "there exists
#          no second matcher", which is unobservable at runtime by construction - an equivalent copy
#          is behaviourally indistinguishable, which is exactly why it survives review. That makes
#          this the demotion order's last rung, and it ships with a committed .valid/.invalid sample
#          pair in ../samples/.
#
# THE BAN IS ANCHORED ON GLOB *INTERPRETATION*, NOT ON PROXIMITY - and that is a CORRECTION to plan
#          31 section 4.9 pin 8, made because the plan's own formulation was smoke-tested and FAILED.
#          Pin 8 asks for "a Split('/') paired with '*' handling". Implemented as a proximity window,
#          that clause RED-HALTS A CORRECT IMPLEMENTATION: the whole-segment anchor test of section
#          4.4 legitimately splits segments, the glob arm legitimately asks `candidate.Contains('*')`
#          and legitimately builds `"**/" + candidate`, and in the reference implementation those sit
#          within a few hundred characters of each other. Measured at author time against a
#          hand-written correct sample (#302): a 600-character window fired TWICE on it. A guardrail
#          that false-reds the correct answer dead-ends every attempt at needs-human, so the clause
#          was changed rather than the window widened.
#
#          What is actually forbidden is INTERPRETING a glob - comparing a path segment against a
#          wildcard string, which is the one construct a re-implemented matcher cannot avoid and a
#          correct implementation never needs. DETECTING a glob (`Contains('*')`) and CONSTRUCTING one
#          (`"**/" + candidate`) stay legal, because section 4.5 requires both.
#
# TWO-LEVEL STRIP (section 11a): $raw is never matched against and never reassigned. The REQUIRED
#          clause reads $code (comments gone, literals intact). The BANS read $scan (literals gone
#          too), so a doc comment explaining "we do not re-implement the glob grammar" and a message
#          string containing an asterisk are both invisible to them.
#
# MEASURED BASELINES on master @1490d2a: the subject file does not exist, so every clause is 0. After
#          this task, the required clause must be >= 1 and both bans must stay 0. (A forbidden-present
#          clause is exempt from the zero-baseline rule anyway - a ban green before its task has run
#          is a healthy ban, #478 - but the required clause is not, and it measures 0 as it should.)
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'src/Guardrails.Core/Loading/HandoffScopeCoverage.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is this task's primary deliverable; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the shared primitive is called, and called on BOTH arms -----------------------------
# -cmatch, not -match: C# identifiers are case-SENSITIVE, and a case-insensitive require-present clause
# false-GREENS on text C# would never compile (catalogue taxonomy entry 3).
$calls = [regex]::Matches($code, 'WriteScope\s*\.\s*IsInScope\s*\(').Count
if ($calls -lt 1) {
    $failures += "$rel never calls WriteScope.IsInScope(. Section 4.5 says build NO new primitive: the concrete arm is IsInScope(candidate, [entry]) and the glob arm is IsInScope(entry, [candidate]) with the arguments SWAPPED. If your coverage decision is reached without ever calling it, you have re-implemented the glob grammar."
}
elseif ($calls -lt 2) {
    # The floor is 2 because section 4.5 specifies BOTH arms through the primitive: one call on the
    # concrete arm, two on the glob arm (plain, then '**/'-prefixed). Exactly one call is the
    # half-migrated shape - the concrete arm routed and the glob arm hand-rolled - which is the
    # likeliest way a second matcher survives, since the glob arm is the awkward one.
    $failures += "$rel calls WriteScope.IsInScope( exactly once. Section 4.5 routes BOTH arms through it - the concrete arm as IsInScope(candidate, [entry]) and the glob arm as IsInScope(entry, [candidate]) plus IsInScope(entry, ['**/' + candidate]), arguments SWAPPED. One call means one arm is hand-rolled, and the glob arm is the one that matters: getting its direction wrong ships a check that can never fire."
}

# --- FORBIDDEN: interpreting a glob, rather than detecting or constructing one --------------------
# Reads $code (literals INTACT), because the forbidden construct is a comparison against a string
# literal - stripping literals would make this clause unfireable by construction. Anchored on the
# COMPARISON: `seg == "**"` / `seg != "*"` is glob INTERPRETATION and only a re-implemented matcher
# needs it. `candidate.Contains('*')` (a char literal, no comparison operator) and `"**/" + candidate`
# (concatenation) are both required by section 4.5 and both stay legal.
if ($code -cmatch '[=!]=\s*"\*') {
    $failures += "$rel compares a value against a wildcard string literal (an equality or inequality test against a quoted star or double-star). That is glob INTERPRETATION - walking segments and deciding what a wildcard matches - which is the second copy of the WriteScope grammar section 4.9 pin 8 forbids. Detecting a glob with candidate.Contains(char star) and constructing one by prefixing a double-star-slash are both fine and both required; deciding what it MATCHES is WriteScope.IsInScope's job."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== second glob matcher: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "src/Guardrails.Core/Execution/WriteScope.cs is OUTSIDE this task's writeScope. The fix is to CALL it, never to copy it and never to change it."
    exit 1
}
Write-Output "One matcher: HandoffScopeCoverage routes its glob decisions through WriteScope.IsInScope and owns no local segment-glob logic."
exit 0
