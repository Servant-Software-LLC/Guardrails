# catches: a test file that does not COMPILE. Without this, the red proof in 02 is worthless: a test
#          project that fails to build exits non-zero identically to one that compiles and fails, so
#          garbage would read as TDD red (#155). With the build green, a failing test in 02
#          unambiguously means the tests RAN and FAILED against a validator that does not yet emit
#          these codes. The implementation task's writeScope excludes this file, so a compile error
#          left here is a dead end no downstream task can fix.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md §4); never on a TEST command (#179).
$out = dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -v q --nologo 2>&1
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
    Write-Output "PinAndTierCoexistTests does not compile - the tests must COMPILE and FAIL; not compiling is the mistake to fix"
    exit 1
}
exit 0
