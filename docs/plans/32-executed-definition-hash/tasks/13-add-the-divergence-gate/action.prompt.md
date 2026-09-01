## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "13-add-the-divergence-gate": { "someKey": "someValue" } }`.
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

This task implements stage 13 of `docs/plans/32-executed-definition-hash.md`. **Read sections 6.2, 6.3,
6.4, 6.5 and 13 in full.** Where this prompt and the plan disagree, the plan is authoritative and you
should say so in your summary.

## Task - three pieces of one mechanism

### 1. The diff, at every successful settle (W1-W4)

Compare **two per-file maps over the same FILTERED surface**:

| | value | cost |
|---|---|---|
| before | `task.DefinitionFilesAtLoad` (stage 3) | free |
| after | the same per-file walk over `TaskDefinitionFiles.Enumerate`, at settle | one file walk |

**Never compare two aggregates**, and in particular never compare the full-surface `DefinitionHashAtLoad`
against a filtered recompute - those hash different file sets, so on a task carrying an editor artifact
they differ with nobody having edited anything.

The verdict is *"some label's hash moved, or a label appeared or vanished"* - which is also exactly the
breakdown §6.2 requires the gate to report, so the diff is not extra work done for the check, it **is** the
check.

**Apply the ignore predicate to BOTH sides before diffing.** Stage 5 promoted
`LivePlanEditWatch.IsEditorArtifact` to `internal static` for exactly this. **One predicate, one home.**

**Compute the settle-side per-file hashes exactly as stage 3 computed the load-side ones**, or the diff
reports every file as moved on every settle. `HashText` (`src/Guardrails.Core/Hashing/HashText.cs`,
`internal`) has **no per-file helper**: its surface is `AppendFile(builder, label, absolutePath)`, and
both `LivePlanEditWatch.TryHashFile` and stage 3's capture fold it into a fresh `StringBuilder` and
SHA-256 the result. Mirror that. **Do not add a helper to `HashText.cs`** - it is outside your
`writeScope`, and its framing feeds every recorded definition hash in every plan.

> **A DEVIATION FROM THE PLAN'S WORDING YOU NEED TO KNOW ABOUT.** §5.2 describes `DefinitionFilesAtLoad`
> as *"the FILTERED per-file map"*. It is captured **UNFILTERED**, deliberately: at stage 3 the predicate
> was still `private` (stage 5 is downstream of stage 3, because it needs stage 3's pin), so filtering
> there would have forced a **second copy** of the ignore list - the exact escape §15.2 says every pressure
> points at, and the one that silently un-decides §6.2. Filtering both sides here is equivalent (the filter
> is a pure function of the file name) and keeps the predicate in one place. **Do not inline a second copy
> of the list**; if `IsEditorArtifact` is not reachable, that is stage 5 not having landed - escalate with
> `needsHuman`.

### 2. The report record

`RunReport.ExecutedDefinitionDivergence` - a sibling of the existing `DefinitionDrift`, carrying **BOTH**
per-task hashes and the **moved-file list**. Names already taken in `RunReport.cs`, so pick around them:
`DefinitionDrift`, `HasDefinitionDrift`, `WaveHalt`, `HasWaveHalt`, `DeliveryPendingTerminalGate`,
`WhollyGreenButUndelivered`, `DriftedTask`, `ChangedDefinitionFile`.

On a non-empty diff the harness also:
- records `succeeded` **with the pin** - as milestone A does, unconditionally. §6.4: the settle is **never**
  refused. Refusing discards paid work (#554) **and** leaves the present-but-uncorroborated plan-branch
  commit Part C rule 3 refuses to rewind past, making the remediation strictly worse than the bug;
- records `definitionHashAtSettle` (stage 12's field) with the full-surface on-disk value, **driven by the
  GATE VERDICT, never by hash inequality**;
- appends **one** `decisions[]` entry - `boundary: "definition-divergence"`, `decision:` the shipped
  `DecisionTokens.Halted` - naming the task and which definition files moved, straight from the map diff.

**The run drains to completion.** No in-flight attempt is cancelled, no dispatch is stopped (§6.4): every
later task carries its own pin and its own check, so nothing after the divergence goes undetected, and
killing workers would discard paid work for no correctness gain.

### 3. The delivery gate - ONE added term, and no new delivery path

```csharp
// before
public bool AllSucceeded => !HasDefinitionDrift && !HasWaveHalt && !Aborted && Tasks.All(t => t.IsGreen);
// after
public bool AllSucceeded => !HasDefinitionDrift && !HasWaveHalt && !Aborted
                         && !HasExecutedDefinitionDivergence && Tasks.All(t => t.IsGreen);
```

That is the whole delivery change. §6.5: *"No new delivery path is introduced, which is what keeps the
blast radius of a delivery-gate change to one expression - and is the lesson of #457, where a SECOND gate
that ran after delivery was the defect."* All seven consumers inherit the term; §6.5 traces every one.

**The rendering is stage 15's**, not yours. `RunCommand.cs` is outside your `writeScope`.

## The wrong implementation this stage is most likely to ship

§15.4 names it: **comparing the FULL surface instead of the filtered one.** It is three lines, it passes
P9 through P15 and every other guardrail in this plan, and it blocks an overnight run's delivery on a
`.DS_Store`, a `Thumbs.db`, or a `.swp` left by an operator who opened a guardrail to **read** it - and the
gate samples at **every** settle, so a thirty-task run gives a stray file thirty chances. §6.2: *"A delivery
gate that does that is disabled within a week, and then the real signal is gone too."*

The shipped `AStrayDsStoreMidRun_...` assertion is the only thing that catches it, **this is the only stage
whose implementation can turn it red**, and §15.4 therefore puts it inside your guardrail's filter by name.
Its other four methods stay filtered out until stage 15.

**The RECORDED hash keeps the full unfiltered surface.** `HashText` is untouched, no hash moves, no
migration is owed (§5.5), and a stray artifact remains what it is today: a resume-time drift condition
§7.2 already owns.

## Do NOT

- Do NOT touch `HashText` or `TaskDefinitionFiles` (§11) - they are outside your `writeScope`.
- Do NOT refuse the settle, cancel in-flight work, or stop dispatch (§6.4, §12).
- Do NOT introduce a second delivery predicate (§6.5 / #457).
- Do NOT edit any test file.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/RunReport.cs` and
`src/Guardrails.Core/Execution/Scheduler.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside these paths - including `LivePlanEditWatch.cs`, `RunCommand.cs`,
`JournalModel.cs`, any test file, and the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that
file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
