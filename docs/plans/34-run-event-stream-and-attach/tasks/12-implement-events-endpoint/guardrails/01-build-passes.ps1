# catches: a change that does not compile - every downstream guardrail here runs `dotnet test`
#          --no-build, so without this the real failure would surface as a confusing test-host error
#          instead of the compiler's own message.
# Measured baseline (#478): n/a - exit-code check, no required-present clause.
dotnet build Guardrails.sln --nologo -v q 2>&1 | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.sln does not build - fix the compiler errors above before the tests can run"
    exit 1
}
exit 0
