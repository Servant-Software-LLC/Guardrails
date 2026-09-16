# catches: code that does not compile. Cheapest check first, and it keeps the failure attributable:
#          a broken build here is this task's edit to SuppliedDrain.cs, not a test-runner problem.
#          The SOLUTION is built, not the one project, because the member this task implements is
#          consumed from another project's tests.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The solution does not compile after the SuppliedDrain change — CommitPaths must keep the signature the authored tests call."
    exit 1
}
exit 0
