# catches: a brownfield plan building on a RED base - the EXISTING tests in tests/Guardrails.Core.Tests,
#          the area every code task in this plan modifies (TierResolver/TierResolution, JournalModel,
#          JournalJson, TierProvenance, TaskExecutor), are already failing on the starting code.
#          Asserting them green BEFORE the DAG means a later work task's tests-pass failure is
#          attributable to THAT task rather than to pre-existing breakage, and a new test's red is
#          unambiguous (#181). Re-emits the failure DETAIL at the END so a red baseline's WHY reaches the
#          halt feedback, not just `[FAIL] <name>` (#179, stacks/dotnet.md 4.2 BLOCK capture, #608).
#
# Scope: the touched test project, MINUS this plan's own about-to-be-authored category. This `!=`
#        exclusion is the ONE place the plan-wide trait stands ALONE (#455) - every task-level filter in
#        this plan names its own pair's test class beside it.
#
# Required-present baseline (#478): none. This is a positive / assert-present preflight over EXISTING
#        tests, so it is green on arrival BY CONSTRUCTION - the named exception in SKILL.md Step 7.0a,
#        the same class as every #181 baseline. It goes RED when the starting tree is broken.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED (#455)

# `EscalationLadder` is the trait THIS plan's new tests carry; excluding it keeps the baseline from ever
# going red on tests that do not exist yet. Measured at authoring time: the trait appears 0 times in
# tests/Guardrails.Core.Tests today, so on the starting tree this filter selects the whole existing
# project - which is exactly the pre-plan area this baseline is about.
$filter = 'Category!=EscalationLadder'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary at all,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "the existing tests in tests/Guardrails.Core.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0, and a baseline that executed nothing certifies "the area is green" over an
# empty set (the catalogue's vacuous-baseline warning arriving through a filter typo). Key on the
# EXECUTED count (Passed+Failed; "Total:" would also count [Skip]ped tests), never on
# "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against tests/Guardrails.Core.Tests."
    exit 1
}
exit 0
