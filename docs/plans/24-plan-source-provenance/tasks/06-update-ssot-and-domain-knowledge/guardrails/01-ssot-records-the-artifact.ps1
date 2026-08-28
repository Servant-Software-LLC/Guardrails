# catches: an SSOT that never learned about the artifact this plan added - the contract lands in code
#          and the single source of truth still describes a harness that reads plan.md and forgets it.
#          The next agent to touch state/ reads the SSOT, not the git log, so an unrecorded artifact is
#          an artifact that gets deleted by a well-meant refactor. Invariant 4: the contract moves in
#          the SAME change-set as the code.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302: you cannot
# synthesize a meaningful "invalid" design doc). The PRECEDENT check is the mandatory substitute, and
# it is applied per clause below: every literal token demanded here has a sibling precedent already in
# this exact document, so the task is asked for the form the document already uses, not a form invented
# by a guardrail. This guardrail asserts the tokens are PRESENT; it cannot and does not judge whether
# the prose around them is any good - a human reviews that.
#
# baseline counts on the untouched tree - MEASURED with Select-String over this exact subject, with
# this clause's own case sensitivity (-cnotmatch -> -CaseSensitive), not assumed:
#   state/plan-source\.json      0
#   sourceSha256Lf              0
#   declaredDelegatedDecisions  0
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 01-05 write
#   only under src/, tests/, and this task is the only one whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "docs/plans/02-schemas-and-contracts.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the SSOT is the single source of truth for this contract and cannot be skipped"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# PRECEDENT: 'state/guardrails-review.json' already appears in this document (the section 1 layout tree
# and the section 13 heading) as a state/ artifact named by its full relative path. Same form asked for.
if ($doc -cnotmatch 'state/plan-source\.json') {
    $failures += "$f never names 'state/plan-source.json' - the provenance artifact is not recorded in the SSOT. Follow the form the document already uses for 'state/guardrails-review.json' (the section 1 layout tree, plus its own subsection)."
}

# PRECEDENT: camelCase JSON field names are named inline in this document as literal tokens -
# 'mergeOnSuccess' and 'expectedDurationSeconds' are both already there. Same form asked for.
if ($doc -cnotmatch 'sourceSha256Lf') {
    $failures += "$f never names the 'sourceSha256Lf' field - the LF-normalized hash is half the design (a raw mismatch is usually core.autocrlf, not tampering), and an SSOT that documents only one hash documents the wrong contract. Name the field the way 'mergeOnSuccess' and 'expectedDurationSeconds' are already named in this file."
}

if ($doc -cnotmatch 'declaredDelegatedDecisions') {
    $failures += "$f never names the 'declaredDelegatedDecisions' field - it is what the declared-count gate reads, so the gate's contract is undocumented without it. Name the field the way 'mergeOnSuccess' and 'expectedDurationSeconds' are already named in this file."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
