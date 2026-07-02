# catches: the parallel-execution work regressing anything across the suite - the triad-teardown
#          re-baseline, a cross-milestone interaction, or skill/example (golden round-trip) drift. The
#          whole-suite green is terminal-only (the catalogue's rule); every per-task guardrail above was
#          filtered to its own area precisely so this is the one place the whole suite runs.
dotnet test Guardrails.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "the full test suite has failures after the parallel-execution work"
    exit 1
}
exit 0
