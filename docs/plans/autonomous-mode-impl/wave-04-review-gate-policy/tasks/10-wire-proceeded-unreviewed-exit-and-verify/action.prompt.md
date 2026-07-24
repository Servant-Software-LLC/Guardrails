## Harness contract (do not remove)
- Read input state from the JSON file at the `GUARDRAILS_STATE_IN` path provided in the
  appended sections; write ONLY new/changed keys as a JSON object to `GUARDRAILS_STATE_OUT`.
- Write everything you publish under this task's WAVE-QUALIFIED id as the single top-level key —
  `wave-04-review-gate-policy/10-wire-proceeded-unreviewed-exit-and-verify` — NOT the stableId. (This task
  publishes nothing to state — the rule is documented for a later editor.)
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures;
  do not start over.
- If you cannot proceed without a human decision, write `{"needsHuman": "<question>"}` to the
  state-out path and stop.

## Task

**Wire the distinct proceeded-unreviewed exit code + verify the composition root — PART 2 of 2, the DAG
SINK** (issue #361 Phase 4; the #120 lesson). Task 09 wired the Finalize delivery flip + the RunReport
unreviewed-wave surface; the review-gate resolution (task 05) records the `proceeded-unreviewed` decision.
YOUR slice surfaces the distinct exit code AND is where the full drive-the-real-CLI-run proof (all four
facts of the authored `RunOutcomeWiringTests`) finally goes green — because only now is EVERYTHING wired.

**Architecture caveat (#203) — verify before you build on it.** Cite DURABLE markers (grep the symbols),
never line numbers. Confirm each still holds in the materialized tree (which now includes tasks 05 + 09):
- `ExitCodes` (`src/Guardrails.Cli/ExitCodes.cs`) — grep `public const int`. Today: `Success = 0`,
  `HarnessError = 1`, `TaskFailed = 2`, `Cancelled = 3`, `EscalationsPending = 4`. Add
  **`ProceededUnreviewed = 5`** (the next free value) with an XML-doc comment, mirroring the existing
  constants' style. (The design left the exact value open — "confirm"; the plan-of-record pins 5, the next
  free distinct value, so a firstmate consumer can tell a proceed-unreviewed run apart from green (0),
  needs-human (2), and an answer-required halt (4). If a human review decides otherwise, they edit here.)
- `RunCommand` (`src/Guardrails.Cli/Commands/RunCommand.cs`) — grep how it maps a `RunReport` to an exit
  code today (it returns `ExitCodes.TaskFailed`/`EscalationsPending`/`Cancelled` for those cases). Return
  `ExitCodes.ProceededUnreviewed` when the run recorded a `proceeded-unreviewed` decision (read the
  RunReport unreviewed-wave surface task 09 added — grep the field). Also RENDER the permanent
  "ran with N unreviewed wave(s)" warning to the console (the run is indelibly flagged). Verify the exact
  RunReport accessor before branching on it; if the report exposes no unreviewed-wave signal, write
  `{"needsHuman": "how does RunReport surface the proceeded-unreviewed wave count?"}` and stop rather than
  inventing one.

Precedence note: an answer-required escalation halt already exits `EscalationsPending = 4`; a
proceed-unreviewed run that is otherwise green exits `ProceededUnreviewed = 5`. Do not let one mask the
other — a run with unresolved escalations is 4; a green-but-unreviewed run is 5.

Do NOT change the Scheduler / RunReport / RunOutcomePolicy logic (tasks 03/05/09 own that). This task is
the exit-code constant + the RunCommand mapping/rendering only. Design of record:
`docs/plans/12-autonomous-mode.md` §7.1 (exit code) + §5.2 (Option P) + §5 floor 3.

**In-attempt regression check (issue #253 + #374 — do NOT skip, and run it PLAINLY):** run ONLY your
targeted filter, via the **Bash tool**, as a **plain** command — no `&`, no pipe, no `2>&1 |`, not the
PowerShell tool (issue #374):

    dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~RunOutcomeWiringTests"

ALL FOUR facts must PASS now — delivery suppressed (WhollyGreenButUndelivered), the CLI exits 5,
the run flags N unreviewed waves, and no review marker was written. Do NOT run the full unfiltered
`dotnet test tests/Guardrails.Integration.Tests` (its fixture-leaking classes drop `outside.txt`/
`src/output.txt` into the worktree → write-scope false-positive rollback, #253). If a
`git checkout <ref> -- <path>` salvage recovery is blocked by the permission wall (#374), do NOT fight it —
re-author the file directly with `Write` instead.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/ExitCodes.cs` and
`src/Guardrails.Cli/Commands/RunCommand.cs`. The harness runs a post-action `git diff` membership check
and REJECTS any edit outside these two paths — including `Scheduler.cs`/`RunReport.cs` (task 09),
`RunOutcomePolicy.cs` (task 03), or the authored test. An out-of-scope edit fails the task immediately and
consumes a retry. If the exit code genuinely needs a change to the Scheduler/report (e.g. the report
exposes no unreviewed-wave signal), do NOT edit it — write `{"needsHuman": "<what is missing>"}` and stop.

Completion criteria (your guardrails check these): `ExitCodes.cs` declares `ProceededUnreviewed = 5`,
`RunCommand.cs` returns `ExitCodes.ProceededUnreviewed`, and ALL FOUR `RunOutcomeWiringTests` facts pass —
the drive-the-real-CLI-run composition-root proof, including the distinct exit code and the never-forged
review marker.
