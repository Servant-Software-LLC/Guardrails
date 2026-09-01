## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `08-capture-the-model-digest-from-the-wire`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "08-capture-the-model-digest-from-the-wire": { "someKey": "someValue" } }`.
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

This task implements the capture half of section 3.3 of `docs/plans/30-telemetry-phase-1.md`. Read
section 3.3's `DECIDED 2026-09-01` block: the maintainer put both the schema field and the capture in
Phase 1, and the work "must not drift back toward field-only". Where this prompt and the plan disagree,
the plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

## Task

Make `07-author-tests-model-digest-capture`'s `ModelDigestCaptureTests` pass by reading the wire's
`system_fingerprint` in `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` and carrying it onto
`PromptResult.ModelDigest` (declared by task 04; nothing populates it yet).

### The two read sites

Both are private static folds in that file, and both already lift the model tag with
`observedModel ??= ReadString(…, "model")`:

- **`ApplyChunk`** — one `chat.completion.chunk` from the SSE stream.
- **`ApplyWholeCompletion`** — a whole `chat.completion` body, the fallback for a server that ignores
  `"stream": true`.

`system_fingerprint` is a sibling of `model` at the same object level in both shapes, so it is read the
same way, at the same place, with the same `??=` first-wins discipline. Grep for the method names; do
not navigate by line number.

The value then rides out the same route the model already takes: the accumulated turn (`StreamedTurn`,
returned by `ReadStreamedTurnAsync`) into the per-turn fold in the turn loop — grep for
`observedModel ??= turn.ObservedModel` — and finally onto the `PromptResult` the runner returns. Follow
that member the whole way; the tests assert on `PromptResult`, and a value captured in `ApplyChunk`
that never reaches the result is exactly the structurally-dead shape `AttemptRecord.Usage` shipped as
once already (#475).

**Carry it on the failure path too.** The turn loop's failure branch already restates
`ObservedModel = failure.ObservedModel ?? observedModel` when it returns early; an attempt that learned
what served it must not lose that the moment it goes wrong. The digest is the same kind of fact.

### Absent stays absent

A response with no `system_fingerprint` leaves `ModelDigest` **null**. Never `""`, never the model tag,
never a hash the harness computes for itself. One of the authored tests asserts exactly this and is
green before you start — its job is to STAY green through your edit, and a fabricated placeholder is
the failure it exists to catch.

### Do NOT widen the protocol

Do **not** add an engine-specific out-of-band call to obtain a digest — no Ollama `/api/show`, no
`/v1/models` probe, no second request of any kind. The class's own design rule (in the type's
doc-comment, grep for `operator-facing TEXT ONLY`) is that `engine` selects one sentence and nothing
else: *a plan configured for MLX and one configured for Ollama emit BYTE-IDENTICAL requests for the
same model, wire and prompt*. Adding a probe would make the engine name a code path and break that
guarantee.

Widening the protocol is a decision this plan did not make. If you conclude the digest cannot be
obtained from the response body for some engine, that is a **finding to surface, not a scope to take**:
say so in your summary, and the maintainer decides. An engine that volunteers no `system_fingerprint`
correctly yields a null digest — the plan's own §3.3 records that a Claude row's digest is permanently
null for the same reason, and an honest absence is the designed outcome.

### Do NOT edit the authored tests

`tests/Guardrails.Core.Tests/Prompts/ModelDigestCaptureTests.cs` is outside this task's writeScope.
Make the tests pass by fixing the runner. If a test is genuinely wrong or incompatible with the plan,
emit `{"needsHuman": "<why>"}` to the state-out path rather than changing it — an out-of-scope edit
fails the write-scope check and burns a retry.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs`. After this task completes, the harness runs
a `git diff` check and rejects any edit outside that path — including changes to other production
files, the authored test file, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry.
