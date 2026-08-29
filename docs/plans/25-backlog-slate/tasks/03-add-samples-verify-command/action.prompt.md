## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "03-add-samples-verify-command": { "someKey": "someValue" } }`. The harness
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

Add the **`guardrails samples verify [folder]`** CLI verb: it drives the `SampleVerifier` task 02
implemented and reports every mismatch with the guardrail path, the sample path and the observed exit
code.

**Write these files:**

1. `src/Guardrails.Cli/Commands/SamplesCommand.cs` — the `samples` command group and its `verify`
   leaf, following the house pattern in `src/Guardrails.Cli/Commands/SkillsCommand.cs` (a group
   `Command` whose leaf carries the options and the action) and taking the shared optional folder
   positional from `src/Guardrails.Cli/Commands/FolderArgument.cs`.
2. `src/Guardrails.Cli/CommandFactory.cs` — register the group so the verb is reachable from the real CLI.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/SamplesCommand.cs` and `src/Guardrails.Cli/CommandFactory.cs`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside those paths —
including `SampleVerifier.cs`, any test project, or a `.csproj`. An out-of-scope edit fails the task
immediately and consumes a retry. If you hit a compile error caused by a missing symbol in another
file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and
stop.

> **Where to register: the house site, `CommandFactory.BuildRootCommand`.** Add one line beside the
> existing registrations, in the same form as its neighbours:
> ```csharp
> rootCommand.Add(SamplesCommand.Create(io));
> ```
> Take the `IConsoleIo io` that `BuildRootCommand` already receives — do NOT reach for
> `SystemConsoleIo.Instance` inside the factory. That parameter exists precisely so tests can build a
> root command with a `StringWriter`-backed double and capture output per-invocation; hard-coding the
> process-global console defeats it for your verb alone, silently, and only for tests written later.
>
> Do **not** register in `Program.cs`. It is six lines that delegate to this factory, so a subcommand
> added there is invisible to every `CommandFactory`-based test — the verb would work by hand and be
> untestable in the house harness. `Program.cs` is out of scope for this task.

### What the verb does

**Read `docs/plans/25-backlog-slate.md` §1 before you start** — it is the plan of record and it settles
the shape. Then read `src/Guardrails.Core/Samples/SampleVerifier.cs` (task 02's landed
implementation) and `src/Guardrails.Cli/Commands/ValidateCommand.cs` (the closest house precedent for
a folder-taking verb).

- **Load the folder with `PlanLoader`, not `PlanProbe.LoadAndValidate`.** This verb verifies SAMPLES;
  it is not a second `validate`. A folder that loads but carries validation diagnostics must still be
  sample-verified — that is exactly the mid-authoring case this is useful in. Refuse only when the
  loader could not produce a `PlanDefinition` at all, and then say so and exit non-zero.
- **Call `SampleVerifier`.** Do not re-implement pair discovery, the two-way sample binding, or the
  classification — a second implementation of that policy in the CLI drifts from the one the preflight
  phase (task 04) runs, and the two disagreeing is the exact failure this feature exists to detect.
- **Print one line per finding**, and each line must carry the **guardrail path**, the **sample path**
  and the **observed exit code**. A report that says "a pair is wrong" without saying which is
  unactionable, and the operator who cannot act on it deletes the check.
- **Say WHY the check exists in the failure text.** The harness already lints the guardrail that can
  never PASS (**GR2055**). The dangerous polarity — the guardrail that can never FAIL — has no check,
  and running the `.invalid` half *is* that detector. An operator who understands that will not delete
  it; one who reads only "sample mismatch" will.
- **Exit codes:** `ExitCodes.Success` when every pair checks out, `ExitCodes.HarnessError` when any
  finding is reported or the folder could not be loaded. Print a final summary line naming how many
  pairs were verified and how many findings were reported, so a green run says something rather than
  nothing.
- **Read-only.** The verb executes guardrail scripts against committed samples; it writes nothing into
  the plan folder or the workspace beyond its own temp dirs. It must be CI-runnable and cheap.

### Pin the spellings — the guardrails and a future operator both depend on them

- The group command is constructed as `new Command("samples", …)` and its leaf as
  `new Command("verify", …)`. Do not route the leaf's name through a parameter; there is one spelling
  here and no alias to serve.
- The public factory is `SamplesCommand.Create(IConsoleIo io)`, matching every sibling command.

### Not in scope, deliberately

- **`validate` does not change.** Validate is static and offline, runs in editors and mid-authoring,
  and must stay that way. Making it execute arbitrary PowerShell is a semantic change this plan does
  not make (plan of record §1).
- **No preflight wiring here.** The preflight-phase step is task 04 and touches
  `src/Guardrails.Cli/PlanPreflightPhase.cs`, which is outside your write scope.
- **No SSOT edit here.** Recording the verb in `docs/plans/02-schemas-and-contracts.md` belongs to
  this plan's terminal documentation task.
