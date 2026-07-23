# catches: a union at wave-04's exit that left git conflict markers on any file wave-04 produced or
#          modified (a non-clean integration of the run-outcome-policy / review-gate-resolution /
#          overwatcher-auto-tier / finalize / exit-code branches). Union-safe / CONDITIONAL (#125/#165):
#          gate on each file being present, THEN scan it — passes trivially at a partial merge where a
#          contribution has not landed yet. Line-anchored markers only (#187). This is wave-04's GR2028
#          integration re-run. scope: integration. wave-04 is the LAST wave, so whole-build/whole-suite
#          stay LOCAL (see 02/03) and this is the union-safe invariant.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$files = @(
    'src/Guardrails.Core/Execution/RunOutcomePolicy.cs',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Core/Execution/RunReport.cs',
    'src/Guardrails.Core/Execution/Overwatch.cs',
    'src/Guardrails.Core/Execution/SchedulerFactory.cs',
    'src/Guardrails.Cli/ExitCodes.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs',
    'tests/Guardrails.Core.Tests/SchedulerReviewGateTests.cs',
    'tests/Guardrails.Core.Tests/OverwatchAutoTierTests.cs',
    'tests/Guardrails.Integration.Tests/RunOutcomeWiringTests.cs',
    'docs/plans/02-schemas-and-contracts.md'
)
foreach ($rel in $files) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) { continue }   # not produced at this union yet — nothing to verify
    $content = Get-Content -Raw -Path $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers — the wave-04 union did not cleanly integrate"
        exit 1
    }
}
exit 0
