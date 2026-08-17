## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/03-author-tests-unavailability-classification`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/03-author-tests-unavailability-classification": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

**DoR §6.3 hands this wave an explicit open question:** *does the shipped `PromptFailureKind`
quarantine already catch a bare DNS / connection-refused / TLS shape, or does it need an additive
`Unavailable` classification?* Your job is to answer it **in executable form** — one test per shape,
so the answer is a passing or failing assertion rather than a paragraph.

Author a NEW file:

- **`tests/Guardrails.Core.Tests/ModelTiering/ConnectionUnavailabilityClassificationTests.cs`**
- namespace `Guardrails.Core.Tests.ModelTiering`
- class **`ConnectionUnavailabilityClassificationTests`** — this exact name; the implementation task's
  guardrail and the wave exit gate both filter on it
- decorated **`[Trait("Category", "TierResolution")]`** at class level. Load-bearing: the plan-root
  baseline preflight excludes `Category!=TierResolution`, and there is an EXISTING, currently-green
  `ClaudeSignalClassifierTests` in this project — do **not** add your red tests to that class, or you
  would turn a green baseline class red.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/ConnectionUnavailabilityClassificationTests.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside that
path — including `ClaudeSignalClassifier.cs` (the next task owns it), the existing
`ClaudeSignalClassifierTests.cs`, or the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The ruling these tests encode

§6.3: **a connection-level failure at launch is `Transient`/*unavailable* and rides the SHIPPED #115
transient-pause machinery** — bounded exponential backoff, **no retry-budget consumption**. **No new
probe enum is introduced in v1** — do not invent an `Unavailable` member of `PromptFailureKind` and
do not test for one. The expected classification for every shape below is
**`PromptFailureKind.Transient`**.

Read `src/Guardrails.Core/Prompts/ClaudeSignalClassifier.cs` first (it is `internal`, and
`Guardrails.Core.Tests` has `InternalsVisibleTo`, so you can call it directly). Some shapes are
already covered by its `TransientPhrases` list — **assert those too**. A test that passes today is
not waste here: it is the half of the answer that says "already covered", and it pins the coverage
against a future edit that removes a phrase.

### Shapes to cover (one `[Theory]` row or `[Fact]` each)

Use **realistic verbatim error text**, not the phrase the classifier greps for — the point is that a
real message classifies, not that a keyword does.

1. **DNS resolution failure**, several real spellings:
   `getaddrinfo ENOTFOUND api.anthropic.com`, `Could not resolve host: api.anthropic.com`,
   `Name or service not known`, `EAI_AGAIN`.
2. **Connection refused / reset** — e.g. `connect ECONNREFUSED 127.0.0.1:11434`,
   `read ECONNRESET`. (Expect these to be covered already; assert anyway.)
3. **TLS / handshake timeout** — e.g. `TLS handshake timeout`,
   `unable to get local issuer certificate`, `SSL routines::unexpected eof while reading`.
4. **A missing CLI at launch** — the OS message a failed process start yields:
   Windows `The system cannot find the file specified` / `is not recognized as an internal or
   external command`, POSIX `No such file or directory` / `command not found`.
5. **A negative control, and it is the most important test in the file.** Text that must stay
   **`PromptFailureKind.Error`** — an ordinary agent failure that merely *mentions* a network word,
   e.g. `The test asserted the connection string was refused by the parser` or
   `Compilation failed: cannot resolve symbol 'HostName'`. Without this, the implementer can make
   every other row pass by classifying anything containing "connect" or "resolve" as Transient, and
   a genuine logic failure would then loop on the pause machinery instead of consuming its budget
   and surfacing. State the DoR's own rule in a comment: *a miss is conservative — an unrecognised
   error yields `Error`, never a false `Transient` that could loop.*
6. **The reset hint stays intact**: `ExtractResetHint` still returns the hint from a rate-limit
   message and returns null for a DNS failure (there is no reset time to report).

Give each test a name that says which shape it covers (`DnsResolutionFailure_IsTransient`,
`OrdinaryFailureMentioningConnection_StaysError`, …) — the names are the readable form of the answer.

Some of these will PASS on the current code and some will FAIL. **That is the answer**, and it is the
correct outcome for this task: the guardrail requires at least one genuine RED, which is what says
the quarantine needs the additive extension. Do not implement the extension — that is task 04.
