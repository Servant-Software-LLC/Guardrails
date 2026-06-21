# Leaf A of the diamond. Independent of leaf B (no dependsOn between them), so the
# harness runs them concurrently in two isolated segment worktrees (maxParallelism: 2).
#
# Writes its committable artifact into the SEGMENT WORKTREE root ($GUARDRAILS_WORKSPACE),
# which GitWorktreeProvider.Integrate commits to the plan branch with a Guardrails-Task:
# trailer. The write stays inside this task's declared writeScope ("out/hello.txt").
$ErrorActionPreference = 'Stop'

$outDir = Join-Path $env:GUARDRAILS_WORKSPACE 'out'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Set-Content -NoNewline -Path (Join-Path $outDir 'hello.txt') -Value 'Hello from leaf A'

# State fragment: single-writer-per-key — the top-level key MUST equal this task's id.
Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{"01-write-hello": {"wrote": "out/hello.txt"}}'

exit 0
