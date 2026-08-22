## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in. This plan is WAVED, so the
  key is the WAVE-QUALIFIED id, not the bare folder name:
  `{ "wave-01-config-net/01-allocate-diagnostic-codes": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
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

Allocate three diagnostic codes in `src/Guardrails.Core/Loading/DiagnosticCodes.cs`. This task
writes **only** that file.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path. An out-of-scope edit fails the task
immediately and consumes a retry. In particular do **not** edit `PlanValidator.cs` — emitting these
codes is tasks 03 and 05, and their `writeScope` owns that file.

**Why this task exists at all, and why it owns the file alone.** Stage 1 of this epic had two
same-tier tasks each allocate a diagnostic code and each edit the "CURRENT next-free code" marker.
The agents handled it *well* — one skipped a number with a comment explaining the concurrent
allocation — and the **merge still could not combine them**, costing a run abort and a corrupted
SSOT. There is no mechanism for that negotiation, so the codes are pre-named here and one task owns
the file.

### The three codes — take these exact numbers

They are already **reserved by name** in this file's own next-free marker block. Do not renumber
them, and do not take `GR2065`: these three are gaps *below* the next-free line, deliberately held
open for this epic by `docs/plans/17-model-tiering.md` §13.2.

| Constant | Value | Severity | Meaning (DoR §4.2 / §12.6) |
|---|---|---|---|
| `NonRoutableBlockIsDefault` | `GR2051` | warning | a `costly: true` **or** `routing`-less block is named `default` in a tiering-configured file — untagged work then falls to legacy resolution and lands on the reserved model, so the reservation evaporates through the back door |
| `CostlyBlockRoutingInert` | `GR2052` | warning | a `costly: true` block also declares `routing` — the routing can never apply, because the candidacy predicate excludes costly blocks first (§6.2) |
| `PinAndTierCoexist` | `GR2053` | warning | a full pin (`action.runner`/`action.model`) and `action.tier` are both set on one action — the tier is dead weight the pin overrides (§6.1, DA F3) |

All three are **warnings**, never errors. DoR §12.6 is explicit that the plan still runs.

### What to write

1. **Three `public const string` declarations**, each with an XML doc comment in the style of the
   surrounding constants in this file: state what it catches, why it is a warning rather than an
   error, and cite the DoR section. Follow the file's existing conventions — read the neighbouring
   GR2047–GR2050 block first and match it.
2. **Retire the three codes from EVERY reservation statement in the file — there are TWO, not one.**
   - **`~line 848`** — "GR2051–GR2054 also remain RESERVED by name in docs/plans/17-model-tiering.md §13.2".
   - **`~line 573`** — "Deliberately NOT taken by this slice (still reserved in §13.2, still free):
     GR2051 (NonRoutableBlockIsDefault), GR2052 …, GR2053 …". This one is easy to miss because it
     reads as historical narration about Stage 1, but it is a live claim and the next allocator will
     believe it. Grep the whole file for `GR2051` before you finish; both sites must be handled.

   **GR2054 `RoutingNumericNonPositive` must still be named as reserved** — it is the v2 probes code
   (#227) and nothing in this plan takes it.

   **The exact wording rule, because a machine has to check it.** Do not write GR2051, GR2052 or
   GR2053 on the same line as the word **"reserved"** or **"free"** — not even in a past-tense note.
   Say **TAKEN** or **ALLOCATED** instead ("GR2051–GR2053 were ALLOCATED by Stage 3"). Keep any line
   that still reserves GR2054 **on a line of its own**. This is not stylistic: the guardrail cannot
   tell a historical note from a live claim, so the rule is stated in terms it can check. Two earlier
   drafts of that guardrail tried to infer tense and both rejected correct work.
3. **Leave the `CURRENT next-free code` line alone.** It reads `GR2065` and stays `GR2065` — these
   three were gaps below it, so allocating them does not advance the counter. Advancing it would be
   a silent renumbering of somebody else's next code.

### What NOT to do

- Do **not** invent a fourth code, and do **not** take `GR2054` or `GR2060`/`GR2061` (all three are
  reserved by name for other work in that same block).
- Do **not** write any validation logic. This task allocates identifiers; tasks 03 and 05 emit them.
- Do **not** reuse a literal that already appears in this file. Two constants sharing a value
  compiles cleanly and is exactly the kind of defect a build cannot see.
