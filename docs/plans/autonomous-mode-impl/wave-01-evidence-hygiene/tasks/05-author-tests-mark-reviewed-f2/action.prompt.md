## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-01-evidence-hygiene/05-author-tests-mark-reviewed-f2` — NOT the stableId. (This task publishes
  nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING integration tests (TDD red) for the `mark-reviewed` **F2 stamp-time hygiene checks**
(issue #366), plus the MINIMAL stubs to compile. The design of record is
`docs/plans/16-review-attestation-provenance.md` §4 (F2) and §5 (the `mark-reviewed` change). The repo
uses **xUnit (xunit.v3)**. Invoke the CLI in-process the way the existing
`tests/Guardrails.Integration.Tests/ReviewMarkerCliTests.cs` does (build a `RootCommand`, add
`MarkReviewedCommand.Create(io)` with a `StringConsoleIo` double, `root.Parse(args).InvokeAsync()`).

Write two artifacts (both in scope):

1. **The test file** `tests/Guardrails.Integration.Tests/MarkReviewedF2Tests.cs` — tests that must FAIL
   against the stubs:
   - **Bare unchanged**: `mark-reviewed <folder>` (no evidence) stamps `source: bare`, clears GR2025,
     and is NEVER refused (the shipped manual-confirmation flow — assert the marker is written and
     `attestation.source == "bare"`).
   - **F2 pass → review-artifact**: with a report written under `<plan>/state/reviews/…md` embedding a
     `Plan-Definition-Hash:` line equal to the plan's current `PlanDefinitionHash` (compute it via
     `Guardrails.Core.Journal.PlanDefinitionHash.Compute` on the loaded plan), `mark-reviewed --evidence
     <report>` stamps `source: review-artifact` with `evidence { reportPath, reportDigest }`.
   - **F2 fail → downgrade to bare**: a report whose embedded hash MISMATCHES the current planHash, and
     separately a `reportPath` that resolves OUTSIDE `<plan>/state/reviews/` (a `..` escape), each
     downgrade to `source: bare` (never a fabricated `review-artifact`).
   - **`--source machine`**: stamps `source: machine`.
   - **`--reviewer <id>`**: records the self-reported `actor`.
   - **Symmetric digest**: the SAME report bytes with CRLF vs LF line endings produce the SAME
     `reportDigest` (newline-normalized like `PlanDefinitionHash`).

2. **The minimal stubs** in `src/Guardrails.Cli/Commands/MarkReviewedCommand.cs`: add the `--evidence`,
   `--source`, and `--reviewer` options so the tests compile, and stub the F2 / evidence-class code path
   to `throw new NotImplementedException();`. KEEP the existing BARE stamp path working (do not break the
   shipped behaviour) — only the new review-artifact/F2 path is stubbed. The tests MUST COMPILE and FAIL
   (failing is intentional; not compiling is a mistake). Do NOT implement F2.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/MarkReviewedF2Tests.cs` and
`src/Guardrails.Cli/Commands/MarkReviewedCommand.cs`. After this task the harness runs a `git diff`
check and rejects any edit outside these paths — including other production files, neighbouring tests,
or a `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a
compile error from a missing symbol in another file (e.g. a missing attestation member), do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
