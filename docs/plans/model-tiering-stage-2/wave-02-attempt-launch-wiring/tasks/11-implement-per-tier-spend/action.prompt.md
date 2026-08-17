## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/11-implement-per-tier-spend`, NOT the stableId and
  NOT the bare folder name. The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/11-implement-per-tier-spend": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the `JournalTierSpend` stub so the tests authored by
`10-author-tests-per-tier-spend` pass, and render the result in the run summary. **Do NOT edit those
tests.** If they are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the
state-out path rather than changing them.

**`docs/plans/17-model-tiering.md` §9.3 is the design of record and wins over any paraphrase here.**

This is **#230-lite**, and §9.3 is unusually direct about why it matters: it is *"the single most
important v1 deliverable after the routing itself — the evidence base for whether the deferred
subsystems (probes, ladder, steering) are ever worth building."* The whole point is to turn "is a
stronger model worth what it costs" from an argument into a number.

### 1. The aggregation (`src/Guardrails.Core/Journal/JournalTierSpend.cs`)

Pure aggregation over the persisted `JournalDocument` — no new data collection. Group every attempt
by `provenance.tier`, summing `costUsd` and, where present, `usage.inputTokens` /
`usage.outputTokens`. Model it on its sibling `JournalCost` (same file layout, same "null when there
is nothing to report" discipline), and keep the two consistent: `JournalCost.Total` remains the total
and must not change.

- **Ascending rung order** — `ActionTiers.All`'s order, not dictionary order, so the line does not
  shuffle between runs.
- **Degrade to tokens-only** where no cost was reported: print the volume and omit the money, never
  `$0.00`. A costless local provider still shows volume; that is why the tokens axis exists.
- **Overhead spend belongs to no rung.** `document.OverheadCostUsd` (the overwatcher, the AI-merge
  worker, the needs-human triage) is folded into `JournalCost.Total` and must NOT land in a rung's
  bucket.
- §9.3's worked shape: `hard: 42k tok / $3.12 · easy: 180k tok / $0`.

### 2. The CLI (`src/Guardrails.Cli/Commands/RunCommand.cs`)

`PrintTotalCost` already reads the freshly-persisted journal and prints
`Total prompt cost: $<total>` — and already omits itself when no attempt recorded a cost, "so
deterministic-only plans stay noise-free". Extend that function (grep for `PrintTotalCost`; **do not
rely on a line number**) in the same spirit: print the per-tier line only when there is one to print.

```
if (JournalTierSpend.Summarize(document) is { } perTier) { output.WriteLine(...); }
```

### Invariant 7 — the rule this task is most likely to break

§9.3 is **stricter** than "add a per-tier section". On a **tiering-inactive run** — no attempt
resolved through routing, so nothing carries `provenance.tier` — the summary prints **exactly today's
cost line**: no per-tier section, no header, and **no `untiered:` bucket**. A mixed run reports the
tiered rungs and stays silent about the rest.

That rule is why the aggregator returns a **nullable/empty-typed** result rather than a string the
caller has to test: the CLI's suppression must be a pattern-match on "there is nothing to report",
not a `string.IsNullOrEmpty` on a rendered line that some future edit makes non-empty. A naive
aggregator emitting an empty or `untiered:` section on every existing user's run is the single most
likely way this wave breaks Invariant 7 — and every existing single-model user is downstream of it.

Note the two halves are checked separately: the authored tests pin the AGGREGATOR's suppression, and
a guardrail pins the CLI's — the call site must be guarded by the pattern-match, because an
aggregator that correctly returns null still prints a blank line if the caller does not check.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Journal/JournalTierSpend.cs` and `src/Guardrails.Cli/Commands/RunCommand.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including the test file, `JournalCost.cs`, the journal model, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

Nothing here changes routing. If you find yourself reading `promptRunners` or `TierResolution`, stop:
this task reads the JOURNAL and only the journal.
