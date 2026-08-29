# catches: the whole point of this task being lost while everything still compiles and every other
#          check stays green - AttemptRouteResolved raised AFTER `_actionRunner.RunAsync` returns.
#          That mutant declares the member, raises the event, forwards it through both decorators and
#          delivers EXACTLY NOTHING: it is AttemptModelResolved wearing a different name, and the
#          fourteen minutes of blank cell that docs/plans/29-model-visibility-ux.md section 1.1
#          MEASURED are still fourteen minutes of blank cell. Presence is not timing.
#          It also catches the cheaper miss: no raise in this file at all (the member declared and the
#          decorators dutifully forwarding an event nobody ever sends - the unwired-factory failure,
#          #120, at contract granularity).
#
# WHY THIS IS A SOURCE GREP AND NOT A TEST (#468 demotion gate, rung 3 - stated because an
# unexplained source-shape check on a behavioural claim is itself a finding):
#   The claim is "the raise happens BEFORE the action runs". Observing it at runtime means driving a
#   real TaskExecutor attempt with a recording observer AND a clock - the ORDER of two observer calls
#   relative to a child process launch. TaskExecutor.RunAttemptAsync launches a real prompt-runner
#   subprocess; the existing suites that drive it (AttemptModelDisclosureTests) assert on WHAT was
#   raised, never on what had not happened yet. A test proving "this call preceded that process
#   launch" would need to inject a fake IActionRunner that records the observer's state at entry -
#   buildable, but it would be a test of a fake's bookkeeping, and it is not a seam this plan owns.
#   What IS behaviourally observable was NOT demoted and is covered elsewhere: that the member exists
#   and binds (guardrail 01, the compiler), and that both decorators forward it (guardrail 03, plus
#   the AttemptModelForwardingTests-shaped regression pin 06-author-tests-model-in-row adds).
#   THE ORDERING CLAUSE IS THREE-ANCHOR, NOT TWO, AND THAT IS THE FIX FOR THE RESIDUAL THIS FILE USED
#   TO MERELY DECLARE. A one-sided `raise.Index < run.Index` bound is satisfied by everything above the
#   action, which is most of the file - and /guardrails-review MEASURED two mutants that passed it at
#   exit 0:
#     M1  the raise relocated INSIDE the §6.2 no-route branch. It compiles, it is still above RunAsync,
#         and the branch guard means it fires for NO ATTEMPT THAT EVER LAUNCHES - the consuming surface
#         is empty forever, which is a strictly WORSE outcome than the post-action raise this file was
#         written to catch.
#     M2  the raise moved into an unrelated private method DEFINED EARLIER IN THE FILE and never called
#         from the attempt path. Also above RunAsync, also never raised. This is verbatim the residual
#         the previous version of this comment declared and left open.
#   The lower anchor closes both. `_journaler.NoRoute(` is the LAST statement of the no-route branch,
#   so requiring NoRoute.Index < raise.Index < RunAsync.Index pins the raise to the 1,381 characters of
#   straight-line code between the branch settling and the action launching - which is exactly the
#   window §4.3 specifies, expressed in the only terms a text scan has.
#   HONEST RESIDUAL, stated rather than implied and now much narrower: this still compares TEXT
#   POSITIONS in one file, so it proves WHERE the call is written, not that it is REACHED. Two things
#   still satisfy it: a local function declared inside that window whose body is never invoked, and a
#   statement inserted between `_journaler.NoRoute(` and the branch's closing brace (which needs the
#   `return` restructured, so it is not a shape an implementation arrives at by accident).
#   /guardrails-review should re-check that residual by reading the diff.
#   SECOND RESIDUAL, and the one the action prompt is told about so it is not a surprise: because the
#   clause is positional, EXTRACTING THE RAISE INTO A PRIVATE HELPER defined below RunAsync would read
#   as a post-action raise and red-fail a correct implementation. The prompt therefore asks for the
#   raise INLINE at the site, which is what every other `_observer.` raise in this file already does.
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root. FOUR samples, one per
# distinct defect, because the ordering window has two bounds and each has its own mutant:
#   samples/02-route-event-raised-before-the-action-runs.valid.cs                   -> expect 0
#   samples/02-route-event-raised-before-the-action-runs.invalid.cs                 -> expect 1 (clause 4a: raised AFTER RunAsync)
#   samples/02-route-event-raised-before-the-action-runs.invalid-no-route-branch.cs -> expect 1 (clause 4b: M1, raised inside the no-route branch)
#   samples/02-route-event-raised-before-the-action-runs.invalid-earlier-method.cs  -> expect 1 (clause 4b: M2, raised from an unrelated earlier method)
# e.g.
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/samples/02-route-event-raised-before-the-action-runs.valid.cs';   ./docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/guardrails/02-route-event-raised-before-the-action-runs.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/samples/02-route-event-raised-before-the-action-runs.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/guardrails/02-route-event-raised-before-the-action-runs.ps1  # expect 1
# RE-RUN ALL FOUR after ANY edit to this file, not just the clause you touched: M1 and M2 were both
# MEASURED at exit 0 against the previous, one-sided version of clause 4.
#
# baseline counts - RE-MEASURED 2026-08-29 over this exact subject (src/Guardrails.Core/Execution/
# TaskExecutor.cs) on the untouched tree, with this file's own strip applied, not assumed, not copied:
#   _observer\.AttemptRouteResolved\s*\(    0   <- the clause is NOT pre-satisfied
#   _journaler\.NoRoute\s*\(                1   at scan index 20157   (the LOWER anchor, UNIQUE)
#   _actionRunner\.RunAsync\s*\(            1   at scan index 21538   (the UPPER anchor, UNIQUE)
#   _observer\.AttemptModelResolved\s*\(    1   at scan index 22989   (the positive control)
# Three indices, and their spacing is the measurement that proves this clause DISCRIMINATES rather
# than merely passing:
#   * the SHIPPED attempt-model raise sits 1,451 characters AFTER the action runs, so an
#     AttemptRouteResolved placed beside it - the post-action mutant - fails the UPPER bound;
#   * the no-route branch settles at 20157 and the action launches at 21538, so the legal window is
#     1,381 characters of straight-line code inside ONE method (verified by dumping the region: it
#     contains no method boundary, only statements). A raise anywhere earlier - inside the no-route
#     branch, or in any method defined above it - fails the LOWER bound.
# Note `BuildProvenance(` is NOT used as the lower anchor even though it also sits in the window: it
# matches TWICE (19694 and 43053), so it is not unique and a first-match anchor would be fragile.
# `_journaler.NoRoute(` matches exactly once.
# No ancestor task writes into this subject: tasks 01-04 name only LogSiteRenderer.cs, LogServer.cs,
# OnTheFlyDiagramObserver.cs, HtmlDiagramRenderer.cs and tests.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/TaskExecutor.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - cannot verify where the attempt-route event is raised"
    exit 1
}

# THE TWO-VARIABLE RULE (catalogue): one strip, two levels, and each clause reads the level it needs.
# Every clause here reads $scan, because every token it matches is a C# IDENTIFIER: a mention inside a
# comment, a string, or a nameof() must not satisfy any of them (#521). The strip only DELETES and
# REPLACES-IN-PLACE, never reorders, so index comparisons over $scan are faithful to source order.
$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

$raise   = [regex]::Match($scan, '_observer\s*\.\s*AttemptRouteResolved\s*\(')
$run     = [regex]::Match($scan, '_actionRunner\s*\.\s*RunAsync\s*\(')
$noRoute = [regex]::Match($scan, '_journaler\s*\.\s*NoRoute\s*\(')
$model   = [regex]::Match($scan, '_observer\s*\.\s*AttemptModelResolved\s*\(')

$failures = @()

# CLAUSE 1 - the ANCHOR SANITY check, and it is NOT a finding about this task's own work. If either
# ordering anchor is gone the window clause below cannot be evaluated at all, and a guardrail that
# silently skipped its own comparison would exit 0 having certified nothing. Reported separately, one
# message per missing anchor, so a retry agent reads "the anchor moved", not "your raise is misplaced".
if (-not $run.Success) {
    $failures += "$f no longer contains a _actionRunner.RunAsync( call, which is the UPPER anchor this check measures the raise against. Either the attempt path was restructured (NOT this task's job - it is out of scope) or the file is not what this guardrail thinks it is. Do NOT move the raise to satisfy this: restore the call site, or escalate needsHuman with the two quotes the harness contract asks for."
}
if (-not $noRoute.Success) {
    $failures += "$f no longer contains a _journaler.NoRoute( call, which is the LOWER anchor this check measures the raise against - it is the last statement of the section 6.2 no-route branch, and the raise must come AFTER it. Either the attempt path was restructured (NOT this task's job - it is out of scope) or the file is not what this guardrail thinks it is. Do NOT move the raise to satisfy this: restore the call site, or escalate needsHuman with the two quotes the harness contract asks for."
}

# CLAUSE 2 - a REGRESSION clause, green on arrival BY DESIGN (#478's named exception: "this existing
# thing still exists"). docs/plans/29-model-visibility-ux.md section 9 puts changing
# AttemptModelResolved OUT OF SCOPE, and the cheapest way to make the new event look right is to
# RETARGET the old raise instead of adding one. This says the old raise is still there.
if (-not $model.Success) {
    $failures += "$f no longer raises _observer.AttemptModelResolved( - the existing post-action attempt-model disclosure (#349) was deleted or renamed. It is explicitly OUT OF SCOPE for this task (design section 9): the new event is ADDITIVE and the old one becomes the confirmation or correction of what it announced. Restore it; AttemptModelDisclosureTests asserts on it and guardrail 04 runs that suite."
}

# CLAUSE 3 - the raise exists AT ALL, anchored on `_observer.` AND the call paren, so a mention in a
# doc comment, a `nameof(IRunObserver.AttemptRouteResolved)` (which is followed by `)`, never `(`) or
# the name inside an operator-facing message string does not satisfy it (#76 / #521).
if (-not $raise.Success) {
    $failures += "$f never CALLS _observer.AttemptRouteResolved( - the interface member may exist and both decorators may forward it, but nothing ever sends the event, so every consuming surface is fed by an event that is never raised (#120, the unwired factory, at contract granularity). Raise it in the attempt path, INLINE, after the section 6.2 no-route branch returns and before the _actionRunner.RunAsync call. A mention is not a call: a comment, a nameof(), or the name in a message string does NOT satisfy this."
}

# CLAUSE 4 - the ORDERING, which is the entire reason this event exists, expressed as a WINDOW rather
# than a one-sided bound. Only evaluable when all three anchors were found; clauses 1 and 3 have
# already reported a missing one, so this stays quiet rather than adding a second, confusing message
# about the same absence.
#
# 4a - the UPPER bound. A raise after the action returns is AttemptModelResolved wearing a different
# name: presence without timing.
if ($raise.Success -and $run.Success -and $raise.Index -gt $run.Index) {
    $failures += "$f raises _observer.AttemptRouteResolved( at index $($raise.Index), AFTER _actionRunner.RunAsync( at index $($run.Index) - so it fires once the action has already finished, which is exactly when AttemptModelResolved already fires. That mutant compiles, forwards through both decorators and changes NOTHING: the live Model cell still reads its placeholder for the whole attempt (MEASURED at 14m02s and longer on docs/plans/24-plan-source-provenance/state/run.json). Move the raise ABOVE the RunAsync call - after the 'if (route is { NoRoute: true })' branch returns, where route and provenance are already in scope. If you extracted the raise into a private helper, INLINE it: this check compares text positions in one file, so a helper defined below RunAsync reads as a post-action raise."
}

# 4b - the LOWER bound, and the clause that closes the two MEASURED mutants an upper bound alone let
# through (M1: the raise relocated inside the no-route branch, which fires for no launching attempt at
# all; M2: the raise moved into an unrelated method defined earlier in the file, which fires never).
# Both compile, both sit above RunAsync, and both exited 0 against the one-sided form. Everything above
# `_journaler.NoRoute(` is either the resolution itself, the no-route branch, or an earlier method -
# none of which is the launch site.
if ($raise.Success -and $noRoute.Success -and $raise.Index -lt $noRoute.Index) {
    $failures += "$f raises _observer.AttemptRouteResolved( at index $($raise.Index), BEFORE _journaler.NoRoute( at index $($noRoute.Index) - i.e. above the point where the section 6.2 no-route branch settles and returns. Two shapes land here and both are worse than a late raise, because both fire for NO attempt that ever launches: the raise placed INSIDE the 'if (route is { NoRoute: true })' block (the guard excludes every attempt that reaches the action), and the raise moved into a private method defined earlier in the file (never called from the attempt path). Put it INLINE in the attempt path, AFTER that branch returns and BEFORE the _actionRunner.RunAsync call - the window where route and provenance are both in scope and the attempt is genuinely about to launch."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
