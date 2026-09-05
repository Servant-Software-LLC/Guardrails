## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-author-tests-retry-loop-escalation`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "05-author-tests-retry-loop-escalation": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "05-author-tests-retry-loop-escalation": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Author the FAILING tests that prove the escalation ladder is actually WIRED into the retry loop — the
one proof in this plan that runs the production path rather than a unit of it.

**Write only this file:**
`tests/Guardrails.Core.Tests/Escalation/RetryLoopEscalationTests.cs` — class name
**`RetryLoopEscalationTests`**, namespace `Guardrails.Core.Tests.Escalation`, class-level attribute
`[Trait("Category", "EscalationLadder")]`. Both are pinned: this plan's guardrails select this pair
with `--filter 'Category=EscalationLadder&FullyQualifiedName~RetryLoopEscalationTests'`.
**Do NOT use `[Trait("Category", "TierResolution")]`** — that trait already exists and belongs to the
shipped tier-resolution suite.

You write **no stub**. `TaskExecutor` already exists, so this file compiles against today's tree and is
red at RUNTIME, not at compile time — the same shape as
`tests/Guardrails.Core.Tests/Execution/RouteWarmthTests.cs`, whose own header explains it. Nothing in
the retry loop escalates yet; task `06-implement-retry-loop-escalation` wires it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Escalation/RetryLoopEscalationTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including any production file,
neighbouring test files, and the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Drive the REAL loop, and assert an effect only the production path emits

Read `tests/Guardrails.Core.Tests/Execution/ModelDigestProvenanceTests.cs` first and reuse its fixture
shape: it runs a **real serial run** (`maxParallelism: 1`, no worktree provider) of a single PROMPT
task through the real `PlanLoader`, `TaskExecutor` and `Scheduler`, with
`PromptRunnerRegistry.Build(config, factory)` handing back a stub `IPromptRunner` instead of spawning
the `claude` CLI. That fixture is the harness for all five behaviours here; do not invent a second one.

Three rules about what you must assert, and they are the whole value of this file:

- **Assert on the JOURNAL, not on the stub.** *"The seam was called"* is not an assertion — a fake
  satisfies it whether or not anything is wired. Read `journal.Document`'s attempt records and assert
  on `Provenance.Tier`, `Provenance.TierSource` and `Provenance.EscalatedFrom`: those bytes exist only
  because `TaskExecutor` resolved a route, threaded it into `BuildProvenance`, and recorded it.
- **And record what model the stub runner was actually invoked with** (`PromptInvocation` carries it).
  This is REQUIRED, not optional, and it has its own row in the table below. It is the only thing
  standing between the journal and a genuine silent failure: a ladder applied in `BuildProvenance`
  instead of at `ResolveRoute` makes the journal say *"escalated to hard"* while the runner is handed
  the easy model — every journal assertion above passes, the operator reads a green escalation, and the
  stronger model never ran. Capture the invocation your stub receives and assert the ESCALATED attempt
  was invoked with the STRONGER block's model. That is an assertion about production behaviour, not a
  call count.
- **Make attempt 1 fail its GUARDRAILS, not its action.** The trigger is `guardrail-failed` and nothing
  else. Give the fixture task a script guardrail that exits non-zero on the first attempt (the existing
  serial-run fixtures already write real guardrail scripts into a temp plan folder — follow them).

### The registry the fixture needs

A two-rung registry: one `promptRunners` block with a `routing` block serving `easy`, another serving
`hard` (or `medium`), and the task tagged `action.tier: easy`. Build it the way
`tests/Guardrails.Core.Tests/ModelTiering/TierResolverCandidateSelectionTests.cs` builds registries —
read that file and reuse its shape rather than spelling a registry a second way.

### The behaviours, and the EXACT test method name each must carry

| behaviour | test method name | on this tree |
|---|---|---|
| a guardrail-failed attempt makes the NEXT attempt resolve one rung stronger | `AGuardrailFailedAttempt_MakesTheNextAttemptResolveOneRungStronger` | RED |
| the escalated attempt records `escalated` + the rung it started from | `TheEscalatedAttempt_RecordsTierSourceEscalatedAndTheRungItClimbedFrom` | RED |
| the escalated attempt is INVOKED with the stronger block's model | `TheEscalatedAttempt_IsInvokedWithTheStrongerBlocksModel` | RED |
| a TIMEOUT does not escalate — **but a guardrail failure in the same fixture does** | `ATimeoutAttempt_DoesNotEscalateTheNextAttempt` | RED |
| a single-runner plan resolves identically on every attempt — **but a two-rung plan escalates** | `OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst` | RED |

Fold one more assertion into the FIRST test rather than giving it a row of its own: the escalated
attempt still receives the retry `feedbackPath` from the attempt before it. `feedbackPath` is passed to
the next attempt independently of which model runs it, so escalating must not trade away the #179
feedback loop — assert the escalated attempt got BOTH a stronger route and the feedback.

**Every row is RED, and the last two are red because each carries a CONTRAST ARM.** Their negative
half — *"a timeout does not escalate"*, *"a single-runner plan does not escalate"* — is true on this
tree and would be satisfied by `Assert.True(true)`; a hollow body would pass, and task 06 could then
escalate on a timeout with every guardrail in this plan green. Pairing each negative with the positive
it must be distinguished FROM makes the whole test red before task 06 and green after, which is what a
census can actually check. Write both halves in ONE test method, under the name pinned above:

- `ATimeoutAttempt_DoesNotEscalateTheNextAttempt` — escalation is triggered by `guardrail-failed`
  **only**, never by a timeout, a max-turns stop, a transient pause or a permission wall. A timeout is
  evidence the model produced *slow* work, not *wrong* work, and the harness already has separate
  counters (`timeoutRetries`, `maxTurnsRetries`) and separate remedies for it. Drive a stub runner
  whose first attempt TIMES OUT and assert attempt 2's provenance is on the SAME rung with `TierSource`
  unchanged and `EscalatedFrom` absent — **and in the same fixture with attempt 1 failing its
  guardrails instead, attempt 2 IS one rung stronger.** The negative half is what catches an over-broad
  trigger in task 06; the contrast arm is what proves the negative half is measuring anything at all.
- `OnASingleRunnerPlan_TheSecondAttemptResolvesTheSameRouteAsTheFirst` — a config with ONE
  `promptRunners` block and **no `routing` block at all** has nowhere to climb, and must degrade to
  today's behaviour **silently**: no error, no escalation, the same route on every attempt. This is
  every plan in existence today, so a regression here breaks everyone — **and the same plan with a
  two-rung registry DOES escalate.** Without that second half the test cannot tell "correctly declined
  to climb" from "the ladder is not wired at all", which is precisely the state of this tree.

**The tests MUST COMPILE and FAIL — all five rows.** Failing is the point. NOT compiling is a mistake
to fix. Do NOT wire the retry loop — task 06 does that.
