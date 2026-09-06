## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `09-immunize-existing-guardrail-fixtures`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-immunize-existing-guardrail-fixtures": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "09-immunize-existing-guardrail-fixtures": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Three existing test files carry **synthetic guardrail fixtures** that the incoming GR2037 entry `#608a`
will red. They belong to no other task, so left alone they surface at the **terminal gate**, where
nothing can fix them (the #587 tripwire shape). This task removes that exposure *before* the entry
exists, so each file is green both now and after.

| file | the fixture | the fix |
|---|---|---|
| `tests/Guardrails.Core.Tests/GuardrailRequiresForbiddenTokenTests.cs` | `HistoricalTask06Guardrail` — a task guardrail body | `ErrorActionPreference = 'Continue'` → `'Stop'` |
| `tests/Guardrails.Core.Tests/JitPrefixVetoTests.cs` | `GateBody` — a plan-root terminal guardrail | same |
| `tests/Guardrails.Core.Tests/ProducerCoverageTests.cs` | the body written by `PlanGate(...)` | same |

**Grep, do not trust this table.** It was measured at authoring time and the tree moves — and an earlier
draft of it was wrong in exactly this way: it also named two integration-test files, whose bodies turned
out to be stub *agent runners* (they read stdin and emit `{"type":"result"}`), which the validator never
scans. Search your three files for `ErrorActionPreference = 'Continue'`, check each hit really is a
**guardrail** body and not a stub runner or a fake CLI, and fix every genuine hit. If your grep returns a
different set, trust the grep and say so in your summary.

**Scope boundary (harness-enforced):** Write only to those three files. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — in particular
`tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs` belongs to a **different** task, and
`.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json` to a third. An out-of-scope
edit fails the task immediately and consumes a retry.

## Two constraints

- **The change must not alter what the fixture tests.** Setting `'Stop'` instead of `'Continue'` does not
  change a fixture's `#73` / `#187a` / `#462` behaviour — which is precisely why this is safe to do
  before the entry lands. Run the three test files afterwards and confirm they still pass. If a
  fixture's whole point is that it sets `'Continue'`, that is a finding, not something to paper over:
  write `{"needsHuman": "<which fixture and why>"}` to the state-out path and stop.
- **Smallest possible diff.** One line per fixture. Do not reformat, do not touch a fixture that is
  already clean, and do not "improve" a neighbouring test while you are in the file.
