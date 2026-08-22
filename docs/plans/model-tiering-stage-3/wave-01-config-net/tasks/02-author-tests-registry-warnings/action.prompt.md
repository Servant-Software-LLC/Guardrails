## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. This plan is WAVED, so the key is the WAVE-QUALIFIED id:
  `{ "wave-01-config-net/02-author-tests-registry-warnings": { "someKey": "someValue" } }`.
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

Author xUnit tests for two validate-time warnings that **do not exist yet**. The tests must COMPILE
and FAIL. Failing is the deliverable; not compiling is a mistake to fix.

Write them to `tests/Guardrails.Core.Tests/ModelTiering/TieringRegistryWarningTests.cs`, in a single test
class named exactly **`TieringRegistryWarningTests`**, every test tagged
`[Trait("Category", "ModelTieringStage3")]`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/TieringRegistryWarningTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including
`src/Guardrails.Core/Loading/PlanValidator.cs`, other test files, or the `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a missing
symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**No stubs are needed here, and you must not write any.** Unlike the usual TDD pair, the production
type already exists and compiles: `PlanValidator` is real, and `DiagnosticCodes.NonRoutableBlockIsDefault`
/ `CostlyBlockRoutingInert` were allocated by task 01. Your tests therefore compile against the real
API and fail because the validator does not yet *emit* those codes. That is the red.

### The behaviours to encode — the exact test method name, and the state each must be in TODAY

Your guardrail binds each behaviour to the method name below and checks it in the runner's own TRX.
Use these names verbatim.

**Read the third column — the five tests are NOT all red, and that is deliberate.** Four assert a
warning that does not exist yet, so they fail. One asserts a **silence** — that a code is *absent* —
and a negative assertion cannot fail before the feature exists. It passes today and must keep passing.

| Behaviour | Test method name | State on arrival |
|---|---|---|
| GR2051 fires when the registry `default` pointer names a `costly: true` block, in a tiering-configured file | `WarnsWhenCostlyBlockIsDefault` | **Failed** |
| GR2051 fires when the `default` pointer names a block with **no `routing`** at all, in a tiering-configured file | `WarnsWhenRoutinglessBlockIsDefault` | **Failed** |
| GR2052 fires when a `costly: true` block **also** declares `routing` | `WarnsWhenCostlyBlockDeclaresRouting` | **Failed** |
| GR2052 and GR2048 **compose** — a plan with both an inert costly-routing block and a genuinely unservable tier reports both, neither masking the other | `ComposesWithUnservableTier` | **Failed** |
| GR2051 is **silent** when the file does not configure tiering (no `routing` on any block, no `tiering` block) — Invariant 7 | `SilentWhenTieringNotConfigured` | **Passed** — must exist, must run, must not be `[Skip]`ped |

### What each test must actually assert

- Assert on the **diagnostic code and its severity**: a `Diagnostic` whose `Code` equals the constant
  from `DiagnosticCodes` and whose severity is **warning**. All three codes in this wave are warnings —
  DoR §12.6 is explicit that the plan still runs. A test that asserts only "some diagnostic was
  produced" is hollow and the census cannot tell it from a real one.
- For the **silent** case, assert the code is **absent** from the diagnostics — not that the collection
  is empty (an unrelated warning may legitimately be present).
- Follow the conventions of the existing validator tests in `tests/Guardrails.Core.Tests/` — read a
  neighbouring test class first and match how it builds a plan fixture and invokes the validator.
  Do not invent a new fixture style.

### Do not

- Do NOT implement the warnings. Emitting them is task 03, whose `writeScope` owns `PlanValidator.cs`.
- Do NOT "fix" the silent test by making it assert the code is PRESENT. It is *supposed* to pass today
  — see the expected-state table above. Converting it would delete the only Invariant-7 protection in
  this plan while turning the census green, which is the worst possible outcome and looks like success.
- Do NOT use `Assert.True(true)`, or assert only that a value the test itself constructed is non-null.
  A test that never invokes the validator is a tautology; the per-test census exists to catch exactly
  that and will report it by name.
