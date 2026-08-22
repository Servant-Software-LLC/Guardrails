## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. This plan is WAVED, so the key is the WAVE-QUALIFIED id:
  `{ "wave-01-config-net/05-implement-pin-and-tier-coexist": { "someKey": "someValue" } }`.
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

Make `PinAndTierCoexistTests` pass by emitting **GR2053 `PinAndTierCoexist`** from
`src/Guardrails.Core/Loading/PlanValidator.cs`. Do not modify the tests.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/PlanValidator.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path. An out-of-scope edit fails the task
immediately and consumes a retry. **If the authored tests are genuinely wrong or incompatible, emit
`{"needsHuman": "<why>"}` rather than changing them.**

### You are the SECOND task to edit this file — read task 03's change first

Task 03 added GR2051 and GR2052 to this same file, and its work is already on your base. **Run
`git log -1 --stat` and `git diff HEAD~1 -- src/Guardrails.Core/Loading/PlanValidator.cs` before you
start**, and add your check the way task 03 added its two — same helper, same placement convention,
same message style. This wave was deliberately sequenced so the two of you never edit this file
concurrently: a union conflict in two or more files can never be AI-resolved (#458), and it cost this
epic's Stage 1 a whole run. Do not undo, restructure, or "tidy" task 03's code.

### What to emit

**GR2053 `PinAndTierCoexist`** — warning. An action carries **both** a pin and `action.tier`. The
tier is dead weight the pin overrides (DoR §6.1, DA F3), so the author almost certainly believes they
tiered a task they actually pinned.

**A pin is `action.runner` OR `action.model` — either one alone.** This is not a reading of the DoR's
slash; it is what the resolver does. `src/Guardrails.Core/Prompts/TierResolver.cs` line 139:

```csharp
if (action.Runner is not null || action.Model is not null)
```

That branch sits **above** the tier read and returns immediately, so a `model`-only pin kills the
tier just as completely as a `runner` pin. One of the authored tests
(`WarnsWhenModelPinAndTierCoexist`) exists specifically to fail if you implement the `&&` reading.
**Confirm that line still says `||` before relying on it**; if the resolver has changed, halt with
`needsHuman` rather than making the validator disagree with the resolver.

### Constraints

1. **Warning, never an error.** DoR §12.6: the plan still runs. The pin is legitimate — the tier is
   merely inert beside it.
2. **Do not duplicate the resolver's predicate.** If a shared helper for "does this action carry a
   pin" already exists, use it. If you must express it here, keep it to the one condition above so a
   later reader can see it matches `TierResolver`, and say so in a comment.
3. **Invariant 7 still holds.** This one is about an action, not the registry, so it can fire on a
   file with no `routing` block — but only when `action.tier` is actually set, and a plan that never
   tiers anything never sets it. Do not add a tiering-configured precondition it does not need; do
   not fire on a bare pin.
