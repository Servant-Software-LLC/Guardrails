## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-implement-gr2060`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-implement-gr2060": { "someKey": "someValue" } }`.
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

Implement **GR2060 `UnproducibleGateRequirement`** — the diagnostic doc 19 §3.1 specified in full and
nobody built. Its predicate:

> A script guardrail requires an exact literal in a **tracked workspace file** that does not contain it,
> and **no task in the plan declares that file in its `writeScope`**.

Task 3 has already written `tests/Guardrails.Core.Tests/ProducerCoverageTests.cs` and it does not
compile, because `Guardrails.Core.Loading.ProducerCoverage` does not exist. **Read that file first — it
is the specification.** Your job is to make those ten tests pass without editing them.

**Three files, and the split matters:**

1. **`src/Guardrails.Core/Loading/ProducerCoverage.cs`** — a NEW file holding the whole check, on the
   `HandoffScopeCoverage.cs` precedent: one check family, one file. Read the clause with the helpers
   task 1 lifted into `GuardrailClauseText`, and ask `IGitTrackedFileProbe` (task 2) whether the path is
   tracked.
2. **`src/Guardrails.Core/Loading/DiagnosticCodes.cs`** — add the `UnproducibleGateRequirement = "GR2060"`
   constant with an XML doc in the house style. GR2060 is **reserved by name** in the block near the
   bottom of that file; you are allocating it. **Do NOT touch the reservation block or the next-free
   marker** — that is task 8's deliverable, and doing it here would collide with it.
3. **`src/Guardrails.Core/Loading/PlanValidator.cs`** — **one call-site line**. The check lives in
   `ProducerCoverage`; the validator invokes it.

**All ten conservatism conditions are load-bearing.** PowerShell only; a statically-known path operand;
a one-hop variable association; a requirement clause with a de-regexable witness and a requirement
polarity; the witness absent from current bytes; the file git-tracked; the path not under the plan
folder; coverage decided by `WriteScope.IsInScope` over the **union** of every task's `writeScope`;
GR2041 clean; `planIsClosed`. Each is a place conservatism is spent, and each is pinned by a test that
asserts **silence**.

**Severity: ERROR.** That is deliberate and it is defended in §5.5 — the verdict is a provable
impossibility about the run about to start, its false-positive surface is a *path* rather than a name,
and it has a recovered positive control an independent pass reproduced blind. It is also **conditional**:
tasks 5 and 6 add the `wavePrefixIsIncomplete` allow-list entry that stops an ERROR-severity GR2060
reverting a JIT partial prefix. Do not ship the severity without them; they are the next two tasks.

**Prohibitions that apply to you specifically:**

- **Do NOT widen the extractor to make a test pass** (section 11, prohibition 4). If a test will not go
  green, the honest moves are to fix the reader for the shape it genuinely must handle, or to escalate —
  never to relax the single-quote rule, the one-hop rule, or the git-tracked condition.
- **Do NOT allocate GR2070** (prohibition 2). It is held by name, and a guardrail on this task fails if
  any `DiagnosticCodes` constant takes that value.
- **Do NOT change either `PlanValidator` composition root's signature**, and leave the 73
  `new PlanValidator(` call sites compiling unchanged.

**Your own plan must survive your own check (section 11, item 10).** One of this task's guardrails runs
`guardrails validate` — with the binary you just built — against **this plan's own folder**, asserting
**zero GR2060 findings**. A check that cannot validate the plan that built it has failed its first real
test, and it is far cheaper to learn that here than at a resume three days later.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/ProducerCoverage.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs` and
`src/Guardrails.Core/Loading/PlanValidator.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside these paths. **`ProducerCoverageTests.cs` is NOT yours** — it is task
3's deliverable, and editing a test to make it pass is the one move this whole plan exists to prevent. If
a test looks genuinely wrong, write `{"needsHuman": "<which test and why>"}` to the state-out path and
stop rather than changing it.

## Done when

- `dotnet build` is green and all ten `ProducerCoverageTests` pass, unedited.
- `guardrails validate` on this plan's own folder reports **zero** GR2060 findings.
- No `DiagnosticCodes` constant has the value `GR2070`.
