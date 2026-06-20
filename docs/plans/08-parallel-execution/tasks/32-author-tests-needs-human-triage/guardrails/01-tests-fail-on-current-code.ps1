# catches: tautological tests - NeedsHumanTriageTests that pass against current code, which has no
#          NeedsHumanTriage type and no ai-triage prompt profile. The tests reference the
#          not-yet-existing triage step, so the project will not compile against current code; a
#          non-zero exit (compile OR test failure) proves non-tautology. tests-build OMITTED
#          (compile-coupled).
$file = "tests/Guardrails.Integration.Tests/NeedsHumanTriageTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~NeedsHumanTriageTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "NeedsHumanTriageTests PASS against current code - they are tautological (NeedsHumanTriage / the ai-triage profile do not exist yet)"
    exit 1
}
exit 0
