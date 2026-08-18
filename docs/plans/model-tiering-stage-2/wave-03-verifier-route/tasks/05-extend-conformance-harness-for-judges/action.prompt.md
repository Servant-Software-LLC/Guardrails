## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/05-extend-conformance-harness-for-judges`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/05-extend-conformance-harness-for-judges": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Give **`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`** the one capability
wave 3's conformance clauses need and it does not have: **a plan spec can declare a prompt-JUDGE
guardrail, and the invocation ledger captures the judge's call.**

**Read the harness first.** Today it writes, per task, *"a trivially-passing deterministic
guardrail"* — an `01-ok.cmd` / `01-ok.sh` containing `exit 0`. There is no way to express a
`.prompt.md` guardrail at all, so **every** wave-3 clause about how a judge resolves is currently
unwritable. That is why this is its own task rather than a footnote in the one that authors the
clauses.

### What to add

- A way for a `Stage2PlanSpec` to declare that a task carries a **prompt-judge guardrail** — the
  harness writes a real `NN-<name>.prompt.md` into the task's `guardrails/` folder, with frontmatter
  (at minimum an optional `runner` and, once wave 3 lands it, `tier`).
- The **invocation ledger must distinguish a judge call from the action call** and expose what a
  clause needs to assert on: which runner/block carried it, the model and effort it ran with, and
  the guardrail it belongs to. Task 06's clauses observe the judge route through this ledger and
  through the journal — they cannot see anything you do not surface.
- Keep the existing deterministic-guardrail path working. Every wave-2 clause runs on this harness
  and must keep passing; this is an extension, not a replacement.

Follow the harness's existing shape — how it builds `guardrails.json` as a real JSON document, how
it materialises a task folder, how it records invocations. Do not introduce a parallel mechanism
beside one that already works.

### The prohibition that makes this harness worth having

**The harness must NEVER reference `TierResolver` or `TierResolution`.** This is not style; it is the
whole value of the suite built on top of it. A harness that asks the resolver what it *would* have
chosen produces clauses that PASS against a completely unwired `GuardrailRunner` — they would prove
the resolver, which waves 1–2 already proved, and say nothing about whether anything *calls* it.
That is the #382 "green light over a broken wire", and it is the cheapest possible way to make five
red clauses go green.

Observe the route through the **journal** and the **captured invocation**. Never by consulting the
resolver. Your guardrail enforces this with a fail-on-present check, and wave 2's harness carries the
identical prohibition — you are extending a file that is already bound by it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside that path — including
`Stage2ConformanceTests.cs` (task 06 owns the clauses), anything under `src/`, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do not author conformance clauses here.** Task 06 owns `Stage2ConformanceTests.cs`. Your
deliverable is the capability; theirs is the proof built on it.
