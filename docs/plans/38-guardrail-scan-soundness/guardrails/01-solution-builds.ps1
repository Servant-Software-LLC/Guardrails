# catches: a plan whose tasks each went green but whose merged HEAD does not compile - the classic
#          AI-merge duplicate-definition / stale-reference outcome. LOCAL by design (no `scope` key):
#          a whole-solution build is a TERMINAL POSTCONDITION and would red-halt a correct partial
#          union where a downstream task has not run yet (#125/#165).
$out = dotnet build Guardrails.sln --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    Write-Output "the merged plan branch does not build - see the compiler errors above"
    exit 1
}
exit 0
