## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "02-implement-sample-verifier": { "someKey": "someValue" } }`. The harness
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

Implement `SampleVerifier` so the tests task 01 authored **pass**.

**Write exactly one file:** `src/Guardrails.Core/Samples/SampleVerifier.cs` — replacing the
`NotImplementedException` stubs with real logic.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Samples/SampleVerifier.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including
`tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs`, any other production file, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. **The test file is
NOT yours to edit.** If a test looks wrong, it is still the contract: implement to it. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read the tests FIRST — they are the specification

Task 01 authored `tests/Guardrails.Core.Tests/Samples/SampleVerifierTests.cs` and the stub file you
are about to fill. **This paragraph describes the authoring-time state, before task 01 had actually
run — verify it is still accurate before assuming the same shape applies.** Task 01 was told to choose
the shape it thought the implementation wanted (a `VerifyAsync(PlanDefinition, ProcessRunner,
TimeSpan, CancellationToken)` entry point returning a result carrying a list of findings, each with a
kind, the guardrail path, the sample path and the observed exit code), so the exact signatures are
whatever landed on disk. Read both files before writing a line. `git diff HEAD~1 --stat` and
`git show` will show you what task 01 committed.

You MAY reshape the stub's members (that file is in your write scope) — but only in ways the
**existing tests still compile and pass against**. Changing the stub to dodge a test is the one thing
this task cannot do.

### What the verifier does, step by step

**1. Discover the pairs.** For each `TaskNode` in the plan, look at `<task.Directory>/samples/`. Only
files matching `<base>.valid.<ext>` and `<base>.invalid.<ext>` participate; everything else in that
folder is ignored (the committed corpus really does hold `README.md` and `*.probe.ps1` files there).
Group by `<base>` — the sample's name with its last **two** extensions stripped. Match `<base>` to a
`GuardrailDefinition` in that task's `Guardrails` whose `Name` equals it: `Name` is already the
guardrail file's basename without extension, so the join is an ordinal string equality, not a path
computation.

**2. Run each half.** Resolve the guardrail's interpreter with `InterpreterMap` and launch it through
`ProcessRunner` — the same path `ScriptUnitRunner` already takes for a script guardrail. Do not
re-implement interpreter resolution.

- **Working directory = `plan.Workspace`.** A guardrail's built-in default subject is a
  repo-relative path; running from anywhere else turns "the guardrail ignored the sample" into a
  crash, and a crash reads as a correct rejection. That misdiagnosis is the whole failure mode here.
- **Supply the sample BOTH ways, every run**: the **absolute sample path as the first positional
  argument**, and the same path in the **`GR_SUBJECT`** environment variable. The committed corpus
  uses both conventions (`param([string]$SubjectPath = …)` binds the argument; `$env:GR_SUBJECT`
  binds the variable) and a verifier that supplies only one silently mis-verifies half the corpus.
  Measured on this tree: the `param`-style guardrail at
  `docs/plans/model-tiering-stage-3/wave-01-config-net/tasks/01-allocate-diagnostic-codes/guardrails/02-codes-allocated.ps1`,
  driven by `GR_SUBJECT` alone, exits **0 for BOTH halves** of its own committed pair — it never saw
  the sample and scanned the real tree instead.
- **Bound every run** with the supplied per-sample timeout, and treat a timeout as a run that
  produced no usable exit code — an `Unverifiable` finding naming the timeout, never a silent pass.

**3. Classify.** One finding per problem, each with its own kind and an actionable message:

| Kind | When | The message must say |
|---|---|---|
| `ValidHalfFailed` | `.valid` exited non-zero | the guardrail rejects a representative CORRECT artifact — it false-reds. If the `.invalid` half exited non-zero too, say so: the guardrail may not be reading the sample at all. |
| `InvalidHalfPassed` | `.invalid` exited 0 | this guardrail **can never fail**. GR2055 lints the guardrail that can never PASS; running the `.invalid` half is the only detector for the opposite and more dangerous polarity. If the `.valid` half exited 0 too, say so: the guardrail may not be reading the sample at all. |
| `ReversedPolarity` | `.valid` non-zero **and** `.invalid` zero | the two halves are swapped, or the guardrail's sense is inverted. **ONE** finding, not two — two findings for one cause is noise an operator learns to skim. |
| `MissingHalf` | one side committed without the other | a one-sided pair certifies nothing; name which half is absent. |
| `OrphanSample` | `<base>` matches no guardrail in that task | the pair is STALE — the script was renamed or deleted and the samples were left behind. |
| `Unverifiable` | the matched guardrail is `ActionKind.Prompt`, its interpreter does not resolve, or the run timed out | say which, and say that a pair that cannot be executed is the same "recorded but never run" failure this feature exists to end. Never skip it silently. |

**Every finding names the guardrail path, the sample path, and the observed exit code.** A report that
says "a pair is wrong" without saying which is unactionable, and the operator who cannot act on it
deletes the check.

### Three properties that are not negotiable

- **Read-only apart from its own temp dirs.** The verifier executes guardrail scripts; it must not
  write into the plan folder or the workspace itself. By doctrine the only guardrails that carry a
  sample pair are source-shape checks — they read and grep — so this is cheap and safe in practice,
  and it is the reason the preflight step in task 04 can afford to run on every run.
- **Cheap by construction.** Only guardrails that have a committed sample pair are ever executed. A
  plan with no pairs must cost one directory probe per task and nothing else — no process launches, no
  `PairsVerified` inflation. This step runs before every run once task 04 wires it, so a tax here is a
  tax on every task in every plan (plan of record §7).
- **A findings list, never a throw.** A malformed samples folder, an unreadable file, a guardrail that
  crashes — each is a FINDING with a message, not an exception out of `VerifyAsync`. The callers are a
  CLI verb and a pre-DAG phase; an exception there loses the diagnosis that is the whole product.

### Two things this task must NOT do

- **Do NOT wire anything.** This task implements the type only. The `guardrails samples verify` verb
  (task 03) and the preflight-phase step (task 04) are outside your write scope. In particular, do
  **not** touch `validate` — validate is static and offline, runs in editors and mid-authoring, and
  making it execute arbitrary PowerShell is a semantic change this plan deliberately does not make
  (plan of record §1).
- **Do NOT weaken a test to make it pass**, and do not add `[Fact(Skip=…)]` anywhere — the test file
  is out of scope, so any such edit fails the write-scope check immediately.

Use the BCL and the existing `Guardrails.Core.Execution` types; add no package reference (the
`.csproj` is out of scope). Match the file's surrounding house style — build policy is centralised in
`Directory.Build.props`.
