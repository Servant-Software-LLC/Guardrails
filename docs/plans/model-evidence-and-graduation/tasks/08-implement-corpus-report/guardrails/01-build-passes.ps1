# catches: an implementation that does not compile - surfaced here as a build error rather than as an
#          opaque non-zero from the --no-build test command below, which would otherwise read as a test
#          failure and send the retry chasing the wrong thing.
$out = dotnet build tests/Guardrails.Core.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern ': error' | ForEach-Object { $_.Line } | Select-Object -First 25
    Write-Output ""
    Write-Output "=== Compile errors (re-emitted for the retry feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "The test project does not COMPILE with this implementation - reported here as a build error rather than as an opaque non-zero from the --no-build test command that follows."
    exit 1
}
exit 0
