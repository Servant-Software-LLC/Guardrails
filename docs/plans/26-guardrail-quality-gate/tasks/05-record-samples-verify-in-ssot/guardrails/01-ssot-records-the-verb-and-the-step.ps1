# catches: an SSOT that never learned what this plan shipped - the verb and the pre-DAG sample-pair
#          check land in code while the single source of truth still describes a harness in which a
#          committed sample pair is a claim nothing ever executes. The next agent to touch either
#          reads the SSOT, not the git log, so an unrecorded surface is a surface that gets deleted by
#          a well-meant refactor - and this feature is unusually easy to delete, because it costs time
#          on every run and its value is invisible until the day it catches a reversed pair.
#          Invariant 4: the contract moves in the SAME change-set as the code.
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
# PREFLIGHT STEP itself. Every candidate token for it is either already ambient in this document -
# `preflights/` 51x, `Full Flight Checks` 7x, `preflight phase` 2x, `samples/` 5x,
# `tasks/<id>/samples/` 2x, `GR2055` 6x, all measured, all pre-satisfied, all #478 defects if demanded
# - or a coinage with no precedent here. That includes the class name `PlanPreflightPhase`, which
# measures ZERO in this document precisely BECAUSE the SSOT describes that phase behaviourally rather
# than by type; demanding it would force a form the document does not use for this subject, which is
# the "token with no PRECEDENT in the target artifact" anti-pattern. Prose phrasings of the argument
# (`can never fail` 0x, `cannot fail` 0x) were measured and REJECTED for the opposite reason: they are
# free English, a correct entry writing "a guardrail that can never FAIL" or "never fails" would be
# red, and `never fail` already appears 1x here - a clause a correct implementation can fail is a
# worse defect than an unchecked residual (#479). So clause 2 below is the closest honest proxy: the
# verb and the phase are the ONLY two things that drive `SampleVerifier`, and one implementation
# behind two entry points is itself the contract fact this plan turns on.
# /guardrails-review should re-check that residual against the prose.
#
# baseline counts on the untouched tree - MEASURED with grep over this exact subject, with this
# clause's own case sensitivity (-cnotmatch -> case-sensitive), not assumed (2026-08-29):
#   samples verify                                                   0
#   guardrails samples verify                                        0   (measured too - see the
#                                                                        BARE-vs-PREFIXED note below)
#   SampleVerifier                                                   0
#   Positive control for those zeroes (#500): the same invocation over the same file for a literal
#   known to be present, `PlanDefinition`, returns 36 - so the search reached the document rather than
#   silently skipping it.
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 01-04 write
#   only under src/ and tests/, and this task is the only one in plan 26 whose writeScope names this
#   file.
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
#
# BARE, NOT PREFIXED - and this is a correction I had to make against my own first draft, so it is
# worth stating why. The obvious clause is `guardrails samples verify`. MEASURED, that would have been
# a FALSE RED on a correct document: this file spells `graph --check` SEVEN times and
# `guardrails graph --check` ZERO times, so the DOMINANT house form here is the bare verb. Requiring
# the prefix would reject a document written in this document's own dominant style, which is exactly
# the "can a correct implementation be written that this rejects?" test (#479). `samples verify` is a
# substring of both spellings, so it accepts either - the catalogue's "accept both forms where both are
# legitimate" - and it is also the spelling this plan's own task-03 guardrails use throughout
# (`dotnet run ... -- samples verify <folder>`).
if ($doc -cnotmatch 'samples verify') {
    $failures += "$f never names the 'samples verify' verb - the new verb is unrecorded, so the SSOT still describes a harness in which a committed sample pair is a claim nothing ever executes. Name it the way 'graph --check' and 'guardrails logs --export' are already named in this file (either spelling - bare or 'guardrails'-prefixed - satisfies this check), and record with it the mismatch classes it distinguishes (.valid exits non-zero / .invalid exits 0 / a missing half / a pair with no matching guardrail / a guardrail that fails to parse), that the pair is deliberately NOT run by 'validate' (which must stay static and offline), and that running the .invalid half IS the can-never-FAIL detector whose mirror this file already lints in section 4.7."
}

# PRECEDENT: this document names harness types inline throughout - 'PlanDefinition' (36x),
# 'IRunObserver' (13x), 'PlanLoader' (3x), 'PromptFailureKind' (3x), 'RunJournal' (2x),
# 'ProcessRunner' (1x). Same form asked for. This clause is also the closest available proxy for the
# preflight STEP (see the residual note in the header): the verb and the phase are the only two things
# that drive this type.
if ($doc -cnotmatch 'SampleVerifier') {
    $failures += "$f never names 'SampleVerifier' - so the SSOT records neither the shared verifier nor the fact that the 'guardrails samples verify' verb and the pre-DAG preflight step drive the SAME one. That is a contract fact, not an implementation detail: a second implementation of the pair-verification policy in the CLI would drift from the one the phase runs, and the two disagreeing is the exact failure this feature exists to detect. Name the type the way 'PlanDefinition' and 'PlanLoader' are already named in this file."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
