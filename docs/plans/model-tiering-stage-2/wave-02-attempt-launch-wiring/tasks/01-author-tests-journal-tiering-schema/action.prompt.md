## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/01-author-tests-journal-tiering-schema`, NOT the
  stableId and NOT the bare folder name. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "wave-02-attempt-launch-wiring/01-author-tests-journal-tiering-schema": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Author the failing tests for the **DoR §12.4 journal schema delta** in a NEW file:

- **`tests/Guardrails.Core.Tests/ModelTiering/JournalTieringSchemaTests.cs`**
- namespace `Guardrails.Core.Tests.ModelTiering`
- class **`JournalTieringSchemaTests`** — this exact name; every downstream guardrail filters on it
- decorated **`[Trait("Category", "TierResolution")]`** at class level. This is load-bearing: the
  plan-root baseline preflight excludes `Category!=TierResolution`, so a missing trait would make
  this plan's own intentionally-red tests part of a later baseline.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/JournalTieringSchemaTests.cs`,
`src/Guardrails.Core/Journal/AttemptOutcome.cs` and `src/Guardrails.Core/Journal/JournalModel.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these
paths — including `JournalJson.cs` (the next task owns it), other production files, or the `.csproj`.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error
caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### The declarations you ALSO write (so the tests COMPILE)

You write the tests **and** the minimal model declarations they compile against. The tests must
**compile and FAIL** — failing is intentional, *not compiling is a mistake to fix*.

In **`src/Guardrails.Core/Journal/AttemptOutcome.cs`**:

- add the enum member **`NoRoute`**, XML-documented as DoR §12.4's new attempt outcome: *resolution
  found zero registered candidate blocks at or above the task's rung — a runtime config gap GR2048
  normally prevents. Settles needs-human with "register a provider serving tier ≥ R" feedback.*
  Add it at the END of the enum (an inserted member would renumber the others).

In **`src/Guardrails.Core/Journal/JournalModel.cs`**:

- a new **`public enum TierSource { Task, PlanDefault, Override }`** with XML docs naming the single
  producer of each value per D31: `Task` = the task's own `action.tier` supplied the rung;
  `PlanDefault` = it did not and `tiering.defaultTier` did; `Override` = a full
  `action.runner`/`action.model` pin bypassed resolution. There is deliberately **no** value for the
  legacy path — a legacy-fallback attempt carries no `tierSource` at all.
- a new **`public sealed record AttemptUsage`** with `int InputTokens` and `int OutputTokens`
  (both `{ get; init; }`).
- on **`AttemptProvenance`**, five new optional members, each
  `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` exactly as the existing members are:
  `string? Runner` (the resolved `promptRunners` block name), `string? Kind` (that block's `kind`
  **wire token** — e.g. `"claude"` — not the enum), `string? Tier` (the rung that resolved),
  `TierSource? TierSource`, and `string? Effort` (the resolved route's effort).
  **`Model` already exists and IS the resolved model** (#198/#200) — do NOT add a second
  `resolvedModel` field; document on `Model` that Stage 2 makes it the RESOLVED route's model.
- on **`AttemptRecord`**, `[JsonIgnore(WhenWritingNull)] public AttemptUsage? Usage { get; init; }`.

Do **not** touch `JournalJson.cs` — its `OutcomeToken` switch ends in
`_ => throw new JsonException(...)`, so `AttemptOutcome.NoRoute` compiles and throws when serialized.
**That throw IS the stub your red tests land on**, and task 02 fills it.

### Behaviours to encode (one `[Fact]` or `[Theory]` each)

1. `AttemptOutcome.NoRoute` serializes to the wire token **`"no-route"`** — assert through
   `JournalJson.OutcomeToken(AttemptOutcome.NoRoute)`. (RED: the switch throws today.)
2. The token **`"no-route"`** deserializes back to `AttemptOutcome.NoRoute` through the journal's
   own reader/converter — a round-trip of a `JournalDocument` carrying such an attempt.
   (RED: the reader's switch throws today.)
3. `TierSource` maps to its wire tokens **`"task"` / `"plan-default"` / `"override"`** and back —
   note the kebab spelling of `plan-default`, which is why a plain `Enum.ToString` will not do.
   Put the mapping where the outcome mapping lives (`JournalJson`), i.e. assert a
   `TierSourceToken(...)`-shaped helper and a document round-trip. (RED: no such mapping exists.)
4. A provenance carrying `Runner` / `Kind` / `Tier` / `TierSource` / `Effort` **round-trips** through
   the journal writer + reader with every value preserved.
5. **Absent-not-null**: a provenance with all five new members null serializes to JSON that contains
   **none** of the keys `runner` / `kind` / `tier` / `tierSource` / `effort` — assert on the emitted
   TEXT, not on the re-read object, since an absent key and a null key deserialize identically. This
   is the backward-compatibility half: an existing user's `run.json` must not gain null noise.
6. `AttemptRecord.Usage` round-trips when present, and the `usage` key is **absent** from the emitted
   text when it is null.
7. An **older journal** — an attempt whose `provenance` object has only today's `#198`/`#382` keys,
   and an attempt with no `provenance` at all — still reads without error, with the new members null.

Use the journal's real reader/writer entry points (`JournalReader` / whatever `RunJournal` persists
through), not a hand-rolled `JsonSerializer` call with your own options — the point is that the
SHIPPED serialization does this, and a private options bag would prove nothing.

Do NOT implement the token mappings; that is task 02's job.
