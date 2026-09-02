## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `06-excuse-gr2060-at-jit-gate`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "06-excuse-gr2060-at-jit-gate": { "someKey": "someValue" } }`.
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

Make `JitPrefixVetoTests` (task 5) pass by adding **GR2060 to the JIT breakdown gate's excused set** in
`src/Guardrails.Core/Execution/Scheduler.cs`.

**The change is one member.** `UnsatisfiableWhileIncomplete` today is a single-code comparison against
`PlanGuardrailsMissingIntegrationReRun`. It grows a second code: `UnproducibleGateRequirement`.

**Key it on `wavePrefixIsIncomplete`, NEVER on `PlanIsClosed`.** This is the single most important
sentence in this task, and the design was rewritten once for getting it wrong:

- `wavePrefixIsIncomplete` is **actual knowledge of incompleteness** — it is set from a usable
  `breakdown-intent.json` that still owes folders. `ValidatePlanAfterBreakdown` already receives it.
- `PlanIsClosed` merely observes that no wave folder is **empty**. It returns `true` for an authored
  partial prefix, which is exactly the case that breaks.

They are not interchangeable, and believing they are is the trap this task exists to avoid.

**Three properties to preserve, all already true of the #501 code around you:**

1. **Excused errors stay in the report.** They stop casting a veto they cannot fairly cast; they do not
   vanish. The gate-decision report still names the GR2060 finding. #501's own comment explains why that
   matters: before it, a reader could not tell a suppression that fired from one that never ran.
2. **The suppression is scoped to the JIT breakdown gate, not to `validate`.** A human running
   `guardrails validate` on a partial prefix still sees the ERROR. That is correct — they are asking a
   different question.
3. **`PlanIsClosed` stays as GR2060's condition-10 suppressor** for the empty-stub-wave case it really
   does detect. The two suppressions are **complementary, not alternatives**.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`, and
within it touch **only `UnsatisfiableWhileIncomplete`**. Section 11 prohibition 7 puts the rest of that
file — and `RunCommand`, `TaskExecutor`, `IPromptRunner`, `IActionRunner`, `IProgressSink` — out of
scope entirely. The harness runs a `git diff` check after this task; an out-of-scope edit fails it
immediately and consumes a retry. If the tests cannot pass without touching something else, write
`{"needsHuman": "<what else must change and why>"}` to the state-out path and stop.

## Done when

- All four `JitPrefixVetoTests` pass, unedited.
- `UnsatisfiableWhileIncomplete` names both codes and the excuse is keyed on `wavePrefixIsIncomplete`.
- `PlanIsClosed` is untouched and still suppresses the empty-stub-wave case.
