## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-03-classify-and-escalate/01-ssot-phase3-delta` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Apply the SSOT delta for the criticality dial's **classify-then-act + escalation & reply channel**
(issue #361 Phase 3) to `docs/plans/02-schemas-and-contracts.md`. The authoritative delta is
`docs/plans/12-autonomous-mode.md` §11 ("Proposed SSOT changes"), with the field/record detail in
**§6.2 (decisions[] deltas), §6.3 (autonomy.jsonl), §7.1/§7.2/§7.4/§7.6 (escalation + answer-file
contract), §5.2 (the compound-config clamp)** and the decided values §10 I/L/M (read those first). This
is a docs-only change (invariant 4). Edit ONLY `docs/plans/02-schemas-and-contracts.md`.

Apply exactly these (per doc 12 §11 "Proposed SSOT changes"):

1. **§7 (`run.json` / `decisions[]`)** — extend the `DecisionEntry` shape with the OPTIONAL additive
   fields `gate`, `classification`, `criticality`, `confidence`, `threshold`, `bestGuess`,
   `blockerAttempts`, `blockerWaitedSeconds`, `assessmentRef`, and (for answer-injection) `answerRef` +
   `answeredBy` (doc 12 §6.2). Add the new `decision` tokens **`escalated`**, **`proceeded-best-guess`**,
   **`proceeded-unreviewed`**, **`blocker-retried`**, **`answer-injected`**. State the additions are
   OPTIONAL/additive — existing `drift`/`task`/`wave` entries and existing tokens are UNCHANGED (an
   existing consumer ignores unknown fields).
2. **§8 (log layout)** — add the run-level append-only detail stream `logs/<runId>/autonomy.jsonl`
   (doc 12 §6.3), the escalation records `logs/<runId>/escalations/<seq>-<gate>.json` (carrying the
   `EscalationId` + the `DefinitionHash` captured at escalation time) and their co-located
   `…<seq>-<gate>.answer.json` reply files, with the `open` → `answered` → `consumed` `status` lifecycle
   (doc 12 §7.1/§7.4).
3. **§7.2 (resume / answer-injection binding)** — the escalation captures a `DefinitionHash`
   (`TaskDefinitionHash` for `needs-human`, `WaveDefinitionHash` for `wave-checkpoint`); `seq` is a
   durably **monotonic, never-reused** run counter; a resume consumes an answer only if it echoes the
   escalation identity `{runId, seq, gate, subject}` verbatim, is non-stale against the unit's CURRENT
   hash (dual-hash), is unconsumed (**CAS-guarded**, cross-`runId` `status` persisted in the CREATING
   run's `escalations/` dir), and targets an **answerable** gate (`needs-human` / `wave-checkpoint` only
   — and NOT a clamped hard call under `proceed-unreviewed`). State plainly: **there is NO
   `review-attested` answer kind** (Blocker 2 / #366) — the review gate is never resolved by an answer;
   and the injected `needs-human` `text` is **delimited UNTRUSTED data** that cannot reach the
   verdict surface (§7.4 Finding 4 — the overwatcher denylist is the backstop).
4. **§7.1 (exit codes)** — note that a run ending with unresolved escalations (or that took a
   `proceeded-unreviewed` decision) exits with a **distinct non-zero code** so an automated firstmate
   consumer never reads it as clean green; reconcile with the shipped 0/1/2/3 scheme (recommend 2 =
   actionable/needs-human).

Do NOT restate the harness ALGORITHM (that is doc 12); write the CONTRACT (shapes, tokens, paths,
binding rules). Do NOT edit any other section beyond what these require, and do NOT touch any other
file. (§14 is owned by a parallel worktree — keep §14.4 to at most the one-line cross-reference doc 12
already names; do not restate wave mechanics.)

Completion criteria (your guardrail checks these): §7/§8 now document `autonomy.jsonl`, the
`escalations/` records + `.answer.json` replies, the `answer-injected` / `proceeded-best-guess` /
`blocker-retried` / `escalated` tokens, and the `definitionHash` / monotonic-`seq` / CAS answer-binding
rules.
