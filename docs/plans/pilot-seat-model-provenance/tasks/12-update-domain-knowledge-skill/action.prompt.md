## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "12-update-domain-knowledge-skill": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
> Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
> the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
> `Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
> harness's permission-wall tracker. Instead, FIRST write
> `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
> "reason": "<why>"}}` to the state-out path. The harness (which is NOT subject to that layer) performs
> the write directly, then your guardrails still run normally against the result. If you already
> attempted a direct write and it was refused, do NOT retry it or try workarounds (PowerShell,
> `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

Update `.claude/skills/guardrails-domain-knowledge/SKILL.md` so its provenance/observability description
states the harness now surfaces the actually-run model per attempt (live UI, attempt log, run report,
journal) — per its SELF-UPDATING clause. Reference the durable term `resolvedModel`.

Because this file is under `.claude/`, follow the escape-hatch header above: emit
`needsHarnessWrite` with the full updated file content — do NOT attempt a direct `Write`/`Edit`.