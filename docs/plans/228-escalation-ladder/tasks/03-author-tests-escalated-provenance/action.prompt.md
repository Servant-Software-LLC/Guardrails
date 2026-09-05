## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `03-author-tests-escalated-provenance`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-escalated-provenance": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "03-author-tests-escalated-provenance": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Author the FAILING tests for **escalated provenance** — the journal's answer to *"why is this attempt
on a stronger rung?"* — plus the two data members they compile against.

**Write only these two files:**

1. `tests/Guardrails.Core.Tests/Escalation/EscalatedProvenanceTests.cs` — class name
   **`EscalatedProvenanceTests`**, namespace `Guardrails.Core.Tests.Escalation`, with the class-level
   attribute `[Trait("Category", "EscalationLadder")]`. Both are pinned: this plan's guardrails select
   this pair with `--filter 'Category=EscalationLadder&FullyQualifiedName~EscalatedProvenanceTests'`.
   **Do NOT use `[Trait("Category", "TierResolution")]`** — that trait already exists in this project
   and belongs to the shipped tier-resolution suite.
2. `src/Guardrails.Core/Journal/JournalModel.cs` — add exactly TWO members and change nothing else:
   - a fourth value on the `TierSource` enum, **`Escalated`**, declared **LAST** so nothing above it
     renumbers. Its XML doc says: *a PREVIOUS attempt of this task failed its guardrails, so the
     escalation ladder (#228) moved this attempt one rung up. The rung it started from is recorded
     beside it as `escalatedFrom`.*
   - a member on `AttemptProvenance`:

     ```csharp
     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
     public string? EscalatedFrom { get; init; }
     ```

     Its XML doc says: the rung the FIRST (un-escalated) resolution of this task served. Present only
     on an attempt the ladder moved; absent — never null — on every other attempt, which is what makes
     its presence the escalation signal without a second flag beside it.

   Both are pure DATA members: the declaration IS the implementation. Do NOT touch
   `JournalJson.TierSourceToken`, the `TierSourceConverter`, or `TierProvenance.SourceFor` — task
   `04-implement-escalated-provenance` owns those, and leaving them alone is what makes four of the
   five behaviours below genuinely RED.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Escalation/EscalatedProvenanceTests.cs` and
`src/Guardrails.Core/Journal/JournalModel.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside these paths — including changes to other production files,
neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes
a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file —
write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### THE DISTINCTION THIS TASK EXISTS TO PROTECT

`TierResolution` already carries **`Climbed`**, and it means something else. `Climbed` is TRUE when
`Candidates(RequestedTier)` was EMPTY and the resolver walked to a stronger rung **inside one attempt**
— a CAPABILITY fact: *"no configured runner serves the rung this task asked for."* Escalation is a
different reason to be on a higher rung: *"a previous attempt of this task failed its guardrails."*

An attempt that ran on `hard` because it was tagged `hard`, one that ran on `hard` because nothing
served `medium`, and one that ran on `hard` because it climbed the ladder after two guardrail failures
are three materially different facts, and today only the first two are distinguishable. Do **not**
reuse `Climbed`, do **not** widen its meaning, and do **not** let `RequestedTier`/`Tier` alone carry
the escalation signal — an escalated attempt and a capability climb can produce the identical
`(RequestedTier, Tier)` pair. `TierSource.Escalated` plus `escalatedFrom` is how they stay apart, and
one of the behaviours below is the test that proves it.

Before you start, read `src/Guardrails.Core/Prompts/TierProvenance.cs` (the whole file — it is short)
and `src/Guardrails.Core/Journal/JournalJson.cs`. Two commands worth running rather than trusting:
`grep -n "TierSource" src/Guardrails.Core/Journal/JournalJson.cs` (at authoring time: **16** hits,
covering the `TierSourceToken` switch and the `TierSourceConverter`'s `Read`, both of which THROW on an
unhandled value — that throw is why behaviours 3 and 4 are red), and
`grep -c "TierSource" src/Guardrails.Core/Journal/JournalModel.cs`. If a grep disagrees with this
prompt, trust the grep and say so in your summary.

### The behaviours, and the EXACT test method name each must carry

| behaviour | test method name | on the stub tree |
|---|---|---|
| an escalated route is sourced `Escalated` | `SourceFor_OnAnEscalatedRoute_IsEscalated` | RED |
| a CAPABILITY climb that did not escalate is NOT `Escalated` — **paired with its mirror** | `SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated` | RED — see below |
| the wire token for `Escalated` is `escalated` | `TierSourceToken_ForEscalated_IsTheEscalatedWireToken` | RED |
| the journal round-trips the `escalated` token | `TierSourceConverter_RoundTripsEscalatedThroughTheJournal` | RED |
| `escalatedFrom` is written only when the attempt escalated | `Provenance_WritesEscalatedFromOnlyWhenTheAttemptEscalated` | green — see below |

One of the five is a **DECLARED EXEMPTION** — a correct implementation leaves it GREEN on this tree, so
the census asserts only that it RAN. It is written, never skipped. The discriminator is **not** exempt,
and the reason is worth reading:

- `SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated` is the **discriminator**, and it is
  RED because it has TWO halves. Build a `TierResolution` with `Climbed = true`,
  `RequestedTier = "medium"`, `Tier = "hard"` and `EscalatedFrom = null`, and assert
  `TierProvenance.SourceFor` returns the ORIGIN-derived source (`TierSource.Task` for an `action.tier`),
  **not** `TierSource.Escalated`. That half is true today and must STAY true. On its own it would be
  satisfied by `Assert.True(true)`, so pair it — in the SAME test method — with the mirror: a route
  carrying `EscalatedFrom = "easy"` **and** `Climbed = true` must be `Escalated` *and* still report
  `Climbed`, so the two facts are independently readable. The mirror cannot pass until task 04 adds the
  `Escalated` arm, which is what makes the whole test red now and green after.
- `Provenance_WritesEscalatedFromOnlyWhenTheAttemptEscalated` covers a pure data member whose
  declaration IS its implementation — there is no stub-vs-real distinction to be red about. Serialize
  an `AttemptProvenance` with `EscalatedFrom = "easy"` through the journal serializer and assert the
  `escalatedFrom` key is present with that value; serialize one with `EscalatedFrom = null` and assert
  the key is **absent entirely** (not `"escalatedFrom": null`).

For the round-trip and key-shape assertions, follow
`tests/Guardrails.Core.Tests/ModelTiering/JournalTieringSchemaTests.cs` — read it first and reuse its
serializer helpers rather than inventing a second way to emit and read a journal document.

**The tests MUST COMPILE and FAIL** (the four RED rows). Failing is the point. NOT compiling is a
mistake to fix. Do NOT implement the mapping — task 04 does that.
