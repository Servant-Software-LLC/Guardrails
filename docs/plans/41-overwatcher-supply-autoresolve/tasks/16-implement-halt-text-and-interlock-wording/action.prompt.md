## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `16-implement-halt-text-and-interlock-wording`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "16-implement-halt-text-and-interlock-wording": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `SuppliedHaltTextTests` pass. Three edits, all in `src/Guardrails.Cli/Commands/RunCommand.cs` —
design 41 §2.1 and §6.

### 1. Point the missing-resource halt text at the SHARED predicate

`MissingResourceHaltLines` currently finds the missing path with a private `ResourcePathToken` regex and
takes `Match.Value` — the first match only. **Find the member before you edit it:**
`grep -n 'MissingResourceHaltLines\|ResourcePathToken' src/Guardrails.Cli/Commands/RunCommand.cs`.
Do not trust any line number quoted at you, including one in the design document — the design's own
citation for this member is wrong, and it was caught by grepping.

Replace the private regex with the shared `Guardrails.Core.Execution.MissingResourceSignal`, built by task
`03-implement-missing-resource-facts`. **Grep for its real API before calling it** — the type exists on your
base, and its signature is not restated here on purpose:
`grep -n 'public' src/Guardrails.Core/Execution/MissingResourceSignal.cs`.

Design §2.1 is what the predicate now guarantees, and the tests assert exactly these three deltas:

- **every** token in the question, not only the first — the emitted `guardrails supply` line names them all;
- a segment may contain `@`, so `node_modules/@scope/x/index.js` is matched whole;
- a leading `./` is normalized away, so `./mermaid.min.js` yields the token `mermaid.min.js`.

A token still needs a `/` somewhere, so prose like `e.g.` or `Node.js` never matches — the two shipped
negative controls in the test file pin that and must stay green.

**`MissingResourceSignal` must become the SINGLE producer of the path-token match in this file.** Delete
`ResourcePathToken` outright rather than leaving it beside the new call: two spellings of "names a path"
is the defect §2.1 exists to remove, and a surviving private copy is what a later reader will reach for.

### 2. Stop the interlock banner calling every machine decision a "best guess"

In `RenderUndeliveredWorkWarning`, the `JUDGE THE DECISION FIRST` block reads *"A best-guess that a later
attempt superseded is stale; one that shaped the result you are looking at is not."* Design §6 puts a third
token, `auto-supplied`, into the one shared delivery-interlock set, and an auto-supply is not a guess — it
is a machine decision that put a file in the tree. Generalise that sentence to a **machine decision**.

Keep everything else about the banner intact. It already names the decision token and its subject by
interpolation (`suppressing.Decision` / `suppressing.Subject`), which is precisely why it needs no
per-token knowledge — do not replace that interpolation with an enumeration.

**That sentence is PINNED VERBATIM by an existing test, and updating it is part of this task.**
`tests/Guardrails.Integration.Tests/UndeliveredWorkWarningTests.cs` asserts the whole banner as one
string in two places (`OffWithADecision_NamesBothCauses_AndNeverClaimsTheSettingIsOn` and
`OnWithADecision_NamesTheInterlock_AndThatTheSettingIsOnByDefault`), so the moment you reword the
sentence those two rows go red. That file IS in your `writeScope` — update both pins to the new
wording in the same change.

This is not the "do not edit authored tests" case: this file is pre-existing repo coverage, not a
proof task 15 wrote for you. Leaving it stale does not fail YOUR guardrails — it fails the run's
TERMINAL GATE, on the merged HEAD, after every task in the plan has gone green, and no other task
declares the file, so nothing downstream can repair it.

### 3. Generalise the `--merge-on-success` description

Its `Description` says *"delivers work a machine decision (proceeded-best-guess / proceeded-unreviewed)
would otherwise hold back on the plan branch"* and closes *"Delivery is the DEFAULT, so this flag matters
only for those two cases."* Both the parenthetical enumeration and the "two cases" tail are false once
`auto-supplied` joins the set. Name a machine decision without enumerating only those two tokens.

### Explicitly OUT of scope — do not edit these

- **`ForcedDeliveryRecord.Decision`'s doc comment.** It lives in
  `src/Guardrails.Core/Journal/JournalModel.cs` and still names only the two tokens. That file is **not in
  your writeScope**; design §11 row 7 assigns it to the observer/`by`-field task, which owns `JournalModel.cs`
  and corrects §8's doc comments in the same change. Leave it alone — editing it fails this task on the
  scope check and consumes a retry.
- **`docs/plans/02-schemas-and-contracts.md`** and the other design docs. Task 19 owns those.

### The never-weaker floor

`MissingResourceHalt_ForASinglePlainPath_IsUnchanged` asserts that an ordinary single-path question renders
the SAME three commands it renders today. The predicate is allowed to match MORE; it is not allowed to
change what it already got right. Design §11 row 9 states the acceptance condition in those words:
*the halt text uses `MissingResourceSignal` (`SuppliedHaltTextTests` stay green)*.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Cli/Commands/RunCommand.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
