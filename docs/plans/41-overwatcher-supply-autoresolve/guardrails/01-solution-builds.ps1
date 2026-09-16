# catches: a project that builds alone but breaks the solution once every task has merged - a
#          signature change whose implementers did not all follow (the IRunObserver `by` change is
#          exactly this shape), or a duplicate definition an AI-merge kept.
#          LOCAL, not scope:"integration" (#165): a whole-solution build is a terminal
#          POSTCONDITION. At an intermediate union this plan's merged bytes hold test files
#          referencing types a later implement task has not produced yet, so a union-scoped
#          solution build would FAIL there and roll back a healthy partial merge.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output ""
    Write-Output "The solution does not build on the merged plan HEAD."
    exit 1
}
exit 0
