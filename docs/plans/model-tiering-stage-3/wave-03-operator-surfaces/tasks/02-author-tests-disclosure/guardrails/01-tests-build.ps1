# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155). A non-compiling
#          test project exits `dotnet test` non-zero for a reason that has nothing to do with the behaviour
#          under test, so the census in 02 would read garbage as a clean red. Build FIRST, cheapest-first.
#
#          This task edits exactly ONE file, so a break here is its own - unlike the pre-split task 01,
#          whose six-path scope meant a build failure could come from any of three shared files.
# MEASURED BASELINE 2026-08-23: the integration test project builds green on this wave's entry tree, so
# this clause is a REGRESSION guard on this task's own edit.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Integration.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== Build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "AttemptModelDisclosureTests must COMPILE and FAIL. It does not compile - most likely it references a member no stub from task 01 declares. Task 01 owns those stubs; do NOT edit them from here."
    exit 1
}
exit 0