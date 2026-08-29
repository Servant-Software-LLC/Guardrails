# catches: a merged plan-branch HEAD that does not compile. Say plainly what this gate is and is not
#          for THIS plan, rather than importing a parallel plan's drama: the four work tasks form ONE
#          STRICTLY SERIAL CHAIN (01 -> 02 -> 03 -> 04 -> 05), there is no fan-in, no two tasks run
#          concurrently, and no observer file is touched. So the classic hazard - two sibling segments
#          each appending the same definition to a shared file, which an AI-merge keeps in BOTH regions
#          with no conflict marker and only the compiler sees (#175, CS0101) - CANNOT arise here. Each
#          task's segment is based on its predecessor's already-merged output.
#
#          The honest residual is thinner, and it is this: NO TASK IN THIS PLAN EVER COMPILES THE WHOLE
#          SOLUTION, AND NO BUILD IN THIS PLAN EVER RUNS ON THE MERGED PLAN-BRANCH HEAD. Every task
#          builds the smallest project that covers its own diff, inside its own segment worktree -
#            task 01, 02  ->  dotnet build tests/Guardrails.Core.Tests   (Core.Tests + Core)
#            task 03      ->  dotnet build src/Guardrails.Cli            (Cli + Core)
#            task 04      ->  dotnet build tests/Guardrails.Integration.Tests (Integration.Tests + Cli + Core)
#            task 05      ->  nothing at all; it is a documentation task
#          - so the LAST task to merge compiles nothing, the last task that does compile (04) never
#          touches tests/Guardrails.Core.Tests, and the exact bytes that `mergeOnSuccess` will deliver
#          to the user's branch have been compiled by no one. This gate is that compile, once, on those
#          bytes. The one path two tasks both write - src/Guardrails.Core/Samples/SampleVerifier.cs,
#          the stub from task 01 superseded by the implementation from task 02 - is likewise compiled
#          here in its surviving form against every project in the solution, which is the closest this
#          plan comes to a union hazard.
#
# The solution file is Guardrails.sln (there is no .slnx in this repo) and it carries exactly four
# projects: src/Guardrails.Core, src/Guardrails.Cli, tests/Guardrails.Core.Tests,
# tests/Guardrails.Integration.Tests. MEASURED 2026-08-29: `dotnet build Guardrails.sln --nologo -v q`
# exits 0 in ~15 s with 0 warnings on the untouched tree, so this gate is cheap.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-solution build is a TERMINAL
# POSTCONDITION: at an intermediate union - say task 01's segment merged while task 02's is still
# unsettled - the tree legitimately holds a test file asserting against a stub that throws, and a
# solution build could red-halt a correct run. That is the #125 anti-pattern exactly, and it applies to
# a serial chain as much as to a parallel one, because the harness re-verifies the integration set at
# EVERY union point regardless of topology. This runs ONCE, at run end, on the merged HEAD.
# -v q is correct here (a dotnet BUILD): it leaves the compiler errors and strips the rest.
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan-branch HEAD - the union of this plan's tasks does not compile together (see the compiler errors above). No task in this plan compiled the whole solution, so this is the first time all four projects have seen these bytes: check src/Guardrails.Core/Samples/SampleVerifier.cs (written by tasks 01 and 02, so the only file whose surviving version came from a supersede) and src/Guardrails.Cli/CommandFactory.cs first."
    exit 1
}
exit 0
