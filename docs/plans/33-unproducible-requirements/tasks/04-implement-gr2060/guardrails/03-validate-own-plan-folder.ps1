# catches: a GR2060 that cannot validate the plan that built it. This plan's own SSOT clauses require
#          content in docs/plans/02-schemas-and-contracts.md, and rows 7 and 8 of the handoff table own
#          that file - so GR2060 must be SILENT on this plan's own gate. If it is not, either the check
#          is wrong or this plan trips the very rule it ships, and both are cheaper to learn here than
#          at a resume three days later (section 11, item 10).
#
# THE SELF-LOCK, stated precisely: the run in flight executes via the INSTALLED CLI, so it does not pick
#          up the newly built code mid-run. The lock arrives at the next `dotnet tool update`. That is
#          exactly why this guardrail builds and invokes the LOCAL binary rather than trusting whatever
#          `guardrails` resolves to on PATH - the installed one cannot see the code this task just wrote.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$planFolder = 'docs/plans/33-unproducible-requirements'
if (-not (Test-Path -LiteralPath $planFolder -PathType Container)) {
    Write-Output ('PRECONDITION: ' + $planFolder + ' not found. This guardrail validates this plan with the binary this task just built; without the folder there is nothing to validate.')
    exit 1
}

# Run the LOCAL CLI, not the installed tool.
$log = & dotnet run --project src/Guardrails.Cli/Guardrails.Cli.csproj -- validate $planFolder 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($log -match 'GR2060') {
    Write-Output ''
    Write-Output 'GR2060 FIRED ON THIS PLAN''S OWN FOLDER. Section 11 item 9: this plan requires SSOT content, and rows 7 and 8 of its handoff table own that file, so a correct GR2060 is silent here. Either the check has a false positive - most likely the union-of-writeScope coverage rule, or the plan-folder exclusion (condition 7) - or a task in this plan asserts SSOT content without a paired task owning the SSOT. Do NOT silence the check to get past this; find which of the two it is.'
    exit 1
}

if ($code -ne 0) {
    Write-Output ''
    Write-Output 'validate did not exit 0 on this plan folder. GR2060 is not the cause (it is not in the output above), so this is a different validation error introduced by this task - most likely the new DiagnosticCodes constant or the PlanValidator call site.'
    exit 1
}

# THE WIRING CLAUSE, and it is why the silence clause above is not enough on its own: an UNWIRED GR2060
# emits nothing, so it satisfies "no GR2060 findings" perfectly. Worse, if GR2060 ever DID fire on this
# folder, unwiring it would be the cheapest way to green - and task 3's ten tests would keep passing,
# because they drive ProducerCoverage directly rather than through the composition root. The call site is
# deliverable 3 of this task (section 13 row 4) and nothing else on this task checks it.
# Anchored on the dotted CALL, not the bare type name (#76), over comment-stripped source.
# Baseline (#478): 0 before this task runs - red on arrival, correct for a not-yet-written call site.
$pvPath = 'src/Guardrails.Core/Loading/PlanValidator.cs'
if (-not (Test-Path -LiteralPath $pvPath)) {
    Write-Output ('PRECONDITION: ' + $pvPath + ' not found.')
    exit 1
}
$pv = [regex]::Replace((Get-Content -LiteralPath $pvPath -Raw), '(?m)//.*$', '')
if ($pv -notmatch 'ProducerCoverage\s*\.\s*\w+\s*\(') {
    Write-Output 'PlanValidator.cs NEVER INVOKES ProducerCoverage - the check is built but UNWIRED, and the GR2060-silence assertion above passes precisely BECAUSE it emits nothing. Task 3 tests drive ProducerCoverage directly, so they stay green too. The one call-site line in PlanValidator is deliverable 3 of this task.'
    exit 1
}

Write-Output 'GR2060 is silent on this plan''s own folder, validate exits 0, and PlanValidator actually invokes ProducerCoverage.'
exit 0
