# catches: THE named footgun of this whole task - a transparent decorator that does not forward the new
#          event. IRunObserver.AttemptRouteResolved has a DEFAULT NO-OP BODY, so a decorator that omits
#          it COMPILES CLEANLY, satisfies the interface, and silently drops the disclosure. Guardrail
#          01 cannot see it. Guardrail 02 cannot see it. Nothing anywhere reports the loss.
#          And it is not a corner case: OnTheFlyDiagramObserver wraps OnTheFlyLogSiteObserver wraps
#          live-or-console, so BOTH decorators sit in BOTH chains - one missing forward takes the
#          route disclosure away from every operator in every mode, live and --no-ui alike.
#          This is the product's recurring defect shape (a mechanism that works and reports nothing),
#          it has already happened TWICE on this exact interface - VerifierAdvisoryFound and the #469
#          breakdown-phase pair were each lost this way - and it is written down as a hazard on
#          AttemptModelResolved's own doc block in IRunObserver.cs.
#
# WHY THIS IS A SOURCE GREP AND NOT A TEST (#468 demotion gate, rung 3 - stated because an
# unexplained source-shape check on a behavioural claim is itself a finding):
#   It is demotable here, and it is deliberately NOT demoted at this task - it is demoted at the NEXT
#   one. A runtime proof exists and is cheap: tests/Guardrails.Integration.Tests/ModelTiering/
#   AttemptModelForwardingTests.cs already drives each decorator through the IRunObserver interface
#   with a RecordingObserver inner, and carries a reflection sweep that fails when ANY forwarding
#   observer in the Cli assembly does not DECLARE the member. Extending that shape to the new event is
#   a ~30-line test. But this task is the IMPLEMENTATION task; a test authored by the same action that
#   implements the thing is self-certification, which is precisely what this plan's author-tests /
#   implement split exists to prevent. So the runtime pin is authored by 06-author-tests-model-in-row
#   (as a Group B regression pin, GREEN on arrival and deliberately outside its red census), RUN by
#   07-render-model-in-row-and-index's guardrail 02 as a named green-polarity census, and this grep is
#   what gates THIS task, where the defect would actually be introduced and where attribution belongs.
#   THE RESIDUAL THIS FILE USED TO DECLARE IS NOW CLOSED MECHANICALLY, and it had to be, because the
#   thing it deferred to was a pin that appeared in NO guardrail script. The old text said a mangled
#   argument list "is the half the task-05 pin covers" - but nothing ran that pin, so nothing covered
#   it. /guardrails-review MEASURED the mutant at exit 0:
#     M3  the forward present but with `runner` and `model` TRANSPOSED and `requestedTier` hard-coded
#         to null. It compiles (both are `string`), it satisfies a call-anchored clause, and it
#         destroys BOTH things the event carries: the cell would name the 15-25 character model id
#         instead of the eight-character block, and the section 6.2 climb signal - whose PRESENCE is
#         the signal - is gone for every attempt, forever.
#   Clause A now requires the argument list VERBATIM. That is a shape constraint, and it is justified
#   because the shape is not a style choice: the interface pins the parameter names, both shipped
#   decorators already forward AttemptModelResolved as `(task, attempt, model, requestedModel)` -
#   MEASURED, the interface's own names, in both files - and the action prompt hands the agent the
#   exact one-line forward to copy. A transparent decorator has exactly one correct spelling of a
#   pass-through, and any deviation from it is either a rename with no purpose or the M3 defect.
#   The RUNTIME half is closed too, and this time by something that actually runs: task 06's Group B
#   pin BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver drives each decorator through
#   the IRunObserver interface in BOTH shapes (requestedTier present, requestedTier null), and task 07's
#   guardrail 02 names it in a green-polarity census - so a red pin fails at task 07 rather than never.
#   REMAINING RESIDUAL: a regex cannot see a decorator that forwards to something other than `_inner`.
#   /guardrails-review should re-check that by reading the diff.
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root. GR_SUBJECT replaces the
# whole subject LIST with the one file named, which is what makes a single-decorator sample meaningful:
#   samples/03-both-decorators-forward-the-route-event.valid.cs                  -> expect 0
#   samples/03-both-decorators-forward-the-route-event.invalid.cs                -> expect 1 (declared, never forwarded)
#   samples/03-both-decorators-forward-the-route-event.invalid-transposed-args.cs-> expect 1 (M3: forwarded, mangled)
# e.g.
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/samples/03-both-decorators-forward-the-route-event.valid.cs';   ./docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/guardrails/03-both-decorators-forward-the-route-event.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/samples/03-both-decorators-forward-the-route-event.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/05-raise-attempt-route-resolved/guardrails/03-both-decorators-forward-the-route-event.ps1  # expect 1
# RE-RUN ALL THREE after ANY edit to this file, not just the clause you touched.
#
# baseline counts - RE-MEASURED 2026-08-29 over these exact two subjects on the untouched tree, with
# this file's own strip applied, not assumed. Per subject, identically:
#   _inner\.AttemptRouteResolved\s*\(                     0   <- clause A is NOT pre-satisfied
#   the VERBATIM argument list (clause A's full pattern)  0   <- nor is its strengthened form
#   _inner\.AttemptModelResolved\s*\(                     1   <- clause B, the positive control AND
#                                                                the regression pin
#   _inner\.AttemptModelResolved\s*\(\s*task\s*,\s*attempt\s*,\s*model\s*,\s*requestedModel\s*\)
#                                                         1   <- the PRECEDENT for clause A's shape:
#                                                                the house form for a pass-through in
#                                                                BOTH decorators is already the
#                                                                interface's own parameter names, in
#                                                                order, verbatim.
# The second number is what proves the scan actually reached each file rather than searching an empty
# string. Note that ONE of the two subjects, OnTheFlyDiagramObserver.cs, is in task 02's writeScope and
# has already been edited by the time this runs - which is exactly why the baseline is expressed as a
# token count in THIS file rather than as a line number.
$subjects = if ($env:GR_SUBJECT) {
    @($env:GR_SUBJECT)
} else {
    @(
        'src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs',
        'src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs'
    )
}

$failures = @()

foreach ($f in $subjects) {
    if (-not (Test-Path $f)) {
        $failures += "$f does not exist - this decorator is one of the two transparent IRunObserver wrappers the new event must survive, and it cannot be verified if it is missing"
        continue
    }

    # THE TWO-VARIABLE RULE (catalogue): one strip, two levels, and each clause reads the level it
    # needs. Both clauses here read $scan, because both match a C# IDENTIFIER: a mention in the
    # explanatory comment above the member - which this file is ASKED to carry - must not satisfy the
    # check that the member is actually called (#521).
    $raw  = Get-Content $f -Raw                                  # NEVER matched against
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
    $code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
    $scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
    $scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
    $scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

    # CLAUSE A - the forward is a CALL ON THE INNER OBSERVER. Anchored on the receiver AND the call
    # paren (#76 / issue #521): a doc comment naming the member, a nameof(...) - which is followed by
    # ')', never '(' - and the name inside a message string all fail to satisfy it. Declaring the
    # member and dropping the payload on the floor is the mutant this anchor exists for, and it is the
    # one a "does the type declare it" reflection check would wave through.
    $forwards = ([regex]::Matches($scan, '_inner\s*\.\s*AttemptRouteResolved\s*\(')).Count
    if ($forwards -lt 1) {
        $failures += "$f never CALLS _inner.AttemptRouteResolved( - so the new launch-time route disclosure stops dead at this decorator. The interface member has a DEFAULT NO-OP BODY, which means omitting it (or declaring it with a body that does not forward) COMPILES CLEANLY and drops the event silently, in every mode: this decorator is stacked in BOTH the live and the --no-ui chain, so no operator sees it anywhere. Add the one-line forward in the shape the file already uses for AttemptModelResolved, passing every argument through verbatim. A mention is not a call: a comment, a nameof(), or the name in a message string does NOT satisfy this."
    }
    # CLAUSE A2 - the ARGUMENTS, VERBATIM. A call-anchored clause proves the event leaves this
    # decorator; it says nothing about what leaves WITH it. MEASURED at exit 0 against the previous
    # version: `_inner.AttemptRouteResolved(task, attempt, model, runner, tier, null)` - `runner` and
    # `model` transposed (both are `string`, so it compiles) and `requestedTier` hard-coded null. Its
    # two effects are exactly the two facts the event exists to carry: the consuming cell would name a
    # 15-25 character model id where an eight-character block name belongs, and the section 6.2 climb
    # signal - which IS the presence of requestedTier - would be gone for every attempt.
    # This is a SHAPE constraint and it is deliberate: a transparent pass-through has one correct
    # spelling, the parameter names are pinned by the interface, and BOTH shipped decorators already
    # write the AttemptModelResolved forward as `(task, attempt, model, requestedModel)` - the same
    # names, in order, verbatim. Only evaluated when a forward exists, so a missing forward gets one
    # message and not two.
    if ($forwards -ge 1 -and
        $scan -cnotmatch '_inner\s*\.\s*AttemptRouteResolved\s*\(\s*task\s*,\s*attempt\s*,\s*runner\s*,\s*model\s*,\s*tier\s*,\s*requestedTier\s*\)') {
        $failures += "$f forwards AttemptRouteResolved but NOT with the argument list verbatim. It must read exactly '_inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier)' - same names, same order, nothing substituted. Two mangles compile and are what this clause exists for: transposing runner and model (both are string, so the consuming cell names the model id where the eight-character promptRunners block name belongs), and hard-coding requestedTier to null (its PRESENCE is the section 6.2 climb signal - null it and every climb becomes invisible, forever, in every mode). Do not rename the parameters either: the interface pins them, and both decorators already forward AttemptModelResolved as (task, attempt, model, requestedModel). A decorator that wants to ACT on the event still forwards it unchanged first."
    }

    # CLAUSE B - the POSITIVE CONTROL and simultaneously a REGRESSION pin, green on arrival BY DESIGN
    # (#478's named exception: "this existing thing still exists"). Two jobs, one clause: it proves the
    # strip above did not eat the file (a zero on clause A over an empty $scan would look identical to
    # a real miss), and it catches the cheapest wrong move available here - REPLACING the existing
    # attempt-model forward with the new one instead of adding beside it. Changing
    # AttemptModelResolved is out of scope by design section 9.
    $modelForwards = ([regex]::Matches($scan, '_inner\s*\.\s*AttemptModelResolved\s*\(')).Count
    if ($modelForwards -lt 1) {
        $failures += "$f no longer CALLS _inner.AttemptModelResolved( - the EXISTING #349 attempt-model forward was removed or renamed while adding the new one. It is out of scope for this task (design section 9): the route event is ADDITIVE and sits BESIDE the model event, it does not replace it. Restore the forward; AttemptModelForwardingTests pins it and guardrail 04 runs that suite. (This clause is also this check's positive control: if it fires together with clause A on the same file, suspect the subject path rather than the code.)"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
