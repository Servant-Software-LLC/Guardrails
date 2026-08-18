# catches: an integration suite that does not COMPILE being accepted as the red. A non-compiling test
#          exits dotnet test non-zero identically to one that compiles and fails, so without this the
#          sibling red check passes on garbage (#155) - and task 06 could not fix it, because its
#          writeScope excludes these test files.
# -v q is correct on a BUILD; it is FORBIDDEN on dotnet test (dotnet.md 4/4.2, #462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the solution does not compile - the conformance clauses must COMPILE and FAIL, not fail to compile. If the missing symbol is ResolveJudge or JudgeResolution, those are tasks 01/02's deliverables and should already be on this branch; if it is anything else OUTSIDE your write scope, do not edit that file - emit a needsHuman fragment instead."
    exit 1
}
exit 0
