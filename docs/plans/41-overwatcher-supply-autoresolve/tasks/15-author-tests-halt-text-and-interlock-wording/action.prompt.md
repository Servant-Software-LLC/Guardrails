## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `15-author-tests-halt-text-and-interlock-wording`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "15-author-tests-halt-text-and-interlock-wording": { "someKey": "someValue" } }`.
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

Author failing tests for design 41 §2.1 ("One predicate for 'names a path'") and §6 ("Where it shows") —
the missing-resource halt text moving onto the shared `MissingResourceSignal` predicate, and the #597
delivery-interlock wording generalising from the two best-guess/unreviewed tokens to a **machine
decision**.

**Test file (EXISTS — you EDIT it, you do not create it):**
`tests/Guardrails.Integration.Tests/Supply/SuppliedHaltTextTests.cs`
**Test class:** `SuppliedHaltTextTests`

**There is no stub file, and you must not create one.** Every production member these tests drive is
already `public` and shipped: `RunCommand.RenderNeedsHumanSections`, `RunCommand.RenderUndeliveredWorkWarning`
and `RunCommand.Create`. What is missing is the TEXT and the PREDICATE, not a type — so nothing needs
declaring before these tests compile. Your writeScope is the one test file; it holds no production path at
all, and that is deliberate.

### Change the trait — this is load-bearing, not housekeeping

The class carries `[Trait("Category", "Supply")]` today. **Replace it with
`[Trait("Category", "OverwatchSupply")]`** — the plan-wide trait for plan 41. Do not add the new one
alongside the old; REPLACE it, so exactly one `Category` trait remains on the class. Eleven existing files
carry `"Supply"`, and this plan's baseline preflight excludes its own trait by an exact
`--filter "Category!=OverwatchSupply"` match; a class carrying both traits is not reliably excluded by
that `!=`, which is how a plan silently starts grading itself against its own in-flight edits.

### What the shared predicate changes (design §2.1)

`RunCommand.MissingResourceHaltLines` today finds the path with a PRIVATE `ResourcePathToken` regex and
takes `Match.Value` — the FIRST match only. Task 16 replaces that with the shared
`Guardrails.Core.Execution.MissingResourceSignal`, which differs in three observable ways:

- it returns **every** token in the question, not only the first;
- a segment may contain `@`, so `node_modules/@scope/x/index.js` is matched **whole**;
- a leading `./` is **normalized away**, so an agent writing `./mermaid.min.js` yields the token
  `mermaid.min.js`.

A token still needs a `/` somewhere, so prose like `e.g.` or `Node.js` never matches.

**Do not assert against `MissingResourceSignal`'s own API, and do not reference the type.** It is built by
a sibling task (`03-implement-missing-resource-facts`) and its exact signature is not yours to depend on.
Assert only on the RENDERED halt text, through `RunCommand.RenderNeedsHumanSections` — the same pure seam
the four shipped tests in this file already use. That keeps this file compiling whatever shape the
predicate lands in.

### What the interlock wording changes (design §6 "Where it shows")

Design §6 adds `auto-supplied` to the one shared delivery-interlock token set, so the #597 banner must stop
calling every suppressing decision a *best guess*. Two surfaces in `RunCommand.cs`:

- **The banner** (`RenderUndeliveredWorkWarning`) prints a line reading
  `JUDGE THE DECISION FIRST — run.json → decisions[]. A best-guess that a later attempt` /
  `superseded is stale; one that shaped the result you are looking at is not.` For an `auto-supplied`
  decision that is simply the wrong noun. The banner already names the decision token and its subject by
  interpolation, and that half must keep working.
- **The `--merge-on-success` option Description** says *"delivers work a machine decision
  (proceeded-best-guess / proceeded-unreviewed) would otherwise hold back"* and closes *"this flag matters
  only for those two cases"*. With a third token in the set, the enumeration and the "two cases" tail are
  false.

Reach the option description through the real command surface — `RunCommand.Create(io, TelemetryOverrides.None)`
returns the `Command`, whose `Options` you can find by name (`--merge-on-success`) and read `.Description`
from. Do not re-declare the string in the test.

### Pin these behaviours to these EXACT method names

**Expected RED against the current tree:**

- `MissingResourceHalt_NamesEveryPathTheQuestionNames` — a question naming TWO missing paths yields a
  `guardrails supply` line naming BOTH. Today only the first match is taken, so the second file is
  invisible and the operator supplies half of what the task needs.
- `MissingResourceHalt_NormalizesALeadingDotSlash_ForARootLevelFile` — a question naming
  `./mermaid.min.js` yields the token `mermaid.min.js`: the emitted `guardrails supply` argument must NOT
  carry the leading `./`. (The domain-knowledge skill tells agents to use the `./` form precisely so a
  root-level file matches at all; normalizing it away is what makes the emitted command runnable.)
- `MissingResourceHalt_MatchesAScopedNodeModulesPath_Whole` — a question naming
  `node_modules/@scope/x/index.js` emits that path WHOLE. Assert the whole path is present. Today's regex
  excludes `@`, so it matches a truncated interior substring and the printed command points at a path that
  does not exist — a wrong answer, not a missing one, which is why this is worth its own row.
- `UndeliveredWorkBanner_ForAMachineDecision_DoesNotCallItABestGuess` — with a suppressing `DecisionEntry`
  whose `Decision` is `auto-supplied`, the rendered banner must NOT contain the string `A best-guess`.
  Build the report as `new RunReport { Tasks = [...], WhollyGreenButUndelivered = true,
  DeliverySuppressingDecision = <the entry> }` and render with
  `RunCommand.RenderUndeliveredWorkWarning(report, terminalGatePassed: true, planDirectory, writer)`.
- `MergeOnSuccessOption_DescribesAMachineDecision_WithoutEnumeratingOnlyTheTwoTokens` — the
  `--merge-on-success` description must still say `machine decision`, must NOT contain the literal
  `proceeded-best-guess / proceeded-unreviewed`, and must NOT claim the flag matters only for **two**
  cases. Assert the absence of the enumeration, not the presence of any particular replacement wording —
  task 16 owns the prose.

**DECLARED EXEMPT from the red census — a correct implementation leaves these GREEN on the current tree.
They must still EXIST; the census asserts that, and task 16's forward census requires each observed
`Passed`. Write them correctly; do NOT make them fail to please the census:**

- `MissingResourceHalt_ForASinglePlainPath_IsUnchanged` — **the never-weaker requirement, asserted
  directly.** A question naming exactly one ordinary path (`vendor/mermaid.min.js`) renders the SAME three
  commands, with the same arguments, as it does today. This is the row that refuses a "rewrite the halt
  text" implementation: the predicate is allowed to match MORE, never to change what it already got right.
- `MissingResourceHalt_NamesTheAsymmetry` — shipped; keep it.
- `MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence` — shipped; keep it.
- `BlockedWorkHalt_AboutAnOverScopedTask_KeepsTheOriginalClosingLine` — shipped negative control. A halt
  whose question names no path keeps the ordinary closing line and gets no `guardrails supply`. A widened
  predicate that starts matching prose would break exactly this.
- `DefectiveGuardrailHalt_MentioningAFile_DoesNotGetTheAsymmetryOrSequence` — shipped negative control.
- `UndeliveredWorkBanner_NamesTheAutoSuppliedDecisionAndItsTask` — with an `auto-supplied` suppressing
  entry, the banner names both the token `auto-supplied` and its subject task id. This is green on the base
  because the banner already interpolates `suppressing.Decision` / `suppressing.Subject` rather than
  enumerating tokens — and it exists to keep that true, because §6 requires the banner to name this entry
  and its task once `auto-supplied` joins the token set.

**Keep the four shipped tests in this file passing.** Design §11 row 9 makes it an acceptance condition in
those words: *the halt text uses `MissingResourceSignal` (`SuppliedHaltTextTests` stay green)*. Do not
weaken or delete an existing assertion to make room for a new one.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or touch
the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here breaks a
class that did nothing wrong.

The five pinned tests MUST COMPILE and FAIL. Do NOT implement any of the behaviour — every production file
is outside your writeScope.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Integration.Tests/Supply/SuppliedHaltTextTests.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
