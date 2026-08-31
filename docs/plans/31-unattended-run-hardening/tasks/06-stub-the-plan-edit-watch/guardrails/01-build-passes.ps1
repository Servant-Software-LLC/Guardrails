# catches: a stub that does not compile. Cheapest-first, and the ordering matters more than usual
#          here: guardrail 02 slices method bodies by matching braces, so a file whose braces do not
#          balance would make its clauses report "could not resolve a balanced body" - technically
#          correct but a worse diagnosis than the compiler's. This runs first so the compiler speaks.
#
#          A stub that does not compile is also the worst possible handoff: stage 7's tests are
#          outside its own writeScope, so a broken type here dead-ends THAT task rather than this one.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (strips restore/banner chatter, keeps the compiler errors); banned only
# on `dotnet test`, where it deletes the failure detail (#462).
dotnet build src/Guardrails.Core --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Core does not build with LivePlanEditWatch.cs. Check the namespace (Guardrails.Core.Execution, matching its neighbours), that PlanDefinition and IReadOnlyList are in scope, and that the two records and the enum are declared at namespace level rather than nested inside the class."
    exit 1
}
exit 0
