## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/08-author-tests-run-outcome-wiring` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

Author the FAILING xUnit integration test (TDD red) for the **run-outcome wiring** of a
`proceed-unreviewed` run (issue #361 Phase 4, doc 12 §1 delivery-gating + §5.2 Option P + §7.1 exit code).
This test drives the REAL CLI run path (the #120 composition-root discipline — never a hand-injected
policy); the wiring is NOT in place yet, so the test must FAIL against current code
(`tests-fail-on-current-code`). It goes GREEN only at the sink task (10), once the finalize flip (task 09),
the review-gate resolution (task 05), and the exit code (task 10) are all wired.

The repo uses **xUnit (xunit.v3)**. **Mirror the shipped CLI-run integration test that asserts a run's exit
code + RunReport** (grep `tests/Guardrails.Integration.Tests` for the shipped `Cli_RunWithUnresolvedEscalation`
fact in `SchedulerEscalationWiringTests`, and `WaveExecutionRunTests`, for how they build a real waved
fixture plan, drive `guardrails run` / the `RunCommand` entrypoint, capture the exit code + stdout, and use
the existing Windows-safe temp-git fixture — do NOT hand-roll a temp-git helper, #116).

**Anchor on these REAL shipped symbols (grep them — durable markers, not line numbers):**
- The autonomy config surface: `autonomyPolicy: auto` + an `autonomy` block with
  `gateThresholds: { "review-gate": "proceed-unreviewed" }` (the Option-P opt-in; `ReviewGateDecision` /
  `AutonomyConfig` in `src/Guardrails.Core/Model/AutonomyConfig.cs`). This is NOT a GR2040 error at a
  non-critical dial (grep `ViolatesCompoundConfig` — GR2040 fires only with `critical`).
- `RunReport.WhollyGreenButUndelivered` (`src/Guardrails.Core/Execution/RunReport.cs`) — the shipped
  delivery-suppressed signal; a suppressed-delivery run sets it true (do NOT auto-deliver machine-decided
  work).
- `ReviewMarker.PathFor(planDirectory)` (`src/Guardrails.Core/Review/ReviewMarker.cs`) ⇒
  `state/guardrails-review.json` — the marker the harness must NEVER write.

Write ONE artifact (in scope): `tests/Guardrails.Integration.Tests/RunOutcomeWiringTests.cs` — a script-only
(or fake-runner) fixture waved plan under `autonomyPolicy: auto` + `review-gate: proceed-unreviewed` where a
wave proceeds unreviewed, driven end-to-end. Assert the FOUR facts (each must FAIL against current code):

1. **Delivery is suppressed.** The run resolves `mergeOnSuccess` OFF because a `proceeded-unreviewed`
   decision was recorded — assert `RunReport.WhollyGreenButUndelivered` is true (the verified work stays on
   the plan branch, NOT auto-delivered).
2. **Distinct non-zero exit.** The CLI run returns the distinct code — assert the exit code equals the
   NUMERIC **5** (do NOT reference `ExitCodes.ProceededUnreviewed` — that constant is added by task 10;
   asserting the literal keeps this test compiling now, mirroring how the shipped `Cli_RunWithUnresolvedEscalation`
   asserts its numeric code). It must be distinct from 0 (green), 2 (needs-human), and 4 (EscalationsPending).
3. **Flagged "ran with N unreviewed waves."** Assert the run surfaces the unreviewed-wave count — via the
   captured CLI stdout containing an "unreviewed" wave warning (a stdout-string assertion keeps the test
   compiling against current symbols; do NOT reference a not-yet-added RunReport field).
4. **No forged review marker.** After the run, assert `ReviewMarker.PathFor(planDirectory)`
   (`state/guardrails-review.json`) does NOT exist — the harness never self-attests (doc 12 §5 floor 3).

The test MUST COMPILE (against shipped symbols + numeric literals) and FAIL against current code. If a
real end-to-end `proceed-unreviewed` run cannot be set up deterministically from a fixture (e.g. reaching
the unreviewed-wave boundary needs the JIT breakdown path), do NOT guess a fragile setup — write
`{"needsHuman": "how should the integration test reach a real proceed-unreviewed run — a pre-authored unreviewed wave with review-gate:proceed-unreviewed, or the JIT BreakdownComplete path?"}` to the state-out
path and stop.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&`, no pipe, no `2>&1 |`, not the
PowerShell tool (issue #374): `dotnet test tests/Guardrails.Integration.Tests --filter
"FullyQualifiedName~RunOutcomeWiringTests"`. Do NOT run the full unfiltered
`dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop
`outside.txt`/`src/output.txt` into the worktree → write-scope false-positive rollback, #253).

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/RunOutcomeWiringTests.cs`. After this task the harness runs a
`git diff` check and rejects any edit outside this path — including `Scheduler.cs`/`RunReport.cs` (task 09),
`ExitCodes.cs`/`RunCommand.cs` (task 10), or the shipped symbol files. An out-of-scope edit fails the task
immediately and consumes a retry. If a shipped type is missing a member you need, do NOT edit it — write
`{"needsHuman": "<what is missing>"}` and stop.
