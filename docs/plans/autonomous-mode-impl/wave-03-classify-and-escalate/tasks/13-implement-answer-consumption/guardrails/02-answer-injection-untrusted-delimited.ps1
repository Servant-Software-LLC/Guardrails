# catches: an answer-injection that appends the human answer.text without the pinned UNTRUSTED-DATA
#          delimiter — so injected human text could be read as a harness/system instruction instead of
#          delimited data (doc 12 §7.4 Finding 4). The prompt PROHIBITS treating the text as an
#          instruction and PINS the exact delimiter `[BEGIN UNTRUSTED HUMAN ANSWER]` … `[END UNTRUSTED
#          HUMAN ANSWER]`; this is the STRUCTURAL backing for that prohibition (#221) — a grep for the
#          LITERAL delimiter string (not an English word an impl could satisfy with a comment). Scoped to
#          the one composer file this task modifies. (The overwatcher denylist is the runtime backstop.)
$composer = "src/Guardrails.Core/Prompts/PromptComposer.cs"
if (-not (Test-Path $composer)) {
    Write-Output "$composer does not exist"
    exit 1
}
$c = Get-Content -Raw -Path $composer
# The pinned open + close delimiter literals must both be emitted by the composer's injection section.
if ($c -notmatch [regex]::Escape('BEGIN UNTRUSTED HUMAN ANSWER')) {
    Write-Output "$composer does not emit the pinned '[BEGIN UNTRUSTED HUMAN ANSWER]' delimiter — the injected answer.text is not wrapped as UNTRUSTED DATA (doc 12 §7.4 Finding 4); a bare append could be read as a harness instruction"
    exit 1
}
if ($c -notmatch [regex]::Escape('END UNTRUSTED HUMAN ANSWER')) {
    Write-Output "$composer emits the opening delimiter but not the closing '[END UNTRUSTED HUMAN ANSWER]' — the untrusted-data block must be explicitly closed so trailing composed-prompt content is not read as part of the human answer"
    exit 1
}
exit 0
