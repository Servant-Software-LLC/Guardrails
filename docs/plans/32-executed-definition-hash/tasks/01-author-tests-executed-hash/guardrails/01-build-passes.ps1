# catches: a test file that does not COMPILE - garbage, a real type error, or (the case this stage is
#          shaped around) an assertion naming a member THIS PLAN HAS NOT WRITTEN YET. A non-compiling
#          "test" exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails, so without
#          this the red census in guardrail 02 is gameable by garbage (#155).
#
#          THIS GUARDRAIL IS ALSO THE NO-NEW-API GATE, and that is why no separate source-shape check
#          is emitted for it (the #468 demotion order: a property a compiler already carries does not
#          get a regex). Section 15 row 1 requires every assertion to name only members that exist on
#          today's tree - TaskDefinitionHash.Compute, the journal's recorded hash - so that these tests
#          compile and fail with no stub stage in front of them, which is what lets stages 3, 4 and 5
#          legitimately carry no tests/** path. `TaskNode.DefinitionHashAtLoad`,
#          `DefinitionFilesAtLoad`, `definitionHashAtSettle` and
#          `RunReport.ExecutedDefinitionDivergence` do not exist yet: naming any of them is a CS0117
#          here, and the fix is to rewrite the assertion, never to widen the change into src/** (which
#          is outside this task's writeScope and fails the task immediately).
#
#          A CS0122 on TaskDefinitionFiles means the `using Guardrails.Core.Journal;` is missing - it is
#          `internal` and lives in Journal, not Loading, and Guardrails.Core.csproj carries
#          InternalsVisibleTo for both test assemblies.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). This is a build.
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build - ExecutedDefinitionHashTests.cs is not type-correct. If the error names DefinitionHashAtLoad, DefinitionFilesAtLoad, definitionHashAtSettle or ExecutedDefinitionDivergence, you have named a member this plan writes in a LATER stage: rewrite the assertion against what exists today (the journal's recorded definitionHash and the public TaskDefinitionHash.Compute), which is section 15 row 1's stated constraint. Do NOT add the member - src/** is outside this task's writeScope."
    exit 1
}
exit 0
