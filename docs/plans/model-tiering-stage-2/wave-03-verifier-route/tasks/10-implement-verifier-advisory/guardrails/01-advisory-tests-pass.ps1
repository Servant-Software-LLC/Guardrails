# catches: an implementation whose behaviour deviates from the tests THIS task pair owns.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone (#455).
#          Re-emits the assertion lines at the END so they reach the retry tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
# The SAME $filter string as the pair's inverse check, copied verbatim so the two cannot drift.
$filter = 'Category=TierResolution&FullyQualifiedName~VerifierAdvisoryTests'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
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
    Write-Output ""
    Write-Output "the verifier advisory is not to DoR 6.5. If a de-duplication test is the failing one: record into provenance ALWAYS, but emit a log line ONLY when the observed pair differs from the preflight's prediction. Three surfaces each shouting one condition is how an advisory trains an operator to ignore it."
    exit 1
}

$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}
exit 0
