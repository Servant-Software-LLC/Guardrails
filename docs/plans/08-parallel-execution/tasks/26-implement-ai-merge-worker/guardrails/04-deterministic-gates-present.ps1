# catches: the AI-merge worker's two DETERMINISTIC gates and the byte-producer contract are only
#          TRANSITIVELY verified (via AiMergeWorkerTests). A worker that wires the prompt + reads
#          GUARDRAILS_MERGE_OUT but SKIPS one of the two gates - (i) no conflict markers remain
#          (git diff --check); (ii) blast-radius: the AI touched ONLY the git-reported-conflicted files
#          (git status --porcelain) - turns the AI from a byte-producer-behind-deterministic-checks into
#          a near-verdict-producer (invariant 1 - "a prompt may propose, only a deterministic gate may
#          certify"). The tests might miss a gate if they exercise only the happy path. This file-scoped
#          structural check asserts the resolver SOURCE references both gate commands AND the
#          GUARDRAILS_MERGE_OUT byte channel, so the gates can't be silently dropped.
#          Scoped to the one file this task owns (grep-scope rule). File name confirmed from task 26's
#          action: src/Guardrails.Core/Execution/AiMergeResolver.cs.
$file = "src/Guardrails.Core/Execution/AiMergeResolver.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the AI-merge worker (AiMergeResolver) was not implemented"
    exit 1
}
$text = Get-Content $file -Raw

$required = @{
    'git diff --check'      = "gate (i) - no conflict markers remain (git diff --check) is not referenced; without it the AI's output is trusted with markers possibly still present"
    'git status --porcelain' = "gate (ii) - blast-radius (git status --porcelain: the AI touched ONLY the git-reported-conflicted files) is not referenced; without it an out-of-bounds write past the conflicted set is undetected (the rejected shared-workspace design's cross-file clobber)"
    'GUARDRAILS_MERGE_OUT'  = "the GUARDRAILS_MERGE_OUT byte channel (the worker writes the resolution; the harness reads it) is not referenced; the AI-merge byte-producer contract is the only sanctioned byte path (PromptResult returns metadata only)"
}
$missing = @()
foreach ($needle in $required.Keys) {
    if ($text -notmatch [regex]::Escape($needle)) {
        $missing += "$needle - $($required[$needle])"
    }
}
if ($missing.Count -gt 0) {
    Write-Output ("AiMergeResolver.cs is missing AI-merge deterministic-gate reference(s):`n  - " + ($missing -join "`n  - ") + "`nThe two deterministic checks + the byte-producer contract are load-bearing for invariant 1; they must appear in the resolver source.")
    exit 1
}
exit 0
