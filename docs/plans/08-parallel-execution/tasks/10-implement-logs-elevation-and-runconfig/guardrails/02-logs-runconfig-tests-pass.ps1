# catches: logs elevation / RunConfig implemented wrong - LogsAndRunConfigTests still failing (log
#          path not under logs/<runId>/, default not 3, or the new config keys not surfaced)
dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~LogsAndRunConfigTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "LogsAndRunConfigTests failing - logs elevation / RunConfig additions not implemented to spec"
    exit 1
}
exit 0
