## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `17-implement-branchmoved-halt`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "17-implement-branchmoved-halt": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `BranchMovedHaltTests` pass. Design 39 §1c and §4 (round 4).

**Every refused wave delivery halts the run at that wave** — a `BranchMoved`, `Conflict`,
`DirtyWorkingTree` or `HookRejected` refusal — under `WaveHaltKind.DeliveryRefused`. A refusal reaches
the barrier by one of two routes, and the halt must cover both:

- **The trial could not be built.** `CreateTrialDelivery` returned a `TrialDelivery` whose `Refusal` is
  `Conflict` or `HookRejected`; no gate ran and nothing was promoted. The detail is `trial.RefusalDetail`.
- **The promotion refused.** `PromoteTrialDelivery` returned `BranchMoved` or `DirtyWorkingTree`, with the
  detail on the provider's `LastMergeOnSuccessDetail`. `BranchMoved` here covers both a switched checkout
  and the user's branch advancing after the trial was built, so the detail, not the token, says which.

Then:

- The halt headline reads `Wave '<dir>' delivery REFUSED (<outcome token>): <detail>`, where the token
  is `JournalJson.DeliveryOutcomeToken(outcome)` and the detail comes from whichever route refused.
- `BranchMoved` has two causes under one token, `branch-moved`, and the provider's detail says which. Pick
  the remedy for the halt's `Detail` from that detail:
  - **A switched checkout:** the detail reads `run started on '<branch>'; HEAD is now '<other>'`. The halt's
    `Detail` says to `check out '<branch>' again`, then resume.
  - **A branch that advanced after the trial was built:** the detail reads
    `'<branch>' moved from <sha10> to <sha10> after the trial was built`. The halt's `Detail` says to
    resume, because the next trial includes the new commits. It must not say to check anything out.
- An `AlreadyDelivered` trial is not a refusal: task 29 settles it as `delivered`. Never halt on it.
- **Record the halt as a decision too (review 2026-09-13).** At the halt, append one `DecisionEntry`
  through `_journal.RecordDecision` and raise `_observer.DecisionRecorded`, as the Scheduler's other
  decision sites do. Set:
  - `Boundary = "wave"`;
  - `Policy = AutonomyPolicies.Token(_plan.Config.AutonomyPolicy)`, as `ExecutedDefinitionDivergenceDecision` sets it;
  - `Decision = DecisionTokens.Halted`;
  - `Gate = "delivery-refused"`;
  - `Subject` and `Wave`, both the refused wave's directory;
  - `Headline`, the halt headline.

  Build it in `Scheduler.cs`, and do not edit `DecisionEntry.cs`. `halted` changes no delivery or exit
  code: `RunOutcomePolicy` suppresses delivery only on `proceeded-best-guess` and `proceeded-unreviewed`.
- Later waves do not run.
- The refused wave writes NO completed marker and NO completed status; it settles needs-human, so a
  resume re-attempts its delivery at that wave.
- Task 29 already records the refusal as `refused` on `waves.<dir>.delivered`. Do not write a second
  delivery record; this task owns the HALT and its decision entry.
- Do not add a `RunHaltKind`, and do not write run.json's `halt` section — it is scoped to gates
  (#432). Do not touch `JournalJson.cs`, `RunHalt.cs` or the CLI.

Do not unwind deliveries that already landed, and do not check the pinned branch back out — #588's
safe direction is to refuse and leave the operator's checkout untouched.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs` and
`src/Guardrails.Core/Execution/RunReport.cs`. After this task completes, the harness runs a `git diff`
membership check and rejects any edit outside these paths. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another file,
do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green. The one exception is task 28's `WaveDeliveryWiringTests`, which are already green on your base: this task's guardrail re-runs them, because you edit the same barrier they drive, so keep them green.
