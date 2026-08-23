# catches: a "stub" task that quietly does the WORK. Every test in tasks 02/03/04 is red only because
#          these three declarations are inert; implement any of them here and the corresponding test is
#          green on arrival and proves nothing for the life of the plan. The bans below are therefore
#          not tidiness — they are what keeps three downstream TDD reds real.
#
#          Also catches the opposite: a declaration that is missing or misshapen, which would make those
#          tests fail to COMPILE rather than fail behaviourally (#155) — a red for the wrong reason, and
#          one the test-author tasks cannot fix because these files are outside their writeScope.
#
# TWO-LEVEL STRIP (#470): required clauses read comment-stripped source, bans read comment-AND-literal-
# stripped source. The doc comment this task is told to write on AttemptModelSummary legitimately NAMES
# AttemptModelResolved (it explains which event the formatter serves), so a ban over raw text would red a
# correct implementation for documenting itself.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD: every required clause below is 0 and every
# ban is 0 — the correct shape for a task that has not run (a ban green on arrival is a healthy ban, #478).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$failures = @()

function Get-Stripped([string]$path, [switch]$AlsoLiterals) {
    if (-not (Test-Path $path -PathType Leaf)) { return $null }
    $t = Get-Content -Raw -Path $path
    $t = ($t -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    if ($AlsoLiterals) { $t = $t -replace '"(?:[^"\\]|\\.)*"', '""' }
    return $t
}

# --- required: the three declarations exist ------------------------------------------------------
$required = @(
    @('src/Guardrails.Core/Execution/IRunObserver.cs', 'void\s+AttemptModelResolved\s*\(',
      'IRunObserver does not declare AttemptModelResolved - all twelve of this wave''s tests reference it, so none of them would COMPILE'),
    @('src/Guardrails.Cli/Ui/LiveRunObserver.cs', 'static\s+string\s+AttemptModelSummary\s*\(',
      'LiveRunObserver does not declare the shared AttemptModelSummary formatter - the rendering tests call it directly and would not compile'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs', 'IRunObserver\?\s+observer',
      'Stage2PlanHarness.RunAsync does not accept an optional observer - the disclosure tests cannot watch the real attempt loop')
)
foreach ($c in $required) {
    $code = Get-Stripped $c[0]
    if ($null -eq $code) { $failures += "$($c[0]) does not exist"; continue }
    if ($code -notmatch $c[1]) { $failures += "$($c[0]) does not match /$($c[1])/ - $($c[2])" }
}

# --- bans: the declarations are INERT ------------------------------------------------------------
$live = Get-Stripped 'src/Guardrails.Cli/Ui/LiveRunObserver.cs' -AlsoLiterals
if ($null -ne $live) {
    if ($live -match 'void\s+AttemptModelResolved\s*\(') {
        $failures += "LiveRunObserver DECLARES AttemptModelResolved - that is task 06's deliverable, and task 03's LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault is red ONLY while this class has not declared it. Adding it here turns that test green on arrival"
    }
    if ($live -notmatch 'AttemptModelSummary[\s\S]{0,200}NotImplementedException') {
        $failures += "AttemptModelSummary does not throw NotImplementedException - the stub must be inert, or the two Summary_* tests in task 03 are green before task 06 writes the formatter"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== stub seam: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

# --- the widening changed no existing call site --------------------------------------------------
# Stage2PlanHarness is shared with the whole Stage-2 conformance suite. `observer` is optional precisely
# so nothing else moves; this is the check that the optionality actually held.
# NO -v q on a TEST command (#179).
$out = dotnet test tests/Guardrails.Integration.Tests --nologo --filter "FullyQualifiedName~Stage2ConformanceTests" 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($testExit -ne 0) {
    $detail = $out | Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } } else { Write-Output "(no assertion lines matched - read the full log above)" }
    Write-Output "the Stage-2 conformance suite broke. The observer parameter must be OPTIONAL with a default so no existing call site changes; a required parameter breaks every caller."
    exit 1
}

# ZERO-MATCH GUARD (#455): keyed on the EXECUTED count (Passed + Failed), never Total, which counts skips.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - the conformance suite did not run, so nothing was certified."
    exit 1
}
exit 0
