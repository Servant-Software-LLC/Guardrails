# catches: a factory/scheduler wire that constructs a component it cannot inject, changes the
#          SchedulerFactory.Create signature in a way that breaks the CLI call site (RunCommand — out of
#          THIS task's writeScope, so a break here can only be resolved by keeping the signature
#          compatible), leaves an injected component's Scheduler field UNUSED (CS0169), or otherwise fails
#          to compile across projects. The repo builds with TreatWarningsAsErrors=true, so an unused field
#          / unused local is a hard build failure — this is what forces the construct+inject+dispatch to
#          land coherently. A whole-SOLUTION build (not just Core) is required because this task must NOT
#          break the CLI (RunCommand calls SchedulerFactory.Create) even though it cannot edit it.
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build — the SchedulerFactory/Scheduler escalation wire has a compile error (a constructed-but-unused component under TreatWarningsAsErrors, a broken SchedulerFactory.Create call site, or a missing/misused symbol). Fix the wire so every constructed component is injected AND used by the classify-then-act dispatch."
    exit 1
}
exit 0
