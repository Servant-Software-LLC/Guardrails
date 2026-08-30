## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "00-land-the-required-role-seam": { "someKey": "someValue" } }`.
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

Read: **plan sections 3.4 and 6.5**.

## Task

### What to build
Land the `PromptRole` seam from plan 28 §3.4 as a **mechanical, compile-only** change. You are NOT
assigning correct roles here and you are NOT writing tests — later tasks do both. Your single job is to
make the seam exist and leave the solution building.

## Do exactly this

1. Add the enum, with these three members and no others (§3.4):

```csharp
public enum PromptRole
{
    /// <summary>Produces work: writes files other than its own verdict, or runs commands.</summary>
    Action,
    /// <summary>Renders a verdict on work. Reads; writes only the verdict file.</summary>
    Guardrail,
    /// <summary>Renders an opinion the harness may not treat as a verdict. Reads; writes nothing.</summary>
    Advisory
}
```

2. Add to `PromptInvocation`:

```csharp
/// <summary>What this prompt is FOR. Set by the harness at every call site; a runner may refuse a
/// role its class cannot honestly serve (SSOT §9).</summary>
public required PromptRole Role { get; init; }
```

3. Set `Role = PromptRole.Action` at **every** construction site — in `src` and in `tests` alike.

4. Fold in §6.5's **empty-path convention** while you are in this file: move it out of the comment it
   lives in today and into the XML docs of `StreamLogPath`, `WorkingDirectory` and `PlanDirectory`,
   stating that an EMPTY string is legal and means "no log / no cwd / no plan dir", not "abort".

   This rides along here rather than in its own task for one reason: it edits `PromptInvocation.cs`, and
   this is the only task in the plan that may write that file. Splitting it out would create a second
   task whose sole job is a doc edit to a file it would have to contend with this one over.

## `required` is the point, and a default is the failure

Do **not** give `Role` a default or an initializer, and do not make it nullable. §3.4: *"A default would
let a new call site silently acquire the permissive value. The compiler is the gate."* A default makes
everything compile while touching nothing, which is the one outcome this task must not produce. A
guardrail checks for this directly.

## Action EVERYWHERE, including the sites where it is wrong

Three of the seven `src` producers really are `Action`; four are not (`GuardrailRunner` is a `Guardrail`,
`Overwatch` / `NeedsHumanTriage` / `CriticalityJudge` are `Advisory`). **Set `Action` at all of them
anyway.** That uniform stub is deliberate: the next task authors tests that must FAIL for exactly those
four, and correcting them here would erase the red bar those tests exist to show. Resist the urge to be
helpful — assigning the correct roles is task `02-assign-roles-at-seven-sites`, not this one.

## Finding every site — the compiler, not a grep

`grep "new PromptInvocation"` finds SIX of the seven `src` sites and MISSES most of the test fixtures,
because several construct through a target-typed factory:

```csharp
private static PromptInvocation Invocation(PromptRunnerSettings settings) => new() { ... };
```

`CriticalityJudge.cs:325` is the `src` one shaped this way (§3.4 calls it out by name). So do not work
from a grep: make the change, run `dotnet build Guardrails.sln -c Debug`, and let **CS9035** enumerate
the sites for you. Repeat until it builds. That is the authoritative list and the reason the build is
this task's gate.

Your `writeScope` already names every file the compiler will point at. If it points at one that is NOT
in your `writeScope`, do not edit it and do not work around it — write `needsHuman` saying which file
and which line, because that means the seam has grown a new construction site since this plan was
authored and a human needs to widen the scope.

## Do not

- Do not delete, rename, empty, or `[Skip]` a test to make the build pass. If a fixture will not
  compile, fix it by adding the `Role` line. A guardrail asserts each of the eight fixtures still sets
  `Role` itself.
- Do not touch any file outside your `writeScope`.
- Do not add tests. The seam's tests are the next task's deliverable.
