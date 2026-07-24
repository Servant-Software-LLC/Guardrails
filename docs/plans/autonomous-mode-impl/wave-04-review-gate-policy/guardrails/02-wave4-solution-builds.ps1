# catches: a wave-04 change that compiles in isolation but breaks the whole solution once every branch
#          merges (the new RunOutcomePolicy, the Scheduler review-gate resolution + finalize flip, the
#          RunReport unreviewed-wave surface, the Overwatch auto-tier gate + SchedulerFactory wiring, the
#          ExitCodes.ProceededUnreviewed constant + RunCommand mapping). Terminal postcondition for the
#          wave — LOCAL (no scope key) so it fires ONCE on the merged wave-04 HEAD, not at every
#          intermediate union (#165 — a whole-solution build is not union-safe: an intermediate union may
#          hold a test referencing a type a not-yet-merged sibling produces). wave-04 is the LAST wave, so
#          this whole-plan terminal build runs on the fully-merged HEAD.
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged wave-04 HEAD — a cross-project compilation error across the run-outcome / review-gate / overwatcher-auto-tier / exit-code branches"
    exit 1
}
exit 0
