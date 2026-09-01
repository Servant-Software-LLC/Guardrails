# catches: a re-baseline that does not COMPILE - a mistyped assertion, or (the case this stage invites)
#          an assertion rewritten to name a member this plan has not written yet. A non-compiling test
#          file exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails, so without this
#          the red census in guardrail 02 is gameable by garbage (#155).
#
#          THIS GUARDRAIL IS ALSO THE NO-NEW-API GATE (the #468 demotion order: a property the compiler
#          already carries does not get a regex). Both rewritten assertions are on values the file
#          already holds - `hashAtStart` and `report.AllSucceeded` - so nothing new is needed. If the
#          error names ExecutedDefinitionDivergence, DefinitionHashAtLoad or definitionHashAtSettle, the
#          rewrite reached for a member stages 3, 12 and 13 write LATER: assert on what exists today.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462).
dotnet build tests/Guardrails.Integration.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Integration.Tests does not build - PlanEditedDuringRunTests.cs is not type-correct after the re-baseline. Section 15.1 asks for two assertion SENSES to invert (Assert.NotEqual -> Assert.Equal at :209, Assert.True -> Assert.False at :77) on values the file already holds; it does not ask for any new API. Do NOT add the member - src/** is outside this task's writeScope."
    exit 1
}
exit 0
