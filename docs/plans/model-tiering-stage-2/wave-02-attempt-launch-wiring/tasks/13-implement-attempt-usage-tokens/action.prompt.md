## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/13-implement-attempt-usage-tokens`, NOT the stableId
  and NOT the bare folder name. The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/13-implement-attempt-usage-tokens": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Fill real logic over the `Usage` stub members so the tests authored by
`12-author-tests-attempt-usage-tokens` pass, and carry the numbers all the way to the journal.
**Do NOT edit those tests.** If they are genuinely wrong or incompatible, write
`{"needsHuman": "<why>"}` to the state-out path rather than changing them.

**`docs/plans/17-model-tiering.md` §12.4 is the design of record and wins over any paraphrase here.**

Without this task, `AttemptRecord.Usage` is a schema member nothing ever populates and task 11's
per-tier line reports cost only — which defeats the reason the token axis exists at all: a **costless
local provider** reports no `total_cost_usd`, so tokens are the *only* evidence of what it did.

There are three hops, and all three must land or the datum never reaches the journal.

### 1. Parse — `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`

Populate `ClaudeResult.Usage` from the terminal result event's `usage` object, following the parser's
existing conventions exactly: `TryGetDecimal`/`TryGetInt`-style tolerant readers, and the documented
**last result message wins** rule (`_costUsd`/`_numTurns` already use `?? _existing`).

**`InputTokens` is the TOTAL input consumed:**

```
input_tokens + cache_creation_input_tokens + cache_read_input_tokens
```

This is not a judgement call — on real output from this plan's own wave-1 run, `input_tokens` is
**3,706** against an actual **4,627,863**. Reading `input_tokens` alone understates volume by
**~1250×**, silently. `OutputTokens` is `output_tokens` (do not add
`output_tokens_details.thinking_tokens`; the real payload shows thinking tokens are already *inside*
`output_tokens`, so adding them double-counts).

Missing sub-fields are **zero**, but a missing/unparseable `usage` object is **null** — absent, not
`{ 0, 0 }`. That distinction is load-bearing: a zeroed record is a claim that nothing was consumed,
and task 11's aggregator keys its tokens-only degradation on null.

The parser is fed **untrusted runner output on every attempt**. A `usage` that is a string, a number,
or an object with non-numeric fields must leave `Usage` null *and* must not disturb `total_cost_usd`
or `num_turns` on the same line. Never let it throw.

### 2. Carry — `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`

Map `ClaudeResult.Usage` onto `PromptResult.Usage` at the site that already builds the result — grep
for `CostUsd = result.CostUsd` (**do not rely on a line number**) and add the member alongside, in
the same style. A straight carry: no recomputation, no defaulting to zero.

`PromptResult.Usage` and the `PromptUsage` record already exist — task 12 declared them — and
`PromptInvocation.cs` is deliberately **outside your write scope**: you assign the member, you do not
define it. If its shape is genuinely wrong for what the parser produces, write
`{"needsHuman": "<what is wrong with the PromptUsage shape>"}` rather than editing it, because task
12's tests compile against that declaration and changing it under them is how a pair silently stops
proving anything.

### 3. Journal — `src/Guardrails.Core/Execution/AttemptJournaler.cs`

Populate `AttemptRecord.Usage` from the prompt result. `AttemptUsage` and `AttemptRecord.Usage`
already exist — task `01`/`02` own `JournalModel.cs` and have landed them; that file is **outside
your write scope** and you must not edit it. If the member you need is genuinely missing, write
`{"needsHuman": "<what is missing>"}` rather than declaring it yourself.

Follow the shape `Provenance` already uses: `[JsonIgnore(WhenWritingNull)]`, so a **deterministic
(script) attempt, a runner that reported no usage, and every older journal all simply OMIT the key**.
Adding `"usage": null` to every script attempt's record would be new noise in `run.json` for users
who never opted into any of this.

### The regression risk this task carries

`AttemptJournaler` is also edited by task `08-settle-no-route-as-needs-human`, and you meet its work
for the first time at the wave union gate. A merged journaler that records usage but **no longer
records the `no-route` outcome** compiles cleanly and passes your own filtered tests — the wave's
`03-wave2-unit-suites-green` and `02-stage2-conformance-green` gates are what catch it. Add your
field; do not restructure the method around it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`,
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` and
`src/Guardrails.Core/Execution/AttemptJournaler.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including the test file,
`PromptInvocation.cs` (task 12 owns the declaration), `JournalModel.cs` (task 01/02),
`ClaudeStreamParserTests.cs`, `TaskExecutor.cs`, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry.

Nothing here changes routing or cost accounting. If you find yourself reading `promptRunners`,
`TierResolution` or `JournalCost`, stop: this task moves token counts from the runner's output to the
journal, and nothing else.
