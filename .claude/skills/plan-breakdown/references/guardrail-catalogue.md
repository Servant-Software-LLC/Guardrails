# Guardrail catalogue — archetypes, decision tree, anti-patterns

The quality of a plan breakdown IS the quality of its guardrails. This catalogue is
the selection doctrine: **deterministic over prompts, always.** A unit test beats a
regex; a regex beats an LLM judge. Every guardrail must answer one question:

> **"What wrong implementation does this catch?"**

Write that answer as a comment at the top of every guardrail file (`# catches: …` in
scripts; an HTML comment or frontmatter note in prompt guardrails). If you cannot
write the sentence, the guardrail is decorative — delete it.

**Two layers.** This catalogue holds the **universal** doctrine — archetypes, the
decision tree, the demotion gate, and stack-agnostic anti-patterns. Stack-specific
*idioms* (how .NET registers a project in a solution, how Java declares an interface,
the canonical build command, the layout-specific grep-scope traps) live in a **stack
file** — `references/stacks/<stack>.md` — which SKILL.md Step 0 loads for the detected
stack. When this catalogue says "the exact regex/command lives in the stack file,"
follow that pointer; never bake a `.NET`-only pattern into a guardrail on a JVM/Go/
Python project.

## Archetypes (strongest/cheapest first)

| # | Archetype | Form | Use when | Catches |
|---|-----------|------|----------|---------|
| 1 | **file-exists / file-contains** (regex) | script | Any artifact-producing task — almost always guardrail #1 | Agent claimed success without producing the artifact, or produced the wrong shape |
| 2 | **command-exit-code** | script | Task output is itself runnable; CLI behavior checks | Artifact exists but is broken when actually executed |
| 3 | **build-passes** | script (`dotnet build`) | Any code-producing task | Code that doesn't compile |
| 4 | **specific-tests-pass** | script (`dotnet test --filter`) | Behavior implementation — filter to THIS task's tests; whole-suite green belongs to a terminal integration task only | Wrong behavior, regressions in the targeted area |
| 5 | **lint/format clean** | script | The repo already has a configured linter (never introduce one ad hoc) | Style/usage violations the repo's standards forbid |
| 6 | **schema-validates** | script | Task emits structured data and a schema exists (or you inserted a schema-author task) | Structurally invalid output |
| 7 | **port/endpoint-answers** | script (probe + curl, owns process start/stop, with timeout) | Task delivers a running service behavior | Service that builds but doesn't actually serve |
| 8 | **tests-fail-on-current-code** | script | THE distinctive one — for inserted test-author tasks; run the new tests against the pre-implementation code and require failure (or skipped-with-reason) | Tautological tests that pass against a stub and verify nothing |

> **Compile-coupled tests:** when the new tests reference not-yet-existing symbols
> (a new property, constant, or type), the test project won't even compile against
> current code — so a separate tests-build guardrail would fail at the same moment
> tests-fail-on-current-code requires failure. In that case DROP the tests-build
> guardrail and let tests-fail-on-current-code carry both (non-zero exit = compile
> failure OR test failure, either proves non-tautology). Keep tests-build only when
> the tests compile against current code (e.g. they exercise a CLI flag or file
> output rather than new API surface).
| 9 | **verify-recorded-action-result (don't replay)** | script | The action ALREADY ran an expensive command (a build+test) and the postcondition is expressible from what it recorded — verify the recorded output/artifact instead of re-running the command | A wasteful replay of the action's own work — see the dedicated section for the GOOD-vs-BAD-target rules (this is a speed/flake trade-off, NOT a free correctness win) |
| 10 | **prompt-judge** | `.prompt.md` (writes `{pass, reason}` verdict) | **LAST RESORT** — see the demotion gate | Genuinely subjective properties: tone, clarity, design taste |

### file-contains: structural vs. keyword matching (universal)

A `file-contains` regex must match the **construct**, not a bare keyword that can also
appear in a comment, an import/`using`, a string literal, or a locally-defined copy of
the thing you meant to require. A check for "implements interface IFoo" that greps for
the token `IFoo` passes on `// IFoo`, on `using …IFoo`, and on a class that declares its
*own* local `IFoo` — none of which prove the real type was implemented. Match the
language's declaration syntax instead: `class Foo : IBar` (C#), `implements Foo` /
`extends Bar` (Java/TS), `func (r Recv) Method` (Go). This principle is stack-agnostic;
the **exact regex per language lives in the stack file** (`references/stacks/<stack>.md`,
e.g. the C# class-declaration pattern in `stacks/dotnet.md`).

**Member-order insensitivity (#112).** A structural check must also be insensitive to the
**free ordering of members/accessors** inside the construct. A property's accessors have no
fixed order in C# — `{ get; init; }` ≡ `{ init; get; }` ≡ `{ get; set; }` ≡ `{ set; get; }` —
so a regex keyed on a fixed leading accessor (`…NAME\s*\{\s*get`) **false-passes a "property
removed" check** when the property survives as `{ init; get; }` (init first), shipping an
incomplete refactor green; it **false-fails a "declared" check** symmetrically. Key the match
on the order-free part of the declaration — **up to the opening brace** (`public\s+TYPE\s+NAME\s*\{`)
— and, only if accessor presence matters, test for `(get|set|init)` **anywhere inside** the
accessor block, never a fixed leading accessor. The exact C# property-declaration regex is
`stacks/dotnet.md §3.1`. The same rule applies to any `class/record/interface … { … }` check:
anchor on the part of the syntax whose order the language fixes, never on whichever
member/accessor happens to be written first.

### Positive-effect / non-hollow assertion (universal) (#73)

The structural-vs-keyword rule has a sibling on the *value* side: a guardrail must
assert a **positive observable OUTCOME**, never merely the **absence of an error** — a
zero exit, a `NotNull`, or the bare *presence* of an assertion keyword. An assertion that
green-lights a zero/null/empty result is structurally a **no-op for a "did anything get
produced?" question**: a terminal e2e that runs a full migration and asserts
`Assert.Equal(0, writer.Count)` certifies a no-op while reporting success. This is the
terminal-task analogue of `tests-fail-on-current-code` (archetype #8) — asserting a
zero/null quantity is equivalent to asserting nothing at all about an output quantity.

The trap has two shapes:

1. **Hollow keyword-presence on a count.** A regex that requires only that an assertion
   *mentions* a quantity token — `Assert.*\([^)]*(Moved|Written|Count|Entities)` — matches
   `Assert.Equal(0, writer.Count)`. The keyword is present; the value is zero; the migration
   moved nothing and the run is green. (Note: anchor on `Assert.*\(` / `Assert\.\w+\(`, not
   `Assert\w*\(` — the latter's `\w*` cannot span the `.` in the dotted xUnit form
   `Assert.Equal(`, so it silently never matches it. The point stands either way: matching the
   assertion's *text* is the wrong tool; require a positive *value*, below.)
2. **Absence-of-error standing in for presence-of-effect.** A terminal guardrail whose
   whole assertion is `exit 0` / no exception / `Assert.NotNull(result)`. "It didn't throw"
   and "the handle isn't null" are *necessary* but never *sufficient* for "it produced the
   thing" — an empty result is non-null and throws nothing.

**Rule.** When a task's deliverable is a **non-empty quantity of output** — a migration
moved-count, items written, rows produced, entities created — the guardrail MUST require a
**strictly positive** value, not the presence of an assertion keyword and not merely a
non-error exit. Apply this to the terminal/integration e2e task whose action prompt claims
a "how many items were processed" result (see SKILL.md Step 4's decision tree).

- **Pattern to AVOID** (hollow — matches `Assert.Equal(0, x.Count)`):
  `Assert.*\([^)]*(Moved|Written|Count|Entities)`
- **Pattern to USE** (requires a positive value):
  `(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)`

Even stronger than matching the *source text* of an assertion is reading the **runner's
recorded outcome** (a TRX, a structured result file, or a state key the action published)
and asserting the moved-count `> 0` directly — the source-text regex proves a positive
assertion was *written*, not that the run actually *produced* a positive count. Prefer the
recorded-outcome read when the action emits one (verify-recorded-action-result, #9; the
state-output leaf below); fall back to the positivity regex when it does not.

```powershell
# catches: a terminal e2e that runs the migration but asserts a HOLLOW result -
#          Assert.Equal(0, writer.Count) / Assert.NotNull(...) / a bare exit 0 - so a
#          run that moved ZERO entities still goes green. Require a POSITIVE moved-count.
$test = "tests/Migration.E2E/EndToEndTests.cs"
$src  = Get-Content $test -Raw
# AVOID: -match 'Assert.*\([^)]*(Moved|Written|Count|Entities)'  # passes on Assert.Equal(0, x.Count)
if ($src -notmatch '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)') {
    Write-Output "$test never asserts a POSITIVE moved-count (>0) - a zero-entity migration would pass"
    exit 1
}
exit 0
```

```bash
# catches: a terminal e2e that runs the migration but asserts a HOLLOW result -
#          Assert.Equal(0, writer.Count) / Assert.NotNull(...) / a bare exit 0 - so a
#          run that moved ZERO entities still goes green. Require a POSITIVE moved-count.
set -euo pipefail
test="tests/Migration.E2E/EndToEndTests.cs"
# AVOID: grep -E 'Assert.*\([^)]*(Moved|Written|Count|Entities)'  # passes on Assert.Equal(0, x.Count)
if ! grep -Eq '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)' "$test"; then
    echo "$test never asserts a POSITIVE moved-count (>0) - a zero-entity migration would pass"
    exit 1
fi
exit 0
```

### verify-recorded-action-result: don't replay, but don't trust a log either (#62)

The harness hands every guardrail the action's **already-captured** outcome
(SSOT §5.1): `$env:GUARDRAILS_ACTION_RESULT` → `action-result.json` =
`{ kind, exitCode, summary }`, plus `$env:GUARDRAILS_ACTION_STDOUT` /
`$env:GUARDRAILS_ACTION_STDERR` (the captured streams). So when an action ran an
**expensive** command — the motivating case is a `dotnet build; dotnet test` action — a
guardrail can verify the postcondition by *reading what the action recorded* instead of
**re-running** the whole build+test suite. The replay is what makes the run slow.

**This is a speed/flake trade-off, not a free correctness win.** Replaying re-executes
reality; reading recorded output trusts a log. Verify-don't-replay is sound **only** when
the postcondition is expressible from recorded output the action **could not fabricate**.
Choose the target deliberately:

**GOOD targets** (recorded output the action could not fabricate):
- An **artifact the action produced** — a built DLL, a generated file. Verify it with the
  ordinary archetypes: file-exists (#1) / file-contains (#1) / command-exit-code (#2).
- A **runner-written structured result file** — a TRX / JUnit / coverage file the *test
  runner* wrote (not the action's prose). Assert it exists and parse it for the pass/fail
  totals the runner recorded.
- An **upstream task's state value** read from `GUARDRAILS_STATE_IN` (or the producer's
  fragment) — already covered by the state-output leaf below.
- `GUARDRAILS_ACTION_RESULT.kind` — confirms *which kind* of action ran (e.g. `script`).
  Useful as a cheap sanity assert; it is **not** a substitute for checking the artifact.

**BAD targets — name these as traps, never generate them:**
- **The action's `exitCode`.** At guardrail time it is **ALWAYS 0** — a non-zero action
  fails the attempt *before* any guardrail runs (SSOT §5.1, §6.1). `if ($result.exitCode
  -ne 0)` is a pure **tautology**: it can never fire. (This is also why there is no
  `GUARDRAILS_ACTION_EXIT_CODE` env var — it would be tautological by construction.)
- **The action's own self-reported success line in `_STDOUT`.** Grepping
  `GUARDRAILS_ACTION_STDOUT` for `"Passed!"` / `"Build succeeded"` / `"0 Error(s)"` is an
  **echo-judge**: the action narrates its own success, so the guardrail trusts the thing
  it is supposed to check. It is also **format-brittle** — that exact wording rots across
  SDK / runner versions, so the guardrail silently passes (or spuriously fails) on an
  upgrade. The runner's *structured* result file (TRX) is the honest read; its *prose
  stdout* is not.

**When the strong postcondition isn't expressible from recorded output, re-executing
reality IS the honest gate.** Don't replace a strong replay (e.g. `dotnet test --filter`
that actually runs the targeted tests) with a weak grep just to save time — a slow honest
check beats a fast tautology. Reach for verify-recorded-result only when a GOOD target
above carries the postcondition.

GOOD snippet — verify the runner-written TRX the action's `dotnet test` produced (and a
produced artifact), NOT the exit code and NOT a stdout success word:

```powershell
# catches: the build+test action ran but did not produce its built artifact, OR the test
#          runner recorded failing tests — verified from the recorded TRX, without
#          re-running the build+test suite (a wasteful replay of the action's own work)
$result = Get-Content $env:GUARDRAILS_ACTION_RESULT -Raw | ConvertFrom-Json
if ($result.kind -ne 'script') {
    Write-Output "expected a script action; recorded kind = '$($result.kind)'"
    exit 1
}
# GOOD target 1: an artifact the action PRODUCED (could not fabricate by narrating success)
$dll = 'src/MyProj/bin/Release/net8.0/MyProj.dll'
if (-not (Test-Path $dll)) {
    Write-Output "build artifact missing: $dll (the action claimed success but produced no DLL)"
    exit 1
}
# GOOD target 2: the TRX the TEST RUNNER wrote — parse its recorded totals, do not re-run tests
$trx = Get-ChildItem 'TestResults' -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx result file under TestResults/ — the action did not record a test run"
    exit 1
}
$counters = ([xml](Get-Content $trx.FullName -Raw)).TestRun.ResultSummary.Counters
if ([int]$counters.failed -gt 0) {
    Write-Output "TRX records $($counters.failed) failing test(s) — see $($trx.Name)"
    exit 1
}
# Do NOT add `if ($result.exitCode -ne 0)` (always 0 here — tautology) and do NOT grep
# $env:GUARDRAILS_ACTION_STDOUT for "Passed!" (echo-judge, SDK-version-brittle).
exit 0
```

```bash
# catches: the build+test action ran but did not produce its built artifact, OR the test
#          runner recorded failing tests — verified from the recorded TRX, without
#          re-running the build+test suite (a wasteful replay of the action's own work)
set -euo pipefail
kind=$(jq -r '.kind' "$GUARDRAILS_ACTION_RESULT")
if [ "$kind" != "script" ]; then
    echo "expected a script action; recorded kind = '$kind'"
    exit 1
fi
# GOOD target 1: an artifact the action PRODUCED
dll='src/MyProj/bin/Release/net8.0/MyProj.dll'
if [ ! -f "$dll" ]; then
    echo "build artifact missing: $dll (the action claimed success but produced no DLL)"
    exit 1
fi
# GOOD target 2: the TRX the TEST RUNNER wrote — read its recorded totals, do not re-run
trx=$(find TestResults -name '*.trx' -print0 2>/dev/null | xargs -0 ls -t 2>/dev/null | head -n1)
if [ -z "$trx" ]; then
    echo "no .trx result file under TestResults/ — the action did not record a test run"
    exit 1
fi
failed=$(grep -oP '(?<=failed=")[0-9]+' "$trx" | head -n1)
if [ "${failed:-0}" -gt 0 ]; then
    echo "TRX records $failed failing test(s) — see $(basename "$trx")"
    exit 1
fi
# Do NOT test `jq -r .exitCode "$GUARDRAILS_ACTION_RESULT"` (always 0 here — tautology)
# and do NOT grep "$GUARDRAILS_ACTION_STDOUT" for "Passed!" (echo-judge, runner-version-brittle).
exit 0
```

The action must actually emit a TRX for this to work — `dotnet test --logger "trx"`
writes one. If the action does not produce a runner-written result file and the only
"evidence" of success is its prose stdout, you have **no** GOOD target: keep the honest
replay (`specific-tests-pass`, #4) rather than demoting to an echo-judge.

## Composition-root wiring — the component is CONSTRUCTED/INJECTED in production (#120)

**The recurring lesson, the highest-impact false-green the skill emits.** A plan adds a
new collaborator behind a seam — an `IFoo` interface + a `FooImpl`, injected into some
*assembler* (a factory, a DI container, a `Program.cs`, a `RunCommand`). Every component
task author-tests + implements `FooImpl` against an injected constructor seam, each goes
green, the terminal whole-suite build + test passes — and the feature is **inert**: nothing
ever constructs `FooImpl` and hands it to the assembler in the production path, so the real
entry point never takes the new branch. The tests pass *because they inject the seam
themselves* (`new Scheduler(plan, executor, …, provider)`), which is exactly why they say
nothing about whether production wires it. **Green proves the components in isolation, not
the assembled feature.** This recurred **3×** in one plan (plan-08: the worktree engine, the
AI-merge worker, and the needs-human triage — all built, all unit-tested, all dead from the
CLI because `SchedulerFactory.Create` never constructed/injected them).

This is a sibling of #64 (entry-point wiring) but more general: #64 is the *executable serves
over a port* case (grep `Program.cs` + smoke-test a route); #120 is the *internal collaborator
injected at a composition root* case (a factory/container/wiring method must construct it and
pass it on). The fix is the same shape — **a deliverable plus a guardrail** — applied at the
assembly layer.

**Decision rule — when does this fire?** When a plan introduces a component that must be
**constructed and injected at a production composition root or entry point** to do anything.
Concretely, fire on any of:
- The plan introduces an **`IFoo` + `FooImpl` pair** (or any new collaborator a production
  assembler must construct and pass on). The heuristic: *every `IFoo`/`FooImpl` pair the plan
  adds needs a "wire `FooImpl` into the composition root" deliverable.*
- The new component is reachable only via a **constructor/DI seam** the unit tests inject
  themselves — so the tests pass regardless of whether the production assembler wires it.
- The plan names a **factory / `Program.cs` / `Startup` / DI registration / dispatch site /
  `RunCommand`** that must branch on, construct, or inject the new component.
- The feature activates only under a **mode/flag** (e.g. `maxParallelism > 1`) the production
  dispatch must honour — built machinery reachable "only from xUnit" is the tell.

**Two artifacts close it** (generated in SKILL.md Step 5):
1. **An explicit integration/wiring TASK** — a *named deliverable* distinct from the
   per-component implement tasks: "construct `FooImpl` and inject it into `<the assembler>` so
   the production path uses it." Make a DAG sink depend on it (the wiring is the thing that
   makes the feature real; downstream gates must not be reachable without it). Depends on the
   component-implementation task(s) — the collaborator must exist before it can be wired.
2. **A composition-root guardrail asserting the component is ACTUALLY wired in production** —
   not merely unit-tested in isolation. Two deterministic shapes, strongest first:

   - **(a) Observable-behaviour through the real entry point (strongest).** Drive the real
     composition root / entry point end-to-end (run the CLI on a fixture plan, hit the binary)
     with the new mode active, and assert an **observable output only the wired feature
     produces**. cf. plan-08's `Factory_RunsWorktreeMode_OnCommittedFixturePlan`: it calls the
     **real `SchedulerFactory.Create`** (no manual injection — *a test that injects the provider
     would pass even with an unwired factory and is FORBIDDEN*) at `maxParallelism = 2` and
     asserts the worktree-mode outputs (a `guardrails/<plan>` branch exists; ≥2 commits carry
     `Guardrails-Task:` trailers). This is a `specific-tests-pass` (#4) or
     `port/endpoint-answers` (#7) guardrail pointed at the assembled feature.
   - **(b) Structural/reflection assertion that the assembler injects the collaborator (the
     canonical `Factory_Wires*` shape).** When observable behaviour is too expensive or
     environment-bound to drive in a guardrail, assert structurally that the production
     assembler constructs and passes the collaborator. cf. plan-08's
     `Factory_WiresAiMergeWorker_InWorktreeMode` / `Factory_WiresNeedsHumanTriage_WhenRunnerAvailable`:
     each drives the **real factory** and asserts via reflection that the constructed object holds
     a non-null collaborator field — **with a contrast case** (`maxParallelism = 1`, or a
     script-only plan) asserting it is *null* when it should not be wired, proving the wiring is
     **conditional and real**, not a constant. A pure source grep that `SchedulerFactory.cs`
     contains `new FooImpl(` is the weakest acceptable form (it proves the text exists, not that
     the wired object is reached) — prefer (a), then the reflection form of (b), then the grep.

   The guardrail belongs on the **wiring task** (artifact 1), since that task owns producing the
   wired composition root. It is `scope: "integration"` when it drives the whole assembled feature
   (so it re-runs at union points and on the terminal gate).

**Why the existing gates miss it (state this in the report when it fires).** The TDD pair
(`tests-fail-on-current-code` → `specific-tests-pass`) proves the component; the terminal
whole-suite build + test proves nothing *exercises the composition root* with the new mode —
both go green over an unwired feature. A full-suite-green gate over seam-injected unit tests is
**necessary but not sufficient**. The composition-root guardrail is the missing sufficiency check.

**FORBIDDEN shapes** (the review skill hunts these):
- A guardrail that **constructs `FooImpl` itself and injects it**, then asserts it works — it
  proves the component, never the wiring. The guardrail MUST go through the production assembler.
- Trusting the **terminal whole-suite green** to cover wiring — it cannot; that is exactly the
  structural false-green this archetype exists to catch.
- A **prompt-judge** "is this wired correctly?" — wiring is a deterministic structural fact
  (the object is constructed and passed, or it isn't); demote to (a)/(b).

The .NET realization (the reflection-on-factory pattern + the drive-the-real-factory integration
test) is `stacks/dotnet.md §10`.

## Entry-point wiring + the live smoke-test (server/executable plans) (#64)

A plan whose outcome is a **server or CLI executable** — "a CLI entrypoint that starts a
loopback HTTP server and serves a wizard", "prints a URL", "listens on a port", a `.csproj`
with `Microsoft.NET.Sdk.Web` or `<OutputType>Exe</OutputType>` — decomposes cleanly into
component tasks (scaffold the exe project, implement the launcher, implement the routes),
and **each component compiles and unit-tests green**. The terminal whole-solution build
passes too. Yet a real failure slips through every one of those checks: the `Program.cs`
that never instantiates the `Launcher`. The build is green, the unit tests pass, and the
server 404s everything — because **no task ever wired the entry point to the handler**, and
no guardrail ever ran the binary.

A library/test deliverable is fully covered by the TDD cycle (author tests → implement →
`specific-tests-pass`). An **executable** needs a third kind of check the unit tests
structurally cannot provide: *does running the binary produce the expected observable
behaviour?* `new Launcher().StartAsync()` being absent from `Program.cs` is invisible to any
unit test of `Launcher` — the type works; it's just never called. Two guardrails, on two
inserted tasks (SKILL.md Step 5), close the gap:

1. **Entry-point-wiring (structural grep).** A `file-contains` guardrail on the
   ENTRY-POINT file asserting it references the launcher type — `Program.cs` must mention
   (and start) `Launcher`. This is the universal "structural vs keyword" rule applied to the
   wiring point; the exact .NET regex is `stacks/dotnet.md §7`. It catches the green-build
   `Program.cs` that ignores the launcher. (It is necessary but not sufficient — a grep can't
   prove the wired call actually serves; that's the smoke-test's job.)
2. **Live smoke-test (archetype #7, port/endpoint-answers).** The only guardrail that
   verifies *the exe does what the plan says* rather than *the code compiles*. It STARTS the
   built binary as a background process, POLLS a known route (`/health`, `/current-step`,
   whatever the plan names) until it answers or a bounded timeout elapses, ASSERTS HTTP 200,
   and ALWAYS stops the process in a `finally` (so a failed poll still tears the process
   down). It owns its own start/stop — no separate launch-script ancestor is needed — but the
   route it polls must be produced by an ancestor (artifact-ancestry). The full
   cross-platform script (port handling, bounded poll, `finally` teardown, one actionable
   failure line) is `stacks/dotnet.md §8`.

**Determinism rules for the smoke-test** (it is a live process check, the flakiest archetype
— hold it to these or it poisons the run with false reds):
- **Bounded poll, not a fixed sleep.** Retry the route on a short interval up to a hard
  timeout; a server's warm-up time varies, so a single `sleep 2` is both slower and flakier
  than "poll every 250 ms for up to 15 s".
- **Teardown in `finally`.** The process MUST be killed on every exit path — pass, route
  failure, or exception — or a leaked server holds the port and every subsequent run fails.
- **Deterministic port.** Tell the binary which port to use (CLI arg / env var) and poll
  that exact port, OR parse the port from the URL the binary prints to its captured stdout.
  Never guess. A fixed well-known port risks a collision with a leaked prior run; prefer a
  port the plan fixes for the exe, or an ephemeral one the binary prints.
- **One actionable failure line.** "smoke-test: GET http://127.0.0.1:5005/health did not
  return 200 within 15s (last: connection refused)" converges; "smoke-test failed" loops.

This is **starts-and-serves verification ONLY.** Whether the served page is the *described
UI* — built at all, and returned as real markup — is the **UI-presence** archetype below.
The two compose: this smoke-test proves the exe serves *something*; the served-markup half
of UI-presence proves that *something* is the UI the plan described. Don't duplicate the
process management — the served-markup check *extends* this lifecycle with one body
assertion (see below).

## UI-presence — the described UI was built and is actually served (#66)

A plan whose outcome is **user-facing UI** — "serves a multi-step wizard to the browser",
"a page the user completes", "master/detail view", "tri-state tree", a screen the user
*sees and operates* — has a failure mode distinct from #64's. With #64 in place the binary
starts and a route answers 200; the unit tests pass; the build is green. And still **no UI
exists**: every task decomposed to a JSON HTTP endpoint or a unit test, not one produced an
HTML page, stylesheet, client JS, or a `wwwroot`. The shipped artifact is a working JSON API
with no human-facing frontend, and the run is 100% green because nothing ever asserted a UI
artifact. This is the **most expensive false-green the skill can emit** — a plan promising a
frontend that decomposes to zero frontend tasks.

#64 would only have *caught* that no real UI is served (its smoke-test asserts a 200, which a
JSON root satisfies); it never *builds* the screens. #66 ensures the work to build the UI is
generated in the first place, AND that a guardrail asserts the UI is present and served. The
fix is a **UI-implementation task** per described screen (SKILL.md Step 5) plus a **pair of
deterministic guardrails** — never a prompt-judge:

1. **Asset-exists (archetype #1, file-exists).** A static check that the page/asset the
   screen needs is present on disk (or as a declared embedded resource) — `wwwroot/wizard.html`,
   its stylesheet, its client JS. Scoped to the one file the UI task owns (grep-scope rule). It
   catches the green-build run where no frontend file was ever written. The exact .NET realization
   (`wwwroot/<page>.html` existence, or the embedded-resource manifest check) is `stacks/dotnet.md §9`.
2. **Served-markup-contains (archetype #7, EXTENDING the §64 smoke-test).** The deterministic
   proof that the served root returns the **real UI markup**, not a placeholder, a 404 body, or
   JSON. It reuses the smoke-test's exact lifecycle — start the binary, poll the UI route, tear
   down in `finally` — and adds **one assertion**: the response body **contains a known UI
   element/string** from the page (a heading, a known `id`/`data-` attribute, a wizard step
   label). Asserting HTTP 200 alone is not enough — a JSON API returns 200 from `/`. This is
   **not a second process manager**: fold the body assertion into the existing smoke-test
   guardrail so the process starts once; only stand up a separate one if no executable
   smoke-test exists. The known string MUST come from the markup the UI task produces
   (artifact-ancestry). The .NET realization (the §8 lifecycle with the body-contains assertion)
   is `stacks/dotnet.md §9`.

**Determinism is mandatory here.** UI-presence is *presence and wiring*, never *visual
quality*. The asset-exists grep and the served-markup string are both deterministic; a
prompt-judge "does this look like a good UI" is OUT OF SCOPE and forbidden — it is exactly
the subjective vibes the demotion gate rejects, and worse, it cannot catch the failure
(a frontend can "look good" and still bind to no backend; a present, wired page that
contains the asserted element is the deliverable). The cross-check that a *described* UI
mapped to *some* build-ui task lives in SKILL.md Step 7.0 (exit-criteria self-review).

## The prompt-judge demotion gate

For EVERY candidate prompt-judge, ask all four. Any "no" → demote to a deterministic
archetype:

1. **Is the property genuinely subjective** (tone, clarity, taste)? If a regex,
   schema, or test could check it, it must.
2. **Is it paired with ≥ 1 deterministic guardrail** on the same task? A judge is
   never alone.
3. **Is the judge criterion-specific**, not vibes? "PASS iff the report names every
   failed task" — never "is this good?".
4. **Is it pointed at the raw artifact**, not at anything the action wrote about its
   own work? If the action can game it by writing a flattering summary, point the
   judge at the artifact itself.

The judge prompt must instruct: *you are a verifier; do NOT fix anything; write
`{"pass": bool, "reason": string}` to `GUARDRAILS_VERDICT_OUT`; the reason becomes
retry feedback, so make it actionable.* (The harness appends the full verdict
contract automatically — the prompt only needs the criterion. See
`examples/hello-guardrails/hello-guardrails/tasks/03-quality-check/guardrails/02-tone-is-friendly.prompt.md`
for the golden reference.)

## The decision tree (apply per task)

```
What is the task's primary deliverable?
├── A file/artifact            → file-exists (always) + the strongest content check available:
│                                schema-validates > file-contains-regex > prompt-judge
├── Code (library/feature)     → build-passes + specific-tests-pass (--filter THIS task's tests)
│                                + writeScope on the IMPLEMENTATION task that EXCLUDES the test
│                                │  files (SSOT §3.4): the harness's deterministic read-only
│                                │  write-scope check then catches an implementation that edits the
│                                │  tests instead of fixing the code — see SKILL.md Step 5 and the
│                                │  writeScope test-exclusion rule below
│                                └─ INSERT a test-author task upstream BY DEFAULT (SKILL Step 2
│                                   TDD rule); skip only if tests already exist or behavior is
│                                   too simple for unit tests — state why in task description
│                                   (test-author: tests-fail-on-current-code (8) + declares a
│                                    writeScope covering the test files in task.json; tests-build
│                                    only if tests compile against current code)
├── A runnable script/tool     → file-exists + command-exit-code on a representative invocation
├── A running service /        → entry-point-wiring (grep: the entry point references the launcher)
│    server / CLI executable      + port/endpoint-answers (#7: START the binary, POLL a route,
│                                 ASSERT 200, STOP in a finally) — the ONLY check that the exe
│                                 starts and serves vs merely compiles; see the entry-point-wiring
│                                 section below + stacks/dotnet.md §7–§8
├── A user-facing UI            → asset-exists (#1: the page/asset file is on disk, e.g.
│    (screen/page served to       wwwroot/<page>.html, scoped to the UI file) + served-markup-contains
│    the browser)                 (#7 EXTENDING the smoke-test: same start/poll/teardown, assert the
│                                 body contains a known UI string — NOT just 200, which JSON satisfies).
│                                 INSERT a build-ui-<screen> task per screen, ALONGSIDE the backend
│                                 that serves it. Deterministic only — NO prompt-judge on visual
│                                 quality; see the UI-presence section below + stacks/dotnet.md §9
├── A component injected at a  → composition-root wiring (#120): INSERT a wiring task that
│    production composition       constructs FooImpl and injects it into the production assembler
│    root (IFoo + FooImpl, a      (factory / Program.cs / DI / RunCommand), + a guardrail that
│    factory/DI/Program.cs must   asserts it is ACTUALLY wired — drive the real assembler and
│    construct + inject)          assert observable output (strongest), or reflect on the
│                                 constructed object for the non-null collaborator with a
│                                 contrast case (the Factory_Wires* shape). NEVER inject the seam
│                                 in the guardrail; NEVER trust terminal whole-suite green to
│                                 cover wiring. See the composition-root section + stacks/dotnet.md §10
├── Config/data                → schema-validates; else file-contains on load-bearing keys
├── State output (a key a      → fragment-key-present (read $env:GUARDRAILS_STATE_FRAGMENT,
│    downstream task reads)      parse JSON, assert the key non-null + non-empty; allowed-set
│                                check if a downstream task branches on the value)
├── Docs / prose               → file-exists + file-contains (required headings/terms);
│                                prompt-judge ONLY for genuine subjective quality, never alone
└── Refactor (no new behavior) → build-passes + existing-tests-still-pass (the suite IS the guardrail)
```

**Verify-recorded-result vs. replay (the Code/Refactor branches).** When the *action
itself* already ran the expensive build+test (e.g. a `dotnet build; dotnet test` action)
and recorded a GOOD target — a produced artifact or a runner-written TRX — prefer
**verify-recorded-action-result (#9)** over a guardrail that re-runs the same command.
This is a speed/flake trade-off, sound only against output the action could not fabricate;
when no such recorded target exists, keep the honest replay (`specific-tests-pass`, #4).
See the verify-recorded-action-result section above for the GOOD-vs-BAD-target rules.

**`writeScope` test-exclusion — doctrine (replaces the removed `tests-untouched` triad).**
Test-file integrity is now a **deterministic, read-only harness check**, not a hash-compare
guardrail. The TDD pair declares two scopes: the **test-author task** owns its test files in
`writeScope`; the **implementation task's** `writeScope` EXCLUDES the test files. After the
implementation action runs and before its own guardrails, the harness diffs the task's segment
worktree and asserts every changed path is in scope (SSOT §3.4) — an edit to a test file falls
outside the implementation's scope, fails the check, and retries with feedback naming the
out-of-scope paths. There is no `captureHashes`, no `Get-FileHash` recompute, no `restoreOnRetry`,
and no downstream `tests-untouched` guardrail. The check belongs implicitly to the task whose scope
is declared — the implementation task that must not write the tests — because it runs against that
task's own diff at the moment that task is verified, catching the edit on the exact task that could
have made it. Worktree isolation (physical) + this write-scope check (deterministic) together
replace the `captureHashes`/`tests-untouched`/`restoreOnRetry` triad, with no shared-state hashes to
forge and so no cross-task poisoning surface to defend.

**State-output leaf — the fragment-key contract.** When a task's action publishes a key
to the state fragment (written to `GUARDRAILS_STATE_OUT`) that a downstream task later
reads from its merged snapshot (`GUARDRAILS_STATE_IN`), the *file/build* guardrails do
NOT cover the state hand-off: the action can produce its on-disk artifact yet never write
the key, and the downstream task then runs with a null value. Add a guardrail on the
producing task that reads the not-yet-merged fragment from `GUARDRAILS_STATE_FRAGMENT`
(the env var guardrails get — see schemas.md §5.1), parses it as JSON, and asserts the
key is present, non-null, and non-empty. If a downstream task *branches* on the value,
also assert it is in the allowed set.

```powershell
# catches: action produced its artifact but never wrote the state key a downstream task reads
$fragmentPath = $env:GUARDRAILS_STATE_FRAGMENT
if (-not $fragmentPath -or -not (Test-Path $fragmentPath)) {
    Write-Output "no state fragment written - 'tsw_mechanism_recommended' key is missing"
    exit 1
}
$fragment = Get-Content $fragmentPath -Raw | ConvertFrom-Json
$value = $fragment.'01-research-tsw-write-mechanism'.tsw_mechanism_recommended
if ([string]::IsNullOrWhiteSpace($value)) {
    Write-Output "state key 'tsw_mechanism_recommended' is missing, null, or empty"
    exit 1
}
$allowed = @('rest-api', 'file-drop', 'sdk')
if ($allowed -notcontains $value) {
    Write-Output "state key 'tsw_mechanism_recommended' = '$value' is not in the allowed set ($($allowed -join ', '))"
    exit 1
}
exit 0
```

Drop the allowed-set block when no downstream task branches on the value. Namespace the
key under the producing task id, matching the fragment convention (schemas.md §6.2).

Per task: **minimum 1, typical 2–3, soft max 4** guardrails. Order them
**cheapest-first** by filename (`01-exists`, `02-builds`, `03-tests`, `04-review`) —
the default `failFast` mode stops at the first failure, so a cheap existence check
should fail before an expensive test run or a paid judge ever starts.

## Anti-patterns (the review skill hunts for these — don't generate them)

- **Tautological**: the guardrail checks something the action writes specifically to
  satisfy it ("status.txt contains DONE"). The action controls the evidence.
- **Hollow output assertion** (#73): a terminal/e2e guardrail that asserts only the
  **absence of an error** — `Assert.Equal(0, x.Count)`, `Assert.NotNull(...)`, a bare
  `exit 0`, or the mere *presence* of an assertion keyword
  (`Assert.*\([^)]*(Moved|Written|Count|Entities)` matches `Assert.Equal(0, …)`) — for a
  task whose deliverable is a **non-empty quantity of output** (migration moved-count, items
  written, entities produced). It certifies a no-op: a run that moved zero entities goes
  green. Fix: require a **strictly positive** value — `(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)` —
  or better, read the runner-recorded count / state key and assert `> 0`. See the
  positive-effect / non-hollow assertion section above.
- **Accessor-order-sensitive structural regex** (#112): a property "declared/removed"
  check keyed on a fixed leading accessor — `…NAME\s*\{\s*get` or `…\{\s*set` — is itself a
  finding. C# accessor order is free (`{ get; init; }` ≡ `{ init; get; }`), so the regex
  **false-passes a removal check** when the field survives as `{ init; get; }` (an incomplete
  refactor ships green) and **false-fails a declared check** symmetrically. Fix: match the
  declaration up to the brace (`public\s+TYPE\s+NAME\s*\{`), order-insensitive by construction;
  if accessor presence matters, test `(get|set|init)` anywhere inside the block. See the
  member-order-insensitivity note above; exact regex `stacks/dotnet.md §3.1`.
- **Echo-judge**: a prompt-judge evaluating the action's own claim of success (its
  summary, its commit message) rather than the artifact.
- **Replay-the-action**: a guardrail that **re-runs the action's own command** (e.g. a
  full `dotnet build; dotnet test`) when the postcondition is **cheaply verifiable from
  recorded output** — a produced artifact or a runner-written TRX (SSOT §5.1, the
  verify-recorded-action-result section above). Pure wasted time/flake. Fix: verify the
  recorded artifact/result instead of replaying. (Counter-caution: replaying is the
  HONEST gate when no recorded GOOD target carries the postcondition — don't demote a real
  replay to a weak grep just for speed.)
- **Echo-judge on action stdout / action-exit-code tautology**: a guardrail that greps
  `GUARDRAILS_ACTION_STDOUT` for the action's own success string (`"Passed!"`, `"Build
  succeeded"`) — the action narrates its own success, and the wording rots across runner
  versions — or that tests `GUARDRAILS_ACTION_RESULT.exitCode -ne 0`, which is a pure
  tautology because a non-zero action already failed the attempt before any guardrail ran
  (the recorded exit code is ALWAYS 0 at guardrail time). Fix: read a runner-written
  structured result (TRX) or a produced artifact, never the action's self-report.
- **Over-broad**: "all tests pass" on an early task — it fails for unrelated reasons,
  poisons retries with noise, and serializes the DAG. Whole-suite green belongs to one
  terminal integration task.
- **Compiles-but-never-runs** (server/executable plans, #64): the breakdown emits component
  tasks (scaffold exe, implement launcher, implement routes) each guarded by build +
  unit-tests, plus a terminal whole-solution build — but **no task wires the entry point to
  the launcher and no guardrail ever starts the binary**. Every check is green while the
  server 404s everything (the `Program.cs` that never calls `new Launcher().StartAsync()`).
  Fix: insert the entry-point-wiring task (structural grep, `stacks/dotnet.md §7`) and the
  live smoke-test task (start → poll route → assert 200 → stop in `finally`, `stacks/dotnet.md
  §8`) — see the entry-point-wiring section above. Unit tests structurally cannot catch a
  launcher that is implemented but never called.
- **Built-but-unwired component** (#120) — the recurring lesson, and a structural false-green
  at the assembly layer. The breakdown emits per-component tasks (author tests → implement
  `FooImpl`) each guarded by build + unit-tests through a constructor seam, plus a terminal
  whole-suite gate — but **no task constructs `FooImpl` and injects it at the production
  composition root** (the factory / `Program.cs` / DI / `RunCommand`), and **no guardrail ever
  drives the real assembler with the new mode active**. Every check is green while the feature is
  inert: the production path never branches into it, the machinery is reachable only from xUnit
  (which injects the seam itself). This recurred 3× in one plan (worktree engine, AI-merge worker,
  triage — all built, all dead from the CLI). Distinct from compiles-but-never-runs (#64): there
  the *exe* served nothing over a port; here an *internal collaborator* exists but is never wired
  into the assembler, so the unit-tested seam is dead code in production. Fix: insert the wiring
  task (construct + inject `FooImpl` into the assembler) and a composition-root guardrail that
  drives the REAL assembler and asserts the new mode activates — observable output through the
  entry point (strongest) or a reflection assertion on the constructed object that the collaborator
  is non-null, **with a contrast case** (the `Factory_Wires*` shape). See the composition-root
  section above + `stacks/dotnet.md §10`. The tells `/guardrails-review` hunts: an `IFoo`/`FooImpl`
  pair with no "wire it into the composition root" task; a guardrail that **constructs and injects
  `FooImpl` itself** (proves the component, not the wiring); reliance on terminal whole-suite green
  to cover wiring. **Forbidden "fix":** a prompt-judge "is this wired?" — wiring is a deterministic
  structural fact, asserted by driving the real assembler, never by vibes.
- **Backend-only-greenness for a UI plan** (#66) — the single most expensive false-green.
  The plan describes a **user-facing screen** ("serves a wizard to the browser", "the user
  completes the form", "master/detail view") and the breakdown emits ONLY JSON HTTP
  endpoints, DTOs, and their unit tests — **not one task produces an HTML page, stylesheet,
  client JS, or a `wwwroot`**. Build is green, unit tests pass, and (even with #64's
  smoke-test) the root returns 200 — because a JSON API answers 200. The run is 100% green
  and ships **no human-facing UI whatsoever**. This is distinct from compiles-but-never-runs:
  there the exe served nothing; here the exe serves the *wrong thing* (an API where the plan
  promised a UI), and the UI was never even built. Fix: insert a `build-ui-<screen>` task per
  described screen and the UI-presence guardrails — asset-exists (`stacks/dotnet.md §9`) and a
  served-markup-contains assertion EXTENDING the smoke-test (body contains a known UI string,
  not just HTTP 200) — see the UI-presence section above. The tell `/guardrails-review` hunts:
  a plan whose prose promises a frontend whose task folder contains zero frontend artifacts and
  zero served-markup assertion. **Forbidden "fix":** a prompt-judge "does the UI look good" —
  it is subjective vibes the demotion gate rejects AND cannot catch the failure; the deliverable
  is *presence and wiring*, asserted deterministically.
- **Hidden-state**: the guardrail depends on machine state (network, globally
  installed tools, a developer's home dir) rather than ancestor outputs or the repo.
  Declare required interpreters via `guardrails.json` instead.
- **Unactionable failure**: a guardrail that fails with "FAIL" and nothing else. The
  failure line on stdout becomes the retry feedback — "greeting.txt missing 'Hello'"
  converges; "FAIL" loops.
- **Grep-scope contamination**: a guardrail that checks a property of a file THIS task
  produces but greps the whole project directory for the pattern. A sibling task in the
  same wave can satisfy a broad grep with terminology it happens to share — so the check
  passes even when this task's file is wrong. Scope `Select-String`/`Get-Content` to the
  specific file this task produces, never the project tree.
  - Weak (gameable): `Get-ChildItem src/Desktop -Recurse -Filter *.cs | Select-String -Pattern "LocalAppData"` — a sibling `SettingsService.cs` mentioning `LocalApplicationData` in the same wave satisfies it.
  - Strong: `Select-String -Path "src/Desktop/WorkspaceRecentsList.cs" -Pattern "LocalAppData"` — scoped to the one file this task owns.

## The artifact-ancestry rule

A guardrail may only reference artifacts that are (a) produced by an ANCESTOR task in
the DAG, or (b) pre-existing in the repo. A guardrail that checks something no
upstream task produces will fail forever — that is a missing inserted task (see the
skill's Step 5), not a guardrail problem. Sweep every guardrail against this rule
before writing the folder.
