# catches: a test file that does not COMPILE being accepted as the TDD "red" (#155). A non-compiling
#          test exits dotnet test non-zero identically to one that compiles and fails, so without
#          this the sibling red check would pass on garbage - and the implementation task could not
#          fix it, because its writeScope excludes this test file.
# Builds the whole solution: this task adds members to production types, and a stub that breaks a
# consumer's compile would otherwise surface only at the wave union gate.
# -v q is correct on a BUILD; it is FORBIDDEN on dotnet test (dotnet.md 4/4.2, #462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not compile - JudgeProvenanceSchemaTests must COMPILE and FAIL, not fail to compile. Read the compile errors above: they name the exact symbol. If the missing symbol is OUTSIDE your write scope, do not edit that file - emit a needsHuman fragment to the state-out path instead."
    exit 1
}
exit 0
