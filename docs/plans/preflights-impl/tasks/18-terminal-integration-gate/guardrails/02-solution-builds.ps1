# catches: a project that builds alone but breaks the whole solution after all tasks merge (a cross-project
#          break, an unregistered file, a warning under TreatWarningsAsErrors=true). LOCAL (no scope) - a
#          full solution build is a terminal postcondition, run only in the gate's own attempt on the fully
#          merged HEAD, never at an intermediate union where a downstream TDD task has not run yet (#165).
dotnet build Guardrails.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "full solution build failed on the merged plan-branch HEAD"
    exit 1
}
exit 0
