# catches: a merged HEAD that does not compile — the union of every task's work building
#          individually while the integration of all of them does not. LOCAL by design
#          (#165): a whole-solution build tagged scope:"integration" would re-run at every
#          partial union, where downstream TDD tasks have not landed, and red-halt a
#          correct run.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The merged HEAD does not build — the union of the plan's work is broken."
    exit 1
}
exit 0
