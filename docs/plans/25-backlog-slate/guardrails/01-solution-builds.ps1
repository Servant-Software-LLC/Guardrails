# catches: a merged plan-branch HEAD that does not compile - two cleanly-merged halves that each built
#          in isolation and collide in the union (a duplicate definition the AI-merge kept in both
#          regions leaves no conflict marker and only the compiler sees it, #175). This plan runs THREE
#          chains in parallel and fans them in at task 12, so the first moment all three are compiled
#          together is HERE. The hazard is named in the plan of record section 0: three of the four
#          deliverables want the same observer files, which is why the observer-touching tasks were put
#          on one chain - this gate is what catches the residual the serialisation does not cover.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-solution build is a TERMINAL
# POSTCONDITION: at an intermediate union - say task 06's segment merged while task 09's is still
# unsettled - the tree can legitimately hold test files referencing types a later task has not written
# yet, so a solution build would red-halt a correct run. That is the #125 anti-pattern exactly. It runs
# ONCE, at run end, on the merged HEAD.
# -v q is correct here (a dotnet BUILD): it leaves the compiler errors and strips the rest.
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan-branch HEAD - the union of this plan's tasks does not compile together (see the compiler errors above). Two tasks appending the same member to an observer file is the expected shape of this failure (CS0101, no conflict marker): check ConsoleRunObserver.cs, LiveRunObserver.cs and OnTheFlyDiagramObserver.cs first."
    exit 1
}
exit 0
