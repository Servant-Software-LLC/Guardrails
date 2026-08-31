# catches: a project that builds alone but breaks the SOLUTION - a cross-project compilation error the
#          per-task build guardrails cannot see because each one builds a single project. Three of this
#          plan's changes are exactly that shape: RetryPolicy.AppendSalvageSection / AppendHeader go
#          private -> internal (stage 2) and are then called from PromptComposer (stage 3) in the same
#          assembly; DiagnosticCodes gains two constants (stage 5) that PlanValidator consumes; and
#          LivePlanEditWatch (stages 6/8) is consumed by Scheduler, RunReport and RunCommand ACROSS the
#          Core/Cli assembly boundary. A CS0122/CS0117/CS0246 in any of those pairs surfaces only here.
#
# LOCAL - no `scope` key, deliberately (#165). A whole-solution build is a TERMINAL POSTCONDITION, not
#         a union-safe invariant: at an intermediate union this plan's merged bytes contain test files
#         referencing behaviour whose implementation task has not merged yet, so the solution build
#         FAILS there and the harness rolls a correct wave back. It belongs in the terminal gate's own
#         attempt, once every upstream task has merged. The decision test - "would this pass on a
#         partial merge with a downstream task unsettled?" - answers NO, so it stays local.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). This is a build.
dotnet build Guardrails.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan HEAD. Look first at the three cross-file pairs this plan creates: RetryPolicy's internal AppendSalvageSection/AppendHeader called from PromptComposer, DiagnosticCodes' GR2068/GR2069 constants consumed by PlanValidator, and LivePlanEditWatch consumed from Scheduler/RunReport/RunCommand (a Core -> Cli boundary)."
    exit 1
}
exit 0
