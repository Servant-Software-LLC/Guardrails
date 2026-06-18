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
│                                └─ INSERT a test-author task upstream BY DEFAULT (SKILL Step 2
│                                   TDD rule); skip only if tests already exist or behavior is
│                                   too simple for unit tests — state why in task description
│                                   (test-author: tests-fail-on-current-code (8); tests-build
│                                    only if tests compile against current code)
│                                   Both tasks declare writeScope in task.json (SKILL Step 5):
│                                   test-author → narrow to its test file(s); implementation →
│                                   narrow to its source tree. GR2015 enforces the TDD pair
│                                   guarantee: the implementation's scope must not subsume the
│                                   test-author's output files.
├── A runnable script/tool     → file-exists + command-exit-code on a representative invocation
├── A running service          → port-answers + endpoint-content (curl + contains/schema)
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

**`writeScope` — TDD pair doctrine.** The `writeScope` field in `task.json` bounds which
workspace files a task may write; the harness enforces it at runtime. For a TDD pair:
- **test-author task** → narrow scope to the test file(s) it authors (e.g.
  `["tests/MyProject/MyFeatureTests.cs"]`). The task's `specific-tests-pass` guardrail on
  the implementation task can then safely reference those files — they are contract-protected
  against modification by the implementation task.
- **implementation task** → narrow scope to the source tree it writes (e.g.
  `["src/MyProject/**"]`). GR2015 fires if this scope would subsume the test-author's
  outputs — that is the mechanical guarantee the implementation cannot overwrite the authored
  tests. Never set the implementation task's scope to `["**"]` when a test-author exists.

`writeScope: []` (empty) is correct for pure gate/state tasks that write no workspace files
(build checks, terminal suites, state-publish-only actions). `writeScope: ["**"]` (universal)
is only for genuinely repo-wide tasks (cross-cutting renames etc.) and requires a one-line
justification in the task description. GR2016 (WARNING) flags overlapping scopes among
independent tasks; GR2017 (ERROR) rejects malformed globs (?, brace, negation).

**Read-after-write edges.** When task B reads files task A produces (artifact dependency),
declare `dependsOn: [..., "A"]` on task B — the artifact-ancestry rule (SKILL.md Step 5).
`writeScope` makes these dependencies explicit and checkable: if A writes into a path
B later reads, B must depend on A so the harness serializes them correctly.

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
