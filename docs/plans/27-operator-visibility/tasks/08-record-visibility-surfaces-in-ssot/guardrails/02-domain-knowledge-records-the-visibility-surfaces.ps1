# catches: an SSOT updated and the domain-knowledge skill left stale - the two halves of the same
#          obligation delivered apart. Every agent that works in this repo loads
#          guardrails-domain-knowledge; the SSOT is what it points AT. A skill that does not mention
#          the served diagram, the replaced refresh or the persistent model surface means the next
#          breakdown/review agent reasons about a harness that no longer matches reality - and in
#          this case it is worse than silence: the skill's model-tiering section currently ASSERTS
#          that the model pair "reaches the live table and the --no-ui stream", a claim #524 measured
#          to be false of anything that PERSISTS after a task finishes, and its Diagram bullet
#          describes logs/<runId>/diagram.html as a file on disk that nothing serves. The skill's own
#          frontmatter SELF-UPDATING clause is the standing instruction this check enforces.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302). The
# PRECEDENT check is the mandatory substitute and is applied per clause below: each demanded token
# has a sibling precedent in this exact file, so the task is asked for the form the skill already
# uses. This guardrail asserts the tokens are PRESENT; it cannot judge whether the prose around them
# is any good - a human reviews that.
#
# TOKENS DELIBERATELY NOT DEMANDED, because they are ALREADY AMBIENT in this file and a
# required-present clause on any of them would be GREEN BEFORE THE TASK RAN and would certify
# nothing (#478). MEASURED against this exact subject, case-sensitively:
#   diagram.html         4x   (the Diagram bullet and its Live-status-overlay sub-bullet)
#   /diagram.html        1x   (as `logs/<runId>/diagram.html`, a path - not a route)
#   attempt-route.log    1x   (already named in the #349 Stage 3 paragraph; #524 changes who LINKS it)
#   live table           1x   (in the very sentence this plan makes stale - it may legitimately go)
#   pan/zoom             1x
# Note in particular that `attempt-route.log` is UNUSABLE here for the same reason it is unusable in
# guardrail 01 (3x in the SSOT): the token this plan is most tempted to demand is the one the
# documents already carry, and demanding it would be a clause satisfied before the task started.
#
# EVERY CLAUSE IS A WIDE ALTERNATION, AND THAT IS THE POINT, NOT LAZINESS. A required-present clause
# that demands ONE spelling is failed by a CORRECT entry written in a different but equally
# house-style spelling - a guardrail no correct implementation can pass, which is the polarity GR2055
# exists for. The sibling plan proved it live: a clause demanding `guardrails graph --check` would
# have red-failed a correct SSOT, because the house form is the BARE verb (`graph --check` 7x,
# `guardrails graph --check` 0x). So every alternative below was measured SEPARATELY at 0 against this
# exact file, and each alternation is drawn wide enough that a correct sentence cannot miss all of it.
#
# baseline counts on the untouched tree - MEASURED 2026-08-29 with .NET regex over this exact
# subject, with this clause's own case sensitivity (-cnotmatch -> case-SENSITIVE), not assumed. A
# positive control ran in the same pass (`PlanDefinitionHash` -> 7 hits, `PromptRunnerRegistry.
# FromConfig` -> 1, `IRunObserver` -> 5) to prove the search actually reached this file, so a zero
# below is a measurement and not a search that never opened the door. Every alternative, individually:
#   log-site server 0 · log site server 0 · log server 0 · LogServer 0 · GET /diagram.html 0
#     · serves the diagram 0 · serves the live diagram 0
#   reload 0 · refresh 0 · whole-document 0 · status endpoint 0 · terminal run state 0
#   [Mm]odel column 0 · model per task 0 · per-task model 0 · model for each task 0
#     · model that ran 0 · model in the row 0 · model in the task row 0 · model beside 0
#
# NOTE the asymmetry with guardrail 01, which is why the two files get DIFFERENT alternations rather
# than one copied pair: in the SSOT `refresh` is ambient (11x) and cannot carry a clause, while in
# THIS file it measures 0 - so here the refresh clause can accept the plain word and is correspondingly
# harder to false-red. Likewise `log server`/`LogServer` are ambient in the SSOT (2x each) and 0 here.
# Measure per subject; never copy a clause between two documents on the assumption they read alike.
#
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 01-07 write
#   only under src/ and tests/, and this task is the only one in plan 27 whose writeScope names this file.
#
# ══════════════════════════════════════════════════════════════════════════════════════════════════
# HTML COMMENTS ARE STRIPPED BEFORE ANY CLAUSE RUNS. MEASURED 2026-08-29: appending ONE 172-byte line -
#
#     <!-- TODO(#522/#523/#524): GET /diagram.html is served; the page no longer needs a reload;
#          the index gains a Model column; IRunObserver.AttemptRouteResolved exists. -->
#
# - took this guardrail from exit 1 (all three clauses failing) to exit 0, and took guardrail 01 to
# exit 0 in the SAME stroke: one invisible line, two documents, seven clauses. That is the smallest
# edit an agent under retry pressure can make when a check demands a token, and it renders as NOTHING.
# A record no reader can see is not a record.
# This file carries 2 HTML comments today (76 characters, MEASURED) and neither carries any token
# below, so the strip changes nothing about the baseline - it only closes the door.
#
# FENCED CODE BLOCKS ARE DELIBERATELY *NOT* STRIPPED, and the same decision is recorded at length in
# guardrail 01 (which is where the measurements live: 26 fenced blocks / 43,387 characters in the SSOT,
# with 2 of its 36 `PlanDefinition` occurrences inside one - so refusing to see a fence would red-fail
# a correct document written in its own house style, the GR2055 polarity). THIS subject measures ZERO
# fenced blocks today, so the strip would currently be a no-op here; it is still omitted, because the
# two files must not drift into different reading rules and because the skill may legitimately grow a
# fenced example. The threat model is the difference: an HTML comment is invisible in every renderer,
# a fence is on the page in front of the reader.
# ══════════════════════════════════════════════════════════════════════════════════════════════════
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { ".claude/skills/guardrails-domain-knowledge/SKILL.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the domain-knowledge skill is what every agent in this repo loads, and its SELF-UPDATING clause makes this update part of the same change-set"
    exit 1
}

$raw = Get-Content $f -Raw                              # NEVER matched against
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')     # see the block comment above
$failures = @()

# CLAUSE 0 - the STRIP SANITY / POSITIVE CONTROL, and it is NOT a finding about this task's work.
# Every clause below passes by finding a token, so every clause below fails identically whether the
# token is absent or the search never reached the document (#500). `PlanDefinitionHash` measures 7 in
# this file on the untouched tree and is nothing to do with plan 27. Reported separately so a retry
# agent reads "the subject is wrong", not "your prose is missing". MEASURED 2026-08-29: 7 before the
# strip, 7 after.
if ($doc -cnotmatch 'PlanDefinitionHash') {
    $failures += "$f does not contain 'PlanDefinitionHash' - a token this skill carries 7 times and that plan 27 does not touch. Either GR_SUBJECT points at the wrong file, or the HTML-comment strip above ate the document. Every clause below is a required-present check, so without this control a zero would be indistinguishable from a search that never opened the door. Do NOT 'fix' this by adding the word: fix the subject path."
}

# (1) #522 - the diagram is now SERVED, not merely written to disk.
# PRECEDENT: this skill names harness types inline where the fact needs one - 'IRunObserver.
# AttemptModelResolved' and 'PromptRunnerRegistry.FromConfig' (1x). SEVEN spellings accepted, each
# measured 0 in THIS file (note 'log server' and 'LogServer' are ambient in the SSOT at 2x each but
# absent here, which is why guardrail 01 cannot accept them and this one can) - so the sentence can
# name the server, the type, or the route, whichever reads naturally in the Live-status-overlay
# sub-bullet. The clause must not dictate which of those a correct entry picks.
if ($doc -cnotmatch '(?:log-site server|log site server|log server|LogServer|GET /diagram\.html|serves the diagram|serves the live diagram)') {
    $failures += "$f never records that the live diagram is now SERVED - so its 'Live status overlay (issue #219, a THIRD companion)' sub-bullet still describes logs/<runId>/diagram.html as a file on disk that nothing serves, which is the state #522 fixed. Say that the log-site server serves it, in the sub-bullet that already describes that file. Name the server, the LogServer type, or the GET /diagram.html route - whichever your sentence wants; the skill already names harness types inline (IRunObserver.AttemptModelResolved, PromptRunnerRegistry.FromConfig). Record with it WHY it matters: the diagram emits plan-relative tasks/<id>/... hrefs that are correct for the server and 404 under file://."
}

# (2) #523 - the whole-document refresh.
# PRECEDENT: the same Live-status-overlay sub-bullet already describes this page's behaviour in plain
# prose. The alternation is deliberate and deliberately WIDE - the plan permitted EITHER the larger fix
# (DOM updates over a status endpoint) OR the smaller accepted one (stop at a terminal state, lengthen
# the interval), so a guardrail dictating one spelling would be asking the skill to describe code that
# may not exist. Unlike the SSOT (where 'refresh' is ambient at 11x and only the stem 'reload' is
# usable), THIS file uses neither word today - both measured 0 - so the plain words are accepted here.
# That makes this clause weak but UNFALSIFIABLY SAFE: it cannot red-fail a correct entry, and a check
# a correct implementation can fail would be strictly worse than this one's weakness.
if ($doc -cnotmatch '(?:reload|refresh|whole-document|status endpoint|terminal run state)') {
    $failures += "$f never records that the live diagram stopped reloading the whole document - so the skill still implies the #219 overlay behaves as it did when a <meta http-equiv=refresh> reloaded it every 3 seconds forever, killing pan/zoom and scroll on every tick and never stopping after the run ended. Add it to the Live-status-overlay sub-bullet, in that bullet's own voice, and say which outcome landed: DOM updates over a status endpoint, or a refresh that stops at a terminal run state."
}

# (3) #524 - the model where it persists.
# PRECEDENT: the model-tiering section already describes the operator-facing model surfaces in prose -
# the paragraph beginning "Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)", which names
# 'attempt-route.log', the literal 'requested model:' key and 'IRunObserver.AttemptModelResolved'
# inline. Same form asked for, in that same paragraph - which is also the one this plan makes stale.
if ($doc -cnotmatch '(?:[Mm]odel column|model per task|per-task model|model for each task|model that ran|model in the row|model in the task row|model beside)') {
    $failures += "$f never records that the model now appears in the task ROW and per task on the run-level log index - so the skill's '#349 Stage 3' paragraph still claims the pair reaches the live table and the --no-ui stream, a claim #524 measured to be false of anything that persists after a task finishes (the console line was written ABOVE the pinned live region and scrolled out of view). Update that paragraph IN PLACE, in the prose form it already uses; do not add a new section. Say too that the task page now links attempt-route.log by name with a label saying what it answers."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
