## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-implement-shared-json-extractor": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 3.3**.

## Task

### What to build

**1. Implement `PromptJsonExtractor`** so `PromptJsonExtractorTests` goes green: last fenced ```json
block, else the last top-level JSON object, must parse, otherwise nothing.

**2. Route BOTH existing strict consumers through it** - this is the deliverable that makes section
3.3 a payoff rather than a claim:

- `src/Guardrails.Core/Execution/OverwatchProposal.cs` - today `JsonDocument.Parse(resultText)` on
  the WHOLE message, then requires an object with a string `diagnosis`. Parse via the extractor
  instead, then apply the same shape requirement.
- `src/Guardrails.Core/Execution/NeedsHumanTriage.cs` - the sidecar writer, same shape, same change.

This only ever WIDENS what parses on paths that **fail closed today**, so it cannot make a Claude
run worse. Keep every downstream behaviour identical: a message that parses today must still parse
to the same value, and one that yields nothing must still yield nothing (the advisory
`RecordNoVerdict` path is unchanged).

**Do NOT edit `PromptJsonExtractorTests.cs`** - it is outside your writeScope.

### A settled decision you must implement (do not re-litigate)

The first run halted here, correctly. `OverwatchProposalFenceTests.Unfenced_Prose_StaysNull` (issue #551,
authored the same day as this plan ran) asserts that prose containing a JSON object parses to **null** -
the exact opposite of section 3.3's payoff and of `PromptJsonExtractorTests.ProseAroundJsonObject_ObjectIsRecovered`,
which task 04 already pinned green. The two cannot both hold, and #551's test is the one that is wrong.
The maintainer has ruled: **the plan wins.**

So, in one change:

1. **Wire `OverwatchProposal.TryParse` through the full shared extractor** - the bare-object-in-prose
   fallback included, not just the fenced branch. Delete the local `Unfence` helper; it exists only
   because the shared extractor did not yet, and its own comment says to collapse it into this one.
   Do the same for the triage sidecar in `NeedsHumanTriage`.

2. **Rewrite that one test rather than deleting it** - `OverwatchProposalFenceTests.cs` is in your
   writeScope for this and nothing else. Replace `Unfenced_Prose_StaysNull` with a test that guards
   SHAPE instead of position, e.g.:

   ```csharp
   [Fact]
   public void Unfenced_ProseMentioningANonVerdictObject_StaysNull()
   {
       // TryParse requires a 'diagnosis' string, so a stray object recovered from prose
       // is still not a verdict.
       Assert.Null(OverwatchProposal.TryParse(
           "I checked the config, it had {\"maxTurns\": 50} in it."));
   }
   ```

   Update its surrounding comment too: the narrowness bound is no longer "only a fenced block parses"
   but "only a well-SHAPED verdict parses, wherever it appears". That is a real counterweight and it
   must survive - it is what still stops the overwatcher manufacturing a verdict out of arbitrary JSON.

3. **Leave every other test in that file alone.** The fenced cases, the unfenced-bare-object case and the
   blank-input cases are all still correct and must stay green.

Accepted cost of this ruling, so you do not flag it as a defect: a hedged-but-complete verdict
("...I think the answer is {full valid verdict} but I am not certain") now parses. The model did emit a
complete verdict; treating it as one is the trade section 3.3 chose in exchange for working at all on
weaker models.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/PromptJsonExtractor.cs`, `src/Guardrails.Core/Execution/OverwatchProposal.cs`, `src/Guardrails.Core/Execution/NeedsHumanTriage.cs`, `tests/Guardrails.Core.Tests/OverwatchProposalFenceTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
