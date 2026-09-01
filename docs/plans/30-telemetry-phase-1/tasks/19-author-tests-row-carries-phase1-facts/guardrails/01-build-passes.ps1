# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. Both shapes this
#          file depends on have already landed (03 added the journal members it reads FROM, 04a added
#          the thirteen row columns it asserts ON), so these tests MUST compile; a non-compiling "test"
#          exits dotnet test non-zero IDENTICALLY to a failing one, so without this the red signal that
#          guardrail 02 reads would be gameable by garbage (#155).
#
#          There is a second, sharper thing it catches here, and it is why this guardrail matters more
#          on this task than on its siblings. tests/Guardrails.Core.Tests compiles as a WHOLE, so a test
#          file naming a column the row does not have does not fail its own test - it fails the PROJECT,
#          for every sibling task whose segment is based on a tree where this file landed. An earlier
#          draft of this plan made that the intended red; 04a exists to remove it. If this guardrail
#          fails, the shape task did not do its job, and the right move is to escalate rather than to
#          add the missing member from here - which is why the failure text says so.
#
# Cheapest-first: this runs before the per-test census in 02, so a compile error is diagnosed as a
#          compile error rather than being reported as eight unbound behaviours.
#
# -v q is correct on a `dotnet build` and only there (#462): the "never -v q" rule governs test commands,
#          whose Error Message/Expected/Actual block the flag deletes. Build errors are the build's own
#          stdout and survive it.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this guardrail builds the Core test project and cannot run without it."
    exit 1
}

$log = & dotnet build $project --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compilation errors (re-emitted so they land in the harness feedback tail) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "tests/Guardrails.Core.Tests does not build. These tests must COMPILE and FAIL AT RUNTIME - failing is intentional, not compiling is a mistake to fix. If an error says TelemetryRow has no definition for one of the thirteen Phase-1 columns, or that a journal member is missing, then a SHAPE task did not land what it owns (04a owns the row columns, 03 owns the journal members) - do NOT add the member yourself, it is outside this task's writeScope. Write {`"needsHuman`": {`"question`": `"<what is missing>`", `"kind`": `"blocked-work`"}} to the state-out path and stop."
    exit 1
}

Write-Output "Core test project builds - the tests bind against the journal members and the corpus columns their shape tasks landed."
exit 0
