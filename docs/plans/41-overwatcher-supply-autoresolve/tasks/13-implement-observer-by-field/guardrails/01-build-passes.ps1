# catches: code that does not compile. Cheapest check first, and on THIS task it is also the census of
#          implementers: deleting the two-argument interface member is what makes the compiler name
#          every type that still declares it. A break here in a file outside this task's write scope
#          means an implementer was missed — report it rather than editing that file.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The solution does not compile after the SuppliedResourcesCommitted replacement — every implementer and the one Scheduler call site must move to the three-argument form together."
    exit 1
}
exit 0
