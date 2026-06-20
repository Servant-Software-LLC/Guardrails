# catches: the §9 triage step implemented wrong - NeedsHumanTriageTests still failing. The load-bearing
#          cases: triage fired on an agent-emitted {needsHuman} or mid-retry (wrong trigger), the exit
#          code read as a verdict / triage blocking the run (not advisory), no task-level feedback.md
#          written, the needs-human message not pointing at it, or auto-file defaulting ON.
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~NeedsHumanTriageTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "NeedsHumanTriageTests failing - the §9 AI-triage-on-needs-human step (exhaustion-only trigger + tool-vs-local feedback.md + needs-human pointer + advisory/never-a-verdict + auto-file-off-by-default) is not implemented to spec"
    exit 1
}
exit 0
