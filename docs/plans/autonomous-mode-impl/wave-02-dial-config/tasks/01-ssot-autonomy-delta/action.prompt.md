## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-02-dial-config/01-ssot-autonomy-delta` — NOT the stableId. (This task publishes nothing to
  state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Apply the SSOT delta for the criticality dial's **config surface** (issue #361 Phase 2) to
`docs/plans/02-schemas-and-contracts.md`. The authoritative delta is `docs/plans/12-autonomous-mode.md`
§11 ("Proposed SSOT changes"), with the field detail in §3.3/§3.4/§3.5 and the decided values in §10
F/G/I/M/N (read those first). This is a docs-only change (invariant 4). Edit ONLY
`docs/plans/02-schemas-and-contracts.md`.

Apply exactly these (per doc 12 §11):
1. **§2 (`guardrails.json`)** — add the OPTIONAL `autonomy` block with `escalationThreshold`
   (`low < moderate < high < critical`, value = "lowest criticality that still escalates"),
   `gateThresholds` (per-gate keys `needs-human` / `wave-checkpoint` / `review-gate`, the last taking the
   `escalate` / `proceed-unreviewed` acknowledgment, NOT a criticality level), `blockerRetry`
   (`maxAttempts: 5`, `totalWaitSeconds: 900`), and `maxJudgeWidenings: 3`. State **all fields are
   optional and the whole block absent ⇒ the dial is inert ⇒ behaviour is byte-identical to today** (the
   back-compat guarantee). State the **`--autonomous` REQUIRES an effective `maxCostUsd`** rule (a
   built-in `$20` default with a loud warning if unset).
2. **§2.1 (`autonomyPolicy`)** — add a paragraph: the dial is a NEW ORTHOGONAL axis engaging only under
   `autonomyPolicy: auto` in a non-interactive context; it NEVER lowers a floor (verdict surface /
   review gate / unsound rewind / terminal exhaustion). `autonomyPolicy`'s three values and GR2031 are
   UNCHANGED. Note the overwatcher `auto`-tier gate: silent auto-apply requires the PRESENCE of the
   `autonomy` block, not `autonomyPolicy: auto` alone.
3. **New GR codes** — document **GR2039** (an invalid `escalationThreshold` / `gateThresholds` value)
   and **GR2040** (the compound-config incompatibility: `proceed-unreviewed` + a reachable `critical`
   end-state — run-wide `escalationThreshold: critical` OR any per-gate `needs-human`/`wave-checkpoint`
   `== critical`) in the validation/diagnostics section.
4. **§14.4 note** — the dial governs the `wave-checkpoint` gate; the review half stays a floor (§5.2).
   (A one-line cross-reference; §14 is owned by a parallel worktree — do not restate the wave mechanics.)

Do NOT edit any other section beyond what these require, and do NOT touch any other file.

Completion criteria (your guardrail checks these): §2/§2.1 now document `escalationThreshold`,
`gateThresholds`, `blockerRetry`, `maxJudgeWidenings`, and the GR2039 / GR2040 codes.
