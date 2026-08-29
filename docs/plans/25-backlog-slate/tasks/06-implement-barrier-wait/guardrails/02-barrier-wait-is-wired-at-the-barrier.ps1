# catches: BarrierWait built, unit-tested and GREEN in isolation while Scheduler never uses it - the
#          policy is dead code reachable only from xUnit, a barrier-time 429 still ends the run, and
#          #511 is closed with nothing delivered (#120). And its twin: a Scheduler that DOES compute a
#          wait but never RAISES it, so the operator watches an unexplained 30-minute silence and
#          cannot tell a healthy wait from a hung run - the "surfaced" half of the plan's done-when,
#          which is the half that disappears quietly.
#
# Why this is a SOURCE GREP and not a test (#468 demotion order, dotnet.md 10c - the WEAKEST wiring
# form, used here because the stronger ones are structurally unavailable). This task's writeScope is
# exactly two production files and NO test file, so it cannot author a test that drives
# Scheduler.RunBreakdownSegmentsAsync; and task 05's class (BarrierWaitTests) is a policy unit-test
# class whose red census must be satisfiable against a NotImplementedException stub, so a
# Scheduler-driving barrier test cannot live there either. No test in this plan can reach the barrier
# call site. This grep proves the TEXT is there; it does NOT prove the call is reached on the
# production path, and it cannot see whether the pause loop actually re-probes rather than falling
# through. Stated, not glossed: /guardrails-review should re-check that residual, and it is the
# #382 integration-proof gap wearing this plan's clothes.
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root:
#   $env:GR_SUBJECT='docs/plans/25-backlog-slate/tasks/06-implement-barrier-wait/samples/02-barrier-wait-is-wired-at-the-barrier.valid.cs';   ./docs/plans/25-backlog-slate/tasks/06-implement-barrier-wait/guardrails/02-barrier-wait-is-wired-at-the-barrier.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/25-backlog-slate/tasks/06-implement-barrier-wait/samples/02-barrier-wait-is-wired-at-the-barrier.invalid.cs'; ./docs/plans/25-backlog-slate/tasks/06-implement-barrier-wait/guardrails/02-barrier-wait-is-wired-at-the-barrier.ps1  # expect 1
#   Remove-Item Env:\GR_SUBJECT
#
# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive over this exact
# subject (src/Guardrails.Core/Execution/Scheduler.cs), not assumed. Every alternative measured
# SEPARATELY, so a nonzero hiding inside an alternation cannot pass as a zero for the whole clause:
#   new\s+BarrierWait\s*\(                          0
#   \bBarrierWait\s*\.\s*[A-Za-z_]\w*\s*\(          0
#   \.\s*PromptPaused\s*\(                          0
#   (bare word 'BarrierWait' 0, bare word 'PromptPaused' 0 - so not even a doc-comment mention
#    pre-satisfies anything, and the $scan strip below cannot be doing hidden work)
#   No ancestor task writes these tokens into this subject: this task's ONLY ancestor is task 05,
#   whose writeScope is tests/.../BarrierWaitTests.cs + src/Guardrails.Core/Providers/BarrierWait.cs -
#   neither is Scheduler.cs.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/Scheduler.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - cannot verify the barrier wait is wired into the wave barrier"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# DELIBERATE DEVIATION from dotnet.md 11a, stated so a reviewer can re-decide it: 11a assigns REQUIRED
# clauses to $code and reserves $scan for FORBIDDEN ones. Both required clauses below read $scan
# instead. The reason is specific to this subject: Scheduler.cs is dense with operator-facing halt and
# decision TEXT, and the whole point of this task is a new operator-facing message - so a `reason`
# string mentioning "PromptPaused" or "BarrierWait" is a realistic thing for the implementing agent to
# write, and under $code it would satisfy the clause with no wiring at all. Both tokens are C# type /
# member identifiers that no correct implementation can express ONLY inside a string literal, so the
# stricter source costs nothing and closes the #470/#75 hole. Measured above: the strip changes
# nothing on the untouched tree (both bare-word counts are 0).
$failures = @()

# ANCHORED ON THE CALL, NOT THE NAME (#76, and the #521 lesson measured 2026-08-28). The trailing
# `\s*\(` is the whole rule and it is the half that gets dropped in an edit: a clause matching the
# dotted NAME is satisfied by `nameof(BarrierWait.WaitAsync)`, which is valid C#, survives the $scan
# strip (nameof is not a string literal), and invokes NOTHING. A mutant whose only references were
# inside nameof() with zero invocations was MEASURED exiting 0 against the un-parenthesised form.
#
# Both alternatives are call-shaped by construction: `new BarrierWait(` is a constructor invocation,
# and the member alternative requires an argument list. Task 05's prompt pins BarrierWait as a class
# with a constructor-settable ceiling and probe interval, so `new BarrierWait(...)` is the shape a
# correct implementation writes; the static/member alternative is kept so a factory or a static entry
# point is not false-RED. Do NOT relax either back to a bare dotted name or a bare word.
if ($scan -cnotmatch '(new\s+BarrierWait\s*\(|\bBarrierWait\s*\.\s*[A-Za-z_]\w*\s*\()') {
    $failures += "$f does not USE BarrierWait - the barrier wait policy is not wired into the wave barrier, so a provider quota limit at a barrier still ends the run and #511 is closed with dead code. Construct it (new BarrierWait(...)) or CALL a member on it (BarrierWait.Something(...)); naming the type in a comment, a message string, or a nameof() does NOT count."
}

# The 'surfaced' half. PromptPaused is a METHOD - `void PromptPaused(TaskNode, string, TimeSpan, int)`
# at IRunObserver.cs:84, invoked as `_observer.PromptPaused(task, reason, delay, n)` at
# TaskExecutor.cs:218 - so requiring the call paren is correct here and cannot be the mirror false-red
# (#521's other half: a paren demanded of a PROPERTY). Deliberately NOT anchored on the `_observer`
# field name: the receiver is a HOW detail, and pinning it would false-RED a correct implementation
# that routes the call through a helper taking IRunObserver as a parameter.
# NAMED RESIDUAL of that choice (#76's local-method hole): a `this.PromptPaused(...)` call to a
# same-named LOCAL method would also satisfy this clause. Judged remote rather than closed - Scheduler
# does not implement IRunObserver, so such a method would be pure invention with nothing to gain - and
# recorded here rather than left for a reader to discover.
if ($scan -cnotmatch '\.\s*PromptPaused\s*\(') {
    $failures += "$f never CALLS IRunObserver.PromptPaused - the barrier may compute a wait but nothing tells the operator, so a 30-minute barrier pause renders as unexplained silence and reads exactly like a hung run. Raise the EXISTING hook (issue #115's signal); do not add a new observer method - IRunObserver.cs and the observer implementations are out of this task's write scope and another cluster in this plan is editing them (#175). A mention is not a call: nameof(IRunObserver.PromptPaused) does NOT satisfy this - invoke it."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
