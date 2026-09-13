## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `24-author-tests-halt-text`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "24-author-tests-halt-text": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Author failing tests for the **`needs-human` halt text** — design 40 §3, §5 and §6, and the
FIRST of the three surfaces the review's `d40-asymmetry` answer named.

**Test file:** `tests/Guardrails.Integration.Tests/Supply/SuppliedHaltTextTests.cs`
**Test class:** `SuppliedHaltTextTests`

Every test carries `[Trait("Category", "Supply")]`.

**There is no stub file, and here is why** — the same reason tasks 14 and 17 have none. The
production type already exists and what is missing is the TEXT, not a type, so nothing needs
declaring before these tests can compile. Verify that before you assume it:

- `RunCommand.RenderNeedsHumanSections(...)` is **`public static`** and is already driven
  directly by three Integration tests — `NeedsHumanKindRenderingTests`,
  `NeedsHumanTriageSummaryTests`, `RateLimitedRenderingTests`. Read one of them for the shape;
  do not invent a new harness.
- `NeedsHumanClosingLine(kind)` already composes the guidance line per
  `NeedsHumanKinds.BlockedWork` / `NeedsHumanKinds.DefectiveGuardrail`. **Grep for it rather
  than trusting a line number** — this file is large and has moved under several plans.

**Why this task exists at all, stated because it was missing.** The first breakdown of this
plan delivered the skill and the README and SUBSTITUTED THE SSOT for the halt text. §5a calls
all three "v1 acceptance conditions rather than follow-ups", and §3 makes the printed sequence
the discoverability mechanism: the halt is the ONE surface the operator is looking at in the
moment the feature is needed. A run that goes green having skipped it ships a feature nobody
can find.

**Pin these behaviours to these EXACT method names:**

- `MissingResourceHalt_NamesTheAsymmetry` — a `blocked-work` halt whose question is about a
  missing file states, in the rendered output, that **plan-folder edits reach a running plan
  and code artifacts do not**. Assert on the rendered text, not on a resource string you also
  wrote.
- `MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence` — the rendered output
  carries the sequence an operator can paste: `guardrails supply`, then the reset, then the
  resume. §3 is explicit that the sequence is printed BY THE HALT rather than left to be
  looked up, and #431's rule applies: a template the reader has to fill in is not a hand-over.

**Do NOT weaken this to a substring check on a constant you introduce.** The test must drive
the real renderer and read what it produced. A test that asserts a string literal equals
itself is the tautology this plan's every other census exists to prevent.

The tests MUST COMPILE and FAIL. Do NOT change the halt text here.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
