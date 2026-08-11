## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in the appended
  sections; write ONLY new/changed keys as a JSON object to GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level key —
  { "02-implement-stream-model-capture": { ... } } — NOT the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those specific failures; do not start over.
- If you cannot proceed without a human decision, write {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make the `ClaudeStreamParserModelTests` pass by capturing the model the CLI echoes, and flow it out
through `PromptResult`.

1. **`ClaudeStreamParser.cs`** — in `Feed`, capture `model` from the CLI's own stream lines. Today `Feed`
   returns early on any line whose type is not `result` (grep for the result-type guard), discarding the
   system/init line that carries `model`. Read `model` from a `system` (subtype `init`) line when present,
   AND from the terminal `result` line as a fallback; populate `ClaudeResult.Model`. Do NOT force or
   require a model — a stream with no model line must leave `Model` null and must not throw (keep the
   parser null-tolerant; test 3 enforces this).
2. **`PromptInvocation.cs`** — add `public string? ResolvedModel {{ get; init; }}` to the `PromptResult`
   record (CLI-observed actual model; null for a script attempt or when the stream reported none).
3. **`ClaudePromptRunner.cs`** — set `PromptResult.ResolvedModel` from the parser result `ClaudeResult.Model`.

Do NOT edit the test file. If the authored tests are genuinely wrong, write {{"needsHuman": "<why>"}} and stop.