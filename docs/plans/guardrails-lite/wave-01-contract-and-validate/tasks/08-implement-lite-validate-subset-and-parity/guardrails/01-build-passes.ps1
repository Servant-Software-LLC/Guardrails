# catches: a test project that no longer compiles at this task's base - the forward tests-pass checks
#          below run with --no-build, so without a fresh build they would execute a STALE test binary
#          and certify yesterday's code. (This task edits only .psm1/.ps1, so a red here almost always
#          means a merged ancestor broke the build, which the message says.)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# -v q is correct on a BUILD; it is banned only on `dotnet test` (#462).
$project = 'tests/Guardrails.Integration.Tests'
$out = dotnet build $project --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    Write-Output ""
    Write-Output "=== compiler errors (re-emitted for the retry tail) ==="
    $out | Where-Object { "$_" -match 'error CS\d+' } | Select-Object -First 30 | ForEach-Object { Write-Output $_ }
    Write-Output "$project does not build. This task only edits scripts/lite/validate/ - a C# compile error here is outside its writeScope; if it names a test file, escalate with needsHuman rather than editing it."
    exit 1
}
exit 0
