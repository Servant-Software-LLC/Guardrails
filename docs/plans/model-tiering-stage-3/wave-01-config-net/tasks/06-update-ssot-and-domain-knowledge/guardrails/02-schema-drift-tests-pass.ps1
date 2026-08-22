# catches: an SSOT edit that drifts the promptRunners schema block away from the copy the
#          plan-breakdown skill carries. SchemaDriftTests byte-compares the two, and this task's
#          writeScope does NOT include that skill - so a well-meant tidy of the schema block here is a
#          test failure no task in this plan can fix. The task prompt says not to touch that block;
#          this is what makes that instruction enforceable rather than advisory.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the zero-match guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~SchemaDriftTests'
# NO -v q on a TEST command (#179).
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
    Write-Output "SchemaDriftTests is failing - the SSOT's promptRunners schema block no longer matches the plan-breakdown skill's copy. These three codes add NO new config field, so this block should not have been edited at all: revert your change to it. Note the SSOT is eol=lf-pinned, so a line-ending rewrite alone can cause this."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter matching nothing also exits 0, which would certify the drift
# check green without running it. Key on the EXECUTED count (Passed+Failed), never "Total:".
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests; check that SchemaDriftTests still exists under that name."
    exit 1
}
exit 0
