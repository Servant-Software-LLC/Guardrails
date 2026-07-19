## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/02-amend-open-k` — NOT the stableId. The harness REJECTS a fragment keyed
  by anything else (every attempt). (This task publishes nothing to state — the rule is documented for
  a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Amend the **Open K** decision in `docs/plans/12-autonomous-mode.md` §10 (the "Open decisions" section).
The verbatim resolution note is `docs/plans/16-review-attestation-provenance.md` §6 (read it first). Edit
ONLY `docs/plans/12-autonomous-mode.md`.

The existing §10 K entry currently reads "K (DECIDED — NOT v1, sequenced behind #366) …". Amend it to
record that #366 resolves Open K in the NEGATIVE — close / rescope to audit-only — folding in this note
(from doc 16 §6, keep the wording close to verbatim):

> **Open K — resolved by #366: close / rescope to audit-only.** A runtime halt on the review marker
> cannot be a real boundary (the marker is write-forgeable at ~zero cost; there is no unforgeable option
> in a plain-file / same-machine model — #366 §3). Do **not** add `autonomy.reviewGate: enforce`. The
> review floor stays a GR2025 advisory; #366 adds a recorded, deterministic `attestation.source`
> (`review-artifact | bare | machine | legacy`) that an autonomous run's post-hoc report / audit can
> surface (e.g. "this wave was marked `machine`, never human-reviewed") — but it does not gate the run.

Keep the change minimal and localized to §10 K (you may keep the prior "DECIDED" text and append the
resolution, or replace K's body with the note above — either is fine). Do NOT edit §10 K's neighbours or
any other section/file.

Completion criteria (your guardrail checks these): §10 K now contains the phrases "resolved by #366" and
"audit-only".
