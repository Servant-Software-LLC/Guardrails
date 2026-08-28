# catches: a merged plan-branch HEAD that does not compile - two cleanly-merged halves that each built
#          in isolation and collide in the union (a duplicate definition the AI-merge kept in both
#          regions leaves no conflict marker and only the compiler sees it, #175). This plan has a
#          fan-in at task 05, so the first moment the whole thing is compiled together is HERE.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-solution build is a TERMINAL
# POSTCONDITION: at an intermediate union - say task 02's segment merged while task 04's is still
# unsettled - the tree can legitimately hold test files referencing types a later task has not written
# yet, so a solution build would red-halt a correct run. That is the #125 anti-pattern exactly. It runs
# ONCE, at run end, on the merged HEAD.
# -v q is correct here (a dotnet BUILD): it leaves the compiler errors and strips the rest.
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan-branch HEAD - the union of this plan's tasks does not compile together (see the compiler errors above)"
    exit 1
}
exit 0
