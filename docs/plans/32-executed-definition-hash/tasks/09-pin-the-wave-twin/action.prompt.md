## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-pin-the-wave-twin": { "someKey": "someValue" } }`.
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

This task implements stage 9 of `docs/plans/32-executed-definition-hash.md`. **Read sections 5.4, 5.5 and
5.8's P7 in full.** Where this prompt and the plan disagree, the plan is authoritative and you should say
so in your summary.

## Task - three changes across four files, and one of them is a NEW SIBLING, not an edit

1. **`WaveNode.cs`** gains `public string? DefinitionHashAtLoad { get; init; }` - the wave's own
   load-time capture, over its `guardrails/**`, `preflights/**` and optional `brief.md`. Same shape rules
   as stage 3's task captures: **bodiless auto-property, nullable, NOT `required`, never `Lazy<>`, never
   an expression body, never a `??` fallback.** Stage 6's committed anchor test asserts that `WaveNode.cs`
   contains **zero** occurrences of `WaveDefinitionHash` in code, so this type must not name the hasher at
   all - the loader computes the value and hands it in.
2. **`PlanLoader.cs`** populates it **eagerly**, at the single `new WaveNode` expression, exactly as it
   already does for `TaskNode` (stage 3).
3. **`WaveDefinitionHash.cs`** gains a **pinned fold BESIDE** the existing `Compute(WaveNode)`, which is
   **unchanged**. Section 5.4: *"`WaveDefinitionHash.Compute(wave)` - unchanged, current disk, for every
   READ. A pinned form for the single WRITE."* The pinned form folds each constituent task's
   `DefinitionHashAtLoad` plus `WaveNode.DefinitionHashAtLoad`.
4. **`Scheduler.cs`** - write site **W5**, the wave-completion stamp (the journal wave entry **and** the
   `Guardrails-Wave:` marker commit). It calls the pinned form instead of `WaveDefinitionHash.Compute(wave)`.
   **Every other `WaveDefinitionHash.Compute` call site in this file is a READ and stays** - the wave-drift
   compare, the JIT checkpoint's escalation record, the review-gate escalation record, and the wave-proceed
   answer key. There are eight `WaveDefinitionHash.Compute` call sites in `src/**` across three files;
   exactly ONE of them changes.

## THE REQUIREMENT SECTION 15 DOES NOT STATE, AND IT GATES SIX SHIPPED TESTS

Section 15 row 9 changes the wave-completion **WRITE** only. The wave-drift **COMPARE** is a READ and is
deliberately left alone - so on the next resume the harness compares a **pinned** stamped value against a
**disk** recompute.

> **On an unedited tree those two MUST be BYTE-IDENTICAL.**

If they are not, every completed wave reads as **drifted** on the very next resume, and under the default
policy that is an unauthorized wave-drift **halt**. Six shipped tests gate it - five in
`SchedulerWaveExecutionTests` and one in the Integration project - and guardrail 02 runs
`SchedulerWaveExecutionTests` for exactly this reason.

**So reproduce the disk fold's framing exactly**: the per-task entries in **wave-relative task-id order**,
then the wave `guardrails/**`, then `preflights/**`, then `brief.md`, with the same labels and the same
separators. Read `WaveDefinitionHash.Compute` and mirror it; do not invent a new framing. This is the wave
level of section 5.5's no-op property, and the whole reason this plan owes no migration wave.

> **The one place that instruction and the pinned-fold instruction pull apart - resolve it THIS way.**
> The disk form inlines the wave-gate file **BODIES** (it appends each gate file's content into the
> builder). `WaveNode.DefinitionHashAtLoad` is a **SHA of** those bodies. Folding a hash where the disk
> form folds bodies produces a **different digest**, so "fold the wave's capture" and "reproduce the disk
> framing exactly" cannot both be satisfied literally - and getting it wrong makes every completed wave
> read as drifted on the next resume.
>
> **Resolve it by capturing the fold TEXT, not a digest of it**: have `WaveNode.DefinitionHashAtLoad`
> hold the wave-gate portion of the builder's input verbatim (or capture the whole wave hash at
> `WaveNode` construction and stamp that), so the pinned fold appends the same bytes the disk form
> appends. Either shape works; what does not work is folding a SHA into a position the disk form fills
> with bodies. Guardrail 02 runs the six shipped resume tests, so this fails loud rather than silently -
> but it fails after two full `dotnet test` runs, on the most turn-expensive task in the plan, so decide
> it before you write the fold rather than after.

**Beware a false reassurance:** the two wave-drift **positive** tests (the ones that assert a drift IS
reported) would still pass if the fold were wrong, because any mismatch reads as drift. A green on those
is not evidence.

## Why the wave gate folders are pinned too, even though the gate scripts are re-read at execution

Section 5.4 answers it: *"for the same reason section 5.6 gives for the action file - a mid-run edit makes
any single recorded hash a lie, and the design choice is only which lie fails loud. Pinning fails loud."*

## Do NOT

- Do NOT change `WaveDefinitionHash.Compute(WaveNode)`'s signature or behaviour. **Eight call sites bind
  to it**, across `Scheduler.cs`, `ReviewMarker.cs` and `RunCommand.cs` - and `RunCommand` is a different
  assembly. Add a sibling; do not repurpose the original.
- Do NOT pin any wave READ site. Section 5.5: a **wave** review marker keys on `WaveDefinitionHash`
  (`ReviewMarker`), and it is untouched *for reads* - which is what keeps every existing marker valid.
- Do NOT touch `HashText` or `TaskDefinitionFiles` (section 11). Call them.
- Do NOT edit any test file. `WaveExecutedDefinitionHashTests`, `WaveDefinitionHashTests`,
  `SchedulerWaveExecutionTests` and `ExecutedDefinitionHashAnchorTests` are all outside your `writeScope`;
  if one looks wrong, say so with `needsHuman`.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Model/WaveNode.cs`,
`src/Guardrails.Core/Journal/WaveDefinitionHash.cs`, `src/Guardrails.Core/Loading/PlanLoader.cs` and
`src/Guardrails.Core/Execution/Scheduler.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside these paths - including `ReviewMarker.cs`, `RunCommand.cs`, any test
file, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit
a compile error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
