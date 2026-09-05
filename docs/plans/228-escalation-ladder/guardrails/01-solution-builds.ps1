# catches: a project that builds alone but breaks the solution after every task has merged - a member
#          added to JournalModel.cs by one task and consumed from TierProvenance.cs/JournalJson.cs by
#          another compile independently in their own segments; only the merged HEAD proves they agree.
#          LOCAL (no scope key, #165): a whole-solution build is a TERMINAL postcondition, not a
#          union-safe invariant - at an intermediate union this plan's test files reference types whose
#          implementation task has not run yet, so an integration-scoped copy would red-halt a correct run.
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "Guardrails.sln does not build on the merged plan-branch HEAD - read the compiler errors above"
    exit 1
}
exit 0
