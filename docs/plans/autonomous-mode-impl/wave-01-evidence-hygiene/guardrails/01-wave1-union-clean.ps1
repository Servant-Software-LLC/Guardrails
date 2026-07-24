# catches: a union at wave-01's exit that left git conflict markers on any file wave-01 produced/modified
#          (a non-clean integration of the parallel evidence-hygiene branches). Union-safe / CONDITIONAL
#          (#125/#165): gate on each file being present, THEN verify it — so it passes trivially at a
#          partial merge where a contribution has not landed yet. This is the GR2028 integration re-run
#          for the wave (a real union invariant, not a tautological exit 0). scope: integration.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$files = @(
    'src/Guardrails.Core/Review/ReviewAttestation.cs',
    'src/Guardrails.Core/Review/ReviewMarker.cs',
    'src/Guardrails.Cli/Commands/MarkReviewedCommand.cs',
    'src/Guardrails.Cli/Commands/PlanHashCommand.cs',
    'src/Guardrails.Cli/CommandFactory.cs',
    '.claude/skills/guardrails-review/SKILL.md'
)
foreach ($rel in $files) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) { continue }   # not produced at this union yet — nothing to verify
    $content = Get-Content -Raw -Path $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers — the wave-01 union did not cleanly integrate"
        exit 1
    }
}
exit 0
