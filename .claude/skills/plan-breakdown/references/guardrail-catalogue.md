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

### Comment-blind keyword scan — strip comments before forbidden-keyword matching (universal) (#97, #98)

The structural-vs-keyword rule (above) is about a *required* construct a comment/`using`/local
copy can fake. This is its **forbidden**-keyword mirror: a guardrail that scans source text for
**banned** constructs — read-only checks (`MERGE`/`EXEC`/`INSERT`/`xp_cmdshell`), no-shell,
no-eval, `no-console.log`, no-`TODO` — and matches the **raw file including comments** will
**false-POSITIVE on a comment** (and on string literals and disabled code). Same root cause as
the structural rule — *matching raw text, not code* — same fix family — *strip/parse, don't
raw-grep*. But where structural-vs-keyword causes a false **green** (a comment satisfies a
required token), comment-blind scanning causes a false **red**: a comment that merely *names*
the banned thing trips the check.

**Why this is a BLOCKER pattern, not a nuisance.** It is not a wrong implementation passing — it
is a **correct implementation failing permanently**, with no path to recovery. The classic trap is
a *coupled pair*: (1) the action prompt asks the agent to write a self-describing **safety-header
comment** naming the banned constructs (good engineering practice — "READ-ONLY survey; performs no
MERGE/INSERT/EXEC; makes no external calls (no xp_cmdshell…)"); (2) the guardrail keyword-matches
the **raw file** and so flags those keywords *in the header the prompt asked for*. The agent cannot
tell the match came from its own comment, so each retry it strips one mention and exposes the next —
**whack-a-mole to `needs-human`** on a strictly-read-only artifact. Real run (plan 0007 task 01):
attempt 1 flagged `MERGE`/`EXEC`, attempt 2 `EXEC`, attempt 3 `xp_cmdshell` — three *different*
banned keywords across three attempts, all from one safety comment. The harness behaved correctly
(accurate feedback, retries, honest halt) — the **guardrail** was mis-scoped.

**Rule (catalogue doctrine).** Any guardrail that scans a source artifact for **banned keywords**
MUST strip the source language's comments — ideally string literals too — **before** matching. Use
the target language's comment syntax. For SQL (the motivating case), strip `/* */` block comments
and `-- …` line comments first; the same applies to any language — a `//`-comment or docstring that
documents "this code uses no `eval`" must not trip an `eval` ban.

```powershell
# catches: a forbidden-keyword (read-only / no-shell) check that false-POSITIVES on a comment -
#          e.g. a SAFETY-HEADER comment the action prompt asked for ("performs no MERGE/EXEC,
#          no xp_cmdshell") - sending a CORRECT read-only script to needs-human via whack-a-mole.
#          Strip comments BEFORE the keyword scan so only real code is matched.
$raw = Get-Content $f -Raw
$c = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
$c = [regex]::Replace($c,   '--[^\r\n]*', ' ')        # -- line comments
# ...now run the banned-keyword checks against $c (the comment-free code), NOT $raw.
if ($c -match '(?i)\bxp_cmdshell\b') {
    Write-Output "$f calls xp_cmdshell in CODE (not just a comment) - external/unsafe surface"
    exit 1
}
exit 0
```

For a **line-number-reporting** guardrail (e.g. no-forbidden-egress that names the offending line),
do not collapse the file — **blank the comment spans in place, preserving newlines**, so line
numbers in the failure message stay accurate:

```powershell
# strip block comments but KEEP newlines so reported line numbers stay correct
$raw = [regex]::Replace($raw, '/\*[\s\S]*?\*/', { $args[0].Value -replace '[^\r\n]', ' ' })
$lines = $raw -split '\r?\n'
# an existing per-line '--' line-comment skip handles line-comment-only lines
```

```bash
# catches: same - a banned-keyword scan that false-positives on a comment/safety-header.
#          Strip /* */ then -- before matching; scan the code, not the comment.
set -euo pipefail
c=$(perl -0pe 's{/\*.*?\*/}{ }gs; s{--[^\n]*}{ }g' "$f")
if printf '%s' "$c" | grep -Eiq '\bxp_cmdshell\b'; then
    echo "$f calls xp_cmdshell in CODE (not just a comment) - external/unsafe surface"
    exit 1
fi
exit 0
```

**Action-prompt discipline — the breakdown must not grep for what it tells the action to document
(#98).** A self-describing safety header is good practice, but it requires a **comment-safe**
guardrail. During guardrail selection, flag the **direct conflict** when the *same* task both
(a) tells the action to write a header comment naming the banned constructs AND (b) greps for those
constructs without comment-stripping — that pairing is a guaranteed false positive that burns the
full retry budget and escalates a correct artifact. Resolve it by stripping comments in the guardrail
(above); the `# catches:` line already documents intent, so the **action prompt should NOT enumerate
banned keywords** unless its guardrail is comment-safe. The per-language comment syntax lives in the
stack file (`stacks/dotnet.md §11` for SQL/C#).

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

## Production testability seam — insert it upstream of the test-author task (#84)

A test-author task can correctly refuse to author a behavior it knows is **unsatisfiable as
architected**: the production code has no injection point, so the behavior can never be expressed
as a test that eventually goes green. The real run then halts `needsHuman` mid-run and forces a
human to hand-edit production code — defeating the "approve the guardrails once, then let it run"
model. The agent did the right thing (wrote the tests, confirmed them red, refused the unsatisfiable
one); the **breakdown** is what should have caught it, by inserting the seam as its own task.

**This is a sibling of #120 (composition-root wiring) at a different layer.** #120 is about
*production* injecting the *real* impl so the feature is live from the CLI. This (#84) is about
*tests* being able to inject a *fake/double* so a behavior is **expressible as a passing test at
all**. A plan can need both: a seam task (opens the injection point) AND, later, a wiring task
(production constructs and injects the real collaborator). Do not conflate them.

**Why the existing patterns do not cover this.** The compile-coupled-tests pattern (catalogue →
archetype #8 note: the test references a not-yet-existing **DTO/type** the implementation adds) works
when the missing symbol is something the **test constructs** — forcing the whole test file into a
compile failure is the correct red. It does NOT work when only **one behavior of several** needs the
seam: forcing the entire file red to satisfy behavior 3 would stop behaviors 1/2/4 — which are
runtime-testable against the existing surface — from compiling and failing as their own clean red. So
the seam belongs in its **own small upstream task**, not folded into either the test-author or the
implementation task (neither cleanly OWNs it as a verifiable deliverable otherwise).

**Decision rule — when does this fire?** While parsing a test-author behavior: **does expressing this
behavior as a test that can eventually PASS require a production-code seam that does not exist yet** —
a DI constructor overload, a factory delegate, an injectable interface, a fixture source? The
detection tell: the behavior injects a fake/double (`RecordingX`, `FakeX`, `InMemoryX`, a fixture
source) into a type currently constructed **only** via a production constructor with **no injection
point**. The action prompt's "if no seam exists, write `needsHuman` and stop" escape hatch is the
**last resort**, not the default — by run start the seam task should already exist.

**Two artifacts close it** (generated in SKILL.md Step 5):
1. **A production-seam TASK** — `NN-add-<component>-<seam>-seam`, a **pure structural production
   change**: add the constructor overload / factory delegate / injectable interface + its DI
   registration. **No behavior, no endpoint** — the seam only opens an injection point. Edge
   direction: the **test-author task `dependsOn` this seam task** (the seam is upstream; the tests
   compile against it), never the reverse. **TDD-exempt:** a seam is too simple for meaningful unit
   tests — state the exemption reason in the task description.
2. **A structural seam-exists guardrail** on the seam task — pairing `build-passes` (#3) with a
   **structural check that the seam exists** using the stack file's **declaration regex** (the new
   constructor signature / factory delegate / interface), **never a bare name grep** (this is the
   universal structural-vs-keyword rule, §"file-contains: structural vs. keyword matching"). Scope it
   to the one production file the seam task owns (grep-scope rule). The .NET realizations (constructor
   overload, factory delegate, injectable interface) are `stacks/dotnet.md §11`.

With the seam present, the test-author task authors **all** behaviors against the real injection
point: every behavior fails at runtime (the endpoint/feature is still absent) as a clean red, with no
`needsHuman`. The run stays autonomous.

**FORBIDDEN shapes** (the review skill hunts these):
- A test-author task expected to **invent the seam itself** (or to gesture vaguely at "add an
  injection mechanism") — neither it nor the implementation task cleanly owns the seam as a verifiable
  deliverable, and the `needsHuman` escape fires at run time.
- A **bare name grep** for the seam (`Select-String "Launcher"`) instead of the declaration regex — it
  passes on a comment, a `using`, or an unrelated mention (the structural-vs-keyword trap).

## Bulk / unbounded fan-out → scripted ETL, not an agent-per-item loop (#100)

When a task's deliverable is **"process N items where N is unknown and potentially large"** — a web
crawl/scrape, a bulk transform over an unknown-size glob, a mass API fetch, a dataset ETL — the wrong
model is an **agent-iterated loop**: one agent turn-budget covering N fetch+convert+write cycles. Agent
turns are the wrong unit for bulk work. A real run modeled a `portal-crawl` as "use Playwright to
enumerate the in-scope pages and produce a note per page"; the in-scope set turned out to be ~409
pages, the crawl sub-agent hit max-turns (50) and was killed, and the retry hit the same wall
identically — a hard dead-end (`action-failed` → retries fail → `needs-human`) that wasted a large
turn/$$ budget *discovering the wall*. This is a **task-structuring** failure, not a `maxTurns` one:
raising the turn budget (#94) only moves the wall, because bulk fan-out does not scale with turns at
all.

**Decision rule — when does this fire?** When a task fans out over an **external or unknown-size set**:
a website / section / sitemap, a recursive glob, an API listing — "every page under…", "all files
matching…", "each record in…". The tell is **cardinality the plan cannot bound at breakdown time**
("8 expected" → 409 actual). A retry-cheapness / one-session check on **"could this be hundreds of
items?"** trips the rule during sizing (SKILL.md Step 2).

**Structure it as a scripted bulk operation — three moves:**
1. **Scripted-ETL action (the volume goes off the turn budget).** The agent authors and runs **one
   `script`** that does the N-item work in a single execution (Playwright + HTML→markdown; a glob walk
   + transform). The agent's turns go to *writing, verifying, and running* the script — NOT to
   iterating items. This is a `script` action, not a `.prompt.md` that loops. Guard it with the
   ordinary script archetypes — `file-exists` (#1) on the output directory + `command-exit-code` (#2)
   or a count check — and verify the **recorded output** (verify-don't-replay, #9), not a re-run.
2. **Discover-size-first.** Where the set size is unknown, **enumerate/count before** committing to an
   approach, so sizing and any curation are calibrated to reality. This is a cheap upstream probe
   (enumerate the in-scope set, write the count to state or a manifest) that may be its own task
   feeding the ETL task.
3. **Split bulk-capture from per-item derivation.** Make the cheap, complete, **scripted capture** one
   task (deterministic, fits a session — dump all N items locally), and any **agent
   derivation/curation** a separate, **bounded** task over a *selected subset* — never "derive all N."
   "Scripted crawl dumps all 409 pages to local markdown" then "a curate task derives a high-value
   committed subset" is the shape, not one agent told to "crawl and curate 409 pages."

**Relation to siblings.** Complementary to corpus-completeness/substance guardrails (#99 — those
*verify* the captured output is complete and substantive; this *structures the task* so it can be
produced at all) and to `maxTurns` budgeting (#94 — necessary but insufficient; bulk fan-out is the
case where more turns never help). The decision-tree leaf is below; the .NET scripted-crawl shape is
`stacks/dotnet.md §12`.

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

## Corpus / aggregation completeness & substance (#99)

A task whose deliverable is **derived artifacts from a set of inputs** — doc mining, codegen
from a spec, API→docs, dataset import, schema→fixtures, a crawl/enumeration that produces one
output per page — has a failure mode the existing archetypes miss. `file-exists` and
`file-contains` (and `tests-fail-on-current-code`) cover **shape** and **anti-tautology**;
nothing covers the **completeness and substance of a derived corpus**. The result is the worst
kind of false-green: a run that is 100% green and **ships an empty or partial corpus** — worse
than a hard failure, because it *looks done*. Three concrete misses (all the same gap):

- **F1 — hollow artifacts.** `file-exists` + a required marker line (e.g. a `Source:` citation)
  passes a **one-line stub**. The deliverable is empty-but-shaped.
- **F2 — incomplete aggregate/index.** An index that references *one* output "resolves" and
  passes while omitting most of the corpus — silently blinding any consumer that navigates via
  the index.
- **F3 — shallow ingestion.** A crawl that captures 2 of N pages passes, because the guardrails
  verify "everything I *listed* exists," never "I listed *enough*."

Distinct from the comment-stripping family (#97/#98): that is about false **positives** on banned
constructs; this is about false **negatives** on hollow/incomplete derived corpora. It complements
`tests-fail-on-current-code` (anti-tautology for tests) with an anti-tautology for
*extraction/aggregation outputs*.

**The four guardrails.** For a derived-corpus task, add deterministic checks that assert:

1. **Input→output coverage (no silent drops).** Every input — a manifest entry, a source file, an
   enumerated URL — maps to an **existing** output artifact. Iterate the *input* set and fail on the
   first input with no corresponding output; never iterate the outputs (that only proves "what I
   produced exists," F3's blind spot).
2. **Per-output substance floor (anti-stub).** Each derived artifact exceeds a **minimal content
   floor** — e.g. ≥ N non-empty lines or ≥ N characters *beyond* the required boilerplate/marker —
   so a one-line stub (F1) cannot pass. Subtract the boilerplate before measuring, or the marker line
   itself satisfies the floor.
3. **Aggregate/index completeness (`produced ⊆ indexed`).** The index/rollup references **every**
   produced artifact, not just ≥ 1. Compute the produced set and the indexed set and fail if any
   produced artifact is absent from the index (F2).
4. **Ingestion lower bound (where knowable).** A sanity floor on **how many** inputs were processed
   — `count(outputs) ≥ N` — to catch a trivially shallow run (F3). Set N from the manifest/known
   corpus size when one exists.

```powershell
# catches: a derived-corpus run that ships HOLLOW or INCOMPLETE outputs while green - a one-line
#          stub per source (F1), an index naming only 1 of N outputs (F2), or 2-of-N pages
#          ingested (F3). Asserts input->output coverage, a per-output substance floor, index
#          completeness (produced subset of indexed), and an ingestion lower bound.
$inputs   = Get-Content "manifest.txt" | Where-Object { $_.Trim() }   # the input set (1 line per input)
$outDir   = "docs/derived"
$indexFile = "docs/derived/INDEX.md"
$minLines = 5                                                          # substance floor (tune per corpus)
$minInputs = 10                                                       # ingestion lower bound (tune)

# 4. ingestion lower bound
if ($inputs.Count -lt $minInputs) {
    Write-Output "only $($inputs.Count) inputs in manifest (< $minInputs) - corpus looks trivially shallow"
    exit 1
}
$produced = @()
foreach ($in in $inputs) {
    $slug = [IO.Path]::GetFileNameWithoutExtension($in)
    $out  = Join-Path $outDir "$slug.md"
    # 1. input -> output coverage (no silent drops)
    if (-not (Test-Path $out)) {
        Write-Output "input '$in' has no derived output at $out - a source was silently dropped"
        exit 1
    }
    # 2. per-output substance floor (anti-stub): non-empty lines beyond the Source: marker
    $body = Get-Content $out | Where-Object { $_.Trim() -and $_ -notmatch '^\s*Source:' }
    if ($body.Count -lt $minLines) {
        Write-Output "$out has only $($body.Count) substantive lines (< $minLines) - hollow stub"
        exit 1
    }
    $produced += $out
}
# 3. aggregate/index completeness: produced subset of indexed
$index = Get-Content $indexFile -Raw
foreach ($p in $produced) {
    $name = [IO.Path]::GetFileName($p)
    if ($index -notmatch [regex]::Escape($name)) {
        Write-Output "$indexFile does not reference produced artifact '$name' - index is incomplete"
        exit 1
    }
}
exit 0
```

**The honest limit — state it in the doctrine so authors don't over-trust the floors.** These are
**lower bounds, not faithfulness checks.** A deterministic guardrail can enforce "≥ N,
every-input-mapped, non-trivial size, fully indexed" — it **cannot** verify the extraction is
*content-faithful* or *semantically complete* (that the derived doc actually captures its source).
That residual needs a human pass or a demotion-gated prompt-judge (one paired with these
deterministic floors, never alone — the demotion gate below). The floors are **tunable per corpus**:
set N, the substance floor, and the ingestion lower bound from the known corpus size; when none is
knowable, drop guardrail 4 and say so in the breakdown report rather than inventing a number.

**Decision-tree leaf:** *deliverable = derived corpus / aggregate over a set of inputs* → add the
coverage + substance-floor + index-completeness guardrails (lower bounds; note the faithfulness
residual is human/judge work).

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

## A `scope:"integration"` guardrail MUST be UNION-SAFE (#125)

This is an authoring constraint on **which assertion** a `scope: "integration"` guardrail may make —
distinct from the mechanical "mark the build/suite as integration" rule. Per SSOT **§4.3**, the run's
integration-guardrail set re-runs at **EVERY union point** — every fan-in and every **non-FF**
plan-branch integration (SSOT §5.3 case B), on the merged bytes, *before* the merge commit and
*before any downstream action* — **not only on the terminal `integrationGate` sink**. The terminal
gate and the per-union re-verify are **one mechanism at two scopes** (§4.3). So an integration
guardrail runs at moments when **downstream tasks have not run yet**.

**The rule: assert a union-safe INVARIANT, never a terminal POSTCONDITION.** A `scope:"integration"`
guardrail must assert something true of **any valid intermediate union** — an invariant like "every
produced file present is non-empty and conflict-marker-free", "the solution still builds", "the
already-merged tests still pass". It must **NOT** assert a **terminal postcondition** that only holds
once the *whole* plan has merged — "the final combined output exists", "the sink wrote its
aggregate", "all N contributors are present". A terminal postcondition **fails at an intermediate
union** where the producing task hasn't settled yet, turning a healthy partial merge into a spurious
`needs-human`.

**Surfaced live by `parallel-hello`:** a sink-postcondition gate ("the final combined output exists")
was marked `scope:"integration"` and **failed when the 2nd leaf settled as a union before the sink
ran** — the combined output legitimately did not exist yet. The fix was twofold, and it is the
template:

1. **Keep the integration gate union-safe** — re-scope it to an invariant ("any produced file present
   is non-empty and conflict-marker-free"), true at every union including the terminal one.
2. **Move the terminal assertion to a `local` guardrail on the sink** — "the final combined output
   exists" runs **in-attempt on the sink's own segment** (default `"local"` scope), where the sink's
   action has just produced it. A `local` guardrail runs only in the sink's attempt lifecycle, never
   at an upstream union, so it never fires early.

**Decision test (apply to every `scope:"integration"` guardrail):** *"If this ran on a partial merge
where a downstream task has not settled yet, would it pass?"* If **no**, it is a terminal
postcondition wearing an integration scope — demote it to a `local` guardrail on the task that owns
the postcondition, and (if needed) replace it with a union-safe invariant at integration scope. An
integration guardrail asserting a terminal-only postcondition is an **anti-pattern** (see the
anti-patterns list).

Do not fork the contract here — §4.3 (re-verify at every union) and §5.3 (FF vs union) are the SSOT;
this section is doctrine *about how to author within* that contract.

### Overlapping writeScopes need a `scope:"integration"` union-guardrail on the shared file (#132)

The corollary of "the union re-verify is integration-set-only" (SSOT §4.3): the union re-verify runs
**ONLY** the `scope:"integration"` set — it does **NOT** re-run a colliding sibling's per-attempt
`local` guardrails (running `local` guardrails on arbitrary union bytes false-fails — fragment-readers
checking `GUARDRAILS_STATE_FRAGMENT`, anti-tautology `tests-fail-on-current-code`, not-yet-run
downstream tasks). So when an AI-merge resolves a union of **two tasks that both write a shared
file** (overlapping `writeScope`s — colliding siblings), a hunk the merge silently DROPS on that file
is re-verified at the union **only** if a `scope:"integration"` guardrail asserts the shared file's
**union invariant**. A drop catchable solely by a sibling's `local` guardrail is **not** caught at
the union (it surfaces at the terminal gate, or not at all — the accepted v1 residual, SSOT §4.3
"Accepted residual").

**The authoring rule (proactive — emit it when you generate colliding writeScopes).** Whenever the
breakdown produces **≥2 tasks with overlapping `writeScope`s on a shared file/path** (rare by
design — the disjoint-scope CHECK flags most such collisions as a plan-shape smell, prefer disjoint
scopes), author **one** `scope:"integration"` guardrail on the integration / fan-in task that asserts
the **union invariant** on that shared file: the merged file still holds **every** colliding sibling's
contribution (each sibling's distinctive marker / declaration is present, conflict-marker-free,
non-empty). This is exactly the texttools showcase's `components-union-verified` guardrail
(`05-integration-gate/.../03-components-union-verified.json`, `scope:"integration"`). Keep it
**union-safe** (#125 — assert "every contribution PRESENT in the union is intact", an invariant true
of any valid intermediate union, never a terminal "all N present" postcondition that false-fails on a
partial merge). The well-authored plan covers the residual this way; `guardrails-review` emits a WEAK
finding when colliding writeScopes carry no such union-guardrail.

<!-- BEGIN ADDED SECTION #76 — method-call anchoring (auto-merge friendly; do not merge into prose above) -->
## Method-call anchoring — match the call construct, not a bare method name (#76)

The **call-site sibling** of the structural-vs-keyword rule (§"file-contains: structural vs.
keyword matching", which covers *type/member declarations*). That rule says a check for "type
`IFoo` is implemented" must match the declaration, never the bare token `IFoo`. The same trap
exists for **method calls**: a guardrail verifying "file calls method `X` on type `Y`" that greps
a **bare method name** — `RunAsync\s*\(` — false-passes on three things that do NOT prove the real
library method is wired up:

- a **comment** that merely names it — `// then we call RunAsync(scope)`;
- a **local stub/wrapper** method that happens to share the name — `private void RunAsync(...)`;
- any unrelated method called `RunAsync` on a different type.

This is the call-site shape of the green-build false-pass: the guardrail goes green while the
specific library method it was meant to verify is never actually invoked. It surfaced on a
"CLI must call `MigrationRunner.RunAsync`" wiring guardrail written as
`(Get-Content $prog -Raw) -notmatch 'RunAsync\s*\('` — satisfied by a local `RunAsync` wrapper
or a `// RunAsync(scope)` comment, neither of which wires the real runner.

**Rule.** When a guardrail verifies "file calls method `X` on type `Y`," require **both** of two
sequential checks (each a separate `if` so the failure line names the missing half):
1. **A reference to the TYPE** — `TypeName` (or the stricter `TypeName\s*\.`) — rules out a local
   stub that shares only the method name.
2. **The call with a DOT prefix** — `\.MethodName\s*\(` — rules out substring matches in comments
   and standalone method *definitions* (a definition reads `void MethodName(` with no leading dot;
   a call reads `something.MethodName(`).

- **Pattern to AVOID** (matches comments, local stubs, any same-named method):
  `RunAsync\s*\(`
- **Pattern to USE** (two sequential checks — type reference, then dotted call):
  ```powershell
  # catches: a "CLI calls MigrationRunner.RunAsync(...)" wiring claim satisfied by a comment
  #          (// RunAsync(scope)) or a LOCAL method also named RunAsync - neither invokes the
  #          real library method. Require BOTH the type reference and the dotted call construct.
  $prog = "src/Migration.Cli/Program.cs"
  $content = Get-Content $prog -Raw
  if ($content -notmatch 'MigrationRunner') {
      Write-Output "$prog does not reference MigrationRunner - the runner type is never named (a local RunAsync stub would not wire it)"
      exit 1
  }
  if ($content -notmatch '\.RunAsync\s*\(') {
      Write-Output "$prog does not call .RunAsync(...) on an instance - only a bare/commented/locally-defined RunAsync would match without the dot"
      exit 1
  }
  exit 0
  ```

Apply whenever the plan says "task A must call `B.Method()`" where `B` is a specific type in
another project (the entry-point-wiring grep in §"Entry-point wiring" is the executable-specific
instance of the same idea — it already requires `new\s+Launcher\b|Launcher\s*\.\s*\w` rather than
a bare `Launcher`). For the strict-string-literal residual (a banned/expected method name sitting
inside a string), the same caveat as the comment-strip family applies: a regex is a lower bound, a
parser is out of scope — note it in the report if it matters. The .NET realization is
`stacks/dotnet.md §15`.
<!-- END ADDED SECTION #76 -->

<!-- BEGIN ADDED SECTION #74 — no-direct-bypass archetype (auto-merge friendly; do not merge into prose above) -->
## No-direct-bypass — an extracted library must write THROUGH its injected interface (#74)

The **inverse** of the two registration/reference seams (build-descriptor registration,
cross-module reference): those prove a library is *wired in* (registered in the solution, referenced
by the consumer). This proves a library does **not bypass its own abstraction** from the inside. A
library can be correctly registered, building, and passing its tests while still calling the
**concrete** dependency directly in its internals — bypassing the very `IInterface` it was extracted
to enforce. Registration, build, and tests-pass guardrails all go green; the bypass slips through.

It surfaced on an "extract migration-engine library" task: the library was registered, built, and
tested, but nothing prevented the extracted engine from calling `ToscaCloudClient.UploadEntitiesAsync`
directly — bypassing the injected `IDestinationWriter` entirely. The library's whole purpose was to
enforce the writer abstraction; without this guardrail the bypass is invisible to every other check.

**Rule.** When a task extracts a library that **must call through an injected interface rather than a
concrete dependency**, add a guardrail that scans the extracted project's `.cs` files for a **direct
call to the concrete method** and fails if it finds one. Two anchoring requirements (both from the
method-call-anchoring rule, #76 above — a bare-name grep here would *false-RED* on a comment, escalating
a correct library):
1. **Strip comments before the scan** (#97/#98 comment-blind family) — a `// we used to call
   UploadEntitiesAsync directly` comment must not trip the ban (false positive → whack-a-mole to
   `needs-human` on a correct library).
2. **Anchor on the dotted call construct** — `\.UploadEntitiesAsync\s*\(` (optionally `ConcreteType`
   nearby), not a bare `UploadEntitiesAsync`, so a same-named method on a *different* allowed type or a
   string literal does not false-RED.

Scope the scan to the **new library's project folder only** (grep-scope rule), excluding `bin`/`obj`:

```powershell
# catches: <LibraryProject> bypassing <IInterface> by calling <ConcreteClass>.<ConcreteMethod>
#          directly in its internals - registered, building, and tested all stay green while the
#          injected abstraction is bypassed. Strip comments first (so a comment naming the method
#          is not a false RED), then anchor on the DOTTED call construct, scoped to the library only.
$libDir = "PoC/ConformedSources/Migration.Engine"
$hits = Get-ChildItem $libDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Where-Object {
        $raw  = Get-Content $_.FullName -Raw
        $code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
        $code = [regex]::Replace($code, '//[^\r\n]*', ' ')       # // line comments
        $code -match '\.UploadEntitiesAsync\s*\('
    }
if ($hits) {
    $names = ($hits | ForEach-Object { $_.Name }) -join ', '
    Write-Output "$libDir calls .UploadEntitiesAsync(...) directly in [$names] - must write THROUGH the injected IDestinationWriter, not the concrete client"
    exit 1
}
exit 0
```

**Trigger:** the plan or action prompt contains language like "must NOT call `X` directly", "must
write **through** interface `Y`", "the current Exe bypasses the abstraction", or "the engine must
depend only on `IInterface`." This is a **forbidden-call** check (a ban), so it inherits both the
comment-blind caveat (#97/#98 — strip first) and the string-literal residual (a parser is out of
scope; note it if the concrete method name plausibly appears in a string). The .NET realization is
`stacks/dotnet.md §16`.
<!-- END ADDED SECTION #74 -->

<!-- BEGIN ADDED SECTION #75 — covers-key-behaviors guardrail (auto-merge friendly; do not merge into prose above) -->
## Covers-key-behaviors — a test-author task with an enumerated behavior list (#75)

A concrete instance of the **coverage-gap** anti-pattern (the action's stated completion criteria
exceed what the guardrails verify). When a test-author task's action prompt **enumerates specific
named behaviors** to encode — a numbered list under "encode these behaviors: 1. sub-processes are not
filtered 2. ProcessID keying 3. rollup counts" — the standard TDD pair (`tests-exist` +
`tests-fail-on-current-code`) verifies the file *exists* and *fails against current code*, but neither
verifies the **enumerated behaviors are actually present**. An agent can satisfy both with **one**
trivially-failing stub test and never encode behaviors 2–5.

**Rule.** When a test-author task's action prompt enumerates **3 or more** specific named behaviors to
encode, add a `03-covers-key-behaviors.ps1` guardrail that checks the test file for **2–3 of the most
distinctive terms** from the behavior list (one `if` per term, so the failure line names the missing
behavior). Scope the grep to the **one test file the task authors** (grep-scope rule).

```powershell
# catches: a test file that lacks coverage of <Behavior1> or <Behavior2> - both named in the action
#          prompt's "encode these behaviors" list - while tests-exist + tests-fail-on-current-code
#          both pass on a single trivially-failing stub test.
$f = "tests/Migration.Engine.Tests/SubProcessRollupTests.cs"
$content = Get-Content $f -Raw
if ($content -notmatch 'ProcessId') {
    Write-Output "$f does not test ProcessID keying - add a test asserting entities are keyed by ProcessId (behavior 2)"
    exit 1
}
if ($content -notmatch 'RollupCount') {
    Write-Output "$f does not test rollup counts - add a test asserting the parent's RollupCount aggregates its sub-processes (behavior 3)"
    exit 1
}
exit 0
```

**Term-selection rules:**
- Choose terms **distinctive** to the behavior — a domain type name, an enum value, a method name —
  **never** generic words like `test`, `assert`, `Fact`, or `should`, which any stub satisfies.
- Pick the **headline** behaviors most likely to be accidentally omitted (the ones the plan's risk
  section flags), not all of them — **2 checks per guardrail is usually enough**.
- Scope to the **one test file** the task authors (grep-scope rule, §"Grep-scope contamination").

**The honest limit — state it so authors don't over-trust the check.** This is a **lower bound**, the
same class as the corpus substance floors (#99): a distinctive term *present in the file* proves a
test *names* the behavior, not that it *asserts* it correctly — a term in a comment or an unused
variable still matches. It is a cheap guard against the "one stub for five behaviors" failure, not a
faithfulness check; the residual (does the test actually exercise the behavior?) is the
`tests-fail-on-current-code` red plus human review. The breakdown report (Step 7) should **list which
enumerated behaviors were NOT covered** by the key-behaviors guardrail, so the human reviewer can
decide whether to add checks. The .NET realization is `stacks/dotnet.md §17`.
<!-- END ADDED SECTION #75 -->

<!-- BEGIN ADDED SECTION #96 — producer<->consumer name-convention seam (auto-merge friendly; do not merge into prose above) -->
## Name-convention seam — producer files ⟷ consumer lookup by a derived name (#96)

The **third sibling** of the two "independent build passes, but the link is unverified" seams
(build-descriptor registration §1, cross-module reference §2). Here task A **produces artifacts** whose
names a consumer (task B, or a runtime component) **resolves by a derived or mapped name** — not a
literal path B already hard-codes: a URL → embedded resource, a step id → filename, a key → file, a
route → handler, a message-type → schema file. `file-exists`/`file-contains` on A and content checks on
B both pass while the **naming contract between them is never exercised**: B can derive a name A never
produced (case / separator / special-case drift) and fail **only at runtime**.

It surfaced as a real runtime bug a clean guardrail suite passed end-to-end. A browser wizard's step
fragments were produced kebab-case (`wwwroot/steps/source-connection.html`, and the `DestinationSelection`
step served by `destination.html` — an outlier, not `destination-selection.html`); the shell requested
fragments by the **PascalCase step id** — `GET /wizard/pages/SourceConnection.html` → embedded resource
`…wwwroot.steps.SourceConnection.html` → **404 → silent fallback**. Every guardrail passed because each
side was verified **independently**: file-exists + file-contains on each fragment (✅ the kebab files
existed), the shell's stepper/order were correct (✅), and the smoke test asserted step *order*, not
fragment *fetchability* (✅). Nothing verified **the consumer can resolve the producer's artifacts by the
name it derives at runtime** — the single most expensive class of false-green for UI / transport /
convention-heavy plans, because the failure is invisible to the whole suite and surfaces only on the
first real run.

**Rule.** When a task's artifacts are consumed by a name the consumer **derives** (a fetch-by-name, a
reflection / embedded-resource lookup, a convention-based file map, a route→handler resolution), add an
**integration guardrail that DRIVES the real lookup** end-to-end and asserts resolution succeeds for
**every** item in the set. Three properties — each load-bearing:

1. **Consumer-driven names.** Derive the lookup names from the **consumer's own mapping** — parse or run
   the shell's real `STEPS`/`FRAGMENTS` map, the route table, the message-type registry — **never a
   hard-coded copy** of the contract in the test. A test-side copy of the naming convention hides a
   *consumer-side* drift: it would test the test's idea of the names, not the consumer's.
2. **Cover EVERY item.** Iterate the *whole* set (all steps / all message types / all routes), not a
   sample — the drift is typically a **single special case** (the `destination.html` outlier). One
   un-checked item is exactly where it hides.
3. **Drive the real lookup, assert a per-item success marker.** Exercise B against A's *actual*
   artifacts (start the server and `GET` each fragment **through the shell's own map**; or invoke the
   real resolver) and assert **200 + a per-item content marker** — never a 404 / silent-fallback body.
   Resolution succeeding ≠ a 200 from a fallback page; assert a marker that only the *correctly resolved*
   artifact contains.

**Placement — both sides must be present.** This belongs on a task where **producer AND consumer
coexist** — a terminal / integration task (the whole-suite gate or a dedicated end-to-end task), never
on the producer task or the consumer task alone (on either, the other side isn't there to drive). Mark
it `scope: "integration"` — and keep it **union-safe** (#125): "every producer artifact present resolves
through the consumer's derived name" is an **invariant** true of any valid intermediate union where both
sides are present, so it is a legitimate integration-scope assertion, **not** a terminal postcondition.
(If a plan can reach a union where the producer set is only *partially* present, scope the assertion to
"every artifact **that is present** resolves" so a partial merge does not false-RED — the drift you are
hunting is a *wrong-name* failure, not a *missing-file* one, so a present-set invariant still catches it.)

This is **starts-and-resolves** verification — distinct from #64 (the exe *serves something*) and the
#66 served-markup check (the served root is *the described UI*). Compose them: #64 proves the server
answers, #66 proves the root is the UI, and this proves **every derived-name lookup across the set
resolves to the right artifact**. The .NET realization (parse the consumer's embedded-resource / route
map, drive each through the live server, assert 200 + per-item marker) is `stacks/dotnet.md §18`.

**Decision-tree leaf:** *task A's artifacts are resolved by task B (or a runtime component) via a
DERIVED/MAPPED name (not a literal path B hard-codes)* → add a **consumer-driven integration guardrail**
on a both-sides-present task that drives the real lookup for **every** item and asserts 200 + a per-item
marker (union-safe; the per-side independent file-exists / content checks do NOT cover the seam).
<!-- END ADDED SECTION #96 -->

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
├── A derived corpus /         → coverage (every input maps to an output) + per-output substance
│    aggregate over a set of      floor (anti-stub) + index completeness (produced ⊆ indexed) +
│    inputs (doc-mine, codegen    ingestion lower bound (#99). LOWER BOUNDS, not faithfulness —
│    -from-spec, crawl, import)   the semantic residual is human/judge work; see the corpus /
│                                 aggregation completeness section above
├── Config/data                → schema-validates; else file-contains on load-bearing keys
├── State output (a key a      → fragment-key-present (read $env:GUARDRAILS_STATE_FRAGMENT,
│    downstream task reads)      parse JSON, assert the key non-null + non-empty; allowed-set
│                                check if a downstream task branches on the value)
├── Docs / prose               → file-exists + file-contains (required headings/terms);
│                                prompt-judge ONLY for genuine subjective quality, never alone
├── Bulk / unbounded fan-out  → scripted-ETL archetype (#100), NOT an agent-per-item loop: ONE
│    (crawl/scrape, recursive    `script` action does the N-item work in a single run (volume off
│    glob, API listing, ETL —    the turn budget) + file-exists/command-exit-code/count on its
│    "process N items, N         output; INSERT a discover-size-first probe where N is unknown; SPLIT
│    unknown & maybe large")     scripted bulk-capture from a BOUNDED per-item curation task. Raising
│                                maxTurns does NOT help. See the bulk-fan-out section + stacks/dotnet.md §12
├── Test needs an injection   → INSERT an upstream production-seam task (#84): add the DI ctor
│    seam to express a behavior  overload / factory delegate / injectable interface + DI registration
│    (a fake/double injected      (pure structural change, no behavior), guarded by build-passes +
│    into a type with no          a STRUCTURAL seam-exists check (declaration regex, never a bare
│    injection point)             name grep). The test-author task dependsOn it. Distinct from #120
│                                 (which injects the REAL impl in production). See the
│                                 production-testability-seam section + stacks/dotnet.md §11
├── Extracted library that must → no-direct-bypass (#74): scan the LIBRARY project's .cs (strip
│    write THROUGH an injected     comments first, scope to the lib folder, exclude bin/obj) for a
│    interface (not the concrete   DOTTED call to the concrete method (\.ConcreteMethod\s*\() and
│    dependency directly)          FAIL if present — registration/build/tests all pass over a bypass.
│                                 Trigger: "must NOT call X directly" / "write through interface Y".
│                                 See the no-direct-bypass section + stacks/dotnet.md §16
├── Test-author task whose      → covers-key-behaviors (#75): in ADDITION to tests-exist +
│    action prompt enumerates     tests-fail-on-current-code, add a check for 2–3 DISTINCTIVE terms
│    ≥3 named behaviors to        from the behavior list (domain type / enum / method name, never
│    encode                        generic words), scoped to the one test file. LOWER BOUND, not a
│                                 faithfulness check; report which behaviors went unchecked. See the
│                                 covers-key-behaviors section + stacks/dotnet.md §17
├── "Task A must call          → method-call anchoring (#76): TWO sequential checks — reference the
│    B.Method()" on a specific    TYPE (rules out a local same-named stub) AND the dotted call
│    type in another project      (\.Method\s*\(, rules out comments + standalone definitions). NOT a
│                                 bare Method\s*\( grep. See the method-call-anchoring section + §15
├── Producer files ⟷ consumer  → name-convention seam (#96): a CONSUMER-DRIVEN integration guardrail
│    lookup by a DERIVED/mapped    on a both-sides-present task — parse the consumer's real map, drive
│    name (url→resource, step     the lookup for EVERY item, assert 200 + a per-item marker (not a 404/
│    id→file, key→file,           fallback). Union-safe (#125). Per-side file-exists/content checks do
│    route→handler)               NOT cover the seam. See the name-convention-seam section + §18
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
- **Comment-blind keyword scan** (#97, #98): a forbidden-keyword guardrail (read-only / no-shell /
  no-eval) that calls `Get-Content $f -Raw` and matches banned keywords against the **raw file
  including comments** — it false-POSITIVES on a comment, a string literal, or disabled code that
  merely *names* the banned construct. The poison case: the action prompt asked the agent to write a
  **safety-header comment** listing the banned keywords ("performs no MERGE/EXEC, no xp_cmdshell"),
  and the guardrail flags those keywords in the header — sending a **correct** read-only artifact to
  `needs-human` via whack-a-mole (each retry strips one mention and exposes the next). A BLOCKER
  pattern: not a wrong implementation passing, but a correct one failing permanently. Fix: **strip the
  source language's comments before matching** (SQL: `/* */` then `-- …`; blank-in-place preserving
  newlines for line-number-reporting checks). And don't pair a header-documenting prompt with a
  comment-blind grep — that is a guaranteed false positive. See the comment-blind keyword-scan section
  above; per-language syntax `stacks/dotnet.md §11`.
- **Hollow / incomplete derived corpus** (#99): a derived-corpus task (doc mining, codegen-from-spec,
  crawl→one-output-per-page, dataset import) whose guardrails verify only **shape** — `file-exists` +
  a marker line — so it ships a green run over an **empty or partial** corpus. Three tells: a
  one-line **stub** passes a marker check (F1); an **index** naming only 1 of N outputs "resolves"
  (F2); a crawl that captured **2 of N** pages passes because the checks verify "what I listed
  exists," never "I listed enough" (F3). Worse than a hard failure — it *looks done*. Fix: add the
  four completeness/substance guardrails — input→output coverage, per-output substance floor
  (anti-stub), index completeness (`produced ⊆ indexed`), and an ingestion lower bound — noting they
  are **lower bounds**, not faithfulness checks (the semantic residual is human/judge work). See the
  corpus / aggregation completeness section above.
- **Terminal-postcondition at integration scope** (#125): a `scope:"integration"` guardrail that
  asserts a **terminal postcondition** ("the final combined output exists", "the sink wrote its
  aggregate", "all N contributors present") instead of a **union-safe invariant**. Per SSOT §4.3 the
  integration set re-runs at **every** union point (every fan-in / non-FF integration, §5.3 case B),
  on partial merges where downstream tasks have **not run yet** — so a terminal postcondition
  spuriously fails at an intermediate union and escalates a healthy partial merge to `needs-human`
  (surfaced live by `parallel-hello`). Fix: keep the integration guardrail to an invariant true of
  any valid intermediate union ("any produced file present is non-empty and conflict-marker-free");
  move the terminal assertion to a `local` guardrail on the sink (runs in-attempt on the sink's own
  segment, where the output exists). Decision test: *"would this pass on a partial merge with a
  downstream task unsettled?"* — if no, demote to `local`. See the union-safe section above.
- **Overlapping writeScopes with no integration union-guardrail** (#132): ≥2 tasks with **overlapping
  `writeScope`s on a shared file** (colliding siblings — AI-merge territory at the union) and **no**
  `scope:"integration"` guardrail asserting that shared file's **union invariant**. The union re-verify
  is **integration-set-only** (SSOT §4.3) — it does NOT re-run a sibling's per-attempt `local`
  guardrails (they false-fail on union bytes), so a hunk an AI-merge silently DROPS on the shared file
  is caught at the union ONLY by an integration-scoped guardrail; a drop catchable solely by a sibling's
  `local` guardrail is the **accepted v1 residual** (not caught at the union). Fix: author one
  `scope:"integration"` union-guardrail on the integration / fan-in task that asserts the merged shared
  file holds every sibling's contribution (each distinctive marker present, conflict-marker-free) —
  union-safe (#125), like the texttools showcase's `components-union-verified`. Prefer **disjoint**
  writeScopes (the disjoint-scope CHECK flags the collision); emit the union-guardrail when the overlap
  is genuine. See the overlapping-writeScope union-guardrail section above. WEAK — an authoring nudge,
  not a harness bug.
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
- **Test-author left to invent the seam** (#84): a plan needs a production injection seam — a DI
  constructor overload, a factory delegate, an injectable interface — for **one** behavior to be
  expressible as a test that can pass, but the breakdown emits no upstream seam task. Neither the
  test-author task nor the implementation task cleanly OWNS the seam as a verifiable deliverable, so
  the test-author task's `needsHuman` escape ("if no injection mechanism exists, write a needsHuman
  note and stop") fires at run time and a human must hand-edit production code mid-run. Distinct from
  compile-coupled-tests (where the missing symbol is a **type the test constructs** and forcing the
  whole file red is correct): here only one behavior of several needs the seam, so the file must keep
  compiling and failing as its own clean red. Fix: insert `NN-add-<component>-<seam>-seam` (pure
  structural production change, build + a STRUCTURAL seam-exists check via the declaration regex,
  TDD-exempt) the test-author task `dependsOn` — see the production-testability-seam section above +
  `stacks/dotnet.md §11`. **Forbidden "fix":** a bare name grep for the seam (passes on a comment / a
  `using`); use the declaration regex.
- **Agent-per-item loop over a large/unknown fan-out** (#100): a task whose deliverable is "process N
  items where N is unknown and potentially large" — a web crawl, a recursive-glob transform, a mass
  API fetch — modeled as an **agent-iterated loop** (one `.prompt.md` turn-budget covering N
  fetch+convert+write cycles). Agent turns are the wrong unit for bulk work: a few hundred items blow
  the budget, the action hits max-turns and is killed, and the retry hits the same wall identically —
  a hard dead-end on a task that is perfectly doable as a script. Raising `maxTurns` (#94) only moves
  the wall. Fix: model it as a **scripted-ETL `script` action** (the N-item volume happens in one
  script execution, off the turn budget), add a **discover-size-first** probe where N is unknown, and
  **split** the scripted bulk-capture from a **bounded** per-item curation task — see the
  bulk/unbounded-fan-out section above + `stacks/dotnet.md §12`. The tell `/guardrails-review` hunts: a
  crawl/scrape/bulk-transform task written as a prompt that "enumerates … and produces a note per
  item" with no size bound and no script.
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

<!-- BEGIN ADDED ANTI-PATTERNS #74/#75/#76/#96 (auto-merge friendly; do not merge into the list above) -->
- **Keyword-not-structural for a METHOD CALL** (#76): a "file calls `B.Method()`" guardrail that greps
  a **bare method name** — `RunAsync\s*\(` — instead of the call construct. It false-passes on a comment
  (`// RunAsync(scope)`), a **local stub/wrapper** method of the same name (`private void RunAsync(...)`),
  or any unrelated same-named method — none of which invoke the real library method. The call-site
  sibling of the type/member keyword-not-structural trap. Fix: **two sequential checks** — reference the
  **type** (`MigrationRunner`, rules out a local stub) AND the **dotted call** (`\.RunAsync\s*\(`, rules
  out comments and standalone definitions). Apply whenever "task A must call `B.Method()`" on a specific
  type in another project. See the method-call-anchoring section; `stacks/dotnet.md §15`.
- **Library bypasses its injected interface** (#74): a task extracts a library that **must write through
  an injected `IInterface`**, the library is registered + builds + tests pass — but **no guardrail checks
  the library's internals don't call the CONCRETE method directly**, bypassing the abstraction it exists
  to enforce. Registration/build/tests all stay green over the bypass. Fix: a forbidden-call scan of the
  **library project's `.cs` only** (scope to the lib folder, exclude `bin`/`obj`), **comment-stripped**
  (#97/#98 — else a comment naming the method false-REDs a correct library) and **dot-anchored**
  (`\.ConcreteMethod\s*\(`, #76 — else a same-named method or string literal false-REDs). Trigger: "must
  NOT call `X` directly" / "write through interface `Y`" / "the Exe bypasses the abstraction". See the
  no-direct-bypass section; `stacks/dotnet.md §16`.
- **Enumerated behaviors unverified** (#75): a test-author task whose action prompt lists **≥3 named
  behaviors** to encode but whose guardrails are only `tests-exist` + `tests-fail-on-current-code` —
  **neither checks the named behaviors are present**, so **one** trivially-failing stub test satisfies
  both while behaviors 2–N are never encoded (the coverage-gap anti-pattern, made concrete). Fix: add a
  `covers-key-behaviors` check for **2–3 distinctive terms** (domain type / enum / method name — never
  generic words like `test`/`assert`) from the behavior list, **scoped to the one test file**. Name it a
  **lower bound** (a term present ≠ the behavior asserted; the residual is human review) and report which
  enumerated behaviors went unchecked. See the covers-key-behaviors section; `stacks/dotnet.md §17`.
- **Name-convention seam unverified** (#96): task A produces artifacts a consumer (task B / a runtime
  component) resolves by a **derived or mapped name** (url→embedded resource, step id→filename, key→file,
  route→handler, message-type→schema) — and `file-exists`/`file-contains` on A plus content checks on B
  **both pass while the naming contract between them is never exercised**. B derives a name A never
  produced (case / separator / single special-case drift) and **404s/silently-falls-back at runtime** on
  a 100%-green suite — invisible until the first real run (a kebab `destination.html` outlier vs a
  PascalCase `DestinationSelection.html` lookup). Fix: a **consumer-driven integration guardrail** on a
  **both-sides-present** task that **parses the consumer's real map** (never a hard-coded contract copy),
  drives the lookup for **every** item, and asserts **200 + a per-item marker** (not a fallback body).
  Mark it `scope:"integration"` and keep it **union-safe** (#125 — assert "every present artifact
  resolves", an invariant, not a terminal postcondition). The tell `/guardrails-review` hunts: a
  derived-name consumer (fetch-by-name, embedded-resource/reflection lookup, convention file-map) with
  only per-side file-exists/content checks and no end-to-end lookup over the whole set. See the
  name-convention-seam section; `stacks/dotnet.md §18`.
<!-- END ADDED ANTI-PATTERNS #74/#75/#76/#96 -->

<!-- BEGIN ADDED SECTION #94 — maxTurns budgeting doctrine (auto-merge friendly; do not merge into prose above) -->
## maxTurns budgeting — a turn-budget exhaustion is NOT a sizing failure (#94)

A guardrail catches a *wrong implementation*; a turn-budget exhaustion is a different failure class
— a **legitimately-progressing agent killed at the turn cap mid-task** — and the breakdown prevents
it not with a guardrail but by **budgeting `maxTurns` per task** (SKILL.md Step 4a; schemas.md
"Per-task `maxTurns`"). It belongs in this catalogue because the *diagnosis* is the doctrine: when a
prompt task fails on `max_turns` (`"terminal_reason":"max_turns"`, `"Reached maximum number of turns
(50)"`), the wrong fix is "split it further."

**Why "split it further" is wrong here.** The sizing heuristics (Step 2: one-session rule, guardrail-
boundary rule) model *deliverable count*, not *research/discovery overhead*. An integration task whose
assertions share ONE expensive setup (an in-process stdio/MCP harness) is correctly sized — its
assertions cannot be split without **duplicating** that setup, which makes the budget problem worse.
The cost driver is the agent reverse-engineering an unfamiliar SDK before it can write code (grepping
package XML docs for `McpClientOptions`/`CallToolResult.Content`/…), which is real progress, not a
loop. Splitting punishes a well-sized task for a budget problem.

**The doctrine.** Keep the flat **50** default; bump only the predictably turn-expensive archetypes
to a fixed **75** (a first-attempt cushion — actuals in the motivating run were 54 and 32, unguessable
in advance):
- **integration / smoke / e2e** tests, especially an in-process harness, transport-client wiring, or
  spawning a server;
- **work against an unfamiliar third-party SDK** (discover the API before writing code);
- **terminal aggregation / wiring** tasks that connect several unfamiliar seams at once.

A guessed *exact* budget is impossible; the fixed bump only needs to clear the common boundary case
(54 > 50). The real safety net is a **harness-side auto-escalate-on-`max_turns` retry policy**
(×1.5 next attempt) + distinct `max_turns` retry feedback — a SEPARATE harness concern, owned by the
harness developer, NOT emitted by the breakdown. The breakdown's contribution is the deliberate
first-attempt bump and a shared-harness insertion (below) so the heuristic is applied at generation
time, not discovered by a failed run.

**Amortize unfamiliar-SDK discovery (a generative insertion).** When ≥2 downstream tasks need the
same setup against an API no ancestor established, insert ONE upstream harness task that learns the
API and writes the reusable helper (a `<X>TestHost`); the downstream tasks `dependsOn` it instead of
re-discovering the API. This is the test-harness sibling of the production-seam (#84) and
composition-root (#120) insertions — driven by a shared *discovery cost*, not a missing artifact. The
harness task itself gets the `maxTurns: 75` bump (it pays the discovery cost). See SKILL.md Step 4a.
<!-- END ADDED SECTION #94 -->

<!-- BEGIN ADDED SECTION #116 — Windows-safe git test fixture (auto-merge friendly; do not merge into prose above) -->
## Windows-safe git test fixture — author-tests that build a real git repo (#116)

When an author-tests task's tests create a **real git repository**, a hand-rolled temp-repo helper
that assumes POSIX git semantics fails on Git-for-Windows in ways a POSIX-only author never sees —
and because the breakdown generates each author-tests task in isolation, **every** test-author agent
re-discovers (or misses) the same quirks independently, each a fresh `needs-human` halt. The fix is
the same posture as the test-framework decision: **resolve it once at generation time** by emitting
ONE shared, Windows-safe fixture (or injecting a portability directive), not per-task rediscovery
(SKILL.md Step 5a).

**The logged Windows-git lessons the fixture MUST encode** (each is a real halt):
- **Read-only loose objects (#109).** `Directory.Delete(repoRoot, recursive: true)` throws
  `UnauthorizedAccessException` (NOT `IOException`) because Git marks `.git/objects` loose objects
  **read-only** on Windows. → Strip read-only attributes before deleting.
- **Empty-directory prune (task-14).** After `git rm`/`git mv` empties a directory (`src/`),
  Git-for-Windows **prunes it**, so the next `File.WriteAllText(src/New.cs)` throws
  `DirectoryNotFoundException`. → Recreate the directory before writing into it.
- **`merge --abort` rollback failure (W3).** `git merge --abort` fails rc=128 on a dirtied tracked
  path. → Roll back with `git reset --hard <preHead>`, never `git merge --abort`.
- **Non-deterministic hashes.** Platform line-ending translation changes fixture content hashes. →
  Set `core.autocrlf=false` for deterministic hashes across platforms.

**Two ways to satisfy it** (pick per task; prefer the fixture when ≥2 tasks build real repos — the
same amortize-the-discovery logic as #94's shared-harness insertion):
1. Emit a shared `TempGitRepo` fixture (one reviewed file the git-touching tests reuse). The .NET
   realization is `stacks/dotnet.md §11`.
2. Inject a "Windows-Git test portability" directive into the git-touching author-tests action
   prompt, pointing at the fixture and naming the four behaviors above.

This is **authored-test portability**, distinct from runner-level failures (#114/#115). It is applied
at generation time so a Windows-git quirk surfaces in a reviewed fixture, not as a mid-run halt.
<!-- END ADDED SECTION #116 -->

<!-- BEGIN ADDED SECTION #101 — new-.claude/-subdirectory deliverable seeding (auto-merge friendly; do not merge into prose above) -->
## New-`.claude/`-subdirectory deliverable — seed the directory before the run (#101)

This is the **directory analogue of the artifact-ancestry rule** (below): a guardrail referencing a
file no ancestor produces is a missing inserted task; here the missing prerequisite is a *directory*.
Claude Code's `acceptEdits` mode (the default runner profile) auto-approves writes to **existing**
paths but **blocks creating a new subdirectory under `.claude/`** (skills/commands/hooks/agents/
contexts) without interactive confirmation. Headless, there is no human to confirm, so an agent
writing `.claude/skills/<new>/SKILL.md` into a not-yet-existing directory correctly self-blocks to
`{"needsHuman": "..."}` and the run halts (#101).

**Detection (at breakdown time).** A task whose primary deliverable is a file under `.claude/` AND
whose target subdirectory does not already exist (`Test-Path .claude/skills/<name>/`). An existing
subdir needs nothing; only a NEW one trips the barrier.

**Fix — seed it, or warn** (SKILL.md Step 5b):
1. Insert a **directory-seed task** (`NN-seed-<name>-dir`, a **script** action — `New-Item -ItemType
   Directory` + a `.gitkeep` write) immediately before the writing task, which `dependsOn` it —
   making the directory "existing" so `acceptEdits` approves the write. It MUST be a script, not a
   prompt: a script the harness runs directly bypasses the `acceptEdits` tool-permission barrier,
   so it creates the new `.claude/` subdir headlessly; a prompt seed task would hit the same barrier
   it is meant to remove. Prefer this for an unattended run.
2. Or add a `## Pre-conditions` note to the writing task's action prompt requiring the caller to
   pre-create the directory (a committed `.gitkeep`) before the run.

**Guardrail — make the barrier visible.** A `01-dir-seeded.ps1` (`file-exists`, #1) on the writing
(or seed) task asserting the target subdir exists before the write, scoped to the one subdir the task
owns — so the barrier reads as a guardrail failure, not a cryptic `needsHuman`:

```powershell
# catches: a task writing into a NEW .claude/ subdir that acceptEdits cannot create headlessly -
#          the dir was never seeded, so the write self-blocks to needsHuman mid-run. Assert the
#          target subdir EXISTS before the write is attempted.
$dir = ".claude/skills/survey-eval"
if (-not (Test-Path $dir -PathType Container)) {
    Write-Output "$dir does not exist - seed it (a committed .gitkeep) before the run; acceptEdits cannot create a new .claude/ subdir headlessly"
    exit 1
}
exit 0
```

Issue #104 is the harness-side counterpart (granting the write up front); the breakdown owns only the
detection + seeding doctrine here.
<!-- END ADDED SECTION #101 -->

## The artifact-ancestry rule

A guardrail may only reference artifacts that are (a) produced by an ANCESTOR task in
the DAG, or (b) pre-existing in the repo. A guardrail that checks something no
upstream task produces will fail forever — that is a missing inserted task (see the
skill's Step 5), not a guardrail problem. Sweep every guardrail against this rule
before writing the folder.
