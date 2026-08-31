---
description: The #554 forward carry - route the preserved work into the next attempt's composed prompt and into the escalation record
---

## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-carry-salvage-forward-to-prompts": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements stage 3 of `docs/plans/31-unattended-run-hardening.md`. READ THE SECTIONS NAMED
BELOW before you start. Where this prompt and the plan disagree, the plan is authoritative and you
should say so in your summary.

Read: **plan sections 3.3 (the last four seam rows), 3.5 and 8 (the `#554` bullets)**.

## What stage 2 left you, and how to find it

Stage 2 has already merged by the time you run, so `RetryPolicy.AppendSalvageSection` and
`AppendHeader` are `internal static` and take a defaulted `SalvageFraming framing` parameter with (at
least) `Retry`, `Escalation` and `PriorAttempt` values. **This describes the state at plan-authoring
time, before stage 2 had actually run - verify it is still accurate before assuming this exact shape.**
Locate them by SYMBOL, not by line number: grep `RetryPolicy.cs` for `AppendSalvageSection` and for
`enum SalvageFraming`. Any line number quoted in the plan for that file has moved.

`RetryPolicy` and `PromptComposer` are in the same assembly (`Guardrails.Core`), so `internal` is
visible to you without any project change.

## Task

Make the Core `EscalationSalvageTests` pins C1, C2, C3 and C4 pass, and the Integration pin I5. Do not
edit any test.

### Seam 1 - `PriorAttemptRef` gains two OPTIONAL init-only members

`src/Guardrails.Core/Prompts/PromptContext.cs`. `PriorAttemptRef` is **not a positional record** - it is
a record with `required`/optional init properties (`Attempt`, `Outcome`, `LogDir`, `TranscriptPath`,
`FeedbackPath`). Add `SalvagePatchPath` and `SalvageRefName` as **non-`required` init-only** members, so
neither of the two existing construction sites moves:

- `src/Guardrails.Core/Execution/DependencyContextBuilder.cs` (yours)
- `tests/Guardrails.Core.Tests/PromptComposerTests.cs` (**not yours - do not touch it**)

Making either member `required`, or adding a positional parameter, breaks that second site and fails
this task.

### Seam 2 - `DependencyContextBuilder.BuildPriorAttempts` fills them by PROBING

It already walks the journal and already knows each prior attempt's `LogDir`. Fill the two new members
by **`File.Exists(logDir/prior-attempt.patch)`** and by DERIVING the ref name from the task id and the
attempt number (`refs/guardrails/<taskId>/attempt-<N>`).

**No journal schema change.** The patch file's existence IS the record, and the ref name is fully
derivable - journalling either would create a second source of truth for a fact the filesystem already
holds and would need a migration. This also generalises correctly: EVERY prior attempt that left a
patch is now routed, not only an escalating one.

### Seam 3 - `PromptComposer.AppendPreviousAttempt` routes, it does not re-author

Today that method renders priors as a flat bullet list of log paths whose only instruction is "Read the
transcript... and the feedback" - no recovery guidance at all. For a prior attempt carrying a patch it
must now call **`RetryPolicy.AppendSalvageSection(..., SalvageFraming.PriorAttempt)`**.

**One owner of that text.** Do NOT write a second copy of the routing prose in `PromptComposer`. The
whole point of stage 2 making the method `internal` and adding the framing parameter was that this call
site reuses it. Pins C1-C3 assert the composed bytes carry the size-routed choice (`prior-attempt.patch`
for a small edit, `git show "<ref>:<path>"` for a new file), the `writeScope` caveat, and the derived ref
name - all emitted by `AppendSalvageSection`.

**C4 is the silence half and it is easy to break:** a prior attempt with NO patch must produce NO
recovery block. Gate the call on the member being present.

`tests/Guardrails.Core.Tests/PromptComposerTests.cs` asserts on the composed prompt with
`Assert.Contains`/`Assert.DoesNotContain` and constructs priors carrying no salvage members, so an
additive, correctly-gated block leaves it passing. It is outside your `writeScope`; if it goes red, the
block is not gated - fix the gate, not the test.

### Seam 4 - `Scheduler.BuildGateContext` names the ref and the patch

The escalation `Context` string is what a human, or a firstmate answering the escalation, actually
reads at the halt. When the escalating attempt left a salvage ref and a patch, name both. Pin I5
asserts it. Locate `BuildGateContext` by symbol - `Scheduler.cs` is large and any line number the plan
quotes for it may have moved.

### Why the pin is on the composed prompt and NOT on feedback.md

The escalation path returns `FeedbackPath: null`, so the forward carry runs through `PriorAttemptRef`,
not through the inlined-feedback route. A test reading `feedback.md` would pass with the composed
prompt still silent (plan section 3.5 clarification 1). One more path bullet in the list satisfies "names it"
and changes nothing an agent does (clarification 2) - the routing is the deliverable.

### Do NOT

- Do NOT edit any file under `tests/**`. All of this stage's pins were authored by task 01 and are
  outside your `writeScope`; an edit there fails the task immediately and consumes a retry.
- Do NOT edit `RetryPolicy.cs` - it is stage 2's file and outside your scope. You CALL
  `AppendSalvageSection`; you do not change it. If the `PriorAttempt` framing stage 2 landed emits the
  wrong text, that is a `needsHuman`, not a scope widening.
- Do NOT add a journal field for the ref name or the patch path.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/PromptContext.cs`, `src/Guardrails.Core/Execution/DependencyContextBuilder.cs`,
`src/Guardrails.Core/Prompts/PromptComposer.cs` and `src/Guardrails.Core/Execution/Scheduler.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside these paths -
including `RetryPolicy.cs`, any test file, and the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. Do NOT edit the authored tests: make them pass by fixing the
implementation, and if a test is genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the
state-out path and stop.
