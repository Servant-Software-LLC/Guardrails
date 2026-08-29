# catches: a merged plan-branch HEAD that does not compile - two cleanly-merged halves that each
#          built in isolation and collide in the union (a duplicate definition the AI-merge kept in
#          both regions leaves no conflict marker and only the compiler sees it, #175).
#
# WHY THIS PLAN NEEDS IT, stated accurately: this plan does NOT run parallel chains that fan in. It
# is ONE strictly serial chain (01 -> 02 -> 03 -> 04 -> 05 -> 06). But the collision hazard is real
# and specific here - three tasks declare src/Guardrails.Cli/Ui/LogSiteRenderer.cs in their
# writeScope (01, 02, 05) and two declare src/Guardrails.Cli/Ui/LiveRunObserver.cs (04, 05) - and
# the SERIALISATION is precisely what removes it: each task's segment base already carries its
# predecessor's merged output, so no two tasks ever append the same member to the same file from a
# common base. That is the plan-of-record's own reasoning (section 0: "they share files ... so they
# must be serialized on one chain regardless").
#
# This gate catches the RESIDUAL the serialisation does not cover: a hunk the AI-merge mangles or
# drops at a task boundary, a member that survives in two spellings, a type moved out from under a
# caller. It is the first moment every task's output is compiled together.
#
# scope: LOCAL (no sidecar `scope` key), deliberately. A whole-solution build is a TERMINAL
# POSTCONDITION: at an intermediate union - say task 01's segment merged while task 02's is still
# unsettled - the tree legitimately holds a test file (ServeDiagramTests.cs) driving behaviour the
# next task has not implemented yet. It would still COMPILE in that particular case, but the rule is
# not case-by-case: a whole-solution build tagged scope:"integration" re-runs at every union point
# anywhere in the plan and would red-halt a correct run the first time it did not. That is the #125
# anti-pattern exactly. It runs ONCE, at run end, on the merged HEAD.
#
# The solution file is Guardrails.sln (this repo has no .slnx - MEASURED 2026-08-29: `ls *.sln*` at
# the repo root returns Guardrails.sln and nothing else).
#
# -v q is correct here (a dotnet BUILD): it leaves the compiler errors and strips the rest. It is
# NOT carried onto any dotnet test in this plan - there it would delete the failure detail the #179
# re-emit exists to surface.
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan-branch HEAD - the union of this plan's tasks does not compile together (see the compiler errors above). The shared-file writeScopes are where this plan's version of that failure lands: check src/Guardrails.Cli/Ui/LogSiteRenderer.cs (written by tasks 01, 02 and 05) and src/Guardrails.Cli/Ui/LiveRunObserver.cs (written by tasks 04 and 05) first, then LogServer.cs, OnTheFlyDiagramObserver.cs, ConsoleRunObserver.cs and HtmlDiagramRenderer.cs."
    exit 1
}
exit 0
