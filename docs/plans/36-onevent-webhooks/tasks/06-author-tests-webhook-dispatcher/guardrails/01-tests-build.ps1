# catches: a "red" that is really a BROKEN test. A test file that does not COMPILE exits non-zero
#          identically to one that compiles and fails, so without this the census below would accept
#          garbage as TDD red - and task 07's writeScope EXCLUDES this test file, so it could not
#          repair the compile error either. The run would dead-end at needs-human (#155).
# Also covers the STUB surface, transitively (#176): Guardrails.Core.Tests references
#          Guardrails.Core, so a WebhookEventSink.cs whose stubs do not compile - or an EventDelivery
#          that task 03 never landed - fails HERE, where the message names the missing symbol, rather
#          than later as an unattributable failure in a task that never touched it.
# No -v q on purpose: quiet costs nothing here and the "never -v q" rule for dotnet test exists
#          because it suppresses the very failure block a guardrail re-emits. Not worth the foot-gun.
$ErrorActionPreference = 'Continue'
$log = & dotnet build tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj -c Debug --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compile errors in the authored tests or in the WebhookEventSink stubs ==="
    $log -split "`r?`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The test project does not compile. A TDD red must COMPILE and fail - a test that cannot build is a broken test, not a red one. If the missing symbol is EventDelivery, it belongs to task 03: do NOT define a second copy, emit needsHuman."
    exit 1
}
Write-Output "Test project compiles (and with it the WebhookEventSink stubs it references)."
exit 0
