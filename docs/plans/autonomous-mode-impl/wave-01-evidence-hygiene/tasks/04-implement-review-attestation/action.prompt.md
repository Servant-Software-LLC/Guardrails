## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/04-implement-review-attestation` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Implement the review-marker **attestation** block (issue #366) by filling REAL logic over the skeleton
stubs the previous task authored in `src/Guardrails.Core/Review/`. The design of record is
`docs/plans/16-review-attestation-provenance.md` §4/§5/§7 (read it first, especially the "Field rules for
the harness author"). Make the authored `ReviewAttestationTests` pass WITHOUT editing them.

Implement, in `src/Guardrails.Core/Review/ReviewAttestation.cs` and
`src/Guardrails.Core/Review/ReviewMarker.cs`:

- The `ReviewAttestation` record: `source` (`review-artifact | bare | machine`; read-time-only `legacy`),
  self-reported `tool`, optional non-authoritative `actor`, and `evidence { reportPath, reportDigest }`
  present ONLY for `source: review-artifact`.
- `ReviewMarker` read-tolerance + classification: a v1 marker (no `attestation`) reads as `legacy`; a
  malformed `attestation` block deserializes to null → `legacy`, never throwing (mirror the existing
  tolerant `Read`). Readers NEVER gate on the integer `version` — classify by the presence of the
  `attestation` block + its `source`.
- Serialization: bump the WRITTEN `version` to `2`; the new optional members use
  `JsonIgnoreCondition.WhenWritingNull` (a `bare` stamp emits just `attestation.source` + `tool`, no
  `"actor": null` / `"evidence": null` noise); keep the required top-three fields' current `Never`
  serialization for byte-exact back-compat.
- The report-digest helper: sha256 over the report bytes after the SAME newline normalization
  `PlanDefinitionHash` uses (CRLF/CR → LF), symmetric for any reader that re-checks it (F7).

`ReviewMarker.Evaluate` staleness (the `planHash` compare, §13/GR2025) is UNCHANGED — do not touch it.
Do NOT change the CLI (mark-reviewed is a later task). Fill logic over the stubs; do not rename the
public shapes the tests already reference.

**Scope boundary (harness-enforced):** Write only under `src/Guardrails.Core/Review/`. Do NOT edit the
authored tests — if a test is genuinely wrong or incompatible, emit `{"needsHuman": "<why>"}` to the
state-out path rather than changing it (an out-of-scope edit to a test file fails the write-scope check
and burns a retry). Make the tests pass by fixing the implementation.

Completion criteria (your guardrail checks these): the `ReviewAttestationTests` and the existing
`ReviewMarkerTests` in `tests/Guardrails.Core.Tests` all pass.
