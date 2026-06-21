# Leaf B of the diamond. Independent of leaf A — distinct file, distinct writeScope —
# so the two leaves can run concurrently in separate segment worktrees and fan into
# the integration gate without a write collision.
$ErrorActionPreference = 'Stop'

$outDir = Join-Path $env:GUARDRAILS_WORKSPACE 'out'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Set-Content -NoNewline -Path (Join-Path $outDir 'world.txt') -Value 'World from leaf B'

# State fragment: single-writer-per-key — the top-level key MUST equal this task's id.
Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"02-write-world": {"wrote": "out/world.txt"}}'

exit 0
