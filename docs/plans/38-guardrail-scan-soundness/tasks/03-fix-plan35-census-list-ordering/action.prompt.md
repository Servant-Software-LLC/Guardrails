## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `03-fix-plan35-census-list-ordering`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-fix-plan35-census-list-ordering": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "03-fix-plan35-census-list-ordering": { "someKey": "someValue" },
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

`docs/plans/35-event-vocabulary/tasks/04-author-tests-event-vocabulary/guardrails/02-tests-fail-on-stubs.ps1`
calls `$problems.Add(...)` twice inside its `$mustExecute` loop, and creates `$problems` with
`New-Object System.Collections.Generic.List[string]` **after** that loop. On the branch where the
declared exemption actually fails, `$problems` is `$null`, `$null.Add(...)` throws, and under
`$ErrorActionPreference = 'Continue'` the abort skips the rest of the `try` — including the seven-name
`mustFail` red census below it and the guardrail's own `exit 0` — so the process exits **0** and the
harness records a PASS. This is the single live instance of #608 in the repository.

**Move the `$problems = New-Object System.Collections.Generic.List[string]` line so it precedes its first
use.** That is the whole change. Do not restructure the census, do not renumber it, do not touch the
behaviour lists, and do not "improve" the messages: this file is a committed artifact of a merged, green,
shipped run and every other byte of it is correct.

**Scope boundary (harness-enforced):** Write only to that one file. After this task completes, the
harness runs a `git diff` check and rejects any edit outside it. An out-of-scope edit fails the task
immediately and consumes a retry. In particular, the other 863 committed guardrails carrying the same
`ErrorActionPreference` exposure are **deliberately not migrated** (design 38 §8) — do not sweep them.
