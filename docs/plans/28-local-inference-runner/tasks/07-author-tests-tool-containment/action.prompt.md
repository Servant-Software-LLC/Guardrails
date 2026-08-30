## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-author-tests-tool-containment": { "someKey": "someValue" } }`.
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

Read: **plan section 5**.

## Task

### What to build

**1. The stub** `src/Guardrails.Core/Prompts/PromptToolContainment.cs` with
`IsReadable(roots, absolutePath)` throwing `NotImplementedException`.

**2. The test file** `tests/Guardrails.Core.Tests/Prompts/PromptToolContainmentTests.cs`, class
**`PromptToolContainmentTests`** (pinned - the guardrail filters on it).

Section 5 is emphatic about why this primitive exists: `WorkspaceContainment.Escapes` **cannot** do
this job because it rejects every ROOTED path outright, and every path the harness hands a prompt is
absolute. A `Read` guarded by it would refuse every read the harness instructs the model to make.
Read `src/Guardrails.Core/Execution/WorkspaceContainment.cs` and confirm that for yourself before
writing the tests.

The contract: normalise the candidate with `Path.GetFullPath`, normalise each root, accept on a
**directory-boundary** match against any root.

Cover, one `[Fact]` each:

- an absolute path INSIDE a root is readable (the case `WorkspaceContainment` gets wrong)
- an absolute path outside every root is refused
- a `..` traversal that escapes is refused AFTER normalisation
- a **directory-boundary** case: root `/repo/src` must NOT admit `/repo/srcevil/x.cs` - a prefix
  match is not a boundary match
- both roots are honoured (workspace AND plan directory)
- **EMPTY entries are dropped** from the root set (`Path.GetFullPath("")` throws, and
  `CriticalityJudge` supplies two empty ones - a real caller, not a hypothetical)
- **an EMPTY root set DENIES everything** - deliberately, because the only caller with no roots is
  the criticality assessment which needs no tools, and deny-all fails in the direction where being
  wrong is a loud refused tool call rather than a silent read of the whole filesystem

All must FAIL against the throwing stub. **Do NOT implement it.**

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Prompts/PromptToolContainmentTests.cs`, `src/Guardrails.Core/Prompts/PromptToolContainment.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
