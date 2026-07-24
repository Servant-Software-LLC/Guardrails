# catches: a union at wave-02's exit that left git conflict markers on any file wave-02 produced/modified
#          (a non-clean integration of the config / validation / CLI branches). Union-safe / CONDITIONAL
#          (#125/#165): gate on each file being present, THEN verify it — passes trivially at a partial
#          merge where a contribution has not landed yet. GR2028 integration re-run for the wave.
#          scope: integration.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$files = @(
    'src/Guardrails.Core/Model/RunConfig.cs',
    'src/Guardrails.Core/Model/AutonomyConfig.cs',
    'src/Guardrails.Core/Loading/RawManifests.cs',
    'src/Guardrails.Core/Loading/PlanLoader.cs',
    'src/Guardrails.Core/Loading/PlanValidator.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs'
)
foreach ($rel in $files) {
    $path = Join-Path $ws $rel
    if (-not (Test-Path $path)) { continue }   # not produced at this union yet — nothing to verify
    $content = Get-Content -Raw -Path $path
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers — the wave-02 union did not cleanly integrate"
        exit 1
    }
}
exit 0
