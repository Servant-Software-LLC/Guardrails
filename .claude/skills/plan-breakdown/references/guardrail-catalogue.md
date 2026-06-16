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
| 9 | **prompt-judge** | `.prompt.md` (writes `{pass, reason}` verdict) | **LAST RESORT** — see the demotion gate | Genuinely subjective properties: tone, clarity, design taste |

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
│                                + tests-untouched (IMPLEMENTATION task: reads blob hashes the
│                                │  test-author task stored in state; tamper-evident regardless
│                                │  of commit timing — see SKILL.md Step 5 three-part pattern;
│                                │  required whenever an upstream test-author task exists)
│                                └─ INSERT a test-author task upstream BY DEFAULT (SKILL Step 2
│                                   TDD rule); skip only if tests already exist or behavior is
│                                   too simple for unit tests — state why in task description
│                                   (test-author guardrails: tests-fail-on-current-code (8),
│                                    state-fragment-written; tests-build only if tests compile
│                                    against current code)
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
