# catches: a merged plan branch that does not compile - the union of every task's segment can fail to
#          build even when each task built in isolation (a duplicate definition two branches both added,
#          a signature one task changed and another still calls).
# LOCAL (no scope key), deliberately: a whole-solution build is a TERMINAL postcondition. Tagged
# scope:"integration" it would re-run at every intermediate union, where a downstream TDD task has not
# landed yet, and red-halt a correct run (#125/#165).
# Measured baseline (#478): n/a - exit-code check, no required-present clause.
dotnet build Guardrails.sln --nologo -v q 2>&1 | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    Write-Output "the merged plan branch does not build - fix the compiler errors above"
    exit 1
}
exit 0
