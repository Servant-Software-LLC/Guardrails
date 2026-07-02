# catches: a union that left unresolved git conflict markers in the merged bytes — the
# deterministic verdict on the merged tree's own bytes, never git's no-conflict signal alone and
# never an AI's say-so. Genuinely executable (not simulated); mirrors examples/parallel-hello's
# reference union-invariant shape (the re-homed GR2018 whole-repo re-run, form 2 — for plans with
# no build/test tool to invoke at all).
$ErrorActionPreference = 'Stop'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$outDir = Join-Path $ws 'out'
if (-not (Test-Path $outDir)) {
    # No out/ yet at this terminal gate is fine for this illustrative sample plan — nothing to verify.
    exit 0
}

foreach ($file in Get-ChildItem -Path $outDir -Recurse -File) {
    $content = Get-Content -Raw -Path $file.FullName
    if ($content -match '<<<<<<<' -or $content -match '=======' -or $content -match '>>>>>>>') {
        Write-Output ($file.FullName + " contains unresolved git conflict markers")
        exit 1
    }
}

exit 0
