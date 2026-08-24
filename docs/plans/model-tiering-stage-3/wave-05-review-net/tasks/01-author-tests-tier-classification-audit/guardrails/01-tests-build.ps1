# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155). A non-compiling
#          test project exits `dotnet test` non-zero for a reason that has nothing to do with the behaviour
#          under test, so the census in 02 would read garbage as a clean red. Build FIRST, cheapest-first.
#
#          ONE project: everything this task authors lands in Guardrails.Core.Tests. The audit reference
#          implementation, its tests and its fixtures are all test-side by design - the ruling for this
#          wave is that no `validate` code and no diagnostic code is allocated, so nothing here reaches
#          src/ and building the integration project would measure a surface this task cannot touch.
# MEASURED BASELINE 2026-08-24: tests/Guardrails.Core.Tests builds green on this wave's entry tree, so this
# clause is a REGRESSION guard on this task's own edits.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Core.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                 # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "tests/Guardrails.Core.Tests does not compile - the new tests must COMPILE and FAIL. A missing symbol here is most likely TierClassificationAudit / TierClassificationFinding / TierClassificationSubject: that stub is YOURS to write (tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs is in this task's writeScope), so add the declaration rather than deleting the assertion that needs it."
    exit 1
}
exit 0
