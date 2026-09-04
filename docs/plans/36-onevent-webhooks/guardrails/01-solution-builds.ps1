# catches: the plan branch merging to a tree that does not compile. Three tasks add stub members that a
#          later task fills (EventDelivery + the two RunEventStream ctor params, the WebhookEventSink
#          statics, the RunCommand options), and a stub left unfilled or a call site left unmigrated
#          only shows up once every contribution is on one tree.
#          LOCAL (no scope key) on purpose: a whole-solution build is a TERMINAL postcondition, and
#          tagging it scope:"integration" would re-run it at every intermediate union where a
#          downstream TDD task has not landed yet, red-halting a correct run (#125/#165).
$ErrorActionPreference = 'Continue'
$log = & dotnet build Guardrails.sln -c Debug -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Build errors ==="
    $log -split "`r?`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The merged plan branch does not compile. A stub member was left unfilled, or the RunCommand/BuildObserverChain signature change left a call site unmigrated."
    exit 1
}
Write-Output "Solution builds."
exit 0
