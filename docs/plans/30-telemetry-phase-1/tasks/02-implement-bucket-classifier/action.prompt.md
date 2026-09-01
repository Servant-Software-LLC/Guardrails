## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-implement-bucket-classifier`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-bucket-classifier": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the second half of section 3.2 of `docs/plans/30-telemetry-phase-1.md`. **Read
section 3.2's table in full** — it carries the six rules and their measured distribution over 316
tasks across 18 plan folders. Where this prompt and the plan disagree, the plan is authoritative and
you should say so in your summary.

## Task

Fill real logic over the throwing stub in `src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs` so
that `Classify(writeScope, guardrails)` returns the bucket the plan's table specifies.

The rules, restated from section 3.2 — the plan is authoritative:

| bucket | rule |
|---|---|
| `no-write` | `writeScope` is EMPTY (`[]`, the deliberate "writes nothing" declaration) |
| `test-authoring` | writes `tests/**` only, **and** carries a TDD-red guardrail (a guardrail whose name marks it as `tests-fail-on-stubs` or `tests-fail-on-current-code`) |
| `implementation` | writes `src/**` only, gated by a `tests-pass` guardrail |
| `structural` | writes `src/**` or `tests/**` with **no** behavioural gate — stubs, anchors, record additions, renames |
| `code+tests` | writes **both** `src/**` and `tests/**` |
| `documentation` | writes `docs/**` / `.claude/**` only |

Three things the table does not spell out and the authored tests do:

1. **`writeScope` NULL is not `writeScope` empty.** `TaskNode.WriteScope` is documented as null =
   the off-switch (see `src/Guardrails.Core/Model/TaskNode.cs`), which is a different claim from the
   declared `[]`. Null returns `null`; `[]` returns `no-write`.
2. **The write surface decides `code+tests`, and the guardrail shape does not override it.** A task
   writing both `src/**` and `tests/**` is `code+tests` even when it also carries a TDD-red
   guardrail. Section 3.2 calls this out: 67 of the 74 multi-root tasks measured were exactly this
   shape, which is why it is a named bucket rather than an "other".
3. **A write surface no rule matches returns `null`**, which the corpus reader already renders as
   `(unbucketed)`. Do not invent a seventh sentinel and do not force an unmatched surface into the
   nearest bucket — the six rules together did not cover all 316 measured tasks, and a guessed bucket
   is worse than an honest absence.

Read the guardrail archetype off `GuardrailDefinition.Name` (the basename without extension — see
`src/Guardrails.Core/Model/GuardrailDefinition.cs`). Reading a guardrail's NAME is fine; reading the
TASK's name is what the report's legend forbids, and the signature already makes that impossible.

**Do NOT change `Classify`'s signature.** It takes exactly `IReadOnlyList<string>? writeScope` and
`IReadOnlyList<GuardrailDefinition> guardrails` and returns `string?`. One of the authored tests reads
that signature by reflection and will go red if you widen it — including if you "helpfully" add a
`TaskNode` overload that admits the task's identity.

**Do NOT edit the authored tests.** Make them pass by fixing the implementation. If a test is
genuinely wrong or incompatible with the plan's rules, emit
`{"needsHuman": "<why>"}` to the state-out path rather than changing it — an out-of-scope edit to
`tests/Guardrails.Core.Tests/Telemetry/TaskFingerprintBucketTests.cs` fails the write-scope check and
burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Telemetry/TaskFingerprintBucket.cs`. After this task completes, the harness runs
a `git diff` check and rejects any edit outside that path — including changes to other production
files, the authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
