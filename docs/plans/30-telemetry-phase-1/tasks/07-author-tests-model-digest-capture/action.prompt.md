## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-author-tests-model-digest-capture`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-author-tests-model-digest-capture": { "someKey": "someValue" } }`.
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

## Plan of record

This task authors the failing half of section 3.3 of `docs/plans/30-telemetry-phase-1.md`. **Read
section 3.3 in full**, including its `DECIDED 2026-09-01` block: the maintainer overrode the drafting
agent's "schema field now, capture later" lean and put **both** the row-schema field and the capture
that populates it in Phase 1. A field with no capture behind it would be present and empty exactly
when the first sample it exists to disambiguate arrives. Where this prompt and the plan disagree, the
plan is authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**
Do not touch provenance-on-failed-attempts.

## Task

Author **one** file, and only this one:
`tests/Guardrails.Core.Tests/Prompts/ModelDigestCaptureTests.cs`.

Class **`ModelDigestCaptureTests`**, `public sealed`, carrying `[Trait("Category", "ModelEvidence")]`
on the class — the convention every shipped telemetry suite in this project uses (see
`tests/Guardrails.Core.Tests/Telemetry/TelemetryReportTests.cs:15`).

`PromptResult.ModelDigest` already EXISTS when this task runs —
`04-extend-the-transport-record-shape` declared it on
`src/Guardrails.Core/Prompts/PromptInvocation.cs`. Nothing populates it. **These tests must COMPILE
and FAIL at runtime**; not compiling is a mistake to fix, not the intended TDD red.

### What is under test, and where it lives

`OpenAiCompatPromptRunner` already lifts the wire's `model` field off both response shapes, in two
private static folds whose bodies both read `observedModel ??= ReadString(…, "model")`:

- `ApplyChunk` — one `chat.completion.chunk` from the SSE stream.
- `ApplyWholeCompletion` — a whole `chat.completion` body, the fallback for a server that ignores
  `"stream": true` and answers with one JSON object.

Grep for those two method names in `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` rather
than trusting a line number. `system_fingerprint` is the same shape sitting beside `model` at the same
object level, and it has **zero hits repo-wide today** — nothing reads it anywhere.

### How to drive it — the seam, and why this one

`OpenAiCompatPromptRunner`'s constructor is
`public OpenAiCompatPromptRunner(string name, PromptRunnerConfig config, HttpClient httpClient)`: the
transport is **injected**. Construct the real runner with an `HttpClient` over a stub
`HttpMessageHandler` that returns a scripted body, and assert on the `PromptResult` the runner returns.

That drives the runner's OWN parse path end to end. Do **not** invoke `ApplyChunk` /
`ApplyWholeCompletion` by reflection: they are private static with `ref` parameters, a reflective call
pins a parameter list the implementation task is free to change, and the failure would read as
"method not found" rather than as a missing datum.

Three facts about the runner that will otherwise cost you an attempt — all read at authoring time,
all worth re-checking against the file:

1. **The role gate fires first.** `PromptRunnerKinds.ServesRoles(PromptRunnerKind.OpenAiCompat)` is
   `{ Guardrail, Advisory }` (`src/Guardrails.Core/Model/PromptRunnerConfig.cs`, grep `ServesRoles`).
   An invocation with any other `Role` is refused **before a single byte reaches the wire**, so a test
   using the actor role asserts nothing about parsing. Use `PromptRole.Guardrail`.
2. **`ConfigurationFault` fires second** (grep the method name). The `PromptRunnerConfig` you build
   must carry a non-empty `Endpoint`, a resolvable `model`, and a `ContextTokens` of at least 1, or the
   runner returns an error result without POSTing anything.
3. **The stream/whole-completion branch is decided by the BODY**, in `ReadStreamedTurnAsync`: a line
   starting `data: ` is an SSE frame and goes to `ApplyChunk`; if no SSE frame was seen at all, the
   accumulated body goes to `ApplyWholeCompletion`. That is how one stub handler serves both pinned
   shapes — script the bytes, not a flag.

The real-socket fixture (`tests/Guardrails.Integration.Tests/OpenAiCompat/FakeOpenAiServer.cs`) is in
the **Integration** project and is out of this task's writeScope. Do not copy it here; the seam this
pair is about is JSON field extraction, and the injected `HttpClient` reaches it through the runner's
own code.

### The pinned behaviours

Encode **exactly these four**, each as a `[Fact]` with **exactly the method name given**. The names are
pinned because this task's guardrail binds each behaviour to its method name in the runner's TRX; a
differently-named test reads as an absent behaviour.

| # | behaviour | test method name (VERBATIM) |
|---|---|---|
| 1 | a streamed chunk carrying `system_fingerprint` sets `PromptResult.ModelDigest` to that value | `AStreamedChunkCarryingASystemFingerprint_SetsTheModelDigest` |
| 2 | a whole `chat.completion` body carrying `system_fingerprint` sets it too — the non-streamed fallback is not a second-class path | `AWholeCompletionCarryingASystemFingerprint_SetsTheModelDigest` |
| 3 | a response carrying NO `system_fingerprint` leaves the digest **null** — never `""`, never the model tag, never a hash the harness computed for itself | `AResponseWithNoSystemFingerprint_LeavesTheDigestNull` |
| 4 | the digest and the observed model are TWO facts, not one | `TheDigestIsIndependentOfTheObservedModel` |

**Behaviour 4 must assert both directions in one test**, and the first direction is what makes it red:

- a response carrying a model AND a **different** `system_fingerprint` yields both, each in its own
  member and not swapped (`ObservedModel` is the model tag, `ModelDigest` is the fingerprint); and
- a response carrying a model and **no** fingerprint yields the model with a null digest.

Written that way it fails on today's tree and it catches the two cheapest conflations — assigning the
model tag into the digest, and stopping the model read once a fingerprint is seen. Written as the
second direction alone it would be green before the capture exists and would certify nothing. Use two
clearly different literal values so no assertion can pass by the two sources agreeing.

**Behaviour 3 is the one test in this file that will be GREEN when you finish, and that is correct.**
Nothing populates the digest today, so a correct test for the null case passes on the stub tree. Its
guardrail declares it as an exemption and asserts only that it RAN. Do not "fix" it into failing, and
do not mark it `[Fact(Skip=…)]` — a skipped exemption is no coverage at all. It exists because the
implementation task rewrites both fold sites, and this is the check that stops a `""` or a fabricated
placeholder being introduced there.

### The Claude runner is OUT of scope for capture, and that is a provider fact

The Claude CLI stream carries a model **tag** and no fingerprint: `ClaudeStreamParser` extracts
`num_turns`, usage, cost and `model`, and there is nothing else on the wire to read. **A Claude row's
digest is permanently null** — that is the provider's disclosure surface, not a gap in this plan and
not a bug to be worked around. Do not author a Claude-side capture test, do not synthesize a digest
from the model tag, and do not assert that a Claude result carries one.

Assert only on `PromptResult` members. Do NOT assert on `system_fingerprint` appearing in the stream
log, in a transcript, or in any rendered surface: those are different tasks' deliverables, and pinning
them here would fail this pair for a change it does not own.

**Do NOT implement the capture.** `src/Guardrails.Core/Prompts/OpenAiCompatPromptRunner.cs` is outside
this task's writeScope and belongs to `08-capture-the-model-digest-from-the-wire`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Prompts/ModelDigestCaptureTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside that path — including changes to other
production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.
