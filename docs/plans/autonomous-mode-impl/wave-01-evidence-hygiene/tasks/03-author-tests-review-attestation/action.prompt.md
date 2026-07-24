## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/03-author-tests-review-attestation` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit tests (TDD red) for the review-marker **attestation** block (issue #366),
plus the MINIMAL stubs needed to compile. The design of record is
`docs/plans/16-review-attestation-provenance.md` §4/§5/§7 (read it first). The repo uses **xUnit
(xunit.v3)** — mirror the existing test project `tests/Guardrails.Core.Tests` (see
`ReviewMarkerTests.cs` for the house style).

Write exactly two artifacts (both in your scope):

1. **The test file** `tests/Guardrails.Core.Tests/ReviewAttestationTests.cs` — tests that encode the
   §366 behaviour, all of which must FAIL against the stubs below:
   - **Round-trip**: a v2 marker with `attestation { source, tool, actor, evidence { reportPath,
     reportDigest } }` serializes and deserializes preserving every field (`source: review-artifact`).
   - **Back-compat / legacy**: a v1 marker JSON (no `attestation` block) deserializes with the block
     absent and CLASSIFIES as `legacy`; a v2 marker with a malformed `attestation` block reads
     tolerantly (block null → `legacy`), never throwing (mirror `ReviewMarker.Read`'s tolerance).
   - **Reader never gates on `version`**: classification keys on the presence of the `attestation`
     block + its `source`, never on the integer `version`.
   - **`WhenWritingNull`**: a `bare` stamp serializes with just `attestation.source` (+ `tool`) and
     emits NO `"actor": null` / `"evidence": null` noise; the required top-three fields keep their
     current `Never` serialization for byte-exact back-compat.
   - **Symmetric digest**: `reportDigest` = sha256 of the report bytes after the SAME newline
     normalization `PlanDefinitionHash` uses (CRLF/CR → LF); a CRLF and an LF checkout of the same
     report digest identically.
   Reference `docs/plans/16-review-attestation-provenance.md` §4 for the exact field rules.

2. **The minimal stubs** so the test project COMPILES but the behaviour is genuinely absent:
   - `src/Guardrails.Core/Review/ReviewAttestation.cs` — the `ReviewAttestation` record (+ an evidence
     nested record and the `source` representation) as the DATA declaration, AND any behavioural member
     the tests call (the evidence-class CLASSIFICATION of a marker, and the report-digest helper) as a
     stub that `throw new NotImplementedException();`.
   - Add the OPTIONAL `Attestation` property to `src/Guardrails.Core/Review/ReviewMarker.cs` so the
     round-trip tests compile; leave the read-tolerant classification + `WhenWritingNull` + `version → 2`
     write behaviour as throwing/absent stubs (the implementation task fills them).

   The tests MUST COMPILE and FAIL against these stubs — failing is intentional (TDD red); NOT compiling
   is a mistake to fix. Ensure at least the classification and digest tests exercise the throwing stubs
   so `dotnet test` exits non-zero. Do NOT implement the real behaviour.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ReviewAttestationTests.cs`,
`src/Guardrails.Core/Review/ReviewAttestation.cs`, and `src/Guardrails.Core/Review/ReviewMarker.cs`
(the stub files). After this task completes, the harness runs a `git diff` check and rejects any edit
outside these paths — including other production files, neighbouring test files, or a `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by
a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to
the state-out path and stop.
