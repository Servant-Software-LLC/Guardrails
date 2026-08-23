# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155), and - the sharper
#          risk on THIS task - a collateral break in the ~2,100 lines of shipped conformance assertions
#          the new tests are appended to. A non-compiling Stage2ConformanceTests.cs exits `dotnet test`
#          non-zero for a reason that has nothing to do with the behaviour under test, so the census in
#          02 would read garbage as a clean red; and task 04, whose writeScope EXCLUDES this test file,
#          could not fix it.
# MEASURED BASELINE 2026-08-23: the integration test project builds green on the wave-2 entry tree, so
# this clause is a REGRESSION guard on this task's own edit - it can only go red because of what this
# task wrote.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Integration.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                 # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out |
        Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "the new provenance tests must COMPILE and FAIL. They do not compile - most likely a stub declaration is missing (AttemptProvenance.RequestedModel in JournalModel.cs, ActionRun.ObservedModel in ActionRunner.cs), the tests reference private harness machinery from outside Stage2ConformanceTests, or an existing method in that file was disturbed."
    exit 1
}
exit 0
