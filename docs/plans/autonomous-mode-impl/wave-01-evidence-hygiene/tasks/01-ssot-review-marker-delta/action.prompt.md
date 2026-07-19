## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  the wave dir plus the task folder name (here `wave-01-evidence-hygiene/01-ssot-review-marker-delta`),
  NOT the stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "wave-01-evidence-hygiene/01-ssot-review-marker-delta": { "someKey": "someValue" } }`.
  (This task publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Apply the SSOT §13 delta for issue #366 (review-attestation provenance) to
`docs/plans/02-schemas-and-contracts.md`, section **§13** (the review-marker contract). The
authoritative delta is `docs/plans/16-review-attestation-provenance.md` §12.1 (read it first, plus §4
for the exact JSON shape). This is a docs-only change (invariant 4: the contract lands in the SSOT in
the same change as the harness work in this wave). Edit ONLY `docs/plans/02-schemas-and-contracts.md`.

Make exactly these edits to §13 (per doc 16 §12.1), preserving the surrounding §13 prose:

1. **Replace the marker JSON block** with the v2 shape (doc 16 §4): bump the illustrated `version` to
   `2`; keep `reviewedAt` and `planHash` unchanged; add the OPTIONAL `attestation` object with
   `source`, `tool`, `actor`, and `evidence { reportPath, reportDigest }`.
2. **Add an "Evidence hygiene (issue #366)" subsection** stating: the `source` enum semantics —
   `review-artifact | bare | machine`, with read-time-only `legacy` for a marker that has no
   `attestation` block; that `evidence` is present iff `source: review-artifact`; the **F2 stamp-time
   checks** (the report embeds a `Plan-Definition-Hash:` that must equal the marker's `planHash`;
   `reportPath` must resolve under `state/reviews/`; F2 failure ⇒ downgrade to `source: bare`, never a
   fabricated class); that `reportPath` lives under the hash-EXCLUDED `state/reviews/` tree (cross-ref
   §7.3, so the report cannot re-stale the marker); that `reportDigest` uses the SAME newline
   normalization as `PlanDefinitionHash` and is symmetric across writer/reader (F7); that `actor`/`tool`
   are self-reported and non-authoritative; and the reader rule **"never gate on `version`; classify by
   the `attestation` block."**
3. **Add the trust-boundary sentence**, replacing any "unforgeable" / "can never falsely vouch for
   changed content" framing so it reads: the review floor is only as strong as write-access to the plan
   folder; #366 records a deterministic evidence class + an audit trail for the non-adversarial case —
   it does not prove a human and is not a forgery deterrent (invariant 6). Scope any surviving "can
   never falsely vouch for CHANGED content" claim explicitly to *staleness* so it is not read as a
   forgeability claim.
4. **State explicitly that the marker is read for AUDIT, not by the Scheduler**: there is no runtime
   gate on the review marker (enforce-mode was considered and rejected — cross-ref
   `docs/plans/16-review-attestation-provenance.md` §6); GR2025 stays an advisory warning.
5. **Multi-wave note**: the `attestation` block is per-wave exactly as the marker is; the review report
   lives under `<plan>/<wave>/state/reviews/`. No new wave semantics.

Do NOT edit §7.3 beyond a cross-reference if §13 already has one; do NOT touch any other section or
file. Completion criteria (your guardrail checks these): §13 now contains the `attestation` block, the
`review-artifact`/`bare`/`machine` enum, `reportDigest`, the F2 wording, and the "audit, not a gate"
statement.
