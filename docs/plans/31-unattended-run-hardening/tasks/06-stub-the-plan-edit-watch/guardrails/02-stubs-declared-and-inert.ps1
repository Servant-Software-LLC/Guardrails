# catches: a "stub" task that quietly does the WORK. Every test stage 7 authors is red ONLY because
#          Poll() and Rebaseline() are inert; implement either here and the corresponding test is
#          green on arrival and proves nothing for the life of the plan. The bans below are therefore
#          not tidiness - they are what keeps stage 7's TDD red real, and stage 8's verdict with it.
#
#          Also catches the opposite: a declaration that is missing or misshapen, which would make
#          stage 7's tests fail to COMPILE rather than fail behaviourally (#155) - a red for the wrong
#          reason, and one the test-author task cannot fix because this file is outside ITS writeScope.
#
#          And the third case, which is this plan's own addition: a THROWING CONSTRUCTOR. Stage 7's
#          tests construct the watch in order to call the two methods. If the ctor throws
#          NotImplementedException, every test fails with a construction exception -
#          indistinguishable from "the type is missing" - and the stage-8 implementer learns nothing
#          from the red. Argument validation is fine; only NotImplementedException is banned there.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED
#          clauses read $code (comments gone, literals intact). The INERTNESS clauses read method
#          bodies sliced out of $code, so a doc comment that names NotImplementedException cannot
#          satisfy an inertness clause and cannot trip the constructor ban.
#
# THE BRACE SCAN, not a proximity window: a window (#478's warning) cannot say WHICH member it read,
#          and this file's three members sit within a few hundred characters of each other. The
#          scanner blanks comments and neutralizes braces inside string literals, both
#          length-preserving, so offsets still index the original text.
#
# ZERO-MATCH GUARD, in the form a source check has one: the scan copy's length must equal the
#          source's, and each named member must resolve to a BALANCED body. A member that does not
#          resolve is reported as such rather than silently skipped - a skipped member is a clause
#          that certified nothing.
#
# MEASURED BASELINES on master @1490d2a: the subject file does not exist, so every clause is 0. That
#          is the correct shape for a task that has not run (#478).
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'src/Guardrails.Core/Execution/LivePlanEditWatch.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is this task's ONLY deliverable, and stage 7's tests cannot compile without it."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# The brace-scan copy: comments blanked and literal braces neutralized, both LENGTH-PRESERVING so the
# offsets still index $raw.
$blankKeepingNewlines = { param($m) $m.Value -replace '[^\r\n]', ' ' }
$neutralizeBraces     = { param($m) $m.Value -replace '[{}]', '_' }
$scan = [regex]::Replace($raw,  '/\*[\s\S]*?\*/',   $blankKeepingNewlines)
$scan = [regex]::Replace($scan, '(?m)//[^\r\n]*',   $blankKeepingNewlines)
$scan = [regex]::Replace($scan, '"""[\s\S]*?"""',   $neutralizeBraces)
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"',  $neutralizeBraces)
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"',  $neutralizeBraces)
if ($scan.Length -ne $raw.Length) {
    Write-Output "PRECONDITION: the scan copy ($($scan.Length) chars) desynchronized from the source ($($raw.Length) chars), so every offset below would slice the wrong region. This guardrail is defective for this input - report it rather than reshaping the file."
    exit 1
}

# Returns the ORIGINAL text of the member's body, or $null when it is not found or does not balance.
function Get-MemberBody([string]$pattern) {
    $sig = [regex]::Match($scan, $pattern)
    if (-not $sig.Success) { return $null }
    $open = $scan.IndexOf('{', $sig.Index)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i = $open; $i -lt $scan.Length; $i++) {
        if     ($scan[$i] -eq '{') { $depth++ }
        elseif ($scan[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $raw.Substring($open, $i - $open + 1) }
        }
    }
    return $null
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once - never an exit-1 chain that
# reports one gap per attempt.
$failures = @()

# --- REQUIRED: the whole section 5.2 surface is declared -------------------------------------------
# -cnotmatch on every required clause: C# identifiers are case-SENSITIVE, and a case-insensitive
# require-present clause false-GREENS on text C# would never compile (catalogue taxonomy entry 3).
$required = @(
    @{ Pattern = 'record\s+PlanEditedFile\s*\('
       Why     = "the PlanEditedFile record. Section 5.2 pins it as (string TaskId, string Label, PlanEditKind Kind); stage 7's tests name all three." },
    @{ Pattern = 'enum\s+PlanEditKind'
       Why     = "the PlanEditKind enum. Section 5.2 pins Added / Removed / Modified." },
    @{ Pattern = 'record\s+PlanEdit\s*\('
       Why     = "the PlanEdit record. Section 5.2 pins it as (string TaskId, string OldHash, string NewHash, IReadOnlyList<PlanEditedFile> Files)." },
    @{ Pattern = 'class\s+LivePlanEditWatch'
       Why     = "the LivePlanEditWatch class itself." },
    @{ Pattern = 'IReadOnlyList\s*<\s*PlanEdit\s*>\s+Poll\s*\('
       Why     = "Poll(), returning IReadOnlyList<PlanEdit>. Stage 7's tests call it directly, so a wrong return type is a COMPILE failure they cannot fix." },
    @{ Pattern = 'void\s+Rebaseline\s*\(\s*params\s+string\s*\[\s*\]'
       Why     = "Rebaseline(params string[] taskIds). The params form is load-bearing: section 5.3 requires a plan-wide, no-argument call after each of the five harness writers." }
)
foreach ($r in $required) {
    if ($code -cnotmatch $r.Pattern) {
        $failures += "$rel does not declare $($r.Why) Stage 7's tests are outside this task's writeScope and cannot compile without it, so a missing declaration dead-ends that task, not this one."
    }
}
foreach ($member in @('Added', 'Removed', 'Modified')) {
    if ($code -cnotmatch ('(?<![A-Za-z0-9_])' + $member + '(?![A-Za-z0-9_])')) {
        $failures += "$rel does not declare the PlanEditKind member '$member'. Section 5.2 pins all three."
    }
}

# --- INERT: Poll and Rebaseline throw ------------------------------------------------------------
$mustBeInert = @(
    @{ Name = 'Poll';       Pattern = 'IReadOnlyList\s*<\s*PlanEdit\s*>\s+Poll\s*\(' },
    @{ Name = 'Rebaseline'; Pattern = 'void\s+Rebaseline\s*\(' }
)
foreach ($m in $mustBeInert) {
    $body = Get-MemberBody $m.Pattern
    if ($null -eq $body) {
        $failures += "$rel : could not resolve a balanced body for $($m.Name)(). Either it is declared without one (an expression-bodied member or an interface-style declaration), or the braces do not balance. This guardrail cannot certify its inertness, so it does not pretend to."
        continue
    }
    if ($body -cnotmatch 'NotImplementedException') {
        $failures += "$rel : $($m.Name)() does not throw NotImplementedException. It is stage 8's deliverable; a body that returns an empty list, or that silently does nothing, makes stage 7's tests GREEN ON ARRIVAL and they then prove nothing for the life of the plan."
    }
}

# --- BAN: the constructor does NOT throw NotImplementedException ----------------------------------
# Anchored on the ctor's own signature - `LivePlanEditWatch(` NOT preceded by `class`/`new`.
$ctorBody = Get-MemberBody '(?<!class\s)(?<!new\s)LivePlanEditWatch\s*\(\s*PlanDefinition'
if ($null -eq $ctorBody) {
    $failures += "$rel : could not resolve a balanced body for the LivePlanEditWatch(PlanDefinition) constructor. Section 5.2 pins that signature and stage 7's tests call it; declare it with a body."
}
elseif ($ctorBody -cmatch 'NotImplementedException') {
    $failures += "$rel : the constructor throws NotImplementedException. Stage 7's tests construct the watch in order to call Poll() and Rebaseline(); a throwing ctor makes EVERY one of them fail with a construction exception, which is indistinguishable from 'the type is missing' and tells the stage-8 implementer nothing. Store the PlanDefinition (or ignore it) and return. Argument validation is fine - only NotImplementedException is banned here."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== stub seam: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Stub sound: the section 5.2 surface is fully declared, Poll() and Rebaseline() are inert, and the constructor is not."
exit 0
