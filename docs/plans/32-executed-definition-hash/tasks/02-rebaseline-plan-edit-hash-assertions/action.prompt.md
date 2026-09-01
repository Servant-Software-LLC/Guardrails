## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-rebaseline-plan-edit-hash-assertions": { "someKey": "someValue" } }`.
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

This task implements stage 2 of `docs/plans/32-executed-definition-hash.md`. **Read section 15.1 in
full** - it specifies every assertion that moves, by method name rather than by line number, and it says
why. Also read sections 5.5, 6.2 and 6.5. Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

## Why this stage exists at all

`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs` shipped with plan 31 and encodes
**today's** contract: a mid-run plan-folder edit is *advisory and inert*. This plan makes it **gating**.
Two of its assertions therefore invert, on a file **no implementation stage may write** - section 11
forbids every implementation stage from touching `tests/**`. Without this stage the plan has no green
path: stage 5 turns line 209 red on a file no other row can fix.

## Task

Rewrite **exactly two assertions**, in place. Change nothing else about the file's behaviour.

### Row 1 - `AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges`

Today (near line 209):

```csharp
Assert.NotEqual(hashAtStart, recorded);
```

becomes an **`Assert.Equal(hashAtStart, recorded)`**. `hashAtStart` (near line 185) is
`TaskDefinitionHash.Compute` over the loaded node **before** the run; after this plan the recorded hash
IS that load-time pin, computed from the same bytes at the same moment. Give the assertion a message
naming the new contract, in the style of the file's existing assertion messages.

**Its sibling at line 190 - `Assert.True(report.AllSucceeded, ...)` - MUST NOT MOVE.** That is section
15.1's "one assertion that must NOT move" and section 6.7's P16: a stray `.DS_Store` is an editor
artifact, the in-run gate compares the **ignore-list-filtered** surface, so the run stays green and
delivers. It is, in the plan's words, *"the only thing standing between the delivery gate and being muted
within a week."* Leave it exactly as it is.

**The comment at lines 204-206 is not a comment - it is the SSOT's reasoning, and it must be
RE-DERIVED, not deleted.** It reads, in substance: *HashText enumerates `"*"` and filters nothing, so the
artifact IS part of the definition - and must stay that way; moving the ignore list into HashText would
move every recorded definition hash in every plan.* Every word of that stays true (sections 4.4 and 5.5).
What changes is the sentence it supports: the artifact is still part of the **recorded** definition, and
is now deliberately **outside the in-run gate's comparison surface** (section 6.2). Rewrite the comment to
say both - keep the HashText reasoning verbatim in substance, and add the filtered-gate consequence. Do
**not** delete the reasoning along with the assertion it used to justify.

**The rewritten comment MUST still contain both words verbatim: `HashText` and `enumerates`.** Guardrail
03 keys on exactly those two, and they are pinned here so the guardrail and this prompt agree by
construction rather than by luck. They are single WORDS on purpose - a multi-word phrase would be split
by an innocent comment re-wrap, and a marker a correct rewrite can break by reflowing is a false red with
no remedy. (One marker alone would be too weak: an unrelated comment could carry `HashText` while the
reasoning was gone.)

### Row 2 - `AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision`

Today (near line 77):

```csharp
Assert.True(report.AllSucceeded, "...");
```

becomes **`Assert.False(report.AllSucceeded, ...)`**. A `guardrails/*.ps1` script is a **real definition
file** - not an editor artifact - so after this plan the settle-time divergence gate fires, the run is not
reported green, and delivery is blocked (sections 6.3 and 6.5). Write the message so a future reader
understands why the sense inverted, and keep the rest of the test (the "exactly one observed plan-edit
decision" assertions) untouched: the watch's advisory behaviour is unchanged by this plan.

### What this stage must NOT do

- **Do NOT touch `ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero` or
  `TheRenderedText_CarriesAllThreeSection51Consequences`.** Section 15.1 rows 3-5 belong to **stage 14**,
  deliberately: they depend on the CLI advisory string and the exit code, which only stage 15 may change,
  and pairing them into one author-tests -> implement pair is what stops a harness shipping *"Nothing was
  halted and nothing was re-run"* beside `exit 2` and a blocked delivery.
- **Do NOT delete a test, mark one `[Fact(Skip = ...)]`, or narrow one to its passing half.** The file's
  `[Fact]` count is **5** before and after; guardrail 03 asserts it.
- **Do NOT rename any method in this stage.** (Stage 14 renames one; that is its job, not yours.)
- **Do NOT change the fixture** - `CreateMidRunEditPlan`, the `MidRunWrite` enum, `MidRunLine`. The edit
  IS the thing under test (section 11): a task that "stabilizes a flaky timing test" by removing the edit
  has deleted the plan.

## What "red" means here, and why that is correct

After your change this file carries **legitimate red** until its implementers land: row 1 goes green at
**stage 5** (the worktree settle stamps the pin), row 2 at **stage 13** (the divergence gate). That is
expected and is why stages 3-12 run **filtered** `tests-pass` guardrails. Your own guardrail 02 asserts
exactly that: both rewritten methods must be observed **Failed** on today's tree. A rewrite that is green
here did not change the contract.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path - including production files, neighbouring
test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you
hit a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
