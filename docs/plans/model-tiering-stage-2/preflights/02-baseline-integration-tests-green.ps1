# catches: a RED Guardrails.Integration.Tests at plan start, misattributed to this plan at the worst
#          possible place. The terminal gate runs `dotnet test Guardrails.sln` - Core AND Integration
#          - and now also runs the Stage2ConformanceTests real-seam suite, which LIVES in the
#          Integration project. So pre-existing Integration breakage would surface as a terminal-gate
#          failure: no retry, delivery withheld, and the blame landing on this plan's changes.
#
#          This is the SECOND area preflight (#181 is deduped one-per-area, not one-per-plan): the
#          first covers Guardrails.Core.Tests, the area wave 1 modifies. This covers the area wave 2
#          rewires and the area the conformance suite lands in.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # FIRST line - the run summary this guard reads is LOCALIZED

# The plan-wide trait stands ALONE here, as in the sibling preflight, and ONLY here (#455): the
# `!=` exclusion means "everything except the tests this plan is about to write".
$filter = 'Category!=TierResolution'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first

# EXIT CODE FIRST, guard second: a test host that never ran exits NON-zero with no summary at all.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the existing tests in tests/Guardrails.Integration.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it, or its terminal gate will fail for a reason this plan did not cause (#181)"
    exit 1
}

# ZERO-MATCH GUARD: exit 0 alone does not mean the area is green - a filter matching nothing, or a
# malformed one, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" counts [Skip]ped).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
