## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-author-tests-staging-tree`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-author-tests-staging-tree": { "someKey": "someValue" } }`.
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

Author failing tests AND the minimal stubs they compile against for the staging tree that
`guardrails supply` writes into (design 40 §1).

**Test file:** `tests/Guardrails.Core.Tests/Supply/SuppliedStagingTreeTests.cs`
**Test class:** `SuppliedStagingTreeTests` (namespace `Guardrails.Core.Tests.Supply`)
**Stub file:** `src/Guardrails.Core/Execution/SuppliedStagingTree.cs` — a `SuppliedStagingTree`
whose members throw `NotImplementedException`, so the test project COMPILES.

Every test carries `[Trait("Category", "Supply")]`.

**Pin these behaviours to these EXACT test method names** — a guardrail binds each one and
requires it to be observed `Failed` against the stubs:

- `StagedPathFor_PutsTheFileUnderLogsRunIdSupplied` — a workspace-relative path `vendor/x.js`
  for run `R` resolves to `logs/R/supplied/vendor/x.js`.
- `StagedPathFor_PreservesTheWorkspaceRelativeLayout` — nested directories survive; the staged
  layout IS the destination layout, because §1 deliberately has no separate destination argument.
- `StagedPathFor_RefusesAPathEscapingTheWorkspace` — `../../etc/passwd` (and an absolute path
  outside the workspace) is refused. This is GR2019's traversal rule applied to a CLI argument.
- `StagedPathFor_RefusesAnAbsolutePathOutsideTheWorkspace` — an absolute path that is not under
  the workspace root is refused, distinctly from the `..` case.
- `DrainableFiles_OnAnEmptyTree_IsEmpty` — the whole feature is inert when nothing was staged
  (§2 step 1, the never-weaker requirement).
- `DrainableFiles_EnumeratesEveryStagedFileWithItsDestination` — each staged file is returned
  paired with the workspace path it will land at.

The tests MUST COMPILE and FAIL against the stubs — failing is intentional; NOT compiling is a
mistake to fix. Do NOT implement the behaviour.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them — including other production files, neighbouring tests, or any `.csproj`. An out-of-scope
edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is
missing>"}` to the state-out path and stop.

