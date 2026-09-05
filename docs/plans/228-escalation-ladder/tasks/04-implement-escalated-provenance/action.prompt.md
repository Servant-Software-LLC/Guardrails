## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-implement-escalated-provenance`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "04-implement-escalated-provenance": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "04-implement-escalated-provenance": { "someKey": "someValue" },
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

Make every test in `tests/Guardrails.Core.Tests/Escalation/EscalatedProvenanceTests.cs` pass, by
teaching the two mapping sites about the `TierSource.Escalated` value task 03 declared.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/TierProvenance.cs` and `src/Guardrails.Core/Journal/JournalJson.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including the test file, `JournalModel.cs`, other production files, and the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. **Do NOT edit the authored tests.**
Make them pass by fixing the implementation. If the authored tests are genuinely wrong or
incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing them.

Read the test file first — it is the specification. Then:

**Find every site yourself; do not trust a list.** Run
`grep -n "TierSource" src/Guardrails.Core/Journal/JournalJson.cs` and cover **every** hit that
enumerates the enum's values. At authoring time that returned **16** hits, and the two that enumerate
values are `TierSourceToken(TierSource)` — a `switch` whose `_` arm **throws** `JsonException` — and
the nested `TierSourceConverter.Read`, a `switch` on the wire string whose `_` arm throws too. If your
grep returns a different number or different sites, **trust the grep**, cover what it found, and say
so in your summary. Those two throwing default arms are exactly why the tests are red: a fourth enum
value with no arm does not silently mis-serialize, it throws at the moment a run writes `run.json` —
which is the correct design and must stay that way, so **do not replace either `_` arm with a fallback**.

The three changes:

1. `TierProvenance.SourceFor(action, route)` — an ESCALATED route maps to `TierSource.Escalated`. The
   route carries the escalation as **`TierResolution.EscalatedFrom` being non-null**; there is no
   separate boolean, deliberately, because two fields claiming one fact is how they drift.
2. `JournalJson.TierSourceToken` — `TierSource.Escalated` emits the wire token `escalated`.
3. `JournalJson.TierSourceConverter.Read` — the wire token `escalated` reads back as
   `TierSource.Escalated`.

### The one thing that must NOT change

`TierResolution.Climbed` already means *"`Candidates(RequestedTier)` was empty, so the resolver walked
to a stronger rung inside ONE attempt"* — a CAPABILITY fact. It is **not** escalation, and an escalated
route may also have climbed. Nothing you write may key on `Climbed`, widen it, or let it participate
in the `Escalated` decision: a capability-climb attempt and an escalated attempt have to stay
separately readable in the journal, and there is a test in this pair whose only job is to prove they
do. The precedence order in `SourceFor` also has to keep its existing meaning — a `Pinned` route is
still `Override` and a `Legacy` route still records no source at all — because the ladder never
escalates either of those, so an escalated route arriving on one of those branches would be a bug, not
a case to accommodate.
