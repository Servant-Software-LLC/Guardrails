# catches: a "red" that is really a COMPILE FAILURE (#155). A test file that does not compile makes
#          dotnet test exit non-zero identically to one that compiles and fails, so without this the
#          census below would accept garbage - and the implementation task, whose writeScope excludes
#          the test file, could never fix it. Building FIRST makes the census's non-zero unambiguous.
$out = dotnet build tests/Guardrails.Core.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern ': error' | ForEach-Object { $_.Line } | Select-Object -First 25
    Write-Output ""
    Write-Output "=== Compile errors (re-emitted for the retry feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "The authored test file (or its stubs) does not COMPILE - a TDD red must compile and fail, not fail to build. Add the minimal NotImplementedException stubs the tests need."
    exit 1
}
exit 0
