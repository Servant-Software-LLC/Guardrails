# catches: a Finalize that still delivers machine-decided work — it never consults RunOutcomePolicy, so a
#          best-guess/proceed-unreviewed run still auto-merges to the user's branch (the #340 danger). Cheap
#          STRUCTURAL fast-fail complement to the drive-the-real-run integration proof (which lives on the
#          sink task 10, where the exit code is also wired). Two scoped checks: (1) Scheduler.cs consults
#          RunOutcomePolicy at finalize (SuppressesDelivery); (2) RunReport.cs carries an Unreviewed surface
#          (the 'ran with N unreviewed waves' count). Each check is scoped to the ONE file that owns it.
$scheduler = "src/Guardrails.Core/Execution/Scheduler.cs"
$report    = "src/Guardrails.Core/Execution/RunReport.cs"

if (-not (Test-Path $scheduler)) { Write-Output "$scheduler does not exist"; exit 1 }
if (-not (Test-Path $report))    { Write-Output "$report does not exist";    exit 1 }

$sc = Get-Content -Raw -Path $scheduler
if ($sc -notmatch 'RunOutcomePolicy' -or $sc -notmatch 'SuppressesDelivery') {
    Write-Output "Scheduler.cs Finalize does not consult RunOutcomePolicy.SuppressesDelivery — a run that recorded a proceeded-best-guess/proceeded-unreviewed decision would still auto-deliver machine-decided work to the user's branch (#340). Gate delivery on SuppressesDelivery."
    exit 1
}

$rp = Get-Content -Raw -Path $report
if ($rp -notmatch '(?i)Unreviewed') {
    Write-Output "RunReport.cs carries no unreviewed-wave surface — the 'ran with N unreviewed waves' count (RunOutcomePolicy.ProceededUnreviewedWaveCount) is not surfaced for the CLI to flag + the distinct exit code (task 10) to key on."
    exit 1
}
exit 0
