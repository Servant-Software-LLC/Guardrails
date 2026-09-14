## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `22-author-tests-entry-baseline-kind`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "22-author-tests-entry-baseline-kind": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Author failing tests for the wave entry gate distinguishing the **two baseline kinds** —
design 39 §1c(1).

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveEntryBaselineKindTests.cs`
**Test class:** `WaveEntryBaselineKindTests`

Every test carries `[Trait("Category", "WaveDelivery")]`.

**Why this task exists, and why it is not optional.** §1c calls this *"the only place in this
design where the harness must grow a new distinction"*, and it is the control the whole
post-delivery refresh rests on: the design says a refresh is safe **because** the next wave
re-verifies its own baseline. Today it does not. `Scheduler.RunWaveEntryGateAsync` documents
*"Skip-once: a passed entry marker for this wave is not re-evaluated on resume"* — for **both**
kinds. So after a refresh pulls the user's own commits into the run's tree, the next wave would
pass over a tree it never checked. The design's own word for that is *"silently"*.

The first breakdown of this plan shipped the refresh (tasks 14/15) and **not** this. That is why
the pair exists and why it is ordered BEFORE task 14.

**The distinction:**
- A **positive** baseline ("these existing tests pass") must be **RE-EVALUATED** after a
  delivery, because the tree it asserts over has changed underneath it.
- A **negative** baseline ("this behaviour is not yet present") keeps **skip-once**: it is
  monotone, so re-running it after the wave that satisfied it would false-RED.

**There is no stub file, and here is why** — the same reason tasks 14 and 16 have none. The
production type already exists: `RunWaveEntryGateAsync` is there, and what is missing is the
DISTINCTION, not a type. **Grep `Scheduler.cs` for `RunWaveEntryGateAsync`** rather than
trusting a line number; this file is large and several tasks in this plan edit it before you.

**Pin these behaviours to these EXACT method names:**

- `APositiveBaselineEntryCheck_ReEvaluatesAfterADelivery`
- `ANegativeBaselineEntryCheck_KeepsSkipOnce` — the monotone half. Getting this wrong in the
  other direction is a false-RED on every resume, which is why both directions are pinned.
- `AnEntryGateAfterARefresh_RunsAgainstTheRefreshedTree` — the end-to-end one: the point of the
  whole distinction is that the tree a refresh produced is actually checked.

`ANegativeBaselineEntryCheck_KeepsSkipOnce` is declared EXEMPT from the red census: today's
`RunWaveEntryGateAsync` already skips every passed entry marker whatever its kind, so a correct test of
the monotone half is green on arrival. Write it to assert the guarantee, not to fail. It must still
exist, and task 23's forward census requires all three Passed.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The other two tests MUST COMPILE and FAIL. Do NOT implement the distinction.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveEntryBaselineKindTests.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
these paths. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
