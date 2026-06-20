# catches: the AI-merge worker implemented wrong - AiMergeWorkerTests still failing. The load-bearing
#          cases: the exit code read as a verdict, the blast-radius / marker checks bypassable, or the
#          B-3 colliding-sibling-unconditional re-verify not catching an AI-dropped hunk.
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~AiMergeWorkerTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "AiMergeWorkerTests failing - the AI-merge worker (byte producer + deterministic checks + B-3 re-verify) is not implemented to spec"
    exit 1
}
exit 0
