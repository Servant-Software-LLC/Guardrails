## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `21-update-ssot-supply-contracts`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "21-update-ssot-supply-contracts": { "someKey": "someValue" } }`.
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

Record the three contracts design 40 §6 names, in `docs/plans/02-schemas-and-contracts.md`:

- **§1** — `logs/<runId>/supplied/` as a harness-owned staging tree: written by
  `guardrails supply`, drained and deleted by the harness, **never read by a guardrail**.
- **§7 `run.json`** — the `supplied[]` section: `at`, `commit`, `paths`, `bytes`.
- **§8** — the `SuppliedResourcesCommitted` observer event.

Write it in the document's own conventions — match the surrounding sections' shape rather than
importing design 40's. **Do not reword existing prose to match a checking pattern**; the
guardrail requires tokens the contract genuinely needs, and if one reads wrong in context, say
so via `needsHuman` rather than bending the document around it.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry.

