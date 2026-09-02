## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `08-advance-the-code-ladder`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "08-advance-the-code-ladder": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Advance the diagnostic-code ladder now that GR2060 is allocated. Two files, four edits, all mechanical —
but one of them has a documented trap.

**A. `src/Guardrails.Core/Loading/DiagnosticCodes.cs` — the reservation block.**

The block lists three codes reserved by name. After this task it lists three again, with different
membership:

1. **REMOVE** the `GR2060 — docs/plans/19-producer-coverage.md §1 …` line. GR2060 is now a shipped
   constant above it (task 4 added it).
2. **ADD**, in the same idiom as its neighbours:

   > `GR2070 — docs/plans/33-unproducible-requirements.md §6.3 (a guardrail requiring a named argument`
   > `whose declaring member no task may widen). DESIGNED AND DECLINED: it has never fired on a real`
   > `defect at any commit in this repository — see §3.4. Do not allocate without a positive control.`

   **The reason-line matters more than the reservation.** A bare *"reserved"* invites the next author to
   spend the code; a line saying *the design exists and the evidence did not* sends them to §6.3, where
   the three durable findings are.
3. **ADVANCE** the `CURRENT next-free code` marker to **GR2071**.
4. Leave **GR2061** and **GR2054** reserved, unchanged.

**THE TRAP — read this before you grep.** There are two next-free markers in that file and only one is
live. The marker near **line 1026** is current. The one near **line 565** is a **quoted historical**
marker naming GR2047; it is a record of what was true then, not an instruction. Editing it would corrupt
a historical note, and reading it as authoritative has already misled both a human and a guardrail on
this codebase. Grep for both, confirm which is which, and change only the live one.

**B. `docs/plans/02-schemas-and-contracts.md` — §14.10's GR-code paragraph.**

- Record **GR2060** (`UnproducibleGateRequirement`) as **shipped**, and remove it from the
  reserved-by-name list there.
- Add **GR2070** to that reserved-by-name list.
- Advance next-free to **GR2071**.
- Leave GR2061 and GR2054 unchanged.

Per that paragraph's own standing instruction, **`DiagnosticCodes.cs` wins** — re-verify against the
code immediately before you write the doc, not from memory.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Loading/DiagnosticCodes.cs`
and `docs/plans/02-schemas-and-contracts.md`. Within the SSOT, touch **only §14.10's code paragraph** —
§4.8 is task 7's deliverable and is already written. Do NOT add a `GR2070` constant: it is held by name,
not allocated, and a guardrail on this task fails if a constant takes that value.

## Done when

- The reservation block lists GR2070 (with its reason line), GR2061 and GR2054 — and no longer GR2060.
- The live next-free marker reads GR2071; the historical marker at ~:565 is untouched.
- §14.10 agrees with the code, and no constant has the value `GR2070`.
