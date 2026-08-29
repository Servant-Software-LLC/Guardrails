# catches: an SSOT that never learned what this plan shipped - the served diagram route, the replaced
#          whole-document refresh, the model surface and a NEW PUBLIC INTERFACE MEMBER land in code
#          while the single source of truth still describes a log server that 404s its own diagram, a
#          during-run page that reloads itself every 3 seconds forever, a run-level index with no model
#          in it, and an IRunObserver whose only attempt-model event fires after the action has already
#          finished. The next agent to touch any of them reads the SSOT, not the git log, so an
#          unrecorded surface is a surface that gets deleted by a well-meant refactor. Invariant 4: the
#          contract moves in the SAME change-set as the code - and clause 4 is the one clause here
#          that is a CONTRACT, not a UX note.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302: you cannot
# synthesize a meaningful "invalid" design doc, and there is no behavioural rung to demote into). The
# PRECEDENT check is the mandatory substitute, and it is applied per clause below: every literal token
# demanded here has a sibling precedent already in this exact document, so the task is asked for the
# form the document already uses, not a form invented by a guardrail. This guardrail asserts the
# tokens are PRESENT; it cannot and does not judge whether the prose around them is any good - a human
# reviews that.
#
# TOKENS DELIBERATELY NOT DEMANDED, and saying so is part of the check being honest. Each is the
# obvious word for one of the three surfaces and each is ALREADY AMBIENT in this document, so a
# required-present clause on it would be GREEN BEFORE THE TASK RAN and would certify nothing (#478):
#   diagram.html            31x   (the file is named all over sections 10.1 and 12)
#   /diagram.html            4x   (as `logs/<runId>/diagram.html`, a path - not the route)
#   meta refresh             3x   (about the log-site INDEX pages, which this plan does NOT change)
#   http-equiv               1x   (the very sentence #523 makes stale - it may legitimately go away)
#   attempt-route.log        3x   (already documented in sections 7/8; #524 changes who LINKS it)
#   live progress table      3x
#   pan/zoom                 2x
#   LogSiteRenderer          3x   ·  LogServer 2x  ·  task row 1x  ·  terminal state 1x
# The four clauses below are what is left after that subtraction; the residual (that the ambient
# words are used in a sentence that is actually TRUE) is the action prompt's job and a human's.
#
# CLAUSES 1-3 ARE WIDE ALTERNATIONS, AND THAT IS THE POINT, NOT LAZINESS. Clause 4 is deliberately
# NOT one, and the asymmetry is principled rather than sloppy: clauses 1-3 demand PROSE, which a
# correct author may legitimately phrase a dozen ways, so a single-spelling demand would red-fail a
# correct entry. Clause 4 demands a C# IDENTIFIER - the name of a member that now exists in
# src/Guardrails.Core/Execution/IRunObserver.cs - and an identifier has exactly one spelling. A wide
# alternation there would be the mirror mistake: it would accept a paragraph that gestured at "a new
# launch-time event" without ever naming the member a reader has to grep for. A required-present clause
# that demands ONE spelling is failed by a CORRECT entry written in a different but equally house-style
# spelling - a guardrail no correct implementation can pass, which is the worst object this repo
# produces and the polarity GR2055 exists for. The sibling plan proved it live: a clause demanding
# `guardrails graph --check` would have red-failed a correct SSOT, because the house form there is the
# BARE verb (`graph --check` 7x, `guardrails graph --check` 0x). So every alternative below was
# measured SEPARATELY at 0 against this exact file, and the alternation is drawn wide enough that a
# correct sentence describing the change cannot plausibly miss all of them.
#
# baseline counts on the untouched tree - MEASURED 2026-08-29 with .NET regex over this exact
# subject, with this clause's own case sensitivity (-cnotmatch -> case-SENSITIVE), not assumed. A
# positive control ran in the same pass (`PlanDefinitionHash` -> 35 hits, `GET /tasks/` -> 6) to prove
# the search actually reached this file, so a zero here is a measurement and not a search that never
# opened the door. Every alternative, individually:
#   GET /diagram.html 0 · serves the diagram 0 · serves the live diagram 0 · log-site server 0
#   reload 0 · whole-document 0 · status endpoint 0 · terminal run state 0 · no longer refresh 0
#     · stops refresh 0 · stop refresh 0
#   [Mm]odel column 0 · model per task 0 · per-task model 0 · model for each task 0
#     · model that ran 0 · model in the row 0 · model in the task row 0 · model beside 0
#   AttemptRouteResolved 0            <- clause 4, a brand-new identifier, so a zero is expected AND
#                                        the clause cannot be pre-satisfied by anything. Its sibling
#                                        precedent IS present and measured in the same pass:
#                                        IRunObserver 13 · AttemptModelResolved 1 · attempt-route.log 3.
#                                        Those three are the positive controls for clause 4 and the
#                                        reason it is asked for in this form: this document ALREADY
#                                        names IRunObserver members inline (IRunObserver.DecisionRecorded,
#                                        IRunObserver.WaveGateFinished, IRunObserver.AttemptModelResolved),
#                                        so clause 4 asks for the house form, not an invented one.
#
# SPELLINGS DELIBERATELY EXCLUDED FROM THE ALTERNATIONS because they are AMBIENT here, and including
# one would pre-satisfy the whole clause before the task ran (#478):
#   refresh 11x  ·  diagram.html 31x  ·  /diagram.html 4x  ·  meta refresh 3x  ·  drops the refresh 1x
#   http-equiv 1x  ·  attempt-route.log 3x  ·  log server 2x  ·  LogServer 2x  ·  is served 1x
#   served by 4x  ·  serves it 2x  ·  live progress table 3x  ·  pan/zoom 2x  ·  task row 1x
# Note `reload` measures 0 while `refresh` measures 11: this document says "refresh" and never
# "reload", which is exactly why `reload` can carry a clause and `refresh` cannot.
#
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 01-06 write
#   only under src/ and tests/, and this task is the only one in plan 27 whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "docs/plans/02-schemas-and-contracts.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the SSOT is the single source of truth for these contracts and cannot be skipped"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# (1) #522 - the served diagram.
# PRECEDENT: the section 12 Routes table already names every route in exactly this form -
# 'GET /tasks/{id}', 'GET /tasks/{id}/files', 'GET /tasks/{id}/source' ('GET /tasks/' 6x, MEASURED), so
# the 'GET ' prefix IS the house form here and demanding it is not the sibling plan's bare-verb trap.
# The token is also the exact route task 02's own action prompt pins ("GET /diagram.html must return
# 200"), so this asks the document to name what actually ships. The prose alternatives are there in
# case the author records the fact in narrative rather than in the table; the common substring of both
# spellings, '/diagram.html', is UNUSABLE (4x ambient), so an alternation is the only safe form.
if ($doc -cnotmatch '(?:GET /diagram\.html|serves the diagram|serves the live diagram|log-site server)') {
    $failures += "$f never names 'GET /diagram.html' - the served-diagram route is unrecorded, so the SSOT still describes a log server that answers 200 for the route the diagram LINKS TO and 404 for the diagram itself, which is the whole of #522. Add the route to the section 12 Routes table the way 'GET /tasks/{id}' and 'GET /tasks/{id}/source' are already named there, and record with it (a) that the two halves of the feature disagreed about their own transport - index.html emits absolute http:// URLs, the diagram emits relative ones - and (b) that the logs/<runId>/ tree is deliberately NOT served as static files, because a blanket file server rooted there would expose every attempt log."
}

# (2) #523 - the whole-document refresh.
# PRECEDENT: section 10.1's own "During-run vs final" bullet already describes this page's refresh in
# plain prose. This is the clause most at risk of the prose-phrasing false red, because the plan
# permitted EITHER outcome (DOM updates over a status endpoint, or a refresh that stops at a terminal
# state) and prose is free - so the alternation is drawn as wide as the measurements allow. The
# workhorse is the bare stem `reload`, which measures 0 here while `refresh` measures 11: this
# document says "refresh" and never "reload", so `reload` catches "no longer reloads", "reloading the
# whole document", "without a full reload" and every neighbour, without being pre-satisfied. The rest
# cover the phrasings that avoid the word entirely. If a future edit makes ALL of these ambient, the
# honest move is to delete this clause and name the residual - never to demand one invented spelling.
if ($doc -cnotmatch '(?:reload|whole-document|status endpoint|terminal run state|no longer refresh|stops refresh|stop refresh)') {
    $failures += "$f never records that the live diagram stopped reloading the whole document - so section 10.1's 'During-run vs final' bullet still states the during-run page carries a <meta http-equiv='refresh' content='3'> tag, which #523 replaced. Update that bullet IN PLACE, in the prose voice it already uses ('the final page ... drops the refresh'), and say which outcome actually landed: DOM updates over a status endpoint, or a refresh that stops at a terminal run state with an interval matched to how fast a DAG's status really changes. Record the costs that made it worth doing - pan/zoom and scroll dying every tick, clicks lost mid-reload, Mermaid re-laid-out for content that changes at task boundaries, and a page that reloaded forever after the run ended."
}

# (3) #524 - the model where it persists.
# PRECEDENT: section 12.3 already describes the log index's per-task contents in prose - "every task
# with its status word; a task with attempts on disk is a link to its page, a not-yet-run task is
# plain text". Same form asked for, in that same section. EIGHT spellings accepted, every one measured
# 0 separately, so the sentence can be phrased however its author wants to say "the index names the
# model for each task" - the clause must not dictate which synonym a correct entry picks.
if ($doc -cnotmatch '(?:[Mm]odel column|model per task|per-task model|model for each task|model that ran|model in the row|model in the task row|model beside)') {
    $failures += "$f never records that the run-level log index carries the model per task - so the SSOT still describes the surface as it was when it contained ZERO occurrences of 'model' and attempt-route.log was linked from nowhere (#524). Add it in section 12.3, in the prose form that section already uses for what the index shows per task; record that the model also appears in the TASK ROW (beside cost and duration, where it persists after the task finishes) and that the task page links attempt-route.log by name with a label saying what it answers; and state which index actually carries it (during-run vs final/--export), rather than leaving the next reader to find the gap."
}

# (4) #524 - THE CONTRACT MEMBER. Not a prose clause: this one names a symbol.
# PRECEDENT: this document already names IRunObserver members inline and by their exact C# spelling -
# IRunObserver.DecisionRecorded, IRunObserver.PromptPaused, IRunObserver.WaveGateFinished,
# IRunObserver.OverwatchNoVerdict and, one paragraph from where this belongs,
# IRunObserver.AttemptModelResolved (MEASURED: IRunObserver 13x, AttemptModelResolved 1x). So the
# identifier IS the house form here.
# WHY IT EARNS A CLAUSE OF ITS OWN, when clause 3 already covers the model surface: clause 3 is about
# what an OPERATOR sees; this is about what the next AGENT is allowed to assume about a PUBLIC
# interface. A new member with a default no-op body is invisible to the compiler when a decorator
# forgets it - the recurring defect this repo keeps re-finding - and the SSOT is the only place a
# future implementor learns the member exists at all. Invariant 4 is not satisfied by the UX note.
# -cnotmatch: C# identifiers are case-sensitive, and 'attemptrouteresolved' is not the member.
if ($doc -cnotmatch 'AttemptRouteResolved') {
    $failures += "$f never names AttemptRouteResolved - the new IRunObserver member this plan added is unrecorded, so the SSOT still describes an observer contract whose only attempt-model event (AttemptModelResolved) cannot fire until the runner has already finished, and a future implementor has no way to learn a launch-time route event exists. This is a CONTRACT change, not a UX note: Invariant 4 says it moves in the same change-set as the code. Record it beside the existing 'The live twin - IRunObserver.AttemptModelResolved' paragraph, as its launch-time counterpart, naming the member inline the way this document already names IRunObserver.DecisionRecorded and IRunObserver.WaveGateFinished. Say WHEN it fires (before the action runs, from the same resolution attempt-route.log is built from), that AttemptModelResolved is unchanged and becomes its confirmation-or-correction, that requestedTier is non-null ONLY on a section 6.2 climb so its presence IS the climb signal, and that its default no-op body means a transparent decorator which omits it compiles cleanly and swallows the disclosure in every mode."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
