## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12-author-tests-caller-scope`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-author-tests-caller-scope": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Author failing tests AND the stub for the caller-scoping rule the review DECIDED (design 40 §5a
and §6): **a task agent may supply only paths inside its own `writeScope`; an operator
invocation is unrestricted.**

**Test file:** `tests/Guardrails.Core.Tests/Supply/SupplyCallerScopeTests.cs`
**Test class:** `SupplyCallerScopeTests`
**Stub file:** `src/Guardrails.Core/Execution/SupplyCallerScope.cs`

Every test carries `[Trait("Category", "Supply")]`.

**The detection, and what it does NOT prove.** A `supply` invoked from inside a task action sees
`GUARDRAILS_STATE_OUT` / `GUARDRAILS_WORKSPACE`; one invoked from an operator's shell does not —
reliably, because #442 made the `GUARDRAILS_*` namespace hermetic across the process boundary.
That stops the accidental and naive case. It does **not** stop a determined agent, which can
clear the variables. Do not write a test asserting it does; the §4 provenance record is the real
defence and it is a different task.

**Pin these behaviours to these EXACT method names:**

- `OperatorInvocation_IsUnrestricted` — no `GUARDRAILS_*` in the environment ⇒ any workspace path.
- `TaskInvocation_MaySupplyInsideItsOwnWriteScope` — the JIT case the reviewer valued: an agent
  authoring a script it then needs on the base almost always owns that path already.
- `TaskInvocation_IsRefusedOutsideItsWriteScope` — the bypass, closed.
- `TaskInvocation_UsesTheSameMembershipRuleAsTheWriteScopeCheck` — scoping must agree with
  `WriteScope.IsInScope`, the rule the harness already enforces at write time. Two mechanisms for
  one decision is how a file becomes suppliable and unwritable at the same moment.

The tests MUST COMPILE and FAIL. Do NOT implement the rule.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

