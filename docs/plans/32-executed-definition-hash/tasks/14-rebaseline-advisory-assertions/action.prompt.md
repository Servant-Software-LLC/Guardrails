## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "14-rebaseline-advisory-assertions": { "someKey": "someValue" } }`.
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

This task implements stage 14 of `docs/plans/32-executed-definition-hash.md`. **Read section 15.1 in
full**, plus sections 5.1, 5.6 and 6.5. Where this prompt and the plan disagree, the plan is authoritative
and you should say so in your summary.

## Why these three assertions are HERE and not in stage 2

Section 15.1: all three depend on surfaces only **stage 15** may change - the CLI exit code and the
literal advisory string `RunCommand.RenderPlanEditWarning` emits. An earlier draft of the plan put every
rewrite in stage 2 and the string fix in the last stage: **twelve stages apart, with the red landing on
the one stage that cannot fix it.**

> **And the stall was not the worst outcome.** The cheapest green leaves these assertions **passing**: an
> implementer who never touches the advisory ships a harness that prints *"Nothing was halted and nothing
> was re-run."* beside `exit 2` and a blocked delivery - a message that is now false, on the exact surface
> this plan exists to make honest, in a product whose thesis is that nothing is marked done unverified.

Pairing them with stage 15 into one author-tests → implement pair is what removes that option. You are
that pair's first half.

## Task

Rewrite **three assertions across two methods** in
`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`. Change nothing else about the file's
behaviour.

### Row 3 - `ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero`, near lines 161 and 167

```csharp
Assert.Equal(ExitCodes.Success, exit);                 // -> exit 2 (needs-human, never 1)
Assert.True(delivery!.Delivered, "...");               // -> Delivered == false
```

Both invert: after this plan a run whose plan folder was edited mid-run **halts at exit 2 and does not
deliver**, with the work retained on the plan branch (§6.5, §6.7's P13). Add an assertion that the work is
retained if the fixture makes that cheap.

**The method name no longer describes the behaviour, so RENAME it** to
**`ARunCarryingOnlyAPlanEditObservation_HaltsWithExitTwoAndDoesNotDeliver`** - pinned; guardrail 02's
census filters on it.

### Rows 4 and 5 - `TheRenderedText_CarriesAllThreeSection51Consequences`, near lines 251 and 257

```csharp
Assert.Contains("post-edit", advisory, StringComparison.OrdinalIgnoreCase);   // -> the PRE-edit hash
Assert.Contains("Nothing was halted", advisory, StringComparison.OrdinalIgnoreCase);  // -> something IS halted
```

The advisory today says the task will record the **post-edit** hash and that *"Nothing was halted and
nothing was re-run."* After this plan the **pre-edit** hash is recorded and, on a real definition edit,
something **is** halted. Both the string and the assertions invert.

**Keep the method name** - the three §5.1 consequences it names are still three. Keep
`StringComparison.OrdinalIgnoreCase`: the literal in `RunCommand` is uppercase (`POST-edit`), which is why
the shipped assertion matches at all. **Invert the CLAIM, not the comparison.**

Assert the new text carries the three facts §9 requires an operator to be told: **which files moved**,
that the task ran the **pinned** bytes, and that `task.json` is **held from load** while prompts and
guardrail scripts are **not**. §9: *"This is the one place a half-true message actively misleads, so it is
asserted on the string."* Pick the assertions you would want to read at 3am; you are writing the
specification stage 15 implements.

## What this stage must NOT do

- **Do NOT touch `AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges`.** Its
  `Assert.True(report.AllSucceeded, ...)` is §6.7's **P16** and §15.1's *"one assertion that must NOT
  move"* - the only thing standing between the delivery gate and being muted within a week. Stage 13 has
  just landed the gate; this file's tripwire is what proves the gate stayed quiet on an editor artifact.
- **Do NOT touch `AGuardrailEditedMidRun_...`** (stage 2's row 2) or
  `AJitWaveBreakdownFollowedByRevert_...`.
- **Do NOT delete a test, mark one `[Fact(Skip = ...)]`, or narrow one to its passing half.** The
  `[Fact]` count is **5** before and after; guardrail 03 asserts it.
- **Do NOT delete the HashText rationale comment** stage 2 re-derived (§15.1). It must still contain the
  words `HashText` and `enumerates`; guardrail 03 asserts both.
- **Do NOT change the fixture.** The edit IS the thing under test (§11).

## What "red" means here

After your change both manifested methods carry **legitimate red** until **stage 15** lands the exit-code
branch and the corrected advisory. Guardrail 02 asserts exactly that. A rewrite that is green here did not
change the contract.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path - including `RunCommand.cs`, production
files, other test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
