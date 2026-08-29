# catches: starting this plan on a RED Guardrails.Integration.Tests. The command tasks add tests to that
#          same project, so a pre-existing failure there would be re-attributed to whichever task ran
#          next - burning its retry budget on breakage it did not cause, and making a new test's "red"
#          ambiguous (red-because-missing vs red-because-already-broken). Scoped with
#          Category!=ModelEvidence to the tests that exist BEFORE this plan writes any: a whole-project
#          run would, mid-plan, compile test files referencing types later tasks have not implemented yet
#          (#165/#176 compile-coupling).
#          Re-emits the failure detail at the END so a red baseline's WHY reaches the halt output (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category!=ModelEvidence'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the halt output) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "Guardrails.Integration.Tests is ALREADY FAILING on the starting code - fix the pre-existing breakage before this plan builds on it. No task has run; nothing was scheduled."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this baseline certified nothing. The --filter '$filter' matched no tests or is malformed; a green start was never actually observed."
    exit 1
}
exit 0
