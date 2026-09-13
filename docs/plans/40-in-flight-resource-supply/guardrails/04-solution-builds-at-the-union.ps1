# catches: a union that merged two siblings' work into a tree that does not COMPILE. Until this
#          existed the entire per-union re-verify set (SSOT §4.3) was one conflict-marker scan,
#          while five tasks shared one directory scope in Guardrails.Core/Execution and three
#          shared Guardrails.Cli — the colliding-sibling shape #132's accepted residual is about.
#          A dropped hunk or a duplicated definition on a shared file leaves no conflict marker.
#
#          UNION-SAFE, and NOT the #125 anti-pattern the sibling whole-suite check would be: this
#          plan pairs every test file with its stub IN THE SAME TASK, so a partial merge never
#          contains a test referencing a type no task has landed. A whole-SUITE run would still
#          be a terminal postcondition (a test can be red at a union pending its implement task),
#          which is why that one stays LOCAL in 02-all-tests-pass.ps1 and only this BUILD is
#          tagged for the union.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if (-not (Test-Path 'Guardrails.sln')) {
    # Union-safe: nothing to build here yet.
    exit 0
}

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output ""
    Write-Output "The UNION does not build. This is a merge-integration failure, not a task failure:"
    Write-Output "each contributing task built in its own segment. Look for a hunk the AI-merge dropped,"
    Write-Output "or the same definition added twice on a file two siblings both write (CS0101)."
    exit 1
}
exit 0