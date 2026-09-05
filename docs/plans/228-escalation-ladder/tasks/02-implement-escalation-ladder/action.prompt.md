## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-implement-escalation-ladder`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-escalation-ladder": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "02-implement-escalation-ladder": { "someKey": "someValue" },
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

Fill real logic over the stubs in `src/Guardrails.Core/Prompts/EscalationLadder.cs` so that every test
in `tests/Guardrails.Core.Tests/Escalation/EscalationLadderTests.cs` passes.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/EscalationLadder.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the test file, other production
files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
**Do NOT edit the authored tests.** Make them pass by fixing the implementation. If the authored tests
are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than
changing them.

Read `tests/Guardrails.Core.Tests/Escalation/EscalationLadderTests.cs` first — it is the specification.
Then read `src/Guardrails.Core/Prompts/TierResolver.cs`.

**Call the existing resolver. Do not write a second one.**
`TierResolver.SelectCandidate(RunConfig, string)` already walks *rungs at or above* the requested one,
already applies the costly floor, already orders candidates by ascending strength, and already returns
a `NoRoute` result when nothing serves. Escalation is *calling that resolver with a higher rung* —
nothing more. Grep the file for `SelectCandidate` before you start (at authoring time that returned
**8** hits, all inside `TierResolver.cs` itself; if your grep returns something different, trust the
grep and say so in your summary). A second candidate-filtering path here would be the D22a divergence
the resolver's own comments spend three paragraphs forbidding, and it would silently drift the day the
costly predicate moves.

The rules the tests encode:

- `NextRung(servedRung)` — the rung immediately ABOVE `servedRung` on `ActionTiers.All` (ascending), or
  `null` when `servedRung` is the top rung, is null, or is not on the ladder.
- `Apply(config, route, escalations)`:
  - `escalations <= 0` ⇒ `route` unchanged. **This is the byte-identical case for every first attempt
    and every plan that never fails a guardrail — get it exactly right before anything else.**
  - `route` has no rung to climb from — `Pinned`, `Legacy`, `NoRoute`, or `Tier` is null ⇒ `route`
    unchanged.
  - Otherwise, once per escalation: `NextRung` of the rung currently SERVED, resolved through
    `SelectCandidate`. A resolution with a runner becomes the current one; a null `NextRung` or a
    `NoRoute` resolution **stops the climb and keeps what you have** — no exception, no invented route.
  - `EscalatedFrom` on the returned resolution is the rung the ORIGINAL `route` served, set once and
    preserved across every step. If no step succeeded, return the original route with `EscalatedFrom`
    still **null** — nothing escalated, so nothing may claim it did.

**Do not touch `Climbed`.** It already means something else — *"`Candidates(RequestedTier)` was empty,
so the resolver walked to a stronger rung inside ONE attempt"*, a capability fact. An escalated attempt
and a capability climb are different facts about how an attempt reached a rung, and the journal has to
tell them apart. `EscalatedFrom` carries escalation; `Climbed` keeps carrying what it already carries.
An escalated attempt whose resolution also climbed sets both, independently.
