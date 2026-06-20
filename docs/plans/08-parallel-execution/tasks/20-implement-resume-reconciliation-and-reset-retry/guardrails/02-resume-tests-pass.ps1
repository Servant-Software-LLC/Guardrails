# catches: resume / reset-retry implemented wrong - ResumeAndResetRetryTests still failing (FF'd commit
#          not read by resume, a stale segment ref treated authoritative - W-1, or a reset-retry that
#          targets preHead instead of taskBase and loses upstream work)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ResumeAndResetRetryTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "ResumeAndResetRetryTests failing - resume reconciliation (W-1) / taskBase reset-retry not implemented to spec"
    exit 1
}
exit 0
