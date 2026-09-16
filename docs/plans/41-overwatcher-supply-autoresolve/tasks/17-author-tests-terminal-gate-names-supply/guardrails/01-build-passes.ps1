# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling test project
#          exits dotnet test non-zero identically to one that compiles and fails, so without this the
#          red census below cannot tell garbage from a real red - and the implementation task (task 18,
#          whose writeScope is src/Guardrails.Cli/PlanGuardrailPhase.cs ONLY) could not fix the compile
#          error anyway, dead-ending the run (#155).
#          Subject: tests/Guardrails.Integration.Tests/Supply/SuppliedTerminalGateHaltTests.cs (NEW)
#          The WHOLE solution is built: this file drives PlanGuardrailPhase.EvaluateAsync and the journal
#          model across the Cli and Core assemblies, and the Cli ships no InternalsVisibleTo.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# NOT carried onto any dotnet test line in this folder - see 02/03 (#462).
$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output ""
    Write-Output "The solution does not compile - the TDD red must COMPILE and fail, not fail to build."
    exit 1
}
exit 0
