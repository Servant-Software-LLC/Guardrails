# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails, so without
#          this the red signal in guardrail 02 is gameable by garbage (#155). With the build green,
#          02's per-test census reads outcomes the runner actually produced.
#
#          It also catches the specific way this stage goes wrong: a pin that names SalvageFraming or
#          PriorAttemptRef.SalvagePatchPath does not compile today (those members are stage 2 and 3's
#          deliverables), so a CS0117/CS0246 here usually means the "observable artifact only"
#          constraint in the prompt was broken. Guardrail 03 says so in plain words; this one is what
#          makes the failure fast.
#
# BOTH projects, because this stage authors into both and a Core-only build would leave the
# Integration file uncompiled until the census tried to run it.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD - it strips restore/banner chatter and leaves the compiler errors. It is
# banned only on `dotnet test` (#462).
$failures = @()

dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Core.Tests does not build - EscalationSalvageTests.cs is not type-correct. If the error names SalvageFraming, SalvagePatchPath or SalvageRefName, you named a member stage 2/3 has not written yet: drive DependencyContextBuilder.BuildPriorAttempts over a hand-laid log dir and assert on the composed prompt bytes instead."
}

dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $failures += "tests/Guardrails.Integration.Tests does not build - EscalationSalvageTests.cs is not type-correct. If the error names a restrictToScope argument, you called an overload stage 2 has not written yet."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
