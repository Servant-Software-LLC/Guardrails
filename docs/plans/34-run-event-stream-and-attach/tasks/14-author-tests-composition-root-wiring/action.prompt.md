## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in (e.g.
  `14-author-tests-composition-root-wiring`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "14-author-tests-composition-root-wiring": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

This is the guardrail that decides whether any of the rest is real. Both projections can be fully built,
fully unit-tested and completely GREEN while nothing constructs them at the production composition root -
in which case the feature is reachable only from xUnit and is inert from the CLI. That failure has
recurred three times in this repo at exactly this kind of seam, so it gets its own test class.

Create `tests/Guardrails.Integration.Tests/RunEvents/RunCommandObserverWiringTests.cs`, class
**`RunCommandObserverWiringTests`**, every test carrying `[Trait("Category", "RunEvents")]`.

Drive the REAL `BuildObserverChain` extracted in task 13. **Do NOT construct the projections yourself and
pass them in** - injecting the thing under test makes the assertion pass even when production never wires
it, which is the whole defect.

Pin these test METHOD names:

- `BuildObserverChain_ConstructsTheEventsProjection`
- `BuildObserverChain_ConstructsTheObserverProjection`
- `BuildObserverChain_WiresBothProjections_InTheNoUiBranch` - the `--no-ui` branch is the one that
  matters most here: an unattended run is exactly the configuration this feature exists to serve
- `BuildObserverChain_WiresBothProjections_InTheLiveUiBranch`
- `BuildObserverChain_StillWiresTheExistingObservers` - the CONTRAST case: the chain keeps the
  observers it already had, so "wired" is a real assertion and not merely "something was constructed"

These MUST COMPILE and FAIL - task 13 extracted the seam without changing what it builds, so nothing
constructs the projections yet. Do NOT edit `RunCommand.cs`; that is task 15.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/RunEvents/RunCommandObserverWiringTests.cs`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
