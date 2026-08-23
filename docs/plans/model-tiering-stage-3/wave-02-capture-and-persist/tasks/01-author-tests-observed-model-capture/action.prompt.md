## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-02-capture-and-persist/01-author-tests-observed-model-capture": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
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

Author the **failing tests** — and only the minimal stub declarations they need to COMPILE — for the
first half of #349: reading the model the Claude CLI **echoes** off its own `stream-json` output.

### Why this exists (one paragraph, then the specifics)

`AttemptProvenance.Model` today records the model the harness **asked for** — the resolved route, or the
`"(cli default)"` sentinel when nothing named one. The CLI already tells us what actually **ran**: its
stream opens with `{"type":"system","subtype":"init","model":"claude-…"}`, and the harness already tees
that stream to `claude-stream.jsonl`. `ClaudeStreamParser.Feed` returns early on every line whose
`type != "result"`, throwing the init model away. **That one discard is the entire gap.** We parse the
echo. We never force `--model` to find out — forcing one would pin the zero-setup user who deliberately
passes nothing, and would record the model we *requested*, which is the weaker fact.

### Files to write — and nothing else

1. **The test file: `tests/Guardrails.Core.Tests/ModelTiering/ObservedModelCaptureTests.cs`**, declaring
   exactly one test class named **`ObservedModelCaptureTests`**. The class name is load-bearing: both
   this task's guardrails and the next task's select on `FullyQualifiedName~ObservedModelCaptureTests`.
   Give it `[Trait("Category", "TierResolution")]` at class level, matching every sibling in that folder.

2. **Stub declaration A — `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs`:** add
   `public string? Model { get; init; }` to the **`ClaudeResult`** record, with an XML doc comment. Declare
   it and nothing more — do **not** touch `Feed`, `Build`, or any `TryGet*` helper. Task 02 populates it.

3. **Stub declaration B — `src/Guardrails.Core/Prompts/PromptInvocation.cs`:** add
   `public string? ObservedModel { get; init; }` to the **`PromptResult`** record, with an XML doc comment.
   Declare it and nothing more — do **not** touch `ClaudePromptRunner`. Task 02 populates it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/ObservedModelCaptureTests.cs`,
`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` and `src/Guardrails.Core/Prompts/PromptInvocation.cs`
(the two stub files). After this task completes, the harness runs a `git diff` check and rejects any edit
outside these paths — including changes to other production files, neighbouring test files, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and stop.

### The behaviours, and the EXACT test-method name each must carry

The next guardrail reads a TRX result file and requires each of these four method names to be present and
`Failed`. Name them exactly:

| test method | behaviour |
|---|---|
| `InitLine_Model_IsCaptured` | a stream whose `{"type":"system","subtype":"init",…}` line carries `"model":"claude-sonnet-5-20260101"` yields `ClaudeResult.Model == "claude-sonnet-5-20260101"` |
| `ResultLine_Model_IsTheFallback_WhenInitCarriedNone` | an init line with **no** `model`, and a terminal `result` line carrying `"model":"claude-haiku-4-5-20251001"`, yields that value — the fallback the brief specifies |
| `InitModel_Wins_OverADifferingResultLineModel` | **both** lines carry a model and they DIFFER — init's value wins. Without this, "init, falling back to result" and "result, falling back to init" are indistinguishable, and the two orderings disagree exactly when a session switched models mid-run |
| `ClaudePromptRunner_CarriesTheObservedModel_OffARealStream` | the **REAL** `ClaudePromptRunner`, driven against a stub CLI, yields `PromptResult.ObservedModel` equal to the model in the stream it emitted |

Author **two more** tests, deliberately GREEN from the start — they are the regression half, and they are
NOT in the census above:

- `NoModelAnywhere_YieldsNull_NotAnEmptyString` — a stream with no `model` on any line yields `null`, never
  `""`. Absent must stay absent: a `""` would read as "the runner reported a model and it was blank".
- `PromptResultObservedModel_DefaultsToNull_AndRoundTripsWhatIsAssigned` — the declaration guard on
  `PromptResult`.

### How to write them — follow the sibling that already did this

`tests/Guardrails.Core.Tests/ModelTiering/AttemptUsageTokensTests.cs` is the same shape of change (#475:
mine a field off the stream, carry it onto `PromptResult`) and it is the pattern to copy — read it first.
Specifically:

- The parse tests feed synthetic JSONL to `ClaudeStreamParser.ParseAll`, exactly as
  `tests/Guardrails.Core.Tests/ClaudeStreamParserTests.cs` does.
- The **carry** test drives the REAL `ClaudePromptRunner` against a tiny OS-picked fake CLI — the pattern in
  `tests/Guardrails.Core.Tests/ClaudePromptRunnerStreamLogTests.cs`. No `claude` binary is involved. This
  matters: a mapping asserted only on hand-constructed objects goes green against a runner that carries
  nothing, which is precisely how `AttemptRecord.Usage` shipped structurally dead (#475). Fake the CHILD
  PROCESS underneath the real runner; never fake the runner.

### The red must COMPILE

Failing is the point; **not compiling is a mistake to fix**. With the two stub declarations above, the test
project compiles and every behavioural test fails against a member nothing populates. Do NOT implement the
parse or the carry — that is task 02's whole deliverable, and its `writeScope` targets those files.
