## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/12-author-tests-attempt-usage-tokens`, NOT the stableId
  and NOT the bare folder name. The harness REJECTS a fragment keyed by anything else
  (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/12-author-tests-attempt-usage-tokens": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the failing tests for **DoR §12.4's per-attempt `usage` block** — the tokens axis that lets a
**costless local provider still show volume**, which is the entire reason #230-lite has a token
dimension alongside cost. Task `11-implement-per-tier-spend` aggregates these numbers; nothing
produces them yet.

You write the tests **and** the minimal stub members they compile against. The tests must **compile
and FAIL** — failing is intentional, *not compiling is a mistake to fix*.

- **`tests/Guardrails.Core.Tests/ModelTiering/AttemptUsageTokensTests.cs`**
- namespace `Guardrails.Core.Tests.ModelTiering`
- class **`AttemptUsageTokensTests`** — this exact name; task 13's guardrail and the wave exit gate
  filter on it
- decorated **`[Trait("Category", "TierResolution")]`** at class level (the plan-root baseline
  preflight excludes `Category!=TierResolution`)

**Read `tests/Guardrails.Core.Tests/ClaudeStreamParserTests.cs` first.** It is the existing suite for
the exact seam you are extending, and it already establishes how to feed synthetic JSONL lines to the
parser and assert on the built `ClaudeResult`. Match its idiom; do not invent a new fixture style.

### The stub members to declare

Two files, both in your write scope, and in **both** cases you declare the member and leave it
**unpopulated** — that is what makes your tests red:

1. **`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`** — a `ClaudeUsage` record with
   `int InputTokens` and `int OutputTokens`, and `public ClaudeUsage? Usage { get; init; }` on
   `ClaudeResult`. Do **not** parse it in `Build()`; task 13 does that.
2. **`src/Guardrails.Core/Prompts/PromptInvocation.cs`** — `public PromptUsage? Usage { get; init; }`
   on `PromptResult`, with a `PromptUsage` record (`int InputTokens`, `int OutputTokens`) beside it.
   Model the doc comment on the neighbouring `CostUsd` ("…reported by the runner; null when
   unknown"). Do **not** wire it in `ClaudePromptRunner`; task 13 does that.

`AttemptUsage` and `AttemptRecord.Usage` already exist — task `01-author-tests-journal-tiering-schema`
owns the journal model and has landed them. **Do not redeclare or edit them**;
`src/Guardrails.Core/Journal/JournalModel.cs` is outside your write scope.

## The trap this suite exists to catch — read this before writing a single assertion

The obvious implementation reads `usage.input_tokens` and is **wrong by three orders of magnitude**.
Here is a **real terminal result event**, taken verbatim from this plan's own wave-1 run
(`logs/2026-08-17T05-10-23Z-d2e9/.../01-author-tests-candidate-selection/attempt-1/claude-stream.jsonl`):

```json
{
  "type": "result",
  "num_turns": 55,
  "total_cost_usd": 5.026434999999999,
  "usage": {
    "input_tokens": 3706,
    "cache_creation_input_tokens": 148337,
    "cache_read_input_tokens": 4475820,
    "output_tokens": 51465,
    "output_tokens_details": { "thinking_tokens": 26670 },
    "service_tier": "standard"
  },
  "modelUsage": {
    "claude-opus-5[1m]": {
      "inputTokens": 3706, "outputTokens": 51465,
      "cacheReadInputTokens": 4475820, "cacheCreationInputTokens": 148337,
      "costUSD": 5.026434999999999
    }
  }
}
```

`input_tokens` is **3,706**. The attempt actually consumed **4,627,863** input tokens
(`3706 + 148337 + 4475820`). A naive read understates volume by **~1250×** — and it does so
*silently*, producing a per-tier line that looks plausible and means nothing. Since §9.3 calls
#230-lite "the evidence base for whether the deferred subsystems are ever worth building," a
1250×-wrong evidence base is worse than none.

**So `InputTokens` is the TOTAL input the attempt consumed: `input_tokens` + `cache_creation_input_tokens` + `cache_read_input_tokens`.** Pin that with a test carrying all three fields
and asserting the sum. Note that cache-read tokens are cheap, not free, and they are unambiguously
*volume* — which is what this axis measures.

Note also the two shapes are **redundant and differently-cased**: `usage` (snake_case, canonical) and
`modelUsage.<model>` (camelCase, per-model). Prefer `usage`; a test asserting the parser ignores
`modelUsage` when `usage` is present is worth having, since a future runner version may drop either.

### The behaviours to encode

1. **Parsing the full input total.** A result event with all three input fields yields
   `InputTokens == input_tokens + cache_creation_input_tokens + cache_read_input_tokens`, and
   `OutputTokens == output_tokens`. This is the assertion the whole task exists for.
2. **Partial fields.** An event whose `usage` carries only `input_tokens` and `output_tokens` (no
   cache fields) parses to exactly those numbers — absent is zero, never a crash.
3. **Absent `usage` ⇒ null, not zero.** A result event with no `usage` object at all yields
   `ClaudeResult.Usage == null`. `AttemptUsage { 0, 0 }` is a *claim that nothing was consumed*;
   null is the truthful "the runner did not report." §12.4's absent-not-null discipline applies —
   and task 11's aggregator degrades on null, so a zeroed record would make a costless provider
   report `0 tok` instead of its real volume.
4. **Last result wins.** `ClaudeStreamParser`'s existing contract is that the last result message
   wins (see its class doc). Assert usage follows the same rule, so a stream with two result events
   reports the second's usage.
5. **Malformed usage does not throw.** A `usage` that is a string, a number, or an object whose
   token fields are non-numeric leaves `Usage` null and does not break parsing of `total_cost_usd`
   or `num_turns` on the same line. The parser is fed untrusted runner output on every attempt; a
   throw here fails an otherwise-successful task.
6. **`PromptResult.Usage` is carried, not recomputed.** Assert the mapping from `ClaudeResult.Usage`
   to `PromptResult.Usage` preserves both numbers. Keep this at the type level — do not try to run a
   real `claude` process.

Do NOT populate the parser, map it in `ClaudePromptRunner`, or journal it — that is task 13.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/AttemptUsageTokensTests.cs`,
`src/Guardrails.Core/Prompts/PromptInvocation.cs` and
`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including `ClaudePromptRunner.cs`
(task 13 owns it), `JournalModel.cs` (task 01/02 own it), `ClaudeStreamParserTests.cs`, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
