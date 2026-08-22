## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. This plan is WAVED, so the key is the WAVE-QUALIFIED id:
  `{ "wave-01-config-net/03-implement-registry-warnings": { "someKey": "someValue" } }`.
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

Make `TieringRegistryWarningTests` pass by emitting **GR2051** and **GR2052** from
`src/Guardrails.Core/Loading/PlanValidator.cs`. Do not modify the tests.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/PlanValidator.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the test file, `DiagnosticCodes.cs`,
and any other production file. An out-of-scope edit fails the task immediately and consumes a retry.
**If the authored tests are genuinely wrong or incompatible, emit
`{"needsHuman": "<why>"}` rather than changing them.**

### What to emit

**GR2051 `NonRoutableBlockIsDefault`** — warning. In a **tiering-configured** file, the registry
`default` pointer names a block that is `costly: true` **or** declares no `routing` at all. The
consequence to state in the message: an untagged task with no `defaultTier` falls to legacy resolution,
lands on the reserved model, and the reservation evaporates through the back door (DoR §4.2).

**GR2052 `CostlyBlockRoutingInert`** — warning. A `costly: true` block **also** declares `routing`.
The routing can never apply, because the candidacy predicate excludes costly blocks first (§6.2).
It is a warning rather than an error precisely so that **GR2048 can still report the real
consequence** — the two must compose, and one of the authored tests asserts exactly that.

### Three things that decide whether this is correct

1. **Reuse the shipped candidacy predicate — never re-implement it.** `PromptRunnerConfig.ServesTier`
   and its costly-ignoring twin `DeclaresTier` already encode "what counts as a candidate". A second
   copy of that logic here is how the two drift apart. Read them before you write anything.
2. **Both are WARNINGS, and neither may fail a build.** DoR §12.6 is explicit: the plan still runs.
   Use whatever helper the surrounding validator code already uses to add a warning — match the
   neighbouring GR2047–GR2050 implementations rather than inventing a shape.
3. **Invariant 7 is load-bearing.** Neither warning may fire on a file that does not configure
   tiering — no `routing` on any block and no `tiering` block. A single-model user who never asked
   for tiering must see byte-identical output. One of the authored tests pins this.

### Where it goes

The three tier-bearing GR2043 sites and `ValidateTieringInert` are already in this file — read them
first and follow the existing structure. Do NOT restructure the validator to accommodate the new
checks; add them the way the existing ones are added.
