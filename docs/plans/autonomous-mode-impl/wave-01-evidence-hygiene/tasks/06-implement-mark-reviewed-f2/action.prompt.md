## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/06-implement-mark-reviewed-f2` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the `mark-reviewed` **F2 stamp-time hygiene checks** (issue #366) in
`src/Guardrails.Cli/Commands/MarkReviewedCommand.cs`, filling real logic over the stubs the previous
task authored. The design of record is `docs/plans/16-review-attestation-provenance.md` §4 (F2) and §5.
Make the authored `MarkReviewedF2Tests` pass WITHOUT editing them.

Implement:
- **`--evidence <path>`**: run the F2 checks and, on pass, stamp `source: review-artifact` with
  `evidence { reportPath, reportDigest }`:
  - **(a) Plan-binding**: parse the `Plan-Definition-Hash:` line the report embeds and assert it EQUALS
    the marker's `planHash` (the current `PlanDefinitionHash`). Missing/mismatched ⇒ FAIL F2.
  - **(b) Path containment**: `reportPath` must resolve (full-path containment, not substring) under
    `<plan>/state/reviews/`. A `..` escape or out-of-tree path ⇒ FAIL F2.
  - On EITHER failure, DOWNGRADE to `source: bare` — never fabricate `review-artifact`.
  - `reportDigest` = sha256 of the report bytes, newline-normalized (CRLF/CR → LF) the SAME way
    `PlanDefinitionHash` normalizes (F7), symmetric for any re-checking reader.
- **`--source machine`**: stamp `source: machine` (honestly labelled automated stamp).
- **`--reviewer <id>`**: record the self-reported, non-authoritative `actor`.
- **Bare path UNCHANGED**: `mark-reviewed <folder>` with no/invalid evidence stamps `source: bare`,
  clears GR2025 exactly as today, and is NEVER refused (invariant 5). `mark-reviewed` never refuses a
  stamp.

Do NOT change `ReviewMarker.Evaluate` staleness. Do NOT touch the attestation record shapes
(`ReviewAttestation`) beyond consuming them. Reuse the digest normalization from the attestation
implementation where possible.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/MarkReviewedCommand.cs`. Do NOT edit the authored tests — if a test is
genuinely wrong, emit `{"needsHuman": "<why>"}` rather than changing it (an out-of-scope edit fails the
write-scope check and burns a retry).

Completion criteria (your guardrail checks these): `MarkReviewedF2Tests` and the existing
`ReviewMarkerCliTests` in `tests/Guardrails.Integration.Tests` all pass.
