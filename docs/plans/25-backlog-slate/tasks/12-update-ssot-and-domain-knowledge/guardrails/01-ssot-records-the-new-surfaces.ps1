# catches: an SSOT that never learned what this plan shipped - the verb, the preflight step, the
#          barrier wait and the model surface land in code while the single source of truth still
#          describes a harness that never executes a sample pair, ends a run on a barrier-time 429,
#          and renders a log index with no model in it. The next agent to touch any of the three
#          reads the SSOT, not the git log, so an unrecorded surface is a surface that gets deleted
#          by a well-meant refactor. Invariant 4: the contract moves in the SAME change-set as the code.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302: you cannot
# synthesize a meaningful "invalid" design doc, and there is no behavioural rung to demote into). The
# PRECEDENT check is the mandatory substitute, and it is applied per clause below: every literal token
# demanded here has a sibling precedent already in this exact document, so the task is asked for the
# form the document already uses, not a form invented by a guardrail. This guardrail asserts the
# tokens are PRESENT; it cannot and does not judge whether the prose around them is any good - a human
# reviews that.
#
# ONE SURFACE IS DELIBERATELY NOT TOKEN-CHECKED, and saying so is part of the check being honest: the
# sample-verify PREFLIGHT STEP. Every candidate token for it is either already ambient in this
# document (`preflights/` 51x, `Full Flight Checks` 7x, `samples/` 5x, `tasks/<id>/samples/` 2x - all
# measured, all pre-satisfied, all #478 defects if demanded) or a coinage with no precedent here, and
# demanding a coinage is the "token with no PRECEDENT in the target artifact" anti-pattern. The verb
# clause below is its proxy; the step itself is covered by the action prompt and by human review.
# /guardrails-review should re-check that residual against the prose.
#
# baseline counts on the untouched tree - MEASURED with Select-String over this exact subject, with
# this clause's own case sensitivity (-cnotmatch -> -CaseSensitive), not assumed:
#   guardrails samples verify                                        0
#   (?:nextProbe|probeInterval)                                      0   (both alternatives measured 0 separately)
#   (?:[Mm]odel column|model per task|per-task model)                0   (all four alternatives measured 0 separately)
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 01-11 write
#   only under src/ and tests/, and this task is the only one in plan 25 whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "docs/plans/02-schemas-and-contracts.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the SSOT is the single source of truth for these contracts and cannot be skipped"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# PRECEDENT: this document already names CLI verbs as literal tokens - 'graph --check' (7x) and the
# section 12.3 heading 'guardrails logs --export'. Same form asked for.
if ($doc -cnotmatch 'guardrails samples verify') {
    $failures += "$f never names 'guardrails samples verify' - the new verb is unrecorded, so the SSOT still describes a harness in which a committed sample pair is a claim nothing ever executes. Name it the way 'graph --check' and 'guardrails logs --export' are already named in this file, and record with it that the pair is deliberately NOT run by 'validate' (which must stay static and offline) and that running the .invalid half IS the can-never-FAIL detector GR2055's mirror leaves uncovered."
}

# PRECEDENT: camelCase identifiers are named inline throughout this document - 'mergeOnSuccess' (21x)
# and 'expectedDurationSeconds' (4x). Same form asked for. The alternation is deliberate (catalogue:
# accept both forms when both are legitimate): the landed code may call the knob either name, and a
# guardrail that dictates which one would be asking the document to describe code that does not exist.
if ($doc -cnotmatch '(?:nextProbe|probeInterval)') {
    $failures += "$f never names 'nextProbe' or 'probeInterval' - the barrier-time wait-and-poll is unrecorded, so the SSOT still describes a run that ENDS on a provider limit at a wave barrier while riding out the identical signal inside a task. Name the knob the way 'mergeOnSuccess' and 'expectedDurationSeconds' are already named in this file, and record that the wait is bounded and surfaced (the operator sees a pause with its reason and next-probe time, not a failure)."
}

# PRECEDENT: section 12.3 already describes the log index's per-task contents in prose - "every task
# with its status word; a task with attempts on disk is a link to its page, a not-yet-run task is
# plain text". Same form asked for, in that same section. Four spellings accepted so the sentence can
# read naturally either way.
if ($doc -cnotmatch '(?:[Mm]odel column|model per task|per-task model)') {
    $failures += "$f never records that the run-level log index carries the model per task - so the SSOT still describes the surface as it was when it contained ZERO occurrences of 'model' and attempt-route.log was linked from nowhere (#524). Add it in section 12.3, in the prose form that section already uses for what the index shows per task, and state which index (final/--export vs during-run) actually carries it."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
