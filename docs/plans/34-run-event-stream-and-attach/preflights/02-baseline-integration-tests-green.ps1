# catches: a brownfield plan building on a RED base - the EXISTING tests in tests/Guardrails.Integration.Tests, which this
#          plan's tasks will extend, are already failing on the starting code. Asserting them green
#          BEFORE the DAG means a later task's tests-pass failure is attributable to THAT task, not to
#          pre-existing breakage, and a new test's red is unambiguous (#181). Re-emits the failure
#          DETAIL at the END so a red baseline's WHY reaches the halt feedback (#179, dotnet.md 4.2).
# Scoped to the AREA and EXCLUDING this plan's about-to-be-authored Category=RunEvents tests - a
# whole-project run would hit the #165/#176 compile-coupling trap once the TDD tasks land.
# Required-present baseline (#478): n/a - this is a positive/assert-present preflight (green on
# arrival by design, the class Step 7.0a exempts).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category!=RunEvents'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the existing tests in tests/Guardrails.Integration.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
