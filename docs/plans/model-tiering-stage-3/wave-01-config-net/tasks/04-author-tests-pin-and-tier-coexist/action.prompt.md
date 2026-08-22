## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. This plan is WAVED, so the key is the WAVE-QUALIFIED id:
  `{ "wave-01-config-net/04-author-tests-pin-and-tier-coexist": { "someKey": "someValue" } }`.
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

Author xUnit tests for **GR2053 `PinAndTierCoexist`**, a validate-time warning that does not exist
yet. The tests must COMPILE and FAIL. Failing is the deliverable; not compiling is a mistake to fix.

Write them to `tests/Guardrails.Core.Tests/Loading/PinAndTierCoexistTests.cs`, in a single test class
named exactly **`PinAndTierCoexistTests`**, every test tagged
`[Trait("Category", "ModelTieringStage3")]`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Loading/PinAndTierCoexistTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including
`PlanValidator.cs`, other test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

No stubs are needed and you must not write any: `PlanValidator` already exists and compiles, and
`DiagnosticCodes.PinAndTierCoexist` was allocated by task 01. The tests fail because the validator
does not yet *emit* the code.

### What a "pin" actually is — read this before writing the tests

DoR §13.2 describes GR2053 as *"a full pin (`action.runner`/`action.model`)"*, and that slash is
easy to read as **and**. **It is OR, and the shipped code settles it**:
`src/Guardrails.Core/Prompts/TierResolver.cs` line 139 is

```csharp
if (action.Runner is not null || action.Model is not null)
```

Either one **alone** bypasses tier resolution entirely. So a task carrying `action.model` and
`action.tier` — with no `action.runner` at all — has a dead tier, and GR2053 must fire on it. A
reading that required both would silently miss that case, which is the whole reason it is pinned as
its own named test below. Confirm the line still says this before you rely on it; if the code has
changed, halt with `needsHuman` rather than guessing.

### The behaviours to encode — and the exact test method name for each

| Behaviour | Test method name |
|---|---|
| GR2053 fires when `action.runner` and `action.tier` are both set | `WarnsWhenRunnerPinAndTierCoexist` |
| GR2053 fires when `action.model` and `action.tier` are both set, with **no** `action.runner` — either pin alone bypasses resolution | `WarnsWhenModelPinAndTierCoexist` |
| GR2053 is **silent** on a pin with no tier — nothing is dead weight | `SilentWhenPinWithoutTier` |
| GR2053 is **silent** on a tier with no pin — the ordinary tiered task | `SilentWhenTierWithoutPin` |

### What each test must actually assert

- Assert on the **diagnostic code and its severity**: a `Diagnostic` whose `Code` equals
  `DiagnosticCodes.PinAndTierCoexist`, at **warning** severity. The tier being dead weight is not an
  error — the plan still runs, the pin simply wins.
- For the **silent** cases, assert the code is **absent**, not that the collection is empty (an
  unrelated warning may legitimately be present).
- Follow the conventions of the existing validator tests — read a neighbouring test class first and
  match how it builds a plan fixture and invokes the validator.

### Do not

- Do NOT implement the warning. That is task 05, whose `writeScope` owns `PlanValidator.cs`.
- Do NOT write a test that passes today. All four must be RED on arrival.
- Do NOT use `Assert.True(true)` or assert only on a value the test itself constructed. A test that
  never invokes the validator is a tautology, and the per-test census will report it by name.
