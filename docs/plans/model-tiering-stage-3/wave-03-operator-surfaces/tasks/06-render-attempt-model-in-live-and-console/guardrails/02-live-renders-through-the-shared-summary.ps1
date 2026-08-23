# catches: a LiveRunObserver.AttemptModelResolved that is DECLARED (so the reflection test in
#          AttemptModelRenderingTests goes green) and renders nothing, or renders a second, inlined copy
#          of the wording that drifts from the plain surface's. Its sibling
#          01-rendering-tests-pass proves the formatter and proves the CONSOLE line agrees with it; it
#          cannot prove anything about the LIVE line.
#
# WHY THIS IS A SOURCE-SHAPE CHECK AND NOT A TEST (#468, the demotion gate). The property is "the Spectre
# write happens, and its text is built from the shared summary". A Spectre `AnsiConsole.MarkupLine` issued
# from inside an active Live region has NO observable effect in a non-interactive test host - which is
# precisely why this repo's three existing advisory renderers (VerifierAdvisoryFound, OverwatchNoVerdict,
# DecisionRecorded on LiveRunObserver) have no rendering test either, and why JitBreakdownVisibilityTests
# says in its own header that "no live terminal renders in a non-interactive test, so the public pure
# function IS the test seam". The pure function is already tested. This is the residue that no test can
# reach. Adding a SECOND pure seam (a markup formatter) would only move the same gap one call deeper.
#
# RESIDUAL, stated rather than hidden: the proximity clause matches `MarkupLine` followed within 300
# characters by a call to `AttemptModelSummary(`. Two adjacent but unrelated statements would satisfy it.
# It is a floor on the live renderer, not a proof of it.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD, against the exact file this scans
# (src/Guardrails.Cli/Ui/LiveRunObserver.cs): `AttemptModelResolved` = 0, `AttemptModelSummary` = 0, and
# the proximity pair = 0. On THIS task's entry tree the count for `AttemptModelSummary` is 1 - task 01's
# throwing stub - and the clauses below are still red, because both key on `AttemptModelResolved`, which
# task 01 is forbidden from adding to this class.
$ErrorActionPreference = 'Continue'
$failures = @()

$path = 'src/Guardrails.Cli/Ui/LiveRunObserver.cs'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the subject is gone, so every clause below would scan a null read.
    Write-Output "$path does not exist - this task's own writeScope entry is missing, so nothing was scanned"
    exit 1
}

$raw = Get-Content -Raw -Path $path

# Strip XML-doc, block and line comments before scanning. Every member of this class carries a long doc
# comment, and this task's prompt names both symbols repeatedly - so without the strip, an agent that
# merely documented what it did NOT do would clear both clauses. String literals are deliberately KEPT:
# the required tokens here are C# identifiers in code position, never text, so stripping literals would
# change nothing and only risk mangling the interpolated markup strings this check reads.
$scan = $raw -replace '(?m)^\s*///.*$', ''
$scan = $scan -replace '(?s)/\*.*?\*/', ''
$scan = $scan -replace '(?m)//.*$', ''

# --- clause 1: the class DECLARES the member ------------------------------------------------------
# Anchored on the declaration up to the parameter list, not a bare name: a class implementing an
# interface gets NO member for a default method, so a missing declaration means every call resolves to
# IRunObserver's empty body and the live surface is silently dead.
if ($scan -notmatch 'void\s+AttemptModelResolved\s*\(') {
    $failures += "$path does not DECLARE 'void AttemptModelResolved(' - IRunObserver's default body is an empty method, so an undeclared member means the live UI silently renders nothing for every attempt"
}

# --- clause 2: the rendered markup is BUILT FROM the shared summary --------------------------------
# Multiline-dotall, bounded window, MarkupLine first: the live line must be composed from
# LiveRunObserver.AttemptModelSummary, the same formatter ConsoleRunObserver's agreement test pins - so
# the two operator surfaces cannot report the same attempt differently.
#
# The `(?!string\b)` lookahead excludes the DECLARATION `AttemptModelSummary(string model, ...)` and
# matches only a CALL `AttemptModelSummary(model, ...)`. Without it the clause false-passes whenever the
# agent happens to place the formatter's declaration within 300 characters after any of this file's many
# existing MarkupLine calls - which says nothing about whether the new renderer calls it.
if ($scan -notmatch '(?s)MarkupLine[\s\S]{0,300}AttemptModelSummary\s*\(\s*(?!string\b)') {
    $failures += "$path never calls AttemptModelSummary( within 300 characters of a MarkupLine - the live line is either not rendered at all, or built from a second inlined copy of the wording that will drift from the plain surface (the console line's agreement test pins that surface to the shared formatter; nothing but this pins the live one)"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== live renderer: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Render under _gate via AnsiConsole.MarkupLine, escaping the harness strings with Markup.Escape, exactly as VerifierAdvisoryFound and OverwatchNoVerdict already do in this file - and build the text from AttemptModelSummary rather than restating it."
    exit 1
}
exit 0
