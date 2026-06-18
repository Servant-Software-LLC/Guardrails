# catches: an over-reaching M7 that guts the #48 single-writer-per-key foreign-key reject behavior
#          while leaving its comments and enum names intact - a bare token grep (single-writer,
#          ForeignKey) passes on those leftovers even if the reject LOGIC is gone. Run the behavioral
#          tests instead: StateManagerTests owns every foreign-key/arbitrary-shared-key rejection test
#          (MergeFragment_ForeignTaskIdKey_IsRejected_*, MergeFragment_ArbitrarySharedKey_IsRejected_*),
#          so the filter selects exactly the #48 reject suite.
dotnet test Guardrails.sln -c Debug --filter "FullyQualifiedName~StateManager" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "single-writer-per-key (#48) foreign-key rejection tests fail - the state-fragment reject logic was altered/removed by the triad retirement; #48 must be preserved."
    exit 1
}
exit 0
