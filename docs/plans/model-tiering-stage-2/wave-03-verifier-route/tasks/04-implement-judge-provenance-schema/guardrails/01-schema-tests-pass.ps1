# catches: an implementation whose behaviour deviates from the tests THIS task pair owns.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455):
#          a trait-only filter asserts the state of every test in the plan, so this task could not go
#          green until tasks it does NOT depend on had run - a deadlock validate and graph --check
#          cannot see. Re-emits the assertion lines at the END so they reach the retry tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The SAME $filter string as the pair's inverse check, copied verbatim so the two cannot drift.
$filter = 'Category=TierResolution&FullyQualifiedName~JudgeProvenanceSchemaTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block, so the re-emit
# below would have only test NAMES to re-emit and #179 is defeated by the flag alone (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "the judge provenance schema does not round-trip to DoR 12.4. If the ABSENT-when-null test is the failing one, the member needs [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] - emitting judge: null on every script attempt is new noise in run.json for users who never opted into any of this."
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
