## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-author-tests-jit-prefix-veto`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-author-tests-jit-prefix-veto": { "someKey": "someValue" } }`.
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

Write `tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs` — class **`JitPrefixVetoTests`**, xUnit v3 —
encoding the **#501 regression** that task 6 fixes. **These tests must be RED now and green after task
6.** That ordering is the whole point: it proves the mitigation does something.

**The defect they pin.** Task 4 has just shipped GR2060 at **ERROR** severity. In
`src/Guardrails.Core/Execution/Scheduler.cs`:

- `ValidatePlanAfterBreakdown` computes
  `excused = wavePrefixIsIncomplete ? errors.Where(UnsatisfiableWhileIncomplete) : []`, then
  `blocking = errors.Except(excused)`.
- `UnsatisfiableWhileIncomplete` is a **single-code comparison** against
  `PlanGuardrailsMissingIntegrationReRun`. GR2060 is not in it.
- `PlanValidator.PlanIsClosed` is `plan.Waves.All(w => w.Tasks.Count > 0)`. It detects an **empty stub
  wave**. It returns **`true`** for a wave authored as a **partial prefix** — 5 task folders of an
  intended 12 — because 5 > 0.

So a JIT partial prefix has an incomplete `writeScope` union by construction; a wave gate requiring
content one of the not-yet-authored tasks will produce looks to GR2060 exactly like a gate nothing can
produce; the resulting ERROR is not excused, casts a veto, and **the authored prefix is reverted
wholesale**. That is verbatim the defect #501 fixed, one code over — and reverted JIT work is the most
expensive thing this harness can throw away.

**Pin these test method names exactly:**

| # | method name | what it asserts |
|---|---|---|
| 1 | `PartialPrefix_TrippingGr2060_IsNotReverted` | the prefix survives the gate |
| 2 | `PartialPrefix_TrippingGr2060_StillReportsTheFinding` | the excused error is still in the gate-decision report |
| 3 | `CompletePlan_TrippingGr2060_IsStillBlocked` | the excuse is scoped to an incomplete prefix, not a licence |
| 4 | `PlainValidate_OnAPartialPrefix_StillErrors` | `guardrails validate` is unaffected by the gate's excuse |

**Test 2 is the one that is easy to get wrong.** *Excused* means the finding stops casting a veto it
cannot fairly cast — it does **not** mean the finding vanishes. An operator reading the gate decision
must still see the GR2060 finding. A mitigation that makes the error disappear would pass test 1 and be
a worse bug than the one it fixed.

**Test 4 draws the other boundary.** The suppression belongs to the **JIT breakdown gate**, not to
`validate`. A human running `guardrails validate` on a partial prefix is asking a different question and
must still get the error.

**Test 3 is the anti-over-correction control.** A complete plan that genuinely cannot produce its own
gate's requirement must still be blocked. If the excuse leaks to complete plans, GR2060 stops meaning
anything.

**Build the prefix the way the harness really does** — a wave whose `breakdown-intent.json` still owes
folders, so `wavePrefixIsIncomplete` is true from actual knowledge rather than inferred from shape.
Driving `PlanIsClosed` instead would test the trap rather than the fix.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including `Scheduler.cs`, which is task 6's
deliverable. An out-of-scope edit fails the task immediately and consumes a retry. The tests failing
**is the expected red**; do not fix `Scheduler.cs` to make them pass.

## Done when

- The file exists with all four pinned method names, each carrying a real `[Fact]` or `[Theory]`.
- `dotnet test --filter FullyQualifiedName~JitPrefixVetoTests` exits **non-zero**, and the failure is a
  genuine assertion failure — the tests compile and RUN and are wrong about today's behaviour, which is
  what makes task 6's fix observable.
