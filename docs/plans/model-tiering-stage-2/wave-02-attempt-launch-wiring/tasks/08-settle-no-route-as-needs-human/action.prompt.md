## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/08-settle-no-route-as-needs-human`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/08-settle-no-route-as-needs-human": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Task 07 wired `TierResolver.Resolve` into the attempt-launch path and left the `NoRoute` branch
explicitly unhandled with a `// task 08 settles NoRoute` marker (grep for it; **do not rely on a line
number** — 07 edited this file before you, so any line reference is stale by construction, and treat
07's structure as authoring-time state you should verify).

Make this `Stage2ConformanceTests` clause pass:

- `NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman`

**Do NOT edit `Stage2ConformanceTests.cs` or `Stage2PlanHarness.cs`.** If the clause is genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing it.

**`docs/plans/17-model-tiering.md` §6.2 and §12.4 are the design of record and win over any
paraphrase here.**

### What `no-route` is, and what it is NOT

`TierResolution.NoRoute` means **no candidate block exists at the requested rung or at any stronger
one**. It is a runtime config gap that `guardrails validate`'s GR2048 normally catches — the same
relationship a missing-CLI failure has to GR2009's PATH probe.

**D30 draws the line and it is the thing most likely to be implemented backwards:** *legacy is the
no-RUNG path, `no-route` is the no-CANDIDATE path, and nothing is both.* Once an effective tier
exists, resolution owns the outcome. So a `NoRoute` result must **never** be turned into a launch on
`promptRunners.<name>.model`, and must never be turned into a launch on the `costly: true` block that
was excluded — the costly floor constrains what the HARNESS may choose, and only a human's pin may
cross it.

### What to build

1. **Settle BEFORE launching.** In `TaskExecutor`, when the attempt's resolution has `NoRoute`, the
   task settles **needs-human** without ever invoking the prompt runner. The clause asserts the fake
   runner was never invoked for that task at all — a no-route discovered after an attempt ran on some
   fallback is not a no-route, it is a silent fallback wearing its name.
2. **Journal the distinct outcome.** The attempt record's `outcome` is the **`no-route`** token
   (`AttemptOutcome.NoRoute`, landed by task 02 with its wire mapping). Add the matching factory to
   `AttemptJournaler` beside its siblings (`Cancelled`, the needs-human path, …) rather than
   synthesising a record inline — the journaler is where the attempt-record shape is owned, and an
   inline copy is how the two drift.
   Record the attempt's `provenance` as usual: `tierSource` and the **requested** rung still apply
   (that is how a reader learns WHICH rung could not be served), while `provenance.tier` — the rung
   actually SERVED — is absent, because none was.
3. **No retry burn.** Retrying resolves identically: v1 resolution is a pure function of the tag and
   the registry, and neither changes between attempts of the same run. Follow the shape the existing
   `NeedsHuman` outcome already uses — straight to needs-human, no retry, no guardrails.
4. **An ACTIONABLE message.** §12.4 pins the shape: name the rung and tell the operator what to do —
   *"register a provider serving tier ≥ `<rung>`"*. Distinguish the two causes GR2048 already
   distinguishes, because they have different fixes:
   - **nothing DECLARES the rung** ⇒ the operator needs a new or widened `routing.tiers`;
   - **the only blocks declaring it are `costly: true`** ⇒ the operator needs a pin, or to clear the
     flag. The resolution's `CostlyCeilingBound` / `CostlyCeilingBlocks` already tell you which case
     you are in and which blocks to name — **read them, do not re-test `Costly`** (that would
     duplicate the candidacy predicate D22a forbids duplicating and would trip wave 1's guardrails).
   Mention that `guardrails validate` (GR2048) reports this statically, so the operator knows where
   to catch it next time.

### Do not disturb the six clauses task 07 made green

Your guardrail runs the whole set task 07 owns as well as your own — all three of you edit
`TaskExecutor.cs` in a chain, so a regression here would otherwise surface only at the wave exit
gate, which is a gate and not a task: no retry budget, and the failure attributed to the wave rather
than to the change that caused it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/TaskExecutor.cs` and
`src/Guardrails.Core/Execution/AttemptJournaler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including the journal model and its wire
mapping (task 02 owns them; `AttemptOutcome.NoRoute` and its `"no-route"` token already exist), the
conformance suite, `TierResolver.cs`, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
