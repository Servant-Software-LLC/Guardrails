# catches: an answer-injection that appends the human answer.text as a bare/undelimited section — or
#          worse, as a harness/system instruction — instead of clearly-delimited UNTRUSTED human-answer
#          DATA (doc 12 §7.4 Finding 4). The prompt PROHIBITS treating the text as an instruction; this
#          is the STRUCTURAL backing for that prohibition (#221 — a prose-only 'inject as data, not
#          instruction' rule an adversarial/lazy impl is free to ignore). Asserts PromptComposer.cs gained
#          an injection section that both (a) references the answer/injected text and (b) marks it as
#          untrusted DATA (not an instruction). Scoped to the one file this task modifies.
$composer = "src/Guardrails.Core/Prompts/PromptComposer.cs"
if (-not (Test-Path $composer)) {
    Write-Output "$composer does not exist"
    exit 1
}
$c = Get-Content -Raw -Path $composer
# (a) An injection section that names the injected answer must exist (new since this task).
if ($c -notmatch '(?i)inject' -or $c -notmatch '(?i)answer') {
    Write-Output "$composer has no answer-injection section — the delimited-untrusted injection (doc 12 §7.4) was not added to ComposeAction"
    exit 1
}
# (b) The section must mark the text as UNTRUSTED DATA and NOT an instruction (the Finding-4 envelope).
if ($c -notmatch '(?i)untrusted' -or $c -notmatch '(?i)not.{0,30}(an )?instruction|data,?\s*not') {
    Write-Output "$composer injects the answer but does not delimit it as UNTRUSTED DATA that is NOT a harness instruction — the Finding-4 envelope (doc 12 §7.4) is missing; injected human text could be read as an instruction"
    exit 1
}
exit 0
