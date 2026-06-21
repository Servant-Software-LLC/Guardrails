# Fan-in sink of the diamond — depends on BOTH leaves, so its segment worktree is forked
# from one upstream with the other merged in (the harness's union point). It therefore sees
# out/hello.txt AND out/world.txt already merged, and combines them into out/greeting.txt.
$ErrorActionPreference = 'Stop'

$outDir = Join-Path $env:GUARDRAILS_WORKSPACE 'out'
$hello = Get-Content -Raw -Path (Join-Path $outDir 'hello.txt')
$world = Get-Content -Raw -Path (Join-Path $outDir 'world.txt')
Set-Content -NoNewline -Path (Join-Path $outDir 'greeting.txt') -Value "$hello + $world"

# State fragment: single-writer-per-key — the top-level key MUST equal this task's id.
Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"03-combine-greeting": {"wrote": "out/greeting.txt"}}'

exit 0
