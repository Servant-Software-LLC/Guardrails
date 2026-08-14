# catches: an AI-merge that unioned two siblings' edits into a conflicted or duplicated file - no task's
#          own guardrail re-runs at a union, so a dropped hunk or a duplicated definition is invisible
#          until the build. UNION-SAFE/CONDITIONAL: every check is gated on the artifact being present,
#          so it passes trivially at an intermediate union where a contributing task has not run yet.
$ErrorActionPreference = 'Stop'
$failures = @()
$watched = @(
    'src/Guardrails.Core/Model/PromptRunnerConfig.cs',
    'src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs',
    'src/Guardrails.Core/Loading/RawManifests.cs',
    'src/Guardrails.Core/Model/ActionDefinition.cs'
)
foreach ($rel in $watched) {
    if (-not (Test-Path $rel)) { continue }
    $content = Get-Content -Raw -Path $rel
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $failures += "$rel contains git conflict markers - the union did not cleanly integrate"
        continue
    }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $failures += "$rel is empty in the union - a merge dropped its contents"
    }
}
# Duplicate-definition scan: an AI-merge of two branches that each appended the same new member to
# different regions keeps BOTH copies with no conflict marker (CS0101, invisible until the build).
# Tasks 02 and 06 both declare `src/Guardrails.Core/` as their writeScope, on branches that merge
# independently - so ALL files below are genuine overlapping-writeScope territory (#132), not just the
# config. The two Tier sites were missed on the first pass: task 05 writes the stub member and task 06
# implements over it, so a Tier duplicated at the union is invisible here until the build.
$defs = @{
    'src/Guardrails.Core/Model/PromptRunnerConfig.cs' = @('Kind','Costly','Strength','Specialization')
    'src/Guardrails.Core/Loading/RawManifests.cs'     = @('Tier')
    'src/Guardrails.Core/Model/ActionDefinition.cs'   = @('Tier')
}
foreach ($file in $defs.Keys) {
    if (-not (Test-Path $file)) { continue }
    $c = Get-Content -Raw -Path $file
    foreach ($member in $defs[$file]) {
        $n = ([regex]::Matches($c, ('(?m)^\s*public\s+[^\s]+\s+' + $member + '\s*\{'))).Count
        if ($n -gt 1) { $failures += "$file declares '$member' $n times - an AI-merge duplicated it" }
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
