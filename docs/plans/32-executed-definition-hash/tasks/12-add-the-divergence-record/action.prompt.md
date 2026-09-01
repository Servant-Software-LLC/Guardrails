## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "12-add-the-divergence-record": { "someKey": "someValue" } }`.
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

This task implements stage 12 of `docs/plans/32-executed-definition-hash.md`. **Read section 6.3 in full**,
plus the blockquote in section 15 that explains why `RunJournal.cs` is in this row. Where this prompt and
the plan disagree, the plan is authoritative and you should say so in your summary.

This is **pure data shape**. It adds the place the divergence gate (stage 13) will write to; it writes
nothing itself.

## Task

1. **`JournalModel.cs`** - `TaskJournalEntry` gains an **optional** `DefinitionHashAtSettle`:

   ```csharp
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? DefinitionHashAtSettle { get; init; }
   ```

   Put it beside the existing `DefinitionHash` (around `JournalModel.cs:374`) and **copy that member's
   attribute exactly**. **Note the type is `TaskJournalEntry`, not `TaskEntry`** - the plan's section 15
   row calls it `TaskEntry`, which is not a type in this repo.

2. **`RunJournal.cs`** - thread it through the three recorders as a **second optional parameter**:
   `RecordAttempt`, `RecordSettle`, `RecordSettleWithAttempt`. All three already carry
   `string? definitionHash = null` and the preserve-on-null idiom
   `DefinitionHash = definitionHash ?? entry.DefinitionHash`; mirror it. **The new parameter MUST be
   optional with a default and MUST go after the existing optional ones**, or every existing call site
   breaks - several of them in `Guardrails.Cli`, a different assembly.

3. **`DecisionEntry.cs`** - the `boundary` token `"definition-divergence"`, declared once as a constant
   beside the existing `PlanEditDecisions.Boundary`, following that class's shape. The `decision` token is
   the **shipped** `DecisionTokens.Halted` (`"halted"`) - do **not** add a new decision token.

### Why `RunJournal.cs` is in this row, spelled out because the plan spells it out

Section 15's blockquote: this row was originally written without it, and tracing where
`definitionHashAtSettle` is actually **written** found the three recorders *"in a file no row's
`writeScope` reached - a task told to deliver a field it could not persist, which is precisely the shape
that cost plan 28 $3.84 and blocked 21 of 31 tasks (#553)."* Neither `GR2068` nor `GR2069` would have
caught it, because the broken cell named no unreachable path - it simply failed to name a needed one.

### The presence rule, and an earlier draft got it wrong three ways in three sections

> **`definitionHashAtSettle`'s presence is driven by the GATE VERDICT, never by hash inequality.**

Keyed on inequality, a stray `.DS_Store` writes the field on a green, delivering run - and §6.6's
drift-accept refusal then keys off it and fires for ordinary artifact drift, which §12 puts explicitly out
of scope. **Gate fired ⇒ field present. Gate silent ⇒ field absent**, and an unedited run's `run.json` is
byte-identical to today's.

You cannot enforce that from here - the gate is stage 13's - but you **can** make the wrong version
impossible to write cheaply: the parameter is optional and defaults to null, and the `JsonIgnore` means a
null is omitted. Guardrail 02 runs stage 10's **P10** silence pin, which is green today and must stay
green: it asserts an unedited run's `run.json` gains **no** new key at all.

## Do NOT

- Do NOT change the existing `DefinitionHash` property, its attribute, or the preserve-on-null idiom.
  Moving a recorded hash owes the migration wave this whole plan is designed to avoid (§5.5).
- Do NOT add a new `decision` token. §6.3 uses the shipped `DecisionTokens.Halted`.
- Do NOT write the field from anywhere. This stage adds the shape; stage 13 is its only writer.
- Do NOT edit any test file.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Journal/JournalModel.cs`,
`src/Guardrails.Core/Journal/RunJournal.cs` and `src/Guardrails.Core/Execution/DecisionEntry.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside these paths -
including `Scheduler.cs`, `RunReport.cs`, any test file, and the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
