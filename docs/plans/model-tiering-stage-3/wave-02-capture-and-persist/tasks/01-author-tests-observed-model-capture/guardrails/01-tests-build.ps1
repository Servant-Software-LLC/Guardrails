# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155). A non-compiling
#          test project makes `dotnet test` exit non-zero for a reason that has nothing to do with the
#          behaviour under test, so the census in 02 would read garbage as a clean red - and the
#          implementation task, whose writeScope EXCLUDES the test file, could not fix it. Build first,
#          cheapest-first: with the two stub declarations present the project must compile.
# MEASURED BASELINE 2026-08-23: the test project builds green on the wave-2 entry tree, so this clause is
# a REGRESSION guard on this task's own edit - it can only go red because of what this task wrote.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Core.Tests -v q --nologo 2>&1
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
    Write-Output "ObservedModelCaptureTests must COMPILE and FAIL. It does not compile - most likely a stub declaration is missing from ClaudeResult (ClaudeStreamParser.cs) or PromptResult (PromptInvocation.cs), or the test references a member no stub declares."
    exit 1
}
exit 0
