# catches: a per-tier aggregation whose behaviour deviates from the tests THIS task pair owns - in
#          particular an implementation that reports every rung correctly but emits a section (or an
#          `untiered:` bucket) on a tiering-INACTIVE run, which would change the run summary of every
#          existing single-model user who never opted into any of this.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~PerTierSpendTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the #230-lite per-tier aggregation is not to DoR 9.3. If a suppression test is the failing one, a tiering-INACTIVE journal must summarize to NOTHING - not an empty section and not an `untiered` bucket (see failure details above)."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter matching nothing, or malformed, also exits 0.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
