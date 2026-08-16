# catches: a project that builds alone but breaks the solution (a broken reference, an unregistered
#          project, a cross-project compilation error only visible once every wave has merged).
# NOTE: deliberately NOT -c Release. Running this plan from a Release-built local binary locks
# Guardrails.Core.dll, so a Release solution build here false-REDs at the terminal gate - where
# there is no retry and delivery is already withheld. A Debug whole-solution build proves the same
# cross-project compile.
# LOCAL - no scope key (#165): a whole-solution build is a terminal postcondition, not a union-safe
# invariant. At an intermediate union a later wave's tests reference types an unrun task has not
# produced, so an integration-scoped solution build would FAIL there and roll back a correct wave.
dotnet build Guardrails.sln --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "solution build failed on the merged plan-branch HEAD"
    exit 1
}
exit 0
