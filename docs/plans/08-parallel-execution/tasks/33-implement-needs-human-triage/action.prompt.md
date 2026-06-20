## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Implement plan 08 §9 (AI triage on needs-human, PO #7 / Decision 8) so `NeedsHumanTriageTests` pass.
The triage step is a constrained prompt action behind the EXISTING `IPromptRunner` seam (the same seam
as `claude` and the AI-merge worker) using a DISTINCT `ai-triage` prompt profile — no new runner shape.

Build all of the following:
- New `src/Guardrails.Core/Execution/NeedsHumanTriage.cs` (the file this task owns; declare
  `class NeedsHumanTriage` there). It runs the triage step.
- **Trigger — exhaustion only.** Triage runs exactly ONCE when a task reaches `needs-human` via
  ATTEMPT EXHAUSTION (action/guardrail failures across the whole retry budget). It does NOT run when
  the agent itself emitted `{"needsHuman": "..."}` (that is already a human ask — skip triage), and it
  does NOT run mid-retry (only on the terminal exhaustion transition).
- **Diagnosis — tool-vs-local.** Given the failed task (`task.json`, every attempt's action output, the
  failing guardrail outputs, and the larger run context), classify the root cause as either a
  Guardrails-TOOL/harness limitation (→ warrants a GH issue against the Guardrails repo; draft a
  ready-to-file title+body) or a problem LOCAL to the current repo (the plan/code/tests for this
  project; no Guardrails issue).
- **`feedback.md` — TASK-LEVEL under the elevated logs.** Write `logs/<runId>/<task-id>/feedback.md`
  (a SIBLING of the `attempt-N/` dirs, NOT inside any attempt dir — the elevated logs layout is task
  10's deliverable). It captures the diagnosis, the evidence drawn on, and (if a tool problem) the
  drafted GH-issue title+body.
- **needs-human message points to it.** The task's `needs-human` message (rendered by the run summary
  and `status`) references the `logs/<runId>/<task-id>/feedback.md` path.
- **ADVISORY — gates nothing.** The task is ALREADY `needs-human` before triage runs; triage cannot
  change that verdict, cannot re-open the task, cannot mark anything done, and cannot burn retry budget.
  Its `PromptResult.IsError` / exit code is NEVER read as a verdict — a failed/throwing triage just
  means "no feedback.md was produced", logged, and the task is still plainly `needs-human` (a prompt
  proposes, a file certifies). Triage must never block or abort the run.
- **Opt-in auto-file (`triageAutoFile`, default OFF — PO Decision 8).** By default, triage only DRAFTS
  the GH issue (title+body) into `feedback.md` and files NOTHING to a remote. Only when
  `triageAutoFile` is explicitly opted in (gated behind a configured repo + token) does it auto-file.
  Default is OFF.

## Contract change (same-change SSOT rule)
In the SAME change, add the §9.2 note to `docs/plans/02-schemas-and-contracts.md` (the schema SSOT)
documenting: the `ai-triage` reserved prompt profile under `promptRunners`; the exhaustion-only trigger
(not agent-emitted `{needsHuman}`, not mid-retry); the TASK-LEVEL `logs/<runId>/<task-id>/feedback.md`
artifact (sibling of `attempt-N/`, distinct from the per-attempt `feedback.md` in §8); the needs-human
message pointer; the strictly-advisory contract (exit code never a verdict, gates nothing); and the
opt-in `triageAutoFile` (default off, drafts only). Place §9.2 immediately after §9.1.

Make `NeedsHumanTriageTests` pass WITHOUT editing them; if genuinely wrong, emit
`{"needsHuman": "<why>"}`. Keep the solution building. Publish nothing to state.
