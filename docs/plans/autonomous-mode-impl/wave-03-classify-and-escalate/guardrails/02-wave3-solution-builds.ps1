# catches: a wave-03 change that compiles in isolation but breaks the whole solution once every branch
#          merges (the new GateClassifier / BlockerRetry / CriticalityJudge / IEscalationSink /
#          FileEscalationSink / AnswerFileConsumer / AutonomyJsonl, the DecisionEntry extension, the
#          PromptComposer injection param, the SchedulerFactory/Scheduler/RunCommand wiring). Terminal
#          postcondition for the wave — LOCAL (no scope key) so it fires ONCE on the merged wave-03 HEAD,
#          not at every intermediate union (#165 — a whole-solution build is not union-safe: an
#          intermediate union may hold a test referencing a type a not-yet-merged sibling produces).
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged wave-03 HEAD — a cross-project compilation error across the classify-then-act / escalation / wiring branches"
    exit 1
}
exit 0
