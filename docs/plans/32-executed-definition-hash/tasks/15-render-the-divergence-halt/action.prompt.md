## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "15-render-the-divergence-halt": { "someKey": "someValue" } }`.
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

This task implements stage 15 of `docs/plans/32-executed-definition-hash.md`. **Read sections 6.5, 6.6, 9
and 15.1 in full.** Where this prompt and the plan disagree, the plan is authoritative and you should say
so in your summary.

> **Every `RunCommand.cs` reference in the plan names a MEMBER, never a line**, and the plan says why: an
> earlier draft's five line numbers were all stale within hours, two pointing at a different member.
> `RenderPlanEditWarning`, `DescribeDelivery`, `planGuardrailsPassed` and `willEvaluateTerminalGate` are
> stable; the line numbers were not. **Find everything by symbol.**

## Task - five surfaces in one file

### 1. Render the halt in the NORMAL end-of-run path, at exit 2

**NOT where `DefinitionDrift` is rendered.** §6.5 is explicit, and calls the alternative a *concrete wrong
answer*: `DefinitionDrift` returns from a **pre-DAG early return**, correct for drift precisely because
nothing ran and no logs were written. **A divergence run executed every task.** Returning there would skip
`WriteDurableFinalSite`, `IngestRunTelemetry` (#535), `PrintSummary` and `PrintStaticIndexLink` -
discarding the logs, telemetry and summary for a run that did thirty tasks' worth of work.

So: render in the **normal end-of-run path, after the summary**, changing only the **exit code** and the
**headline**. Exit **2** (actionable / needs-human), following `DefinitionDrift`'s precedent - **never
exit 1**, which is reserved for infrastructure faults.

**The halt text must name all three facts an operator needs** (§9):
1. **which definition files moved** (the gate hands you the list);
2. that the task ran the **pinned** bytes - the ones it was verified against;
3. that `task.json` and the DAG are **held from load**, while action prompts and guardrail scripts are
   **re-read per attempt**.

§9: *"This is the one place a half-true message actively misleads, so it is asserted on the string."*
Stage 14 wrote those assertions; read them before you write the text.

The remediations are §7.2's, **unchanged** - `--autonomy auto`, `guardrails reset <folder> <taskId>`,
`guardrails reset <folder> -y`. §6.6: *"C is A's finding delivered one run earlier"*, so the gate carries
no remediation vocabulary of its own.

### 2. Correct `RenderPlanEditWarning`'s advisory

It currently tells the operator the task *"records its POST-edit definition hash when it settles"* and that
*"Nothing was halted and nothing was re-run."* After this plan the **PRE-edit** hash is recorded, and on a
real definition edit something **is** halted. Both sentences invert.

**This is the sentence that would have shipped false.** §15.1: the cheapest green leaves stage 14's
assertions passing by never touching this string, producing a harness that prints *"Nothing was halted and
nothing was re-run."* beside `exit 2` and a blocked delivery.

### 3. The terminal gate reports NOT EVALUATED, never PASSED

`planGuardrailsPassed` is `!report.AllSucceeded || await PlanGuardrailPhase.EvaluateAsync(...)`. With the
new `AllSucceeded` term, a divergence run does not merely skip the terminal plan-guardrail phase - **it
records that the gate PASSED**. Make the divergence case report it as *not evaluated*. (§6.5 accepts that
the gate is not run: evaluating a gate whose result cannot change the outcome spends real money for a
number nobody acts on. What it does not accept is recording a verdict that never happened.)

### 4. `DescribeDelivery`'s durable reason

It would write *"the run was not wholly green, so delivery was never attempted"* into `run.json` for a run
whose `tasks{}` shows **every task `succeeded`**. That record exists (#542) so an unattended pipeline with
no console has a machine-readable answer; a wrong one is worse than none. Give the divergence case its own
reason string.

Related and correct: the `*** WORK NOT DELIVERED ***` banner (`WhollyGreenButUndelivered`) goes **false**
and does not fire. That is right **only because the divergence halt replaces it** - §6.5: *"If stage 15
renders nothing, the run goes quiet, which is the failure this plan exists to prevent, one level up."*

### 5. Refuse the drift-accept `[a]` for a divergence-originated drift

§6.6. After a divergence halt the operator re-runs, the §7.2 resume pre-pass mismatches on exactly the
diverged tasks, and the interactive prompt offers `[y] / [a] / [N]`. `[a]` calls
`RunJournal.RecordDriftAccepted`, which **overwrites the recorded hash with current disk and does not
re-run the task**. Reached from a divergence halt that is **one keystroke from re-creating precisely the
lie #556 is about** - and it is worse than the original defect, because it also **un-corroborates the plan
branch**: the task's commit still carries the old `Guardrails-Task-Hash:` trailer while the journal now
carries the new hash, so `SafeSuffixEvaluator`'s trailer-corroboration rule refuses any later Part C
rewind covering that task and steers the operator to a full `guardrails reset -y`.

**Decided: `[a]` is REFUSED for divergence-originated drift.** The condition needs no new state - a task
whose journal entry carries `definitionHashAtSettle` (stage 12's field) is **by construction** one that ran
a definition it does not match. Drop the `[a]` option for those tasks, say why, and name
`guardrails reset <folder> <taskId>` instead.

**`[a]`'s behaviour for an ORDINARY between-runs edit is UNCHANGED** (§12): that trade is already reviewed
and is not this plan's to relitigate.

> **You are the only stage that can implement this, and no stage in this plan authors a behavioural test
> for it.** `ConfirmSafeDriftIfInteractive` is `private static` and gated on `!Console.IsInputRedirected`,
> so no test-authoring row could reach it - stage 11's file would not compile against a member that does
> not exist, and every later row is downstream of you. Guardrail 03 is a source-shape check standing in for
> the test the plan asks for, and the breakdown report flags the gap for the human. If you can make the
> option rendering reachable without changing behaviour, that is a genuine improvement - but do not widen
> your `writeScope` to add a test for it.

## Do NOT

- Do NOT render the halt at the pre-DAG early return (§6.5).
- Do NOT use exit 1. Exit **2**.
- Do NOT introduce a second delivery predicate; `AllSucceeded` (stage 13) is the only gate (§6.5 / #457).
- Do NOT change `[a]` for ordinary between-runs drift (§12).
- Do NOT edit any test file. Stage 14's assertions are your specification; if one looks wrong, say so with
  `needsHuman` rather than changing it.
- Do NOT reach into `src/Guardrails.Core/**`. `Guardrails.Cli` carries **no** `InternalsVisibleTo` into
  Core, which is why `RenderPlanEditWarning` and `DescribeDelivery` are `public static`. If you need
  something internal, that is a Core change and Core is outside your scope - escalate.

## Commit body

Carry a literal **`Fixes #556`** line in the commit body. §15: *"A `fix(#556):` conventional-commit SCOPE
is not a closing keyword"* (#547's lesson). The PR body must repeat it.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside that path -
including every `src/Guardrails.Core/**` file, any test file, and the `.csproj`. An out-of-scope edit fails
the task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
