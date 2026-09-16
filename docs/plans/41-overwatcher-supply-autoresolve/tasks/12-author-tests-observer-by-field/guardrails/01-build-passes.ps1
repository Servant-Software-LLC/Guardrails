# catches: a test file that does not COMPILE being accepted as TDD "red". A non-compiling test project
#          exits non-zero from dotnet test identically to one that compiles and fails, so without this
#          the census below cannot tell garbage from a real red — and task 13 (whose writeScope EXCLUDES
#          all three test files) could not fix the compile error anyway, dead-ending the run (#155).
#          The SOLUTION is built: this task's three test files span BOTH test projects, and its stub
#          sits in Guardrails.Core, whose six implementers must all still compile against the ADDED
#          member (they will — the two-argument member stays until task 13).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$out = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $out
    Write-Output "The solution does not compile — the TDD red must COMPILE and fail. If the break is in a file outside this task's write scope (an implementer of IRunObserver), the three-argument member was REPLACED rather than ADDED: put the two-argument member back and let task 13 do the swap."
    exit 1
}
exit 0
