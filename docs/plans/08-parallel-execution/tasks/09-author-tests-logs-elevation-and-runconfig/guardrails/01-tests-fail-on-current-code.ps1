# catches: tautological tests - LogsAndRunConfigTests that pass against current code (log path still
#          under state/, default still 4, new config keys absent). The tests reference not-yet-existing
#          RunConfig properties, so the project will not compile against current code; a non-zero exit
#          (compile OR test failure) proves non-tautology. tests-build is OMITTED (compile-coupled).
$file = "tests/Guardrails.Core.Tests/LogsAndRunConfigTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test-author task produced no test file"
    exit 1
}
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~LogsAndRunConfigTests" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "LogsAndRunConfigTests PASS against current code - they are tautological (logs elevation / RunConfig additions do not exist yet)"
    exit 1
}
exit 0
