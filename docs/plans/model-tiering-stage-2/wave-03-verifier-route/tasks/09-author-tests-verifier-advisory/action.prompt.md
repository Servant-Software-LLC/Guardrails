## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/08-author-tests-verifier-advisory`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/08-author-tests-verifier-advisory": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the **failing tests** for the DoR §6.5 **verifier advisory**, plus the stub they compile
against.

- **`tests/Guardrails.Core.Tests/ModelTiering/VerifierAdvisoryTests.cs`**
- class **`VerifierAdvisoryTests`**, decorated `[Trait("Category", "TierResolution")]` at class level
- Stub: **`src/Guardrails.Core/Prompts/VerifierAdvisory.cs`** — entry points throwing
  `NotImplementedException`, so a non-zero `dotnet test` unambiguously means the tests ran and FAILED
  rather than failed to compile.

**`docs/plans/17-model-tiering.md` §6.5 is the design of record** and wins over this summary.

### The behaviours the tests MUST encode

1. **The condition.** A judge **weaker than** its actor, or **equal-and-weak**, is an advisory
   condition. Assert that **equal-and-STRONG is NOT** — Opus judging Opus is a real check, and
   flagging it would train people to ignore the advisory entirely.
2. **It NEVER halts.** Advisory means advisory: no error, no load-time refusal, no halt — **in
   attended or unattended mode**. Assert the run-proceeds outcome explicitly. This is the property a
   later reader is most likely to "improve" into a gate; the harness does not block on a
   model-quality opinion.
3. **The de-duplication rule — the reason three surfaces are tolerable.** Assert all three parts:
   - the **preflight** emits **one pre-run summary line per affected task**;
   - the **JIT re-check** records the advisory into that attempt's provenance **ALWAYS**;
   - the JIT re-check emits a **log line ONLY when the observed pair differs from what the preflight
     predicted** — the interesting case, and the only one the preflight did not already say.

   A test that merely checks "an advisory was produced" cannot tell the quiet path from the noisy
   one, and the noisy one is what trains an operator to stop reading advisories.
4. **Agreement is the normal case.** In a correct static implementation the preflight's prediction
   and the JIT observation AGREE — assert that agreement produces the quiet path. A disagreement is
   by definition a resolver bug no preflight could catch, which is precisely why both boundaries
   exist rather than one.

### The test METHOD NAMES are PINNED

Your `03-covers-advisory-behaviors` guardrail matches DISCOVERED test names, never file text — a
behaviour named in a comment earns nothing. Add more tests freely; do not rename these.

| behaviour | method name |
|---|---|
| the condition | `WeakerJudge_IsAdvisoryCondition` and `EqualAndWeak_IsAdvisoryCondition` |
| the exclusion | `EqualAndStrong_NoAdvisory` |
| never halts | `Advisory_NeverHalts_RunProceeds` |
| preflight surface | `Preflight_EmitsOneLinePerAffectedTask` |
| provenance always | `Jit_RecordsAdvisoryIntoProvenance_Always` |
| the quiet path | `Jit_LogsOnlyWhenObservedDiffersFromPreflight` |

Tests must **COMPILE and FAIL** — failing is intentional; NOT compiling is a mistake to fix. Do not
implement the detection; task 10 does.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/VerifierAdvisoryTests.cs` and
`src/Guardrails.Core/Prompts/VerifierAdvisory.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including `TierResolver.cs`,
`GuardrailRunner.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes
a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that
file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
