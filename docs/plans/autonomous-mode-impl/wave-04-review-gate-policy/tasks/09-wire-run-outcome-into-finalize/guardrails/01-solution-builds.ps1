# catches: a finalize wire that compiles in isolation but breaks the solution — e.g. a RunReport surface
#          added with a type mismatch, or a Finalize edit that fails to compile against the shipped
#          decisions accessor. Builds the whole solution (LOCAL — this intermediate wiring task's own
#          attempt; the wave terminal build is the exit gate). Fast-fail complement to the structural check.
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build after the finalize/RunReport wire — a compile error in Scheduler.Finalize or RunReport.cs"
    exit 1
}
exit 0
