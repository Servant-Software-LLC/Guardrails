# model-tiering-stage-1 — a COMPLETED plan folder, repaired after its run

This folder ran to green on 2026-08-14 (`logs/2026-08-14T11-25-42Z-6dcb/`) and **will not run again**.
It is kept because a folder under `docs/plans/` is where the next author looks to see what a real
breakdown looks like — which is exactly why it could not be left as it was.

**As committed after the run, it modelled two defects the doctrine now forbids.** Both were found by
work that came *after* this plan shipped, and both are now fixed here. Read this note before treating
any script in this folder as a pattern to copy; the guardrails below are current doctrine, the run logs
under `logs/` and `state/` are the historical record and were **not** rewritten.

## What was corrected

### 1. Task-level test filters keyed on the bare plan-wide trait (#455, tracked as #463)

Five task-level test guardrails filtered on `--filter "Category=ModelTieringStage1"` alone — no class
term, no zero-match guard. A task-level filter must name **the test class that task pair owns**; the
plan-wide trait alone belongs in exactly one place, the `!=` exclusion in the baseline preflight.

The bare trait breaks in two opposite ways, and this plan hit both:

- **The forward (`tests-pass`) half is a loud deadlock.** Task 06's check selected every Stage 1 test,
  including deliberately-red tests only a *non-ancestor* task could turn green — so the task could not
  go green until work it does not depend on had merged. `validate` and `graph --check` both pass: the
  cycle is between a task and a **sibling's test corpus**, which no DAG check models.
- **The inverse (`tests-fail-on-stubs`) half is a silent tautology, and is the worse mode.** The check
  wants *some* matching test red; keyed on the trait, **any** sibling's intended-red tests satisfied it.
  It went green three times in the real run without proving anything about its own pair's tests.

Which one bites is decided by **merge timing, not by correctness** — tasks 02 and 04 were narrowed
during the run only because they were the two that happened to halt. That is why the fix covers every
task-level test filter in the folder, not the ones that failed.

| file | corrected filter |
|---|---|
| `tasks/01-…/guardrails/02-tests-fail-on-stubs.ps1` | `Category=ModelTieringStage1&FullyQualifiedName~PromptRunnerSchemaTests` |
| `tasks/03-…/guardrails/02-tests-fail-on-stubs.ps1` | `…&FullyQualifiedName~RegistryKindDispatchTests` |
| `tasks/05-…/guardrails/02-tests-fail-on-stubs.ps1` | `…&FullyQualifiedName~ActionTierTests` |
| `tasks/06-…/guardrails/01-tests-pass.ps1` | `…&FullyQualifiedName~ActionTierTests` |
| `tasks/07-…/guardrails/01-proof-tests-pass.ps1` | `…&(FullyQualifiedName~NoRoutingGoldenTests\|FullyQualifiedName~NoRoutingNegativeAssertionTests)` |

Each class name is the one its own task's action prompt pins (task 07's two were read from the
committed test files — see "Residual" below). `~ActionTierTests` and not `~ActionTier`:
`FullyQualifiedName~` is a **substring** match and the shorter spelling would also select the sibling
`ActionTierProvenanceTests` class.

Every narrowed filter now ships with the corrected **zero-match guard** — a `--filter` that matches
nothing, or is malformed, exits **0**. The guard sums the **executed** count (`Passed:` + `Failed:`),
**not** `Total:`, which also counts `[Skip]ped` tests and so can read `>= 1` over a run that executed
none; and `$env:DOTNET_CLI_UI_LANGUAGE = 'en'` is pinned first, because the summary line the guard reads
is localized. Guard ordering differs by polarity **on purpose**: exit-code-first on a forward check
(so a crashed test host is not misdiagnosed as a bad class name), guard-first on an inverse one (so a
host that never started is not certified as "TDD red").

### 2. `dotnet test -v q` with a dead #179 failure-detail re-emit (#462)

Six guardrails passed `-v q` to `dotnet test` **and then grepped for the failure block**. `-v q`
*suppresses* that block — `Error Message:`, the assertion line, `Expected:`, `Actual:`, `Stack Trace:` —
leaving only `[FAIL] <name>`. So the re-emit matched nothing, and the retry feedback (which is only the
**tail** of a failed guardrail's stdout — the whole reason #179 requires re-emitting at the END) showed
*what* failed and never *why*. The next attempt retried blind. The guardrail still failed correctly and
still looked right on the page; only the diagnostic value was gone.

`-v q` is right on `dotnet build` — it strips restore chatter and leaves the errors — which is precisely
why reaching for it on `dotnet test` is such an easy mistake. The `dotnet build` guardrails in this
folder (`0N-tests-build.ps1`, `guardrails/01-solution-builds.ps1`) still carry `-v q` and are correct.

Fixed in: `tasks/02-…/01-tests-pass.ps1`, `tasks/04-…/01-tests-pass.ps1`, `tasks/06-…/01-tests-pass.ps1`,
`tasks/07-…/01-proof-tests-pass.ps1` — **plus two files #462's list did not name**, carrying the identical
defect: `guardrails/02-all-tests-pass.ps1` (the terminal whole-suite gate) and
`preflights/01-baseline-core-tests-green.ps1` (the baseline preflight, where the flag was hidden on the
far side of a PowerShell backtick line-continuation). Both are now `-v q`-free, re-emit correctly, and
carry the culture pin and executed-count guard.

This conjunction — a `dotnet test` line with `-v q` **and** a grep for the block it deletes — is now
rejected mechanically by `guardrails validate` as **GR2037 entry `#462`**
(`.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json`).

## Notes for anyone reading the folder

- **Nothing was at risk.** The plan is settled; every task succeeded and none will re-run. The review
  doctrine that a guardrail fix must be scoped to tasks that have **not** yet succeeded does not bite
  here — there is nothing to re-verify and no rewind cost.
- **These edits change each affected task's `TaskDefinitionHash`.** That is accepted for the same
  reason: a resume or re-run of this plan is not a scenario. Do not read the changed hashes as drift.
- **`validate` now emits an advisory `GR2025`** — the plan has changed since its recorded
  `/guardrails-review`. Expected; the folder is not being re-run, so it has not been re-marked.
- **The run logs were not rewritten.** `logs/` and `state/reviews/` still show the plan as it actually
  executed, including the four attempts task 02 burned on the deadlock. That history is the evidence
  for both issues and is more useful intact.

## Residual

Task 07's action prompt names the folder its tests go in but **not** the class names, so the two classes
its filter selects (`NoRoutingGoldenTests`, `NoRoutingNegativeAssertionTests`) were confirmed from the
committed test files rather than from the prompt. Current doctrine requires the prompt to pin the exact
file and class, so that the guardrail and the prompt agree *by construction* rather than by luck — the
filter here is correct, but it was not authorable at breakdown time.

## References

- `.claude/skills/plan-breakdown/references/stacks/dotnet.md` §4.2 (the #179 re-emit) and §4.3 (filter
  scoping, the zero-match guard, and the `-v q` rule) — the doctrine both fixes conform to.
- Issues #455 (the doctrine fix), #462 (the dead re-emit), #463 (this folder's leftover bare filters),
  #179 (why the detail must reach the retry tail), #248 (verify a pattern against genuine tool output).
