## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/09-implement-verifier-advisory`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/09-implement-verifier-advisory": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement **`VerifierAdvisory`** so the tests authored by `08-author-tests-verifier-advisory` pass.
**Do NOT edit those tests.** If they are genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path rather than changing them.

**`docs/plans/17-model-tiering.md` §6.5 is the design of record.**

Three properties carry the whole feature:

- **Detect weaker-than-actor and equal-and-weak; leave equal-and-strong alone.** "Weak" is `strength`
  when declared, else the provider-kind fallback (`kind != "claude"` implies weak-unless-declared,
  and that fallback is verifier-only). **Reuse whatever task 02 already computes** for the bump
  rather than deriving weakness a second way — two definitions of "weak" that can drift is the same
  class of bug D22a forbids for candidacy, and it would be invisible until the two disagreed.
- **Never halt.** No exception, no non-zero path, no refusal — attended or unattended. The advisory
  is information, not a gate.
- **The de-duplication rule.** Record into provenance **always**; emit a log line **only** when the
  observed pair differs from the preflight's prediction. The run summary aggregates from provenance,
  so nothing is lost by the quieter log.

Keep this a **pure, testable unit**: decide the condition and what each surface should say. Do not
reach into the executor, the journal or the console from here — the surfaces consume this, not the
other way round, and that separation is what makes the de-duplication rule testable at all.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/VerifierAdvisory.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the test file, `TierResolver.cs`,
`GuardrailRunner.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes
a retry.
