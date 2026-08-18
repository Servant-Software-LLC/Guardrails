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

### What to add — the names below are PINNED

Your guardrail keys on these exact identifiers. Both are measured at **zero occurrences** across
`src/` and `tests/` today, so they can only appear as a result of your work. Add whatever else you
need; do not rename these.

| what | pinned name |
|---|---|
| the record describing a judge guardrail on a task spec | **`Stage2GuardrailSpec`** |
| the member on `Stage2TaskSpec` that carries it | **`JudgeGuardrail`** |

- A `Stage2TaskSpec` carrying a `JudgeGuardrail` makes the harness write a real
  `NN-<name>.prompt.md` into that task's `guardrails/` folder, with frontmatter (at minimum an
  optional `runner` and `tier`). A task without one keeps today's deterministic `01-ok` guardrail.
- The **invocation ledger must distinguish the judge call from the action call**. `Stage2RecordedCall`
  already declares `IsGuardrail` — wave 2 added it with the comment *"always false for the plans this
  harness emits … recorded so it stays true if a later wave adds a judge guardrail."* **That already works** — it is derived from the presence of `GUARDRAILS_ACTION_RESULT` in the
  invocation environment, which is true for a judge call by construction, so do not rebuild it and
  do not break it. What you must ADD is what a clause needs to assert on beyond the flag: which
  runner/block carried the judge call, and the model and effort it ran with.

### The verdict contract — get this wrong and EVERY judge guardrail fails

A prompt guardrail passes or fails **solely by its verdict file, never by the runner's exit code**. A
missing or invalid verdict is a FAIL with the reason `guardrail produced no valid verdict (see logs)`.
So a fake runner that returns a successful `PromptResult` and writes nothing produces a judge
guardrail that always fails, and every clause task 06 builds on it dies for a reason that has nothing
to do with routing.

The harness sets **`GUARDRAILS_VERDICT_OUT`** in the guardrail invocation's environment (a staged path
the runner promotes the instant the call returns). Your fake must read it from
`invocation.Environment` and write `{"pass": true, "reason": "..."}` there.

**There is prior art in this very project — read it before you write anything.**
`tests/Guardrails.Integration.Tests/FakeClaudePlanBuilder.cs` already emits a prompt guardrail
(`Path.Combine(taskDir, "guardrails", "01-verdict.prompt.md")`) and its fake already branches on
`GUARDRAILS_VERDICT_OUT` to write a verdict; `PromptOutputStagingTests.cs` does the same. Follow
their shape — the only difference is that `Stage2PlanHarness` fakes `IPromptRunner` in-process
rather than a CLI.

### Keep the existing path working

Every wave-2 clause runs on this harness and must keep passing — this is an extension, not a
replacement. Your guardrail RUNS the existing conformance suite and fails if any of it breaks, so a
restructure that "cleans up" the deterministic path is caught here rather than by task 06.

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
