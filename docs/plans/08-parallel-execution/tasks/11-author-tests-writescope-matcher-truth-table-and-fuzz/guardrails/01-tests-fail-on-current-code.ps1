# catches: a tautological / empty matcher proof harness - WriteScopeMatcherTests that pass against
#          current code, which has no WriteScope.IsInScope/Overlaps. The tests reference not-yet-existing
#          symbols, so the project will not compile against current code; a non-zero exit (compile OR
#          test failure) proves the 27-row table + fuzz properties actually exercise a real matcher.
#          tests-build is OMITTED (compile-coupled: same missing symbols would fail the build at the
#          same instant).
$file = "tests/Guardrails.Core.Tests/WriteScopeMatcherTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the matcher proof harness was not authored"
    exit 1
}
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~WriteScopeMatcherTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "WriteScopeMatcherTests PASS against current code - the matcher does not exist yet, so the proof harness must fail (or fail to compile)"
    exit 1
}
exit 0
