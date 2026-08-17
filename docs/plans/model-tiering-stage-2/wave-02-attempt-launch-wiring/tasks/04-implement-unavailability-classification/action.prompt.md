## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/04-implement-unavailability-classification`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/04-implement-unavailability-classification": { "answer": "..." } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Make `tests/Guardrails.Core.Tests/ModelTiering/ConnectionUnavailabilityClassificationTests.cs` —
authored by `03-author-tests-unavailability-classification` — pass. **Do NOT edit those tests.** If
they are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path
rather than changing them; an out-of-scope edit to a test file fails the write-scope check and burns
a retry.

**`docs/plans/17-model-tiering.md` §6.3 is the design of record and wins over any paraphrase here.**

This task **answers the DoR §6.3 open question** — *does the shipped `PromptFailureKind` quarantine
already catch a bare DNS/refused shape, or does it need an additive classification?* — by making the
measurement in the authored tests come out true.

### The rules, in order of how easy they are to get wrong

1. **NO new probe enum, and no new `PromptFailureKind` member.** §6.3: *"No new probe enum is
   introduced in v1."* A connection-level failure is **`PromptFailureKind.Transient`**, which already
   routes to the shipped #115 transient-pause machinery — bounded exponential backoff, honouring a
   parsed reset hint, bounded by `transientPauseBudgetSeconds`, and **no retry-budget consumption**.
   You are widening the SIGNAL SET the existing classification recognises, nothing else. Do not touch
   the pause machinery, the backoff, or the budget.
2. **Stay inside the vendor quarantine.** `ClaudeSignalClassifier` is documented as *the SOLE home of
   the fragile vendor error-string matching*. Every new signal goes there — a phrase in
   `TransientPhrases` or a compiled `Regex` beside its siblings — never into the harness, and never
   into `ProcessRunner` (which is shared with script actions and guardrails and is out of scope).
3. **A miss must stay conservative.** The existing doc comment states it: *an UNrecognized error
   yields `Error` — never a false `Transient` that could loop.* The authored negative control pins
   this. Match on **discriminating** shapes (`getaddrinfo`, `ENOTFOUND`, `EAI_AGAIN`,
   `could not resolve host`, `name or service not known`, `tls handshake timeout`, …) rather than on
   bare words like `connect` or `resolve`, which appear in ordinary compiler and assertion output.
   If you find yourself adding a phrase that would match a test-failure message, that is the signal
   to make it longer, not to delete the control.
4. **The missing-CLI shape needs a catch, not just a phrase.** `ProcessRunner` calls
   `process.Start()` with no `try`, so a missing executable throws `Win32Exception` out through
   `ClaudePromptRunner.RunAsync` before any text is ever classified. Handle it **in
   `ClaudePromptRunner`** (your scope): catch the launch failure around the `_processRunner.RunAsync`
   call, run its message through `ClaudeSignalClassifier.Classify`, and return a `PromptResult` with
   `Completed = false`, that `FailureKind`, and a `Summary` naming the command that could not be
   launched. Do **not** widen the catch to swallow unrelated exceptions, and do not edit
   `ProcessRunner.cs` — it is out of scope and shared.
   *(Note: `guardrails validate`'s GR2009 PATH probe already warns about a missing runner command at
   validate time; this is the runtime residual, the same relationship `no-route` has to GR2048.)*
5. **Keep the phrase list small, explicit and pinned.** Its doc comment says so, and the authored
   tests are the pin. Adding a regex is fine where a family of spellings shares a stem; adding twenty
   free-text phrases is how this file rots.

### Record the answer

Write the answer to the §6.3 open question as an XML doc comment on the new signal set in
`ClaudeSignalClassifier` — one short paragraph stating **which shapes were already covered**
(`connection refused` / `connection reset` / `connection error` were) and **which needed the additive
extension** (the DNS, TLS-handshake and missing-executable families were not), so a later reader does
not re-open a settled question. Also publish it to state so the SSOT task (14) can document it
without re-deriving it:

`{ "wave-02-attempt-launch-wiring/04-implement-unavailability-classification": { "alreadyCovered": ["..."], "added": ["..."], "newEnumMember": false } }`

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudeSignalClassifier.cs` and
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including `ProcessRunner.cs`,
`PromptFailureKind.cs`, the test file, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

There is an EXISTING `tests/Guardrails.Core.Tests/ClaudeSignalClassifierTests.cs` that pins today's
behaviour. It must stay green: your change is purely additive to the recognised signal set.
