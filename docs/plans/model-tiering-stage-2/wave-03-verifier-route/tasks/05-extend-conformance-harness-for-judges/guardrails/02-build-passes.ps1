# catches: a harness extension that does not COMPILE. The harness is the real-seam host every wave-2
#          clause already runs on and every wave-3 clause will run on, so a non-compiling harness
#          takes the whole Integration test project down - and task 06, whose writeScope excludes
#          this file, could not fix it.
# -v q is correct on a BUILD; it is FORBIDDEN on dotnet test (dotnet.md 4/4.2, #462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output ""
    Write-Output "the solution does not compile after extending Stage2PlanHarness. Read the compile errors above: they name the exact symbol. Wave 2's nine conformance clauses run on this harness and must keep compiling - this is an extension, not a replacement."
    exit 1
}
exit 0
