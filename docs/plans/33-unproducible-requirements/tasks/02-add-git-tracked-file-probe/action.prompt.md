## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-add-git-tracked-file-probe`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-add-git-tracked-file-probe": { "someKey": "someValue" } }`.
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

GR2060 (built in a later task) must ask one question this validator cannot currently answer: **is this
workspace path git-tracked?** Add the probe, following `IScriptSyntaxProbe` as the precedent in every
respect.

**1. `src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs`** — declare `public interface
IGitTrackedFileProbe` in `Guardrails.Core.Loading`, with a member that answers whether a
workspace-relative path is tracked. **Carry across `IScriptSyntaxProbe`'s "silence is not proof"
contract explicitly in the XML doc**: when git is unavailable or the answer cannot be obtained, the
probe reports **not-known**, and a not-known answer must never be read as "untracked". GR2060 is an
ERROR-severity check; a probe that answers "untracked" when it simply could not run would make it fire
on a correct plan, which is the one failure mode this design cannot afford.

In the **same file**, declare `public sealed class NullGitTrackedFileProbe : IGitTrackedFileProbe` — the
no-git default that knows nothing — exactly as `NullScriptSyntaxProbe` lives beside its own interface.

**2. `src/Guardrails.Core/Loading/GitLsFilesProbe.cs`** — the real implementation, backed by
`git ls-files`. One process spawn per validation, not one per path: take the whole candidate set and
answer them together. If git is absent or the command fails, return **not-known** for everything rather
than throwing — `validate` must stay runnable outside a git checkout.

**3. `src/Guardrails.Core/Loading/PlanValidator.cs`** — add a **FIFTH** constructor overload. The chain
today is:

```
PlanValidator()                                              -> this(new PathExecutableProbe())
PlanValidator(IExecutableProbe)                              -> this(probe, BannedPatternRegistry.Load())
PlanValidator(IExecutableProbe, BannedPatternRegistry)       -> this(probe, banned, new InterpreterScriptSyntaxProbe(probe))
PlanValidator(IExecutableProbe, BannedPatternRegistry, IScriptSyntaxProbe)   <- the current terminus
```

Extend it the way the syntax probe was added: the current 3-arg terminus gains a **real default** for the
new probe and delegates to a new 4-arg terminus.

**The N3 gate — read this before you touch the constructor.** There are **73** `new PlanValidator(`
call sites across `tests/` and `src/Guardrails.Cli`. Verify that count yourself before and after:

```
grep -rn "new PlanValidator(" src tests --include=*.cs | wc -l
```

Every one of them must still compile **unchanged**, which means the new parameter arrives with a
default and no existing signature changes arity for an existing caller. In your commit message, state
plainly **which default those 73 call sites now silently receive** — a real `GitLsFilesProbe`, or the
Null one — because that is a behaviour change nobody wrote down at any of the 73 sites.

**Do NOT change either composition root's signature.** They are `src/Guardrails.Cli/PlanProbe.cs:86`
and `src/Guardrails.Core/Execution/Scheduler.cs:2213`. Grep for them; the line numbers are an
authoring-time snapshot and task 1 has already edited this file.

Nothing in this task reads a guardrail clause or emits a diagnostic. It adds a capability and wires its
default; GR2060 arrives in task 4.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/IGitTrackedFileProbe.cs`, `src/Guardrails.Core/Loading/GitLsFilesProbe.cs`
and `src/Guardrails.Core/Loading/PlanValidator.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths — including any of the 73 call sites, the
`.csproj`, or a test file. An out-of-scope edit fails the task immediately and consumes a retry. If a
call site does NOT compile, that is this task's bug to fix **in the constructor's defaulting**, not at
the call site; if you believe a call site genuinely must change, write
`{"needsHuman": "<which call site and why>"}` to the state-out path and stop.

## Done when

- The three files exist with the shapes above and `dotnet build` is green.
- `grep -rn "new PlanValidator(" src tests --include=*.cs | wc -l` still reports **73**, all compiling
  unchanged.
- Neither composition root's signature changed.
