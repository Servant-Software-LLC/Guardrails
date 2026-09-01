# catches: a project that builds alone but breaks the SOLUTION - a cross-project compilation error the
#          per-task build guardrails cannot see, because each of them builds a bounded set. Four of this
#          plan's changes are exactly that shape:
#            - TaskNode and WaveNode gain properties (stages 3, 9) that 27 hand-built nodes across 21 test
#              files must still compile against;
#            - LivePlanEditWatch.IsEditorArtifact goes private -> internal (stage 5) and is then called
#              from the Scheduler's divergence gate (stage 13), in the same assembly but across a
#              visibility boundary the test assemblies also see;
#            - RunJournal's three recorders gain an optional parameter (stage 12) whose call sites include
#              Guardrails.Cli;
#            - RunReport.AllSucceeded gains a term (stage 13) read by the Cli's exit-code and summary
#              rendering (stage 15) across the Core/Cli boundary, where there is NO InternalsVisibleTo.
#          A CS0122, CS0117 or CS9035 in any of those pairs surfaces only here.
#
# LOCAL - no `scope` key, deliberately (#165). A whole-solution build is a TERMINAL POSTCONDITION, not a
#         union-safe invariant: at an intermediate union this plan's merged bytes contain test files
#         asserting behaviour whose implementation stage has not merged yet, so the solution build FAILS
#         there and the harness rolls a correct wave back. It belongs in the terminal gate's own attempt,
#         once every upstream task has merged. The decision test - "would this pass on a partial merge with
#         a downstream task unsettled?" - answers NO, so it stays local.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). This is a build.
dotnet build Guardrails.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged plan HEAD. Look first at the four cross-file pairs this plan creates: the two model records against the 27 hand-built nodes in tests/**, LivePlanEditWatch.IsEditorArtifact called from the Scheduler gate, RunJournal's new optional recorder parameter called from Guardrails.Cli, and RunReport.AllSucceeded's new term read by RunCommand across the Core/Cli boundary."
    exit 1
}
exit 0
