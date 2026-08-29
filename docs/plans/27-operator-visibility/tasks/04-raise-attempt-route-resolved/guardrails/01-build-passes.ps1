# catches: a contract change that does not COMPILE. This task adds a member to a PUBLIC interface and
#          touches two assemblies (Guardrails.Core and Guardrails.Cli), so its blast radius is the
#          whole solution, not one project.
#
# WHY THE WHOLE SOLUTION AND NOT A PROJECT, stated because every other build check in this plan builds
# tests/Guardrails.Integration.Tests and this one deliberately does not. MEASURED 2026-08-29 with
# `grep -rn ": IRunObserver"`: THIRTY types implement IRunObserver across src/ and tests/, and they are
# split across BOTH test projects - tests/Guardrails.Core.Tests carries seven (EscalationSinkTests,
# OverwatchNoVerdictTests, SchedulerBreakdownPhaseEventsTests, SchedulerDriftAutoResolveTests,
# TopologyM2CleanupTests and two more) that building tests/Guardrails.Integration.Tests would NOT
# compile. A default-interface member is designed not to break them - that is the point of the default
# no-op body - but "designed not to" is a prediction, and this is the check that turns it into a
# measurement. Guardrails.sln is the smallest scope that compiles every implementor.
#
# This ALSO transitively proves the member is DECLARED with the pinned shape, which is why there is no
# separate "IRunObserver declares AttemptRouteResolved" grep in this folder: the raise in
# TaskExecutor.cs (guardrail 02) and the two decorator forwards (guardrail 03) all bind to it, so a
# missing or mis-shaped declaration is a compile error here, not a silent pass. A grep asserting the
# declaration would be a rung-3 check for something the compiler already decides - the #468 demotion
# gate in the direction people forget.
#
# The solution file is Guardrails.sln (this repo has no .slnx - MEASURED 2026-08-29: `ls *.sln*` at the
# repo root returns Guardrails.sln and nothing else). MEASURED 2026-08-29 on the untouched tree: this
# exact command exited 0 in 10 s with 0 warnings, 82 bytes of stdout and 0 bytes of stderr. Seconds,
# not minutes.
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet test in guardrail 04 - there it would delete the failure
# detail the #179 re-emit exists to surface (dotnet.md 4.3).
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build - the AttemptRouteResolved contract change is not type-correct (see the compiler errors above). Two shapes are likely and both are in this task's own diff: (a) CS8604 'possible null reference argument' at the raise site, because Directory.Build.props sets Nullable=enable AND TreatWarningsAsErrors=true, so BOTH route.RunnerName and provenance.Model are nullable there - guard the raise on a pattern, e.g. 'if (route is { RunnerName: { } name } && provenance?.Model is { } m)', rather than silencing it with the null-forgiving operator; (b) a decorator forward whose parameter list does not match the interface member exactly. Do NOT 'fix' either by changing the member's signature: it is pinned by docs/plans/29-model-visibility-ux.md section 4.3 and consumed by tasks 05 and 06."
    exit 1
}
exit 0
