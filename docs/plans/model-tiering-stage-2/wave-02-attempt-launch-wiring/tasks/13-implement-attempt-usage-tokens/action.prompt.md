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
`12-author-tests-attempt-usage-tokens` pass. **Do NOT edit those tests.** If they are genuinely wrong
or incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing them.

**`docs/plans/17-model-tiering.md` §12.4 is the design of record and wins over any paraphrase here.**

The token axis exists because a **costless local provider** reports no `total_cost_usd`, so tokens
are the *only* evidence of what it did. Task 11's per-tier line is its first consumer.

**TWO hops, both in your write scope: PARSE, then CARRY.** A third hop — journalling the value onto
`AttemptRecord` — is deliberately NOT part of this task; §3 below explains why, and you must not
attempt it.

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

### 3. Journalling is NOT your job — and this is why

DoR §12.4 also wants the datum on `AttemptRecord.Usage`, and an earlier revision of this task asked
for it. **That hop is severed and no edit inside your write scope can reach it.** `AttemptJournaler`
does not build an `AttemptRecord` from a `PromptResult` — it builds one from an **`ActionRun`**, and
`ActionRun` carries `CostUsd` and no usage sibling:

```
ActionRunner.cs:347   internal sealed record ActionRun
ActionRunner.cs:352       public decimal? CostUsd { get; init; }    <- no Usage
ActionRunner.cs:439       CostUsd = result.CostUsd,                 <- where the carry would go
AttemptJournaler.cs:81        CostUsd = action.CostUsd,             <- reads ActionRun, not PromptResult
```

Landing it needs `ActionRunner.cs`, `RunReport.cs` (`PendingAttempt` has no `Usage` either) and
`Scheduler.cs` (`RecordSucceededSettle` builds a **second** `AttemptRecord` that bypasses the
journaler entirely) — none of which is yours. A previous attempt of this task found exactly this and
correctly halted rather than satisfying a guardrail with a token that journals nothing.

**So do not touch `AttemptJournaler.cs`, and do not add a `Usage` member anywhere in
`src/Guardrails.Core/Execution/`.** Your guardrail no longer asks for it. Getting the value onto
`PromptResult` correctly is the whole deliverable; a separate task owns the trip from there to
`run.json`.

### The regression risk this task carries

Your two files sit on the path every prompt attempt takes. `ClaudeStreamParser` is fed **untrusted
runner output on every attempt** — a malformed `usage` that throws would fail an otherwise-successful
task — and `ClaudePromptRunner` builds the result every attempt depends on. Add your member beside
the existing `CostUsd` handling; do not restructure either method around it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` and
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside those paths — including the test file,
`PromptInvocation.cs` (task 12 owns the declaration), `AttemptJournaler.cs` / `ActionRunner.cs` /
`Scheduler.cs` (out of scope by design, see above), `JournalModel.cs` (task 01/02), or the `.csproj`.
An out-of-scope edit fails the task immediately and consumes a retry.

Nothing here changes routing or cost accounting. If you find yourself reading `promptRunners`,
`TierResolution` or `JournalCost`, stop: this task moves token counts from the runner's raw output
onto `PromptResult`, and nothing else.
