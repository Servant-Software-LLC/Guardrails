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
3. `tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs` — the **agreement test** that
   binds the verb to the shared verifier. Specified in full in *The agreement test* below; it is a
   deliverable of this task, not an optional extra.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/SamplesCommand.cs`, `src/Guardrails.Cli/CommandFactory.cs` and
`tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside those three paths — including
`SampleVerifier.cs`, any OTHER test file, or a `.csproj`. An out-of-scope edit fails the task
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

**Read `docs/plans/26-guardrail-quality-gate.md` §3 (The design) before you start** — it is the plan of record and it settles
the shape. Then read `src/Guardrails.Core/Samples/SampleVerifier.cs` (task 02's landed
implementation) and `src/Guardrails.Cli/Commands/ValidateCommand.cs` (the closest house precedent for
a folder-taking verb).

- **Load the folder with `PlanLoader`, not `PlanProbe.LoadAndValidate`.** This verb verifies SAMPLES;
  it is not a second `validate`. A folder that loads but carries validation diagnostics must still be
  sample-verified — that is exactly the mid-authoring case this is useful in. Refuse only when the
  loader could not produce a `PlanDefinition` at all, and then say so and exit non-zero.
- **Call `SampleVerifier`.** Do not re-implement pair discovery, the two-way sample binding, or the
  classification — a second implementation of that policy in the CLI drifts from the one the preflight
  phase (task 05) runs, and the two disagreeing is the exact failure this feature exists to detect.
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

### The agreement test — what actually binds the verb to the shared verifier

`tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs`. The test class MUST be named
**`SamplesCommandTests`** and every test MUST carry `[Trait("Category", "BacklogSlate")]`. Both are
load-bearing: this task's guardrail filters on the class name, and the plan's baseline preflight
excludes that trait.

**Why this file exists, stated plainly, because it changes how you should write it.** "Call the shared
`SampleVerifier` instead of re-implementing the policy" was, until now, backed by a source grep for the
text `SampleVerifier.VerifyAsync(`. That grep was MEASURED to be defeatable two ways — a dead field,
and (the form this repo's `TreatWarningsAsErrors=true` does not catch, because an unused private
*method* raises no diagnostic) a `private static object NeverCalled() => SampleVerifier.VerifyAsync(…);`
— either of which satisfies the text while `SamplesCommand` re-implements pair discovery and polarity
inline. The reachability smoke does not catch it either, because a faithful duplicate genuinely works.
So the prohibition is now carried by a **property**, not by a spelling:

> **For a corpus of sample pairs, the findings the verb REPORTS must equal the findings
> `SampleVerifier.VerifyAsync` returns for that same corpus.**

That is what an inlined second implementation cannot survive. It passes today, while the copy is still
faithful — and it fails the moment the two drift, which is the only moment the rule matters and exactly
the failure this whole feature exists to detect (the verb and the preflight phase disagreeing about
whether a pair is sound). Write the test so that property is what it checks; do not write a test that
merely asserts the verb printed something.

**Author exactly these test methods, named verbatim — the guardrail's census greps for these names:**

| Test method name | What it must establish |
|---|---|
| `Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACorpusThatProducesFindings` | Build a fixture plan whose pairs produce **at least one** finding of at least two different kinds. Call `SampleVerifier.VerifyAsync` on it directly to get the reference findings. Then invoke the verb on the same corpus. Assert the verb's report accounts for **every** reference finding and reports **no more than** those — same count, and for each one the guardrail path, the sample path and the observed exit code. This is the load-bearing test; a corpus with a single finding, or with only one kind, makes it much weaker. |
| `Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACleanCorpus` | The other half of the equality: a fixture corpus whose pairs are all two-sided sound. `SampleVerifier.VerifyAsync` returns no findings, and the verb must report none and exit `ExitCodes.Success`. Without this half, "agreement" would be satisfied by a verb that reports everything as broken. |
| `Verify_TheVerbsExitCode_FollowsSampleVerifiersVerdict_NotItsOwnJudgement` | Drive both corpora and assert the exit code tracks `SampleVerifyResult.Passed` — success when the verifier passed, `ExitCodes.HarnessError` when it reported any finding. The verdict is the verifier's to make; the verb only reports it. |

**How to write it — the house idiom, which you should copy rather than invent.**
`tests/Guardrails.Integration.Tests/LockCliTests.cs` is the closest precedent and its own class comment
makes the argument: it drives the verb through the REAL composition root,
`CommandFactory.BuildRootCommand(io)`, because "a `lock` that works only via a hand-built root but is
missing from the factory would ship broken". Same shape here:

```csharp
var io = new StringConsoleIo();                       // tests/Guardrails.Integration.Tests/StringConsoleIo.cs
var root = CommandFactory.BuildRootCommand(io);
int exit = await root.Parse(["samples", "verify", planDir]).InvokeAsync();
// io.OutText is exactly this invocation's output - no process-global console, parallel-safe
```

- Build every fixture plan in a **temp directory** and delete it in `Dispose`/`finally`, as
  `FolderArgumentTests` and `LockCliTests` do. Never write a fixture into the repository tree.
- The fixture guardrail scripts are real scripts in the shell the host supports — `.ps1` on Windows,
  `.sh` elsewhere (mirror the `OperatingSystem.IsWindows()` switch the sibling tests use).
- Compare against what `SampleVerifier.VerifyAsync` **actually returns at run time**. Do NOT hard-code
  the expected findings as literals: a hard-coded expectation is a second copy of the policy inside the
  test, so it would drift exactly like the inlined implementation this test exists to forbid, and the
  test would then agree with nothing.

**What this test does not prove, so you know where the remaining risk sits.** It binds the verb's
OUTPUT to the verifier's findings; it does not prove the verb literally calls that method rather than
producing identical output some other way. That residual is what guardrail 01's cheap source grep still
covers, and it is fine — an implementation that produces byte-equal findings for every corpus is the
shared policy, whatever it is spelled like.

### Pin the spellings — the guardrails and a future operator both depend on them

- The group command is constructed as `new Command("samples", …)` and its leaf as
  `new Command("verify", …)`. Do not route the leaf's name through a parameter; there is one spelling
  here and no alias to serve.
- The public factory is `SamplesCommand.Create(IConsoleIo io)`, matching every sibling command.

### Not in scope, deliberately

- **`validate` does not change.** Validate is static and offline, runs in editors and mid-authoring,
  and must stay that way. Making it execute arbitrary PowerShell is a semantic change this plan does
  not make (plan of record §3).
- **No preflight wiring here.** The preflight-phase step is task 05 (its tests are task 04) and touches
  `src/Guardrails.Cli/PlanPreflightPhase.cs`, which is outside your write scope.
- **No SSOT edit here.** Recording the verb in `docs/plans/02-schemas-and-contracts.md` belongs to
  this plan's terminal documentation task.
