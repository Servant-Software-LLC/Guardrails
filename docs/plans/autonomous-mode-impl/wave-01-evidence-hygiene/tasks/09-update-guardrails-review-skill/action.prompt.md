## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/09-update-guardrails-review-skill` — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## `.claude/` deliverable — use the harness-write escape hatch (do not remove)
Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write — the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST read the current file, compose the FULL updated content, and
write `{"needsHarnessWrite": {"path": ".claude/skills/guardrails-review/SKILL.md", "content": "<full
updated file content>", "reason": "add the #366 review-report + mark-reviewed --evidence flow"}}` to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then your
guardrails still run normally against the result. If you already attempted a direct write and it was
refused, do NOT retry it or try workarounds (PowerShell, `dangerouslyDisableSandbox`) — just emit
`needsHarnessWrite` as above.

## Task

Update the `/guardrails-review` skill at `.claude/skills/guardrails-review/SKILL.md` (Step 6/7) so a
review pass leaves the durable evidence #366 requires. The design of record is
`docs/plans/16-review-attestation-provenance.md` §4 (the review-report artifact) and §12.2 (the
skill-author handoff row) — read both first. The `guardrails plan-hash` and `mark-reviewed --evidence`
commands this flow uses were implemented earlier in this wave.

Make these edits to the skill (read the current SKILL.md first; keep its structure and voice):
1. In Step 6/7, instruct the skill to obtain the plan hash via **`guardrails plan-hash <folder>`** (it
   cannot compute `PlanDefinitionHash` itself), then WRITE the review report to
   `<plan>/state/reviews/review-<planHashShort>-<reviewedAtCompact>.md` containing the findings table +
   verdict AND an embedded **`Plan-Definition-Hash: sha256:…`** line (F2a) — human-readable.
2. Then call **`guardrails mark-reviewed <folder> --evidence <report>`**, which runs the F2 checks and
   records `source: review-artifact` + `reportDigest` on pass, or downgrades to `bare` on failure.
3. Document the evidence classes: `review-artifact` vs `bare` vs `machine` (and read-time `legacy`);
   state that `state/reviews/` is under the hash-EXCLUDED tree (no re-stale).
4. Drop any "unforgeable" / "raises forge cost" framing of the review floor; state plainly that the
   class is recorded for AUDIT, not a gate — the marker is only as strong as write-access to the plan
   folder, and the harness never writes the marker on a human's behalf.

Do NOT edit any other skill or file. The whole update is one file under `.claude/skills/`.

Completion criteria (your guardrail checks these): the SKILL.md now references `plan-hash`,
`state/reviews/`, `--evidence`, and `Plan-Definition-Hash`.
