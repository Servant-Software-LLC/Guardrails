# catches: a project that builds alone but breaks the solution (e.g. a broken cross-project reference,
#          a leftover reference to a deleted triad type, or an unregistered/half-migrated project) -
#          the whole-solution Release build is the place a whole-solution build belongs. This task is a
#          plain build prerequisite of the terminal integration-gate sink (task 31, the full-suite green
#          gate); task 31 is the SOLE integrationGate sink (plan §3.3 - exactly one).
dotnet build Guardrails.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.sln Release build failed - the parallel-execution work breaks the solution build"
    exit 1
}
exit 0
