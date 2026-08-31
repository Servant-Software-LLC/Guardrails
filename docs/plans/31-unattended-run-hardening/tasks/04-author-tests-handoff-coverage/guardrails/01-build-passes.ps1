# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. A non-compiling
#          "test" exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails, so without
#          this the red census in guardrail 02 is gameable by garbage (#155).
#
#          The specific shape for THIS task: naming DiagnosticCodes.HandoffPathUnreachable,
#          DiagnosticCodes.HandoffRowSplitAcrossTasks or the HandoffScopeCoverage type does not
#          compile today - they are stage 5's deliverables. A CS0117/CS0246 here almost always means
#          the "assert on the string literals" constraint was broken; guardrail 03 says so in words,
#          this one makes the failure fast.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (strips restore/banner chatter, keeps the compiler errors); banned only
# on `dotnet test`, where it deletes the failure detail (#462).
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - HandoffScopeCoverageTests.cs is not type-correct. If the error names DiagnosticCodes.HandoffPathUnreachable, DiagnosticCodes.HandoffRowSplitAcrossTasks or HandoffScopeCoverage, you named a symbol stage 5 has not written yet: assert on the string literals 'GR2068' and 'GR2069' instead."
    exit 1
}
exit 0
