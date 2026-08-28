## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "05-wire-recorder-into-breakdown": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

Wire the recorder and the gate into the **real** breakdown path, and prove the wiring with a test that
drives the production entry point rather than injecting the seam itself.

**Write exactly three files:**

1. `src/Guardrails.Core/Execution/InitialBreakdownInvoker.cs` — `PrepareInvocation` records the plan
   source.
2. `src/Guardrails.Cli/Commands/BreakdownCommand.cs` — enforce the declared-count gate on the
   produced folder.
3. `tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs` — the composition-root test.

**Scope boundary (harness-enforced):** Write only to those three paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including
`PlanSourceRecord.cs`, `DeclaredCountGate.cs`, the sibling test files, `BreakdownManifest.cs`,
`RunReset.cs`, `PlanGitignore`, or any `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read the two landed halves FIRST — and treat this section as authoring-time state

`PlanSourceRecord` (task 02) and `DeclaredCountGate` (task 04) are **siblings that ran before you**.
Everything this prompt says about their member shapes reflects the state at plan-authoring time,
**before either had actually run** — verify it is still accurate before assuming the same shape
applies. `git log --oneline`, `git show` and a read of the two files under
`src/Guardrails.Core/Breakdown/` are the fastest way to see what actually landed:

- `PlanSourceRecord` was specified to expose `SourcePath`, `SourceBytes`, `SourceSha256`,
  `SourceSha256Lf`, `DeclaredDelegatedDecisions` and an open `Stamps` map, behind a capture factory
  taking a plan path. Its exact signature is whatever task 02 shipped.
- `DeclaredCountGate` was specified as an evaluation over `(declaredDelegatedDecisions, planFolder)`
  returning pass/fail plus a message naming both counts. Its exact signature is whatever task 04
  shipped.

If a landed shape makes an instruction below impossible as written, implement the **intent** and say
so in your summary — do not reshape a sibling's file to match this prompt (it is out of scope).

### Half A — `PrepareInvocation` writes `<outputFolder>/state/plan-source.json`

The single place harness code reads a source `plan.md` is the static method
**`InitialBreakdownInvoker.PrepareInvocation`**, via its private helper **`TryReadPlan`** — cite and
navigate by those **symbol names**; grep for them, and do not rely on a line number, which will have
moved. Its only caller is `BreakdownCommand`. The JIT wave path reads `<wave>/brief.md` and never
touches this method, so nothing else has to change.

That site is the right home for two reasons the design turns on
(`docs/plans/24-plan-source-provenance.md` section 2): it **provably has the bytes**, and it runs
**outside the agent it polices** — it is the thing that invokes that agent.

Requirements:

- Capture the record from the **plan path** and write it to `<outputFolder>/state/plan-source.json`,
  creating the `state/` directory if needed. `BreakdownCommand` creates `outputFolder` immediately
  before calling `PrepareInvocation`, so the folder exists but `state/` does not yet.
- `TryReadPlan` currently calls `File.ReadAllText` and that call stays — it feeds the prompt text, and
  decoded text is exactly right for that. **The hash must not come from it.** `PlanSourceRecord` reads
  the raw bytes itself; hashing a decoded string is the byte-exactness bug the design names by name.
- Keep the method's existing failure posture: it currently swallows a read failure and continues with
  an empty plan text, because a breakdown that cannot read its plan should fail on the breakdown, not
  on a logging helper. Recording provenance must not become a new way for `PrepareInvocation` to
  throw. Its XML doc comment says it prepares "WITHOUT running anything" — that means without running
  the agent; update the comment so the provenance write is documented rather than a surprise.
- The artifact lives under `state/` **and that is not cosmetic.** A field on `guardrails.json` would
  fold into `PlanDefinitionHash`, which keys the review attestation — so recording provenance would
  **de-attest the plan's review** and re-fire GR2025. Do not put it anywhere else, and do not touch
  the hash inputs.
- Record what you found. Do **not** validate the stamps against Charter, and do **not** try to detect
  that the source changed after the breakdown — both are explicitly out of scope
  (`docs/plans/24-plan-source-provenance.md` section 6).
- The plan-root `.gitignore` scaffold is out of your write scope. Whether this artifact ends up
  committed or ignored is not decided by this plan and is not yours to decide here.

### Half B — `BreakdownCommand` enforces the declared-count gate

After the breakdown agent returns, compare what the harness READ against what the agent PRODUCED, and
fail the breakdown when the plan declared **N >= 1** and the folder records **M != N**.

Navigate by symbol name: the command's post-invocation validation runs through
**`PlanProbe.LoadAndValidate`**, and the gate belongs on that same post-return path — grep for
`LoadAndValidate`; do not rely on a line number. Wire the gate so that:

- It reads the declared count from the record `PrepareInvocation` wrote (the harness's own reading),
  not by re-parsing the markdown a second time.
- A gate failure is a **failed breakdown** with the gate's message surfaced to the operator — the
  message already names N, M and the check's two limits, so print it rather than paraphrasing it.
- The existing "authored but does NOT validate" and validate-skipped paths keep behaving as they do
  today. This adds a reason to fail; it removes none.

### The composition-root test — drive the REAL entry point, never inject the seam

Write `tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs`, namespace
`Guardrails.Core.Tests.PlanSource`, every test carrying `[Trait("Category", "PlanSourceProvenance")]`.
Author exactly these methods, named verbatim:

| Test method name | What it proves |
|---|---|
| `PrepareInvocation_WritesPlanSourceJson_IntoTheOutputFoldersStateDirectory` | Call **`InitialBreakdownInvoker.PrepareInvocation(planPath, outputFolder, logDir)`** on a temp plan and a temp output folder, then assert `<outputFolder>/state/plan-source.json` EXISTS and parses. |
| `PrepareInvocation_RecordsTheDeclaredDelegatedDecisionCount_FromTheRealPlanBytes` | The same real call over a plan carrying `**DECISIONS DELEGATED TO YOU: 2**` yields a record whose declared count is `2` — read it back **out of the written file**, not out of an in-memory object you built. |
| `DeclaredCountGate_RejectsAnUnderRecordingFolder_UsingTheRecordPrepareInvocationWrote` | Feed the REAL gate the declared count read back from that written file, against a folder with **no** `decisions.md` (M = 0), and assert it FAILS. This is the never-scanned breakdown. |
| `PlanSourceJson_SurvivesAFreshReset` | Run **`RunReset.Fresh`** over a plan folder holding the artifact and assert `state/plan-source.json` is still there. `RunReset` deletes NAMED files under `state/` — `run.json`, `state.json`, `merge-conflicts.log`, the rewind-intent marker — not the folder, which is exactly why this survives. It is the kind of property a later refactor silently breaks, so it is pinned here. |

**Prohibitions, and both are structurally checked (a guardrail fails the task if you break them):**

- **The test must call `InitialBreakdownInvoker.PrepareInvocation` by name.** A test that constructs a
  `PlanSourceRecord` and writes the JSON itself, then asserts the JSON exists, proves nothing about
  the production path — it is the unwired-factory failure with extra steps.
- **The test must not write a file named `plan-source.json` itself.** Writing the plan fixture with
  `File.WriteAllText` is expected and fine; writing the artifact under test is not.

Practical notes:

- **`Guardrails.Core.Tests` references `Guardrails.Core` only — it CANNOT see `Guardrails.Cli`.** Do
  not try to drive `BreakdownCommand` from this test, and do NOT add a project reference (the
  `.csproj` is out of scope). Half B's wiring is covered by a separate structural guardrail over
  `BreakdownCommand.cs`; state the limitation in a comment at the top of the test class so the next
  reader does not assume the CLI half is under test here.
- Build every fixture in a temp directory and clean up in a `finally` or `IDisposable`. Never write
  into the repository tree, and never point `outputFolder` at a real plan folder.
- `RunReset.Fresh` also prunes git worktrees and branches on a best-effort basis; on a temp non-git
  folder that is a no-op. If it throws there, that is a finding to report — not something to work
  around by not calling it.
