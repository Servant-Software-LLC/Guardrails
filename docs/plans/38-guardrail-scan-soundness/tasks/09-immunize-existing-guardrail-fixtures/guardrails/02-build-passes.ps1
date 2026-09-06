# catches: an immunization that breaks the build. The fixtures live inside C# raw-string and concatenated
#          literals, so a one-character slip is a compile error, not a test failure. The prompt says "run
#          the three test files afterwards and confirm they still pass" - prose-only prohibitions need a
#          structural backing (#221), and this is it.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # a non-zero dotnet exit is DATA here, not an error

$out = dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not compile - one of the three immunized files has a build error (see above). A botched edit inside a C# string concatenation is a compile break this task cannot otherwise see - it would surface as task 05 failing (misattributed to the registry) or at the terminal gate, reintroducing the #587 tripwire shape this task exists to remove."
    exit 1
}
exit 0
