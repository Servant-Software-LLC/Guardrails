# catches: a LiteValidateSubsetTests / LiteValidationParityTests file that does not COMPILE - most often a
#          call into LiteScriptHost with a member the design sheet names but the ancestor task spelled
#          differently, or a reach for Guardrails.Cli.PlanProbe / ReviewMarker.Write with the wrong
#          signature. A non-compiling test exits `dotnet test` non-zero IDENTICALLY to one that compiles
#          and fails, so without this the red census in guardrail 02 is gameable by garbage (#155).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). This is a build.
$project = 'tests/Guardrails.Integration.Tests'
$out = dotnet build $project --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    Write-Output ""
    Write-Output "=== compiler errors (re-emitted for the retry tail) ==="
    $out | Where-Object { "$_" -match 'error CS\d+' } | Select-Object -First 30 | ForEach-Object { Write-Output $_ }
    Write-Output "$project does not build. Fix LiteValidateSubsetTests.cs / LiteValidationParityTests.cs against the REAL API of LiteScriptHost.cs, Guardrails.Cli.PlanProbe and ReviewMarker - those files are outside this task's writeScope, so adapt the tests, never the host."
    exit 1
}
exit 0
