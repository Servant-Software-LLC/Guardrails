# catches: Invariant 7's SHIPPED guard being deleted rather than re-based (#193). This task's
#          writeScope deliberately includes tests/Guardrails.Core.Tests/PromptExecutionSupportModelTests.cs
#          so it can OWN that re-baseline instead of being trapped by a pre-existing test it may not
#          edit - but "may edit" is one keystroke from "may delete", and the cheapest way to make a
#          precedence test stop failing is to remove it. That file pins the exact behaviour a
#          single-model user still gets: action.model > the runner block's model > the "(cli default)"
#          sentinel. The coverage may MOVE; it may not evaporate.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Core.Tests/PromptExecutionSupportModelTests.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file no longer exists - it pins the two-level model precedence a single-model (untagged, legacy-path) run still gets, which is Invariant 7's shipped guard. If the seam moved, MOVE the coverage; do not delete the file."
    exit 1
}
$content = Get-Content -Raw $file

if ($content -notmatch '\[(Fact|Theory)\]') {
    $failures += 'the file declares no [Fact]/[Theory] - its tests were removed rather than re-based'
}

# The three precedence rungs, each still asserted SOMEWHERE in this file.
if ($content -notmatch '\(cli default\)') {
    $failures += 'the "(cli default)" sentinel is no longer asserted - it is the display-only value provenance records when nothing configures a model, and dropping its coverage is how a silent provenance gap returns'
}
# A task-level override and a runner-level model must both still appear as distinct model strings.
$modelLiterals = ([regex]::Matches($content, '"claude-[a-z0-9.\-]+"') | ForEach-Object { $_.Value } | Sort-Object -Unique)
if ($modelLiterals.Count -lt 2) {
    $failures += "only $($modelLiterals.Count) distinct model literal(s) remain - the precedence test needs at least two (a task-level override and a runner-level default) to prove which one wins; with fewer, 'wins' is unobservable"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== two-level precedence coverage: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "this task may re-base PromptExecutionSupportModelTests, but not gut it. If ResolveModelForDisplay survived as a pure helper, the cheapest correct answer was to change nothing at all."
    exit 1
}
exit 0
