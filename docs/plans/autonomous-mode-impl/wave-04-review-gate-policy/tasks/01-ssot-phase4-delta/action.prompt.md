## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/01-ssot-phase4-delta` — NOT the stableId. (This task publishes nothing
  to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Apply the SSOT delta for the criticality dial's **review-gate policy + `proceed-unreviewed` opt-in +
overwatcher `auto`-tier gating** (issue #361 Phase 4) to `docs/plans/02-schemas-and-contracts.md`. The
authoritative delta is `docs/plans/12-autonomous-mode.md` §11 ("Proposed SSOT changes"), with the
governing rules in **§5.2 (review-gate resolution), §5 floor 3 (never forged), §9 Phase 4 (overwatcher
auto-tier gate), §1 (mergeOnSuccess hard rule), §7.1 (exit code)**. Read those first. This is a docs-only
change (invariant 4). Edit ONLY `docs/plans/02-schemas-and-contracts.md`.

Note the earlier waves already landed most of the surface: the `autonomy` block (§2/§2.1), GR2039/GR2040,
the `decisions[]` `proceeded-best-guess`/`proceeded-unreviewed`/`escalated`/`answer-injected` tokens, the
`escalations/` + `autonomy.jsonl` layout, and `EscalationsPending = 4` are ALREADY in the SSOT. Do NOT
re-add them. Add ONLY the Phase-4-specific deltas below.

Apply exactly these (per doc 12 §11 "Proposed SSOT changes"):

1. **§2.1 (`autonomyPolicy`) + §9.2 (overwatcher)** — document that under autonomous mode the
   overwatcher's ALLOWLIST levers (guidance / budget) become dial-governed **silent auto-apply**, but that
   this silent auto-apply is **gated on the PRESENCE of the `autonomy` block, NOT `autonomyPolicy: auto`
   alone**. State the anti-Option-(c) back-compat guarantee plainly: an existing `autonomyPolicy: auto`
   consumer with **no `autonomy` block** ⇒ the overwatcher **still degrades to prompt, byte-identical to
   today**. The DENYLIST (verdict-surface) stays permanently human-only regardless.

2. **§5.3 / §2 (delivery — the #340 reconciliation)** — document that a run that recorded **any**
   `proceeded-best-guess` **or** `proceeded-unreviewed` decision **defaults `mergeOnSuccess` to OFF**
   (machine-decided work is never auto-delivered; the shipped green-but-undelivered warning fires),
   overridable only by an explicit `--merge-on-success`. Phrase it so `mergeOnSuccess` and the
   `proceeded-best-guess`/`proceeded-unreviewed` trigger appear together.

3. **§7.1 (exit codes)** — document that a run that took a **`proceeded-unreviewed`** decision **exits with
   a distinct non-zero code** so an automated firstmate consumer can never read it as clean green AND can
   tell it apart from a plain needs-human (2) and from an answer-required escalation halt (`EscalationsPending
   = 4`). State the pinned value: **`ProceededUnreviewed = 5`** (the next free value after `EscalationsPending
   = 4`). Also state the permanent run flag — the run is marked *"ran with N unreviewed waves."*

Do NOT restate the harness ALGORITHM (that is doc 12); write the CONTRACT (the gate, the tokens, the
exit code, the delivery rule). Do NOT edit any other section beyond what these require, and do NOT touch
any other file. (§14 is owned by a parallel worktree — keep any §14 touch to at most the one-line
cross-reference doc 12 already names; do not restate wave mechanics.)

Completion criteria (your guardrail checks these): the doc now documents (a) the overwatcher auto-tier
silent auto-apply **gated on the presence of the `autonomy` block** (anti-Option-(c) back-compat); (b)
`mergeOnSuccess` defaulting OFF on a recorded `proceeded-best-guess`/`proceeded-unreviewed` decision; and
(c) the distinct `proceeded-unreviewed` exit code (`ProceededUnreviewed = 5`).
