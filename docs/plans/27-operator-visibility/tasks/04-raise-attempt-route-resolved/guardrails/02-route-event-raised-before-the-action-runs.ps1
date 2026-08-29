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
#   the AttemptModelForwardingTests-shaped regression pin task 05 adds).
#   HONEST RESIDUAL, stated rather than implied: this compares TEXT POSITIONS in one file. It proves
#   the raise is written above the RunAsync call; it does not prove the two are on the same control
#   path. A raise placed in an unrelated method that happens to sit earlier in the file would satisfy
#   it. /guardrails-review should re-check that residual by reading the diff.
#   SECOND RESIDUAL, and the one the action prompt is told about so it is not a surprise: because the
#   clause is positional, EXTRACTING THE RAISE INTO A PRIVATE HELPER defined below RunAsync would read
#   as a post-action raise and red-fail a correct implementation. The prompt therefore asks for the
#   raise INLINE at the site, which is what every other `_observer.` raise in this file already does.
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root:
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/04-raise-attempt-route-resolved/samples/02-route-event-raised-before-the-action-runs.valid.cs';   ./docs/plans/27-operator-visibility/tasks/04-raise-attempt-route-resolved/guardrails/02-route-event-raised-before-the-action-runs.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/04-raise-attempt-route-resolved/samples/02-route-event-raised-before-the-action-runs.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/04-raise-attempt-route-resolved/guardrails/02-route-event-raised-before-the-action-runs.ps1  # expect 1
#
# baseline counts - MEASURED 2026-08-29 over this exact subject (src/Guardrails.Core/Execution/
# TaskExecutor.cs) on the untouched tree, with this file's own strip applied, not assumed:
#   _observer\.AttemptRouteResolved\s*\(    0   <- the clause is NOT pre-satisfied
#   _actionRunner\.RunAsync\s*\(            1   at scan index 21538   (the anchor is UNIQUE)
#   _observer\.AttemptModelResolved\s*\(    1   at scan index 22989   (the positive control)
# The two indices are the measurement that proves this clause DISCRIMINATES rather than merely
# passing: the SHIPPED attempt-model raise sits 1,451 characters AFTER the action runs, so an
# AttemptRouteResolved placed beside it - the exact mutant above - fails, and one placed at the
# launch site passes. No ancestor task writes into this subject: tasks 01-03 name only
# LogSiteRenderer.cs, LogServer.cs, OnTheFlyDiagramObserver.cs, HtmlDiagramRenderer.cs and tests.
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

$raise = [regex]::Match($scan, '_observer\s*\.\s*AttemptRouteResolved\s*\(')
$run   = [regex]::Match($scan, '_actionRunner\s*\.\s*RunAsync\s*\(')
$model = [regex]::Match($scan, '_observer\s*\.\s*AttemptModelResolved\s*\(')

$failures = @()

# CLAUSE 1 - the ANCHOR SANITY check, and it is NOT a finding about this task's own work. If the
# RunAsync call is gone the ordering clause below cannot be evaluated at all, and a guardrail that
# silently skipped its own comparison would exit 0 having certified nothing. Reported separately so a
# retry agent reads "the anchor moved", not "your raise is misplaced".
if (-not $run.Success) {
    $failures += "$f no longer contains a _actionRunner.RunAsync( call, which is the anchor this check measures the raise against. Either the attempt path was restructured (NOT this task's job - it is out of scope) or the file is not what this guardrail thinks it is. Do NOT move the raise to satisfy this: restore the call site, or escalate needsHuman with the two quotes the harness contract asks for."
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

# CLAUSE 4 - the ORDERING, which is the entire reason this event exists. Only evaluable when both
# anchors were found; clauses 1 and 3 have already reported the missing one, so this stays quiet
# rather than adding a second, confusing message about the same absence.
if ($raise.Success -and $run.Success -and $raise.Index -gt $run.Index) {
    $failures += "$f raises _observer.AttemptRouteResolved( at index $($raise.Index), AFTER _actionRunner.RunAsync( at index $($run.Index) - so it fires once the action has already finished, which is exactly when AttemptModelResolved already fires. That mutant compiles, forwards through both decorators and changes NOTHING: the live Model cell still reads its placeholder for the whole attempt (MEASURED at 14m02s and longer on docs/plans/24-plan-source-provenance/state/run.json). Move the raise ABOVE the RunAsync call - after the 'if (route is { NoRoute: true })' branch returns, where route and provenance are already in scope. If you extracted the raise into a private helper, INLINE it: this check compares text positions in one file, so a helper defined below RunAsync reads as a post-action raise."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
