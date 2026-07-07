# Stack file — .NET (dotnet)

The **stack-specific** companion to `references/guardrail-catalogue.md`. The catalogue
holds universal doctrine; this file holds the .NET *instantiations* of it — the exact
regex, the canonical build command, the layout-specific traps. SKILL.md Step 0 loads
this file when it detects a .NET workspace (`.slnx` / `.sln` / `.csproj`). On a
JVM/Go/Python project these patterns are wrong or irrelevant — use that stack's file
instead (none ship yet; see "Future stacks" at the foot of SKILL.md Step 0).

Every stack file answers the same six standard questions first (§1–§6, including §4.1 stub-based
TDD `build-passes` + `tests-fail-on-stubs`), in this order, so
the files are mirror-able; stack-specific extensions for particular project kinds follow
(§7–§8 server/executable wiring + smoke-test, §9 UI-presence, §10 composition-root wiring,
§11 strip-comments-before-forbidden-keyword-scan, §12 Windows-safe git test fixture, §13 production testability seam, §14 scripted ETL / bulk fan-out, §15 method-call anchoring, §16 no-direct-bypass, §17 covers-key-behaviors (§17.1 structural [Fact]/[Theory]), §18 name-convention seam, §19 duplicate-definition union sub-check, §20 negative assertion, §21 baseline-green (preflight) root, then WPF).
Each pattern's PowerShell example
follows the catalogue's conventions: a leading `# catches:` line, one actionable
`Write-Output` line on failure, explicit `exit 1` / `exit 0`. Scope every grep to the one
file the task owns.

---

## 1. Build-descriptor registration — a new project must be in the solution

Adding a `.csproj` to disk is not the same as registering it in the solution
(`.sln` / `.slnx`). `dotnet build <solution>` **passes even when the project is
unregistered** — it builds only the projects the solution names, and silently ignores
the orphan. Downstream tasks that build the `.csproj` *directly* pass too, and even a
terminal whole-solution gate passes — so the new project is never compiled in solution
context, ever.

So when a task adds a project to a solution, pair the `file-exists` guardrail on the
`.csproj` with a **`file-contains` guardrail on the SOLUTION FILE** asserting it names
the project:

```powershell
# catches: project created on disk but never registered in the solution file
$slnPath = "PoC/ConformedSources/WorksoftMigrator.slnx"
if ((Get-Content $slnPath -Raw) -notmatch 'WorksoftMigrator\.Desktop') {
    Write-Output "WorksoftMigrator.Desktop not registered in $slnPath"
    exit 1
}
exit 0
```

Match the project *name* (or its `.csproj` path) — `.slnx` is XML listing
`<Project Path="…/WorksoftMigrator.Desktop.csproj" />`; classic `.sln` lists a `Project(...)`
GUID line. Either way the project name is the load-bearing token. Anchor the regex on
the exact project name to avoid matching a substring of a sibling project.

## 2. Cross-module dependency reference — the consumer must reference the producer

When task A creates an abstraction project (e.g. `MigrationAbstractions` with
`IDestinationWriter`) that task B must consume, the build does **not** prove the
reference exists: C# projects compile independently. `WorksoftMigrator.Desktop` builds
fine without a `<ProjectReference>` to `MigrationAbstractions` — it simply can't see the
types. An agent under guardrail pressure can satisfy "implements `IDestinationWriter`"
by defining a **local copy** of the interface inside Desktop, leaving the central
abstraction project as dead code that nothing references.

Add a **`file-contains` guardrail on the CONSUMER `.csproj`** asserting a
`<ProjectReference>` to the producer (place it on the abstraction task, or as an early
guardrail on the consuming task):

```powershell
# catches: abstraction project created but the consumer never references it
$consumerCsproj = "PoC/ConformedSources/WorksoftMigrator.Desktop/WorksoftMigrator.Desktop.csproj"
if ((Get-Content $consumerCsproj -Raw) -notmatch '<ProjectReference[^>]*MigrationAbstractions') {
    Write-Output "WorksoftMigrator.Desktop.csproj missing <ProjectReference> to MigrationAbstractions"
    exit 1
}
exit 0
```

This pairs with the structural implementation check below: the reference proves the type
*can* come from the shared project; the `class … : IFoo` regex proves it *was*
implemented — together they close the "local copy of the interface" loophole.

## 3. Structural implementation check — match the C# declaration, not the token

The universal rule (catalogue → "file-contains: structural vs. keyword matching"): match
the construct, not a bare keyword. The C# instantiation for "class `Bar` implements
interface `IFoo`":

- **Weak (gameable):** `Select-String -Pattern "IDestinationWriter"` — matches a comment
  `// IDestinationWriter`, a `using`, a type reference, or a locally-declared copy of the
  interface. A comment alone passes.
- **Strong:** require the class declaration with the interface in its base list:

```powershell
# catches: an "implements IDestinationWriter" claim satisfied by a comment, a using, or a local copy
$impl = "PoC/ConformedSources/WorksoftMigrator.Desktop/CloudDestinationWriter.cs"
if ((Get-Content $impl -Raw) -notmatch 'class\s+\w+\s*:\s*(\w+,\s*)*IDestinationWriter') {
    Write-Output "CloudDestinationWriter.cs does not declare a class implementing IDestinationWriter"
    exit 1
}
exit 0
```

The base-list regex `class\s+Bar\s*:\s*(I\w+,\s*)*IFoo` allows other base types/interfaces
before `IFoo` (C# permits `class Bar : BaseClass, IOther, IFoo`). Use the concrete class
name when you know it (`class\s+CloudDestinationWriter\s*:`), or `class\s+\w+\s*:` when the
implementing class name is the agent's choice. Scope to the implementing file (pattern 2's
ProjectReference check stops the local-copy escape; this regex stops the comment escape).

### 3.1 Property declaration — match up to the brace, NOT a fixed accessor order (#112)

A structural "property declared / removed" check is **accessor-order-sensitive** if it keys
on a fixed leading accessor. C# accessor order is **free**:
`{ get; init; }` ≡ `{ init; get; }` ≡ `{ get; set; }` ≡ `{ set; get; }` — all the same
property. A regex like `public\s+[^\s]+\s+NAME\s*\{\s*get` only matches when `get` is the
**first** accessor, so it:

- **false-PASSES a "field removed" check** when the field survives as `{ init; get; }`
  (init first): the regex doesn't match, the guardrail concludes the field is gone, and an
  **incomplete refactor ships green** — the structural analogue of the matcher/decoy
  false-green. The motivating case (plan-08 task 08) retained
  `public IReadOnlyList<string> CaptureHashes { init; get; }` and the `\{\s*get` removal
  check passed while the field lingered.
- **false-FAILS a "field declared" check** symmetrically — the property is present as
  `{ init; get; }` but the leading-`get` regex says it is absent.

**Rule.** Key a property-declaration check on the declaration **up to the opening brace** —
the type and name, which are order-free — and stop there. If accessor *presence* genuinely
matters, test for `(get|set|init)` **anywhere inside the accessor block**, never a fixed
leading accessor.

- **Weak (accessor-order-sensitive — false-passes on `{ init; get; }`):**
  `public\s+[^\s]+\s+CaptureHashes\s*\{\s*get`
- **Strong (order-insensitive — matches regardless of get/set/init order):**
  `public\s+[^\s]+\s+CaptureHashes\s*\{`
  (and, only if an accessor must be present, additionally require
  `\{[^}]*\b(get|set|init)\b` inside the block).

The `[^\s]+` type token spans generics (`IReadOnlyList<string>`) and arrays without a
greedy `.*`. Use `(?m)` so `^`/`$` and the per-line scan behave on a multi-line file.

```powershell
# catches: a "triad field removed" claim that false-passes when the field survives with a
#          different ACCESSOR ORDER - e.g. `public bool RestoreOnRetry { init; get; }`
#          (init first) is NOT matched by `\{\s*get`, so the lingering field reads as gone.
#          Key on the declaration up to the brace - order-insensitive by construction.
$tn = Get-Content "src/Guardrails.Core/TaskNode.cs" -Raw
foreach ($field in @('Exclusive','CaptureHashes','RestoreOnRetry')) {
    if ($tn -match "(?m)public\s+[^\s]+\s+$field\s*\{") {
        Write-Output "TaskNode.cs still declares property '$field' (teardown incomplete) - regardless of get/set/init order"
        exit 1
    }
}
exit 0
```

```bash
# catches: same - a property-removal check that false-passes on `{ init; get; }` ordering.
#          Match the declaration up to the brace; do not anchor on a leading `get`.
set -euo pipefail
tn="src/Guardrails.Core/TaskNode.cs"
for field in Exclusive CaptureHashes RestoreOnRetry; do
    if grep -Eq "public[[:space:]]+[^[:space:]]+[[:space:]]+${field}[[:space:]]*\{" "$tn"; then
        echo "TaskNode.cs still declares property '$field' (teardown incomplete) - regardless of get/set/init order"
        exit 1
    fi
done
exit 0
```

For a **field-DECLARED** (presence) check, flip the sense — fail when the
match-up-to-the-brace regex is *absent* — using the same order-insensitive pattern. The
same fix applies to every `class/record/interface … { … }` structural check: anchor on the
order-free part of the declaration, never on whichever member/accessor happens to be written
first. This is the property/accessor instantiation of §3's universal "match the construct,
not the token" rule.

## 4. Canonical build command — how `dotnet build` should appear in guardrails

- **Build a single project** (a code task's `build-passes` guardrail): build THIS task's
  project, not the whole solution — keep failures attributable and the DAG parallel.
  ```powershell
  # catches: code that doesn't compile
  dotnet build PoC/ConformedSources/WorksoftMigrator.Desktop --nologo -v q
  if ($LASTEXITCODE -ne 0) {
      Write-Output "WorksoftMigrator.Desktop does not build"
      exit 1
  }
  exit 0
  ```
- **Build the whole solution** belongs to ONE terminal integration task only (catalogue:
  "all tests pass" / whole-suite green is terminal-only). **This guardrail is LOCAL — no
  `scope` key (#165).** A whole-solution build is a **terminal postcondition**, not a
  union-safe invariant: at an intermediate union in a TDD plan the merged bytes contain test
  files referencing types whose implementation task has not run yet, so the solution build
  FAILS there and the harness rolls the wave back. Keep it LOCAL so it runs only in the
  terminal gate's own attempt, after every upstream task has merged (`01-solution-builds`):
  ```powershell
  # catches: a project that builds alone but breaks the solution (e.g. unregistered or a broken ref)
  dotnet build PoC/ConformedSources/WorksoftMigrator.slnx -c Release --nologo
  if ($LASTEXITCODE -ne 0) {
      Write-Output "solution build failed"
      exit 1
  }
  exit 0
  ```
  Its sidecar declares **no `scope`** (defaults to `"local"`):
  ```jsonc
  // 01-solution-builds.json — LOCAL (runs only at the terminal gate's action)
  { "description": "Full solution build — catches cross-project compilation errors after all plan tasks merge" }
  ```
- **Tests:** filter to THIS task's tests (`dotnet test <proj> --filter "Category=Stats" --nologo`),
  per archetype #4; the whole-suite `dotnet test` (no filter) is the **terminal gate** and is
  likewise **LOCAL — no `scope` key (#165)** (a whole suite is a terminal postcondition for the
  same reason: a Wave-2 union holds tests for not-yet-implemented types):
  ```jsonc
  // 02-all-tests-pass.json — LOCAL (runs only at the terminal gate's action)
  { "description": "Full test suite — catches regressions in all existing tests and verifies all new tests pass on merged HEAD" }
  ```
  **Every `dotnet test` guardrail that asserts tests PASS MUST re-emit the failure DETAIL at the
  end of stdout — see §4.2 (#179).** The bare `dotnet test … ; if ($LASTEXITCODE -ne 0) { … }`
  forms shown above and in §10a are the *skeleton*; for a guardrail that asserts tests pass,
  wrap that skeleton in §4.2's capture-then-re-emit form so the assertion/exception text lands in
  the harness retry-feedback tail. (The §4.1 `tests-fail-on-stubs` red check is the INVERSE — a
  NON-zero exit is its success — so it does NOT re-emit; §4.2 says which archetypes do.)
- **The terminal gate's `scope: "integration"` guardrail (GR2018) is a CONDITIONAL union
  invariant, not the build/suite (#165).** GR2018 still requires the gate sink to carry ≥1
  `scope: "integration"` guardrail — make that the union-safe conflict-marker / union-invariant
  check (the overlapping-writeScope union-guardrail, §10/§17 and the catalogue), written in the
  **conditional gate-then-verify form** so it passes trivially at unions where a contribution has
  not landed:
  ```powershell
  # catches: a union that dropped a colliding sibling's hunk or left conflict markers on a shared file.
  # scope:"integration" — union-safe: gate on the file/contribution being present, THEN verify it.
  $ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
  $path = Join-Path $ws 'PoC/ConformedSources/Importers/CommanderLauncher.cs'
  if (-not (Test-Path $path)) { exit 0 }          # not produced at this union yet — nothing to verify
  $content = Get-Content -Raw -Path $path
  if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {   # line-anchored ours/theirs — no bare '=======' (#187)
      Write-Output "CommanderLauncher.cs contains git conflict markers — the union did not cleanly integrate"
      exit 1
  }
  if ($content -match 'ImportMode') {             # dispatch contribution landed — require the real construct
      if ($content -notmatch 'ImportMode\.\w+|switch.*ImportMode|case ImportMode') {
          Write-Output "ImportMode present only as comment — dispatch construct missing after union"
          exit 1
      }
  }
  exit 0
  ```
  ```jsonc
  // 03-launcher-union-verified.json — INTEGRATION-scoped, union-safe conditional invariant
  { "description": "Union invariant on the shared launcher: conflict-marker-free; each landed contribution is real", "scope": "integration" }
  ```
- Always pass `--nologo` (and `-v q` on builds) so the one actionable failure line isn't
  buried in banner noise. Declare no interpreter for `dotnet` — it's a build tool the
  guardrail invokes, not a script interpreter (those go in `guardrails.json: interpreters`).
- **Never `--nologo` on `dotnet run` (#194).** `--nologo` is a `dotnet build` / `dotnet test`
  flag — it is **not** a `dotnet run` flag. On a `dotnet run --project <proj> -- <args>` line,
  anything before the `--` is parsed by `dotnet run`, and a placed-there `--nologo` **falls through
  to the app's own arg parser** (or errors), failing the guardrail before the app runs. To quiet the
  build chatter `dotnet run` prints, use **`-v quiet` before the `--`**:
  `dotnet run --project src/Guardrails.Cli -v quiet -- validate <folder>`. This bites the
  self-hosting dogfood case (a guardrail that validates a task folder against the freshly-built
  loader, `dotnet run … -- validate <folder>`) — model-generated versions habitually tack on
  `--nologo` after the `--` or before it and self-fail before validate ever runs.
- **Remember the solution-build blind spot (pattern 1):** the terminal `dotnet build
  <solution>` does NOT catch an unregistered project — that's why pattern 1's solution-file
  `file-contains` guardrail exists. Don't let the terminal build stand in for it.

### 4.1 Stub-based TDD "red" — `build-passes` + `tests-fail-on-stubs` for a BEHAVIORAL type (#155)

The .NET realization of the catalogue's stub-based TDD decision (catalogue → "Stub-based TDD"). For a
**behavioral** type under test, the test-author task writes the test file AND the minimal stubs (a
skeleton class whose members `throw new NotImplementedException();`) so the test project COMPILES. Its
two guardrails replace the old single compile-coupled check, in cheapest-first filename order:

`guardrails/01-build-passes.ps1` — proves the test file is syntactically valid and type-correct (the
stubs supply the types). Garbage that does not compile fails HERE, unambiguously:

```powershell
# catches: a test file that does not COMPILE - garbage, or a real syntax/type error. With the
#          minimal stubs the test-author task wrote, the test project must build; a non-compiling
#          "test" exits dotnet test non-zero identically to a failing one, so without this the red
#          signal is gameable by garbage (#155).
dotnet build tests/Inventory.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Inventory.Tests does not build - the test file or its stubs are not type-correct"
    exit 1
}
exit 0
```

`guardrails/02-tests-fail-on-stubs.ps1` — proves TDD red. Because guardrail 01 already proved the
build succeeds, a non-zero `dotnet test` now unambiguously means **the tests ran and FAILED** (the
stubs throw `NotImplementedException`), not that something failed to compile:

```powershell
# catches: tautological tests - tests that PASS against the stubs verify nothing. With the build
#          green (guardrail 01), a non-zero exit here means the tests ran and FAILED against the
#          NotImplementedException stubs = TDD red. A zero exit means the behavior is already present
#          (or the test asserts nothing) - either way the tests are tautological.
dotnet test tests/Inventory.Tests --filter "Category=Stats" --nologo
if ($LASTEXITCODE -eq 0) {
    Write-Output "the Stats tests PASS against the NotImplementedException stubs - they are tautological (no real behavior is asserted)"
    exit 1
}
exit 0
```

The test-author `task.json` `writeScope` covers **both** the test file and the stub file(s); the
implementation `task.json` `writeScope` **EXCLUDES** the test file but **TARGETS** the stub file(s)
(it fills real logic over the skeletons — `src/Inventory.Cli/` covers a stub under that surface). The
implementation task's `02-stats-tests-pass.ps1` then runs the SAME `--filter` and requires exit 0.

**Data-model exception (no behavioral stub possible).** For a pure enum/record/value type, COLLAPSE
the split (define the type + assert `tests-pass` in one task; state "data model — no behavioral stub
possible"). If you keep the split, omit `build-passes` (the test references the not-yet-existing type,
so it won't compile against current code — that non-zero exit IS the red), keep
`tests-fail-on-current-code`, and strengthen `covers-key-behaviors` STRUCTURALLY (assert the
`[Fact]`/`[Theory]` attribute is present, not just that the enum-value tokens appear — §17).

### 4.2 Put the failure DETAIL in the tail — re-emit assertion/exception lines at the END (#179)

The catalogue's failure-detail-in-tail doctrine (catalogue → "Failure detail must reach the retry
tail"), realized for .NET. The harness feeds a failed guardrail's **stdout tail** back to the next
attempt as the retry feedback — the last 60 lines, then the last 4000 chars (a fixed harness contract,
`RetryPolicy.AppendTail`; not something a guardrail can change). Default / minimal-verbosity
`dotnet test` emits each failure's **assertion message and exception/stack trace INLINE, mid-run**,
and ends with only `[FAIL] <name>` lines plus the `Failed: N, Passed: M` count. So a bare
`dotnet test … ; if ($LASTEXITCODE -ne 0) { Write-Output "tests failing"; exit 1 }` puts only the
test *names* in the tail — the agent sees **what** failed but not **why**, and retries blind. (The
motivating case: plan-0009 task 10 burned 12 attempts to `needsHuman` before a human ran the tests
manually to read a one-line `$.itemOutcomes[0].status` JSON error the tail had cut.)

**Rule — every `dotnet test` guardrail that asserts tests PASS must make the failure detail the LAST
thing on stdout**, so the harness tail captures it. The robust form (works regardless of logger
ordering, and across several failures) is **capture → emit the full log → re-emit the
failure-signal lines at the very end**:

```powershell
# catches: an implementation whose output deviates from the specified format. Re-emits the
#          assertion/exception lines at the END so they land in the harness retry-feedback tail
#          (the last ~60 lines of stdout) - default `dotnet test` prints them mid-run and ends with
#          only `[FAIL] <name>` + the count, so the tail would otherwise show WHAT failed, not WHY (#179).
$out = dotnet test tests/Inventory.Tests --filter "Category=Stats" --no-build --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }                 # full log first (for the attempt's saved output)
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "Stats tests failing - flag not implemented to spec (see failure details above)"
    exit 1
}
exit 0
```

Notes that make it robust:

- **The re-emit is the load-bearing part**, not the verbosity flag. You MAY also pass
  `--logger "console;verbosity=detailed"` (which moves failure messages into the end-of-run summary),
  but logger ordering varies by SDK/framework; the explicit capture-and-re-emit **deterministically**
  puts the assertion/exception lines at the tail even with several failures. Prefer it.
- **Bound the re-emitted block** (`Select-Object -First 40` above) so it fits the ~60-line tail in the
  common few-failures case — an unbounded re-emit on a large failing suite would itself overflow the
  tail and re-bury the first failures. 40 lines leaves room for the framing + the final reason line.
- **`--no-build`** assumes an upstream `build-passes` guardrail (§4 / §4.1) already compiled the
  project; drop it if this is the only check that compiles the tests.
- **Keep ONE final actionable reason line** after the re-emitted block (the §4 / catalogue
  one-actionable-line rule still holds) — it is the human-readable summary at the very bottom of the
  tail; the re-emitted detail sits just above it.

**Which archetypes re-emit, precisely:** every realization that asserts tests **PASS** — the §4
filtered `specific-tests-pass`, the §4.1 implementation `02-…-tests-pass` (the SAME `--filter`, exit 0
required), the §4 whole-suite terminal `02-all-tests-pass`, and the §10a `production-wiring` /
`specific-tests-pass` driver. The §4.1 `tests-fail-on-stubs` and the data-model
`tests-fail-on-current-code` are the **INVERSE** — a NON-zero exit is their SUCCESS, so there is no
failure to feed back and they do **not** re-emit (re-emitting there would surface the EXPECTED red as
if it were a problem). Match the construct's polarity: re-emit only where exit 0 is the pass.

## 5. Grep-scope contamination risks specific to .NET layout

.NET's project-per-folder layout under a common solution root is exactly the shape the
universal grep-scope anti-pattern warns about (catalogue → anti-patterns). Concrete
.NET traps:

- **Same-wave siblings in one project.** Two tasks each writing a `.cs` file into
  `WorksoftMigrator.Desktop` in the same wave: a broad
  `Get-ChildItem WorksoftMigrator.Desktop -Recurse -Filter *.cs | Select-String "LocalAppData"`
  is satisfied by *either* file. Scope to the file the task owns:
  `Select-String -Path ".../WorkspaceRecentsList.cs" -Pattern "LocalAppData"`.
- **`using` / namespace echoes.** A token like `IDestinationWriter` appears in every file
  that `using`s the abstraction namespace, so a solution-wide grep for the bare name is
  meaningless — combine the file scope with the structural regex (pattern 3).
- **`bin/` and `obj/` shadow copies.** A recursive grep across a project dir hits compiled
  shadow copies and generated files under `bin/`/`obj/`, matching stale or generated
  content. Target the source file directly, or exclude `bin`/`obj` if you must recurse.
- **Multi-targeted output.** Same as above — `obj/.../*.AssemblyInfo.cs` and generated
  globbing files can contain unexpected tokens. Source-file scoping sidesteps all of it.

## 6. Test framework — detect it, never default it

There is no "standard" .NET test framework to assume. xUnit is the most common in public
repos, which is exactly why a model silently reaches for it — and exactly why the breakdown
must NOT. Resolve the framework from evidence (SKILL.md Step 0 → `$testFramework`; Step 5
framework-selection rule):

- **Detected:** grep every `*.csproj` for the framework's package reference and mirror it
  (its versions and test-SDK setup too).
  ```powershell
  # which test framework does this repo already use?
  Get-ChildItem -Recurse -Filter *.csproj |
    Where-Object { (Get-Content $_ -Raw) -match '<PackageReference[^>]*Include="(xunit|NUnit|MSTest\.TestFramework)"' }
  # the matched Include token (xunit / NUnit / MSTest.TestFramework) is the framework to use
  ```
- **None found (greenfield):** the framework is an unmade decision. Surface it — ask the
  human (`AskUserQuestion`) in an interactive breakdown, or write the test-bootstrap action
  with an honest-halt (`needsHuman`) in an unattended one. **Do NOT write a default into
  this file.** A "xUnit is the .NET greenfield default" rule here would merely relocate the
  silent guess from the model's weights into the stack file; the choice must stay visible
  and reviewable per breakdown (this is the #40 → #42 resolution).

## 7. Entry-point wiring — the executable's `Program.cs` must reference the launcher (#64)

The catalogue's entry-point-wiring section: a server/CLI-executable plan decomposes into
component tasks each guarded by build + unit-tests, but **nothing proves the entry point
actually starts the handler**. `Program.cs` compiles and the solution builds with a
top-level `Console.WriteLine("hello")` that never touches the `Launcher` — the launcher is
implemented, unit-tested, and never called. A build guardrail cannot see this; a structural
grep on the ENTRY-POINT file can.

Detect the executable first (any one signals an exe outcome):

```powershell
# is this an executable / web project? (run during breakdown analysis, not as a guardrail)
Get-ChildItem -Recurse -Filter *.csproj |
  Where-Object { (Get-Content $_ -Raw) -match '(?i)Sdk\s*=\s*"Microsoft\.NET\.Sdk\.Web"|<OutputType>\s*Exe\s*</OutputType>' }
```

Then add a **`file-contains` guardrail on the entry-point file** (`Program.cs`, or the file
holding top-level statements / `Main`) asserting it references the launcher type. Match the
**construct**, not a bare token — a bare `Launcher` grep passes on a `using`, a comment, or
an unused field. Require the type used in a `new`/method-call position:

```powershell
# catches: a Program.cs that builds green but never instantiates/starts the launcher
#          (the "implemented but never called" gap unit tests cannot see)
$entry = "src/Wizard.Cli/Program.cs"
$code  = Get-Content $entry -Raw
if ($code -notmatch '(?m)new\s+Launcher\b|Launcher\s*\.\s*\w') {
    Write-Output "$entry does not instantiate or call Launcher - the entry point is not wired to it"
    exit 1
}
exit 0
```

Use the concrete launcher type the plan names (`Launcher`, `WizardHost`, `App`). The regex
allows either `new Launcher(...)` (the entry point constructs it) or `Launcher.Run(...)` (a
static start) — both are real wiring; a `using …Launcher;`, a `// Launcher` comment, or a
`private Launcher _x;` field declaration are not, and none match. Scope to the single
entry-point file (grep-scope rule, §5). This grep is **necessary but not sufficient** — it
proves the call is *written*, not that it *serves*; §8's live smoke-test proves the latter.

## 8. Live smoke-test — start the binary, poll a route, assert 200, tear down (#64)

Archetype #7 (port/endpoint-answers) for a .NET executable. This is the ONE guardrail that
verifies *the exe does what the plan says* rather than *the code compiles*. It owns the
process lifecycle: start the binary, poll a known route until it answers or a bounded
timeout elapses, assert HTTP 200, and **always** kill the process in a `finally`. Put it on
the inserted smoke-test task (SKILL.md Step 5), downstream of the wiring task and the
route-implementation task.

Hold it to the catalogue's determinism rules: a **bounded poll** (not a fixed sleep), a
**deterministic port** (passed to the binary, or parsed from the URL it prints — never
guessed), **teardown in `finally`** on every exit path, and **one actionable failure line**.
The pattern below uses pwsh cmdlets that work identically under pwsh on Windows, Linux, and
macOS (`Start-Process`, `Invoke-WebRequest`, `Stop-Process`):

```powershell
# catches: the binary builds and unit-tests pass but does not actually start and serve -
#          e.g. Program.cs never started the launcher, or the route is unrouted (404).
#          Starts the exe, polls a known route, asserts 200, and ALWAYS tears the process
#          down in finally so a failed poll never leaks a port-holding server.
$ErrorActionPreference = 'Stop'
$project = "src/Wizard.Cli"          # the executable project (or a published binary path)
$port    = 5099                       # a fixed port the binary is TOLD to use (see note below)
$route   = "/health"                  # a route an ANCESTOR task implements (artifact-ancestry)
$baseUrl = "http://127.0.0.1:$port"
$url     = "$baseUrl$route"
$timeoutSeconds = 20
$proc = $null
try {
    # Start the binary in the background, telling it which port to bind. Capture its output to
    # the attempt's scratch dir so a printed URL / startup error is inspectable on failure.
    $outLog = Join-Path $env:GUARDRAILS_LOG_DIR "smoke-stdout.log"
    $errLog = Join-Path $env:GUARDRAILS_LOG_DIR "smoke-stderr.log"
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $project, "--no-build", "--", "--urls", $baseUrl) `
        -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $lastError = "no response"
    $ok = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            $lastError = "process exited early (code $($proc.ExitCode)) - see $errLog"
            break
        }
        try {
            $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
            if ($resp.StatusCode -eq 200) { $ok = $true; break }
            $lastError = "HTTP $($resp.StatusCode)"
        } catch {
            $lastError = $_.Exception.Message   # connection refused while still warming up, etc.
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not $ok) {
        Write-Output "smoke-test: GET $url did not return 200 within ${timeoutSeconds}s (last: $lastError)"
        exit 1
    }
    exit 0
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
```

Adapt three things per plan, and **state each in the breakdown report**:

- **Launch form.** `dotnet run --project <proj> --no-build` is the portable default and
  assumes an upstream build guardrail already produced the binary (drop `--no-build` if not).
  For a published self-contained exe, set `$project` aside and
  `Start-Process -FilePath "<path>/Wizard.Cli"` (or `.exe` on Windows) directly — same
  `-PassThru`/redirect/`finally` shape.
- **Port.** The example passes `--urls http://127.0.0.1:$port` (the ASP.NET host convention
  for `Microsoft.NET.Sdk.Web`). A plain `<OutputType>Exe` that runs its own listener takes
  its port differently — pass the plan's actual flag/env var. If the binary instead *prints*
  the URL it chose (the "prints a URL" plan signal), read `$outLog` after startup and parse
  the printed `http://…:<port>` rather than fixing `$port` — that is the deterministic read
  for an ephemeral-port binary. Do not poll a port you only assumed.
- **Route.** `$route` MUST be implemented by an ancestor task (artifact-ancestry). Use the
  route the plan names (`/current-step`, `/health`); if the plan names none, surface it as a
  decision in the breakdown report — do not invent one.

**Teardown caveat.** `Stop-Process -Force` kills the started process; if the binary spawns
**child** processes (rare for a single self-hosted listener, possible for `dotnet run` which
launches the built app as a child), kill the tree. Under pwsh 7+,
`Stop-Process -Id $proc.Id -Force` on the `dotnet` host generally takes the app child with
it; when in doubt, launch the **published binary directly** (no `dotnet run` host layer) so
there is exactly one process to stop. Keep the `finally` unconditional either way.

Scope note: this proves the exe **starts and serves** — not that the *described UI* was
built and returned as real markup. That is §9 (UI-presence), which **reuses this exact
lifecycle** and adds one body assertion. Keep this §8 form as the pure starts-and-serves
check; when the plan is UI-facing, use §9's extended form on the smoke-test task instead of
running two process managers.

## 9. UI-presence — the described UI exists on disk and is actually served (#66)

The catalogue's UI-presence archetype for a .NET web executable. A plan promising a
browser-served screen ("serves a wizard", "the user completes the form") can decompose to
JSON endpoints + unit tests and pass §8's smoke-test while serving **no UI at all** — the
root returns 200 with JSON, never an HTML page. Two deterministic guardrails close this, on
the inserted `build-ui-<screen>` task (SKILL.md Step 5). **Neither is a prompt-judge** —
presence and wiring, never visual taste.

### 9a. Asset-exists — the page/asset was actually written

A `file-exists` check (archetype #1) on the page the screen needs, scoped to the one file
the UI task owns (grep-scope rule, §5). For a static `wwwroot` page:

```powershell
# catches: a UI plan that built only backend endpoints - the HTML page the screen needs
#          was never written, so the app serves a JSON API with no frontend
$page = "src/Wizard.Cli/wwwroot/wizard.html"
if (-not (Test-Path $page)) {
    Write-Output "$page does not exist - the wizard UI page was never built (backend-only build)"
    exit 1
}
exit 0
```

For an **embedded-resource** UI (the page is compiled into the assembly, not served from
`wwwroot`), assert the resource is declared in the project file instead of probing disk —
the file exists at author time but ships inside the DLL:

```powershell
# catches: the UI page exists in source but is not embedded, so it is absent at runtime
$csproj = "src/Wizard.Cli/Wizard.Cli.csproj"
if ((Get-Content $csproj -Raw) -notmatch '<EmbeddedResource[^>]*wizard\.html') {
    Write-Output "Wizard.Cli.csproj does not embed wizard.html as a resource - it will be absent at runtime"
    exit 1
}
exit 0
```

Use whichever matches how the plan serves the UI. The asset-exists check is necessary but
not sufficient — a page file can exist and still never be served (wrong route, not mapped as
static files); §9b proves it actually reaches the browser.

### 9b. Served-markup-contains — the served root returns the real UI, not JSON or a 404

This **extends §8's smoke-test** — it does NOT re-implement process management. Take the §8
script verbatim (start the binary, bounded poll, `finally` teardown, deterministic port,
**and its leading `# catches:` line** — the assembled guardrail still opens with one, e.g.
`# catches: the UI route answers but serves non-UI content (JSON / placeholder / 404 body)
instead of the described page`) and change exactly two things: poll the **UI route** (`/`,
`/wizard`), and after asserting the response answers, **assert its body contains a known UI
string** the `build-ui-<screen>` task produced. Asserting HTTP 200 alone is the trap §8
warns about for UI plans — a JSON API returns 200 from `/`. The delta from §8 is the inner
success test (the elided `...` lines are §8's scaffold, unchanged):

```powershell
# (identical §8 start / bounded-poll / finally-teardown scaffold; only the success test differs)
#   $route   = "/"                       # the UI route an ancestor maps (artifact-ancestry)
#   $uiMarker = 'id="wizard-step"'       # a known element/string from wizard.html (the UI task built it)
    ...
        try {
            $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
            if ($resp.StatusCode -eq 200 -and $resp.Content -match [regex]::Escape($uiMarker)) {
                $ok = $true; break
            }
            # 200 with the wrong body is the #66 failure: served, but not the described UI
            $lastError = "HTTP $($resp.StatusCode), body did not contain '$uiMarker' (served non-UI content?)"
        } catch {
            $lastError = $_.Exception.Message
        }
    ...
    if (-not $ok) {
        Write-Output "served-markup: GET $url did not return the wizard UI within ${timeoutSeconds}s (last: $lastError)"
        exit 1
    }
```

Three adaptations, each **stated in the breakdown report**:

- **UI route.** Poll the route that serves the *page*, not a JSON API route. It MUST be
  produced by an ancestor task (artifact-ancestry) — the static-files mapping or the page
  handler.
- **UI marker.** `$uiMarker` is a stable, known string from the page the UI task built — a
  heading, an `id`/`data-` attribute, a step label. Use `[regex]::Escape` so an HTML string
  with regex metacharacters matches literally. The marker MUST come from the markup an
  ancestor produces; if the plan names no concrete element, surface it in the report as a
  human decision — do not invent one.
- **Single process.** Fold this into the existing §8 smoke-test guardrail (one start/stop) when
  the plan already has an executable smoke-test; only stand up a separate guardrail if none
  exists. Running two process managers against the same binary risks a port collision between
  them.

Scope note: this proves the *described UI is present and served* — not that it is *visually
good* (out of scope; a prompt-judge here is forbidden, per the catalogue's UI-presence
section). Presence and wiring is the deliverable.

## 10. Composition-root wiring — the factory CONSTRUCTS and INJECTS the collaborator (#120)

The catalogue's composition-root-wiring archetype for .NET. A plan that adds an `IFoo` + `FooImpl`
pair injected into a production assembler (a `SchedulerFactory`, a `Program.cs`, an
`IServiceCollection` registration, a `RunCommand`) decomposes into per-component tasks each guarded
by build + unit-tests against an **injected constructor seam** (`new Scheduler(plan, executor, …,
provider)`). Each goes green, the terminal `dotnet test` passes — and `SchedulerFactory.Create`
never constructed `FooImpl` and never passed it on, so the production path runs the legacy branch
and the new collaborator is dead code reachable only from xUnit. A build cannot see it; the unit
tests *inject the seam themselves*, so they pass either way. Two guardrail shapes prove the wiring,
both **deterministic** — strongest first. Put them on the inserted wiring task (SKILL.md Step 5).

### 10a. Drive the REAL factory, assert observable behaviour (strongest)

A `specific-tests-pass` (#4) guardrail running an xUnit test that calls the **production factory with
no manual injection** and asserts an output only the wired feature produces. The test MUST construct
the Scheduler via `SchedulerFactory.Create(...)` — **never** `new Scheduler(..., provider)`, which
would pass even with an unwired factory (cf. plan-08 `ProductionWiringTests.Factory_RunsWorktreeMode_OnCommittedFixturePlan`,
which drives the real factory at `maxParallelism = 2` and asserts a `guardrails/<plan>` branch
exists with ≥2 `Guardrails-Task:` trailers — an output only the wired worktree provider yields):

```powershell
# catches: a component (FooImpl) built + unit-tested behind an injected seam but never
#          constructed/injected by the production factory - the feature is dead from the CLI.
#          Drives the REAL SchedulerFactory (no manual injection) and asserts the wired-only output.
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~ProductionWiringTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "production-wiring tests fail - SchedulerFactory does not construct/inject the component (feature inert from the CLI)"
    exit 1
}
exit 0
```

The skeleton above asserts tests PASS (exit 0 is the pass), so it MUST adopt §4.2's
capture-then-re-emit form — wrap the `dotnet test … --filter "…ProductionWiringTests"` call so a
production-wiring failure's assertion/exception text lands in the harness retry tail, not just the
`[FAIL]` name (#179).

The test file is itself a deliverable — insert it via the TDD pair (author the production-wiring test
red, then the wiring task makes it green). The test-author task's `tests-fail-on-current-code`
guardrail proves the test actually fails against the *unwired* factory (it must, by construction).

### 10b. Reflection on the constructed object — the `Factory_Wires*` shape

When the observable behaviour is too environment-bound to drive in a guardrail, assert structurally
that the production factory injects the collaborator. The canonical shape drives the **real factory**
and reflects on the constructed object for the non-null collaborator field, **with a contrast case**
proving the wiring is conditional, not a constant (cf. plan-08
`Factory_WiresAiMergeWorker_InWorktreeMode`: worktree mode → non-null, serial mode → null;
`Factory_WiresNeedsHumanTriage_WhenRunnerAvailable`: runner present → non-null, script-only → null):

```csharp
// catches: SchedulerFactory.Create builds the Scheduler but never injects FooImpl - the
//          collaborator is unit-tested in isolation yet dead in the production path.
[Fact]
public void Factory_WiresFooImpl_WhenModeActive()
{
    // Drive the REAL factory - NEVER `new Scheduler(..., new FooImpl())` (would pass unwired).
    Scheduler active = SchedulerFactory.Create(activePlan, runner, probe, IRunObserver.Null);
    var field = typeof(Scheduler).GetField("_fooImpl", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert.NotNull(field!.GetValue(active));               // wired in the active mode

    // Contrast case - proves the wiring is conditional + real, not a hard-coded constant.
    Scheduler inactive = SchedulerFactory.Create(inactivePlan, runner, probe, IRunObserver.Null);
    Assert.Null(field!.GetValue(inactive));                // NOT wired when the mode is off
}
```

The guardrail is the same `dotnet test --filter` shape as 10a, pointed at this test. The reflection
private-field read is acceptable here precisely because it asserts a **production composition fact**
the public API does not expose; the contrast case is what makes it more than a tautology.

### 10c. Source grep (weakest — last resort)

When neither 10a nor 10b is feasible, a `file-contains` on the FACTORY file that it constructs the
impl, scoped to the one assembler file (grep-scope rule, §5):

```powershell
# catches: the factory file never constructs FooImpl (weakest wiring check - proves the text
#          exists, NOT that the constructed object is reached; prefer 10a/10b)
$factory = "src/Guardrails.Core/Execution/SchedulerFactory.cs"
if ((Get-Content $factory -Raw) -notmatch 'new\s+FooImpl\b') {
    Write-Output "$factory does not construct FooImpl - the component is not wired into the factory"
    exit 1
}
exit 0
```

This is strictly weaker — `new FooImpl(` can sit in a dead branch the production path never reaches,
and the grep cannot tell. Use it only when the factory cannot be driven from a test at all; prefer
10a, then 10b. Mark 10a/10b `scope: "integration"` only when they ALSO pass the catalogue's #125
union-safe decision test, checked plan-wide — not just against unions upstream of the wiring task
(a merge by an unrelated parallel sibling re-verifies it too, SSOT §4.3). In practice these
guardrails assert "the collaborator IS wired," which can't be true until the wiring task's own
attempt has run — so they usually fail that test and belong at `scope: "local"` (the default)
instead. See the catalogue's composition-root section for the full decision rule and the #250
incident this trap caused live in review.

### 10d. Dispatch / factory pairing — the right concrete type for the right mode (#158)

The .NET realization of the catalogue's "Dispatch / factory wiring" archetype. #10 above asks whether
`FooImpl` is constructed/injected **at all**; this asks whether — given a dispatch from an `enum`
`ImportMode` to one of ≥2 `ICommanderImporter` impls — **each mode resolved the right concrete type**.
The build passes with the branches **swapped** (every impl satisfies `ICommanderImporter`, so either
compiles in either arm), and a seam-injected dispatch test (`RecordingImporter` registered via DI,
asserting only that `ICommanderImporter.ImportAsync` was called) passes on the inverted wiring — it
never checks the concrete type. Emit **one proximity guardrail per pairing**, scoped to the one dispatch
file, with a **multiline-dotall** window in **both orders** (`[\s\S]{0,300}`, NOT single-line `.{0,300}`
which stops at the first newline):

```powershell
# catches: ImportMode.TcApiLocal wired to the WRONG importer (e.g. swapped with CommanderRestImporter).
#          Build + the seam-injected DispatchTests (RecordingImporter via DI) + a bare ImportMode|TcApiLocal
#          keyword check ALL pass on the inverted wiring; only this per-pairing proximity check fails.
$file = "src/Commander/ImporterDispatch.cs"
$content = Get-Content $file -Raw
if ($content -notmatch "TcApiLocal[\s\S]{0,300}TcApiLocalImporter|TcApiLocalImporter[\s\S]{0,300}TcApiLocal") {
    Write-Output "$file does not pair ImportMode.TcApiLocal with TcApiLocalImporter within one block - verify the correct importer is wired to the correct ImportMode branch"
    exit 1
}
exit 0
```

Repeat for each `<ImportMode value, ConcreteImporter>` couple. **Decision gate (omit when redundant):**
if the dispatch test asserts the concrete type — `Assert.IsType<TcApiLocalImporter>(dispatch.Resolve(ImportMode.TcApiLocal))`
— the test already catches the swap; drop the proximity guardrail and record why in the covering
guardrail's `# catches:` comment. The C# type-asserting test is the **stronger** form when you can
resolve the real concrete object without standing up the whole feature; the source-proximity grep is the
fallback when the dispatch can only be inspected statically (the resolution is buried behind DI you can't
easily drive in a guardrail). Prefer the type assertion, then the proximity grep.

## 11. Strip comments before a forbidden-keyword scan — SQL and C# syntax (#97, #98)

The catalogue's comment-blind keyword-scan rule (catalogue → "Comment-blind keyword scan"): a
guardrail that scans a **source artifact** for **banned constructs** — a T-SQL survey asserted
read-only (`MERGE`/`EXEC`/`INSERT`/`UPDATE`/`DELETE`/`xp_cmdshell`/`OPENROWSET`), a C# file asserted
free of `Console.WriteLine`/`eval`-shaped calls — must **strip comments first**, or it
false-POSITIVES on a comment that merely *names* the banned thing. The motivating trap (plan 0007
task 01): the action prompt asked for a **safety-header comment** listing the banned keywords, and
the comment-blind guardrail flagged them in the header — whack-a-mole to `needs-human` on a correct
read-only script. Here are the two .NET-relevant comment syntaxes.

**SQL** — strip `/* */` block comments then `-- …` line comments before the keyword scan:

```powershell
# catches: a read-only T-SQL survey check that false-POSITIVES on its OWN safety-header comment
#          ("performs no MERGE/EXEC, no xp_cmdshell") - escalating a correct read-only script to
#          needs-human. Strip SQL comments, THEN scan the code for banned write/external surface.
$sql = "scripts/survey.sql"
$raw = Get-Content $sql -Raw
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
$code = [regex]::Replace($code, '--[^\r\n]*', ' ')       # -- line comments
$banned = 'xp_cmdshell|OPENROWSET|\bMERGE\b|\bINSERT\b|\bUPDATE\b|\bDELETE\b'
$m = [regex]::Match($code, "(?i)$banned")
if ($m.Success) {
    Write-Output "$sql uses banned construct '$($m.Value)' in CODE (not just a comment) - not read-only"
    exit 1
}
exit 0
```

`EXEC` needs care — a read-only survey legitimately calls `EXEC sp_executesql` (the parameterized-
query idiom). Match `EXEC` only when it is **not** `sp_executesql`, against the comment-stripped code:

```powershell
# allow EXEC sp_executesql (read-only parameterized query); ban any other EXEC, in CODE only
if ($code -match '(?i)\bEXEC(UTE)?\b(?!\s+sp_executesql\b)') {
    Write-Output "$sql calls EXEC other than sp_executesql in CODE - external/unsafe surface"
    exit 1
}
```

**C#** — strip `/* */` then `// …` line comments (note `//`, not SQL's `--`) before a banned-call
scan:

```powershell
# catches: a "no Console.WriteLine" (or other banned-call) check that false-positives on a
#          // comment naming the banned call. Strip C# comments, then scan the code.
$cs = "src/Tool/Runner.cs"
$raw = Get-Content $cs -Raw
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
$code = [regex]::Replace($code, '//[^\r\n]*', ' ')       # // line comments
if ($code -match 'Console\s*\.\s*WriteLine') {
    Write-Output "$cs calls Console.WriteLine in CODE (not just a comment) - use the injected logger"
    exit 1
}
exit 0
```

**Line-number-reporting variant** — when the failure line must name the offending source line,
blank the comment spans **in place** (preserve newlines) so reported line numbers stay accurate,
rather than collapsing the file:

```powershell
# blank block-comment spans but KEEP newlines, so a per-line scan reports correct line numbers
$raw = [regex]::Replace($raw, '/\*[\s\S]*?\*/', { $args[0].Value -replace '[^\r\n]', ' ' })
$lines = $raw -split '\r?\n'
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i] -replace '--[^\r\n]*', ' '        # SQL line comment ('//' for C#)
    if ($line -match '(?i)\bxp_cmdshell\b') {
        Write-Output "$sql line $($i + 1): xp_cmdshell in code - external/unsafe surface"
        exit 1
    }
}
exit 0
```

Caveat (state it if it matters for the artifact): regex comment-stripping does not understand a
banned keyword sitting **inside a string literal** (`'-- not a comment'`, `"/* still a string */"`).
For most read-only-survey and banned-call checks this is acceptable (a survey rarely embeds the
banned keyword in a string); when string-literal false positives are a real risk, note it in the
breakdown report — full fidelity needs a parser, which is out of scope for a guardrail. And per the
catalogue's action-prompt discipline: do **not** pair a header-documenting prompt with a
comment-blind grep — strip comments in the guardrail, and keep the banned-keyword list in the
guardrail's `# catches:` line rather than the action prompt unless the guardrail is comment-safe.
<!-- BEGIN ADDED SECTION #116 — Windows-safe TempGitRepo fixture (auto-merge friendly; do not merge into prose above) -->
## 12. Windows-safe `TempGitRepo` test fixture — author-tests that build a real git repo (#116)

The .NET realization of the catalogue's "Windows-safe git test fixture (#116)" doctrine. When an
author-tests task's tests create a **real git repository**, emit this ONE shared fixture (or inject
its directive) so each git-touching test reuses it instead of re-discovering Git-for-Windows
semantics that a POSIX-only helper misses (SKILL.md Step 5a). Each behavior below is a logged halt;
the fixture is non-negotiable on all four. Place it under the test project (e.g.
`tests/<Project>.Tests/TestInfrastructure/TempGitRepo.cs`) and have later git-touching author-tests
tasks `dependsOn` the task that authors it.

```csharp
// A Windows-safe disposable git repo fixture for author-tests. Encodes four logged Git-for-Windows
// lessons a POSIX-only helper misses: read-only loose objects on delete (#109), empty-dir prune on
// git rm/git mv (task-14), reset --hard rollback rather than merge --abort (W3), and deterministic
// hashes via core.autocrlf=false. Reuse this from every test that builds a real repo - do NOT
// hand-roll a temp-repo helper per test.
using System.Diagnostics;

public sealed class TempGitRepo : IDisposable
{
    public string Root { get; }
    public string Head => Git("rev-parse", "HEAD").Trim();

    public TempGitRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "gr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Git("init", "-q");
        // Deterministic hashes across platforms: never translate line endings in fixtures.
        Git("config", "core.autocrlf", "false");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Guardrails Test");
    }

    /// <summary>Write a file, recreating its parent if git pruned it (empty-dir prune, task-14).</summary>
    public void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath);
        // git rm / git mv prunes the now-empty parent on Git-for-Windows; recreate before writing.
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Roll back to a prior commit. NEVER `git merge --abort` — it fails rc=128 on a
    /// dirtied tracked path (W3). `reset --hard` is the reliable rollback.</summary>
    public void ResetHard(string commitish) => Git("reset", "--hard", commitish);

    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (rc={p.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        // Git marks .git/objects loose objects READ-ONLY on Windows: Directory.Delete then throws
        // UnauthorizedAccessException (NOT IOException, #109). Strip read-only on every file first.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(Root, recursive: true);
    }
}
```

When the fixture is its own deliverable, guard the authoring task with a `file-exists` (#1) on
`TempGitRepo.cs` and a `tests-build` (#3) so a non-compiling fixture fails loudly rather than
silently breaking every downstream git test. The four behaviors map one-to-one onto the catalogue's
#116 lessons; do not drop any one — each is a real `needs-human` halt a generated git test hit.
<!-- END ADDED SECTION #116 -->

<!-- BEGIN ADDED SECTION #84 — Production testability seam -->
## 13. Production testability seam — the seam-exists structural check (#84)

The catalogue's production-testability-seam archetype for .NET. A test-author behavior needs an
injection point in production code — a DI **constructor overload**, a **factory delegate**, or an
**injectable interface** — so a test can supply a fake/double (`RecordingDestinationWriter`,
`InMemoryConnectionFactory`, a fixture source). The seam task is a **pure structural production
change** (no behavior, no endpoint); guard it with `build-passes` (§4) plus a **structural seam-exists
check** that matches the **declaration**, not a bare token (§3's universal rule). Scope every grep to
the one production file the seam task owns.

The seam case is whichever of these the plan needs:

### 13a. Injection-constructor overload — a `new` overload that takes the injected dependency

The motivating shape (#84): `Launcher` is constructed only via a production constructor with no way to
inject an `IDestinationWriter`, so behavior 3 (`MovedCountGreaterThanZero`) can never be expressed as a
passing test. The seam adds `Launcher(TextWriter, IWorksoftConnectionFactory, IDestinationWriter)`. A
**bare `IDestinationWriter` grep** passes on a field, a `using`, or a comment — match the constructor
**parameter list** instead:

```powershell
# catches: a seam task that claims to add an injection constructor but the overload taking the
#          injected dependency is absent (so tests still cannot inject a fake - behavior unsatisfiable)
$src = "src/Worksoft.Migrator/Launcher.cs"
$code = Get-Content $src -Raw
# the constructor named `Launcher(` whose parameter list includes the injected interface
if ($code -notmatch '(?s)\bLauncher\s*\([^)]*\bIDestinationWriter\b[^)]*\)') {
    Write-Output "$src has no Launcher(...) constructor overload taking IDestinationWriter - the injection seam is missing"
    exit 1
}
exit 0
```

The `(?s)` lets the parameter list span lines; `[^)]*` keeps the match inside the one parameter list
(it cannot run past the closing `)` into an unrelated call). Use the concrete seam type the plan names.

### 13b. Factory delegate — a `Func<…>`/factory the production type accepts for the dependency

When the seam is a factory the type accepts (rather than the dependency directly), assert the
field/parameter is the **factory type**, declared on the owning type:

```powershell
# catches: the production type does not accept a factory delegate for the dependency, so a test
#          cannot substitute a fake-producing factory (the seam is missing)
$src = "src/Worksoft.Migrator/Launcher.cs"
if ((Get-Content $src -Raw) -notmatch 'Func\s*<\s*[^>]*IDestinationWriter\s*>') {
    Write-Output "$src does not accept a Func<...IDestinationWriter> factory - the factory seam is missing"
    exit 1
}
exit 0
```

### 13c. Injectable interface + DI registration — the abstraction exists AND is registered

When the seam is "introduce `IDestinationWriter` and register it so it can be replaced in tests",
assert BOTH the interface declaration (§3's structural form) AND that the production composition
registers it — otherwise the abstraction is dead and tests still cannot inject through the container:

```powershell
# catches: an injectable-interface seam where the interface exists but is never registered in the
#          container, so a test cannot override the registration with a fake
$iface = "src/Worksoft.Migrator/Abstractions/IDestinationWriter.cs"
if ((Get-Content $iface -Raw) -notmatch 'interface\s+IDestinationWriter\b') {
    Write-Output "$iface does not declare interface IDestinationWriter - the seam interface is missing"
    exit 1
}
$startup = "src/Worksoft.Migrator/Program.cs"
if ((Get-Content $startup -Raw) -notmatch '(AddScoped|AddSingleton|AddTransient)\s*<\s*IDestinationWriter\b') {
    Write-Output "$startup never registers IDestinationWriter in the container - the seam is not injectable at runtime"
    exit 1
}
exit 0
```

The seam task is **TDD-exempt** (a too-simple structural change — state the exemption reason in its
description) and is an **upstream dependency of the test-author task**. With the seam present, the
test-author task authors **all** behaviors against the real injection point; each fails at runtime as a
clean red with no `needsHuman`. Do NOT mark the seam-exists check `scope: "integration"` — it is a
task-local structural check on one production file.
<!-- END ADDED SECTION #84 -->

<!-- BEGIN ADDED SECTION #100 — Scripted ETL -->
## 14. Scripted ETL — model bulk/unbounded fan-out as one script, not an agent loop (#100)

The catalogue's bulk/unbounded-fan-out archetype for .NET. When a task's deliverable is "process N
items where N is unknown and potentially large" (a portal crawl, a recursive-glob transform, a mass API
fetch), model it as a **`script` action** the agent authors and runs ONCE — the N-item volume executes
inside the script run, off the agent's turn budget — NOT a `.prompt.md` that iterates items (that hits
max-turns and dies; raising `maxTurns` only moves the wall). Three task shapes, in DAG order:

### 14a. Discover-size-first probe (where N is unknown)

A cheap upstream task that enumerates/counts the in-scope set BEFORE the crawl commits to an approach
(the "8 expected → 409 actual" calibration). Write the count to state under the task's **FOLDER NAME**
as the single top-level key (the directory the `task.json` lives in, NOT the `stableId`, #164) and
guard the fragment key:

```powershell
# catches: the size probe ran but never published the in-scope count a downstream task sizes against
$fragmentPath = $env:GUARDRAILS_STATE_FRAGMENT
if (-not $fragmentPath -or -not (Test-Path $fragmentPath)) {
    Write-Output "no state fragment written - 'inscope_count' is missing"
    exit 1
}
$fragment = Get-Content $fragmentPath -Raw | ConvertFrom-Json
$count = $fragment.'01-discover-portal-size'.inscope_count
if ($null -eq $count -or [int]$count -lt 1) {
    Write-Output "state key 'inscope_count' is missing or not a positive integer"
    exit 1
}
exit 0
```

### 14b. Scripted bulk-capture — the deterministic crawl/transform

The agent authors a script (e.g. a Playwright + HTML→markdown crawler, or a glob walk + transform) and
runs it. Guard the OUTPUT it produced — never replay the crawl, and never grep the action's stdout for
a self-reported "done". Assert the output directory exists and holds at least the discovered count of
items:

```powershell
# catches: the bulk-capture script claimed success but produced an empty/short output set -
#          fewer captured items than the discover-size probe found in scope (a partial crawl)
$outDir = "artifacts/portal-crawl"
if (-not (Test-Path $outDir)) {
    Write-Output "$outDir does not exist - the scripted crawl produced no output"
    exit 1
}
$captured = (Get-ChildItem $outDir -Filter *.md -Recurse).Count
$stateIn  = Get-Content $env:GUARDRAILS_STATE_IN -Raw | ConvertFrom-Json
$expected = [int]$stateIn.'01-discover-portal-size'.inscope_count
if ($captured -lt $expected) {
    Write-Output "scripted crawl captured $captured pages but $expected were in scope - the capture is incomplete"
    exit 1
}
exit 0
```

This is a `file-exists` (§archetype 1) + count check on **produced output** (verify-don't-replay, #9) —
the script ran the expensive work; the guardrail reads its result. The completeness/substance of the
captured corpus is a separate concern (#99); this proves the capture is **present and complete by
count**.

### 14c. Bounded per-item derivation/curation (a SEPARATE, bounded agent task)

Any agent *derivation* over the captured set is its own downstream task, scoped to a **bounded subset**
("derive a high-value committed subset"), never "derive all N." It is a normal prompt task (guard it by
the artifacts it commits) — the point is that the *unbounded* volume already happened deterministically
in 14b, so the agent task here has a bounded, session-sized job. Keep the discover/capture/curate
boundary in the DAG: 14a → 14b → 14c.
<!-- END ADDED SECTION #100 -->

<!-- BEGIN ADDED SECTION #76 — method-call anchoring (auto-merge friendly; do not merge into prose above) -->
## 15. Method-call anchoring — verify a call to `B.Method()`, not a bare name (#76)

The .NET realization of the catalogue's method-call-anchoring rule (catalogue → "Method-call
anchoring"). A "the CLI calls `MigrationRunner.RunAsync(...)`" wiring guardrail written as a **bare
method-name** grep — `(Get-Content $prog -Raw) -notmatch 'RunAsync\s*\('` — false-passes on a comment
(`// RunAsync(scope)`), a **local** `private void RunAsync(...)` wrapper, or any unrelated same-named
method. None invoke the real runner. Require **two sequential checks**: the **type** is referenced (no
local stub can fake the type name) AND the call carries a **dot prefix** (`\.RunAsync\s*\(` — a method
*definition* reads `void RunAsync(` with no leading dot; a *call* reads `runner.RunAsync(`):

```powershell
# catches: a "Program.cs calls MigrationRunner.RunAsync(...)" wiring claim satisfied by a comment
#          (// RunAsync(scope)) or a LOCAL method also named RunAsync - neither invokes the real
#          library method. Require BOTH the type reference and the dotted call construct.
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

Use the concrete type and method the plan names. The two-check form is strictly stronger than either
alone: the type reference rules out a local same-named stub, the dotted call rules out comments and
standalone definitions. This is the same shape §7's entry-point grep already uses
(`new\s+Launcher\b|Launcher\s*\.\s*\w`, never a bare `Launcher`) — §7 is the executable-entry-point
instance; §15 is the general "A must call `B.Method()`" instance. Scope to the one caller file
(grep-scope rule, §5). Caveat (state it if it matters): a regex cannot tell a real call from the method
name sitting **inside a string literal** — a parser is out of scope; note it in the report if the method
name plausibly appears in a string.
<!-- END ADDED SECTION #76 -->

<!-- BEGIN ADDED SECTION #74 — no-direct-bypass (auto-merge friendly; do not merge into prose above) -->
## 16. No-direct-bypass — the extracted library must not call the concrete dependency directly (#74)

The .NET realization of the catalogue's no-direct-bypass archetype (catalogue → "No-direct-bypass"). A
library extracted to write **through** an injected `IDestinationWriter` is registered (§1), references
its abstraction (§2), builds (§4), and passes its tests — yet its internals can still call
`ToscaCloudClient.UploadEntitiesAsync` **directly**, bypassing the writer. No other guardrail sees this.
Scan the **library project's `.cs` only** (scope to the lib folder, exclude `bin`/`obj` per §5) for a
**dotted** call to the concrete method, **after stripping comments** (§11 — a comment naming the method
would otherwise false-RED a correct library, the #97/#98 trap inverted):

```powershell
# catches: the extracted engine library bypassing IDestinationWriter by calling
#          ToscaCloudClient.UploadEntitiesAsync directly - registered, built, and tested all green
#          while the injected abstraction is bypassed. Strip comments first (a comment naming the
#          method is NOT a real call), anchor on the DOTTED call (#76), scope to the library folder.
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

Use the concrete `ConcreteType.Method` the plan forbids. This is the **inverse** of §1/§2: those prove
the library is wired *in*; this proves the library does not bypass its abstraction from the *inside*.
It is a **forbidden-call** check, so it inherits §11's comment-strip discipline (strip first) and §15's
dot-anchoring (a same-named method on a *different*, allowed type, or the name in a string literal, must
not false-RED). For extra strictness require the concrete type near the call
(`ToscaCloudClient[\s\S]{0,200}?\.UploadEntitiesAsync\s*\(`) when the method name alone is too common.
Caveat: the string-literal residual (the method name inside a string) is the same lower-bound limit as
§15 — note it if it matters.
<!-- END ADDED SECTION #74 -->

<!-- BEGIN ADDED SECTION #75 — covers-key-behaviors (auto-merge friendly; do not merge into prose above) -->
## 17. Covers-key-behaviors — a test file encodes the enumerated behaviors (#75)

The .NET realization of the catalogue's covers-key-behaviors rule (catalogue → "Covers-key-behaviors").
When a test-author task's action prompt enumerates **≥3** named behaviors to encode, the `tests-exist` +
`tests-fail-on-current-code` pair is satisfiable by **one** trivially-failing stub — neither checks the
named behaviors are present. Add a `03-covers-key-behaviors.ps1` that greps the **one test file** the
task authors for **2–3 distinctive terms** (one `if` per term, so the failure names the missing
behavior). Pick a **domain type name, an enum value, or a method name** — never a generic word
(`test`/`assert`/`Fact`/`should`) any stub already contains:

```powershell
# catches: a test file that lacks coverage of ProcessID keying or rollup counts - both named in the
#          action prompt's "encode these behaviors" list - while tests-exist + tests-fail-on-current-code
#          both pass on ONE trivially-failing stub test.
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

Choose the 2–3 **headline** behaviors most likely to be accidentally omitted (the plan's risk-section
ones), not the whole list. Scope to the one test file (grep-scope rule, §5). This is a **lower bound**
(the #99 substance-floor class): a term in a comment or an unused variable still matches, so it proves a
test *names* the behavior, not that it *asserts* it — the residual is the `tests-fail-on-current-code`
red plus human review. The breakdown report must **list which enumerated behaviors went unchecked** so
the reviewer can decide whether to add more terms.

### 17.1 Structural [Fact]/[Theory] check — strengthen the data-model TDD-split coverage (#155)

When a **data-model** test-author task keeps the TDD split (catalogue → "Stub-based TDD" → data-model
exception), its `tests-fail-on-current-code` red is a *compile* failure — gameable by a file that
merely names the enum-value tokens in a comment. A bare-keyword `covers-key-behaviors` then also
false-passes on that comment. Strengthen it: require an actual xUnit **test attribute** in the file,
not just the domain tokens. A comment naming `TcApiLocal` does not carry a `[Fact]`/`[Theory]`:

```powershell
# catches: a data-model test file that NAMES the behaviors in a comment (satisfying a bare-keyword
#          covers-check and a compile-coupled red) but encodes no actual test - assert a real
#          [Fact]/[Theory] attribute is present, the structural construct a comment cannot fake (#155).
$f = "tests/Importer.Tests/ImportModeTests.cs"
$content = Get-Content $f -Raw
if ($content -notmatch '(?m)^\s*\[(Fact|Theory)\]') {
    Write-Output "$f declares no [Fact]/[Theory] test - the enum-value tokens appear only as text (a comment cannot satisfy this)"
    exit 1
}
if ($content -notmatch 'TcApiLocal') {
    Write-Output "$f does not reference ImportMode.TcApiLocal - add a test asserting that enum value"
    exit 1
}
exit 0
```

Pair the structural `[Fact]`/`[Theory]` presence check with the 2–3 distinctive domain tokens. For a
**behavioral** type prefer the §4.1 `build-passes` + `tests-fail-on-stubs` pair instead — the build
guardrail already proves the test file is real code, so the attribute check adds less there.
<!-- END ADDED SECTION #75 -->

<!-- BEGIN ADDED SECTION #96 — name-convention seam (auto-merge friendly; do not merge into prose above) -->
## 18. Name-convention seam — drive the consumer's derived-name lookup over every artifact (#96)

The .NET realization of the catalogue's name-convention-seam archetype (catalogue → "Name-convention
seam") for a web executable whose **shell resolves fragments by a derived name**. The producer wrote
kebab-case fragments (`wwwroot/steps/source-connection.html`, with `DestinationSelection` served by the
outlier `destination.html`); the shell requested them by the **PascalCase step id** — `GET
/wizard/pages/SourceConnection.html` → embedded resource `…wwwroot.steps.SourceConnection.html` → **404
→ silent fallback**. Per-side file-exists + content checks all passed. The guardrail must be
**consumer-driven** (derive the names from the shell's **own** map, not a hard-coded copy), **cover
every item**, and **assert 200 + a per-item marker** (not a fallback body). It **reuses §8's smoke-test
lifecycle** (start the binary, bounded poll, `finally` teardown, deterministic port) and changes the
success test to a per-item loop. Put it on a **both-sides-present** terminal/integration task, mark it
`scope: "integration"`, and keep it **union-safe** (#125 — assert "every artifact that resolves"):

```powershell
# catches: a producer<->consumer NAME-CONVENTION drift the per-side checks miss - the shell derives a
#          fragment name (PascalCase step id) the producer never emitted (kebab-case, with a special-case
#          outlier), so a step 404s -> silent fallback at runtime on a 100%-green suite. Parse the SHELL'S
#          OWN step map (not a hard-coded copy), GET every fragment through the live server, assert 200 +
#          a per-item marker. Reuses the §8 start/poll/finally lifecycle.
$ErrorActionPreference = 'Stop'
$project = "src/Wizard.Cli"
$port    = 5099
$baseUrl = "http://127.0.0.1:$port"
$timeoutSeconds = 20
$proc = $null
try {
    $outLog = Join-Path $env:GUARDRAILS_LOG_DIR "seam-stdout.log"
    $errLog = Join-Path $env:GUARDRAILS_LOG_DIR "seam-stderr.log"
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", $project, "--no-build", "--", "--urls", $baseUrl) `
        -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog -WindowStyle Hidden

    # CONSUMER-DRIVEN: read the step ids from the SHELL's own source, not a hard-coded list. Each id is
    # the exact name the shell derives its fragment URL from - so this set IS the consumer's contract.
    $shell = Get-Content "src/Wizard.Cli/WizardShell.cs" -Raw
    $stepIds = [regex]::Matches($shell, 'StepId\.(\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if ($stepIds.Count -lt 1) {
        Write-Output "could not parse any StepId.* from WizardShell.cs - cannot drive the seam consumer-side"
        exit 1
    }

    # wait for the server to answer at all (bounded poll, §8)
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $up = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { Write-Output "server exited early (code $($proc.ExitCode)) - see $errLog"; exit 1 }
        try { if ((Invoke-WebRequest "$baseUrl/health" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { $up = $true; break } } catch {}
        Start-Sleep -Milliseconds 250
    }
    if (-not $up) { Write-Output "server did not come up within ${timeoutSeconds}s - see $errLog"; exit 1 }

    # COVER EVERY ITEM: GET each fragment through the shell's own URL convention; assert 200 + a per-item
    # marker (the step id echoed in the fragment), NOT a 200 fallback body.
    foreach ($id in $stepIds) {
        $url = "$baseUrl/wizard/pages/$id.html"   # the shell's derived-name convention
        try {
            $resp = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5
        } catch {
            Write-Output "seam: GET $url failed ($($_.Exception.Message)) - the producer never emitted the fragment the shell derives for step '$id' (name-convention drift)"
            exit 1
        }
        if ($resp.StatusCode -ne 200 -or $resp.Content -notmatch [regex]::Escape($id)) {
            Write-Output "seam: GET $url returned HTTP $($resp.StatusCode) without marker '$id' - resolved to a fallback/404 body, not the real fragment (name-convention drift)"
            exit 1
        }
    }
    exit 0
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
```

Three adaptations, each **stated in the breakdown report**:
- **Parse the CONSUMER's real map.** The `StepId.*` regex is illustrative — point it at whatever the
  shell actually derives names from (an `enum`, a `STEPS`/`FRAGMENTS` dictionary, an embedded-resource
  manifest, a route table). The names MUST come from the consumer's own source/runtime, never a
  hard-coded copy in the guardrail (a copy hides a consumer-side drift).
- **The URL/lookup convention.** `"/wizard/pages/$id.html"` is the shell's derived-name form — use the
  plan's actual route/resource convention.
- **The per-item marker.** Assert a string only the *correctly resolved* fragment contains (the step id,
  a known element). A bare 200 is the trap — a silent-fallback page also returns 200.

**Union-safety (#125).** As written, a step whose fragment is missing fails — correct on a terminal
gate, but at an intermediate **union** where the producer set is only partially present this would
false-RED. If the plan reaches such unions, gate the loop on artifacts *present* on the consumer side
("for every step the producer has emitted, the derived-name lookup resolves") so a partial merge passes
while still catching a *wrong-name* drift. This pairs with §8 (the exe serves something) and §9 (the
root is the described UI): §18 proves **every derived-name lookup across the set resolves to the right
artifact**.
<!-- END ADDED SECTION #96 -->

<!-- BEGIN ADDED SECTION #175 — duplicate-definition union sub-check (auto-merge friendly; do not merge into prose above) -->
## 19. Duplicate-definition union sub-check — a shared `.cs` two tasks both define into (#175)

The .NET realization of the catalogue's **duplicate-definition sub-check** (catalogue →
overlapping-writeScope union-guardrail). When **≥2 tasks have overlapping `writeScope` on a shared
`.cs` file** and **both** can append a type/member DEFINITION to it, a 3-way / AI-merge that keeps both
copies of the same new definition produces **no conflict marker** — git unions two appends to different
regions cleanly — so the merged file holds a **duplicate class/member**: `CS0101` ("already contains a
definition for …") / `CS0111`, surfaced only by the build at the terminal gate. This is the exact #175
failure of plan-0009: task 07 and task 09 both wrote `class CommanderRestImporter` into `Launcher.cs`.

Add a **duplicate-definition count check** to the shared file's `scope:"integration"` union-guardrail.
It belongs **inside the file-present gate** of the conditional union-guardrail (§4's
`launcher-union-verified` shape) so it stays **union-safe** — it does nothing at a union where the file
has not landed yet:

```powershell
# Inside the union-guardrail, after the conflict-marker check, still under `if (Test-Path $path)`:
# catches: an AI/3-way merge that kept BOTH task 07's and task 09's `class CommanderRestImporter`
#          appended to Launcher.cs (overlapping writeScope, no textual conflict) - a CS0101 duplicate
#          the build catches only at the terminal gate (#175). Count the declaration; >1 is the merge dup.
$classMatches = ([regex]::Matches($content, 'class\s+CommanderRestImporter')).Count
if ($classMatches -gt 1) {
    Write-Output "Launcher.cs contains $classMatches definitions of CommanderRestImporter - the AI-merge produced a duplicate class (overlapping writeScopes); remove all but one"
    exit 1
}
```

Match the declaration keyword for the construct both siblings could add — `class\s+<Name>`,
`record\s+<Name>`, `interface\s+<Name>`, `enum\s+<Name>` — count `[regex]::Matches(...).Count`, fail on
`-gt 1`, and **name the duplicate** in the failure line (it becomes the human's diagnosis). One check per
definition both writeScopes could produce. This is the union-side safety net; the deeper fix for the
plan-0009 trap is the **missing DAG edge** that forced the agent to redefine the class in its own scope
at all — see §15/the transitive-compilation rule (plan-breakdown Step 3, #176). The harness contributes
**attribution** at the gate (it names the colliding `writeScope` task pairs + shared path on the
`needs-human` diagnosis, SSOT §3.3), not detection — this guardrail is the detection.
<!-- END ADDED SECTION #175 -->

<!-- BEGIN ADDED SECTION #176 — negative assertion (auto-merge friendly; do not merge into prose above) -->
## 20. Negative assertion — an EXCLUDED scenario verified ABSENT from a `.cs` file (#176)

The .NET realization of the catalogue's **negative-assertion** archetype (catalogue → negative
assertion). The **mirror** of §17 `covers-key-behaviors`: §17 fails when a kept token is **absent**
(`-notmatch … exit`); a negative assertion fails when an excluded token is **present** (`-match …
exit`). Emit one whenever a task's action prompt **explicitly excludes** a scenario/keyword the authored
file must NOT contain — a wizard-blocked mode, a forbidden construct, a removed scenario. Without it the
positive coverage check (which only verifies the KEPT scenarios) lets the agent re-add the excluded one
undetected; in plan-0009 a re-added `CommanderRest` reference in the dispatch tests compile-coupled a
downstream wiring task to a non-ancestor's type (the #176 trap):

```powershell
# catches: a dispatch test file that references CommanderRest - Mode C is wizard-blocked and the action
#          prompt EXCLUDED it, but the positive covers-key-behaviors (§17) only checks the KEPT scenarios
#          are present, so a re-added CommanderRest slips through. Fail-on-PRESENT is the negative assertion.
$f = "tests/Importer.Tests/MigrateDispatchTests.cs"
$content = Get-Content $f -Raw
if ($content -match "CommanderRest") {
    Write-Output "$f references CommanderRest - Mode C is wizard-blocked and must not appear in the dispatch tests"
    exit 1
}
exit 0
```

**Pair it with the §17 positive check**, do not replace it — keep one `03-covers-key-behaviors.ps1`
(or a sibling guardrail) that asserts the kept scenarios present AND a negative-assertion line that
asserts the excluded one absent, both scoped to the one file. **GR2026 stays silent on the negative
assertion's keyword by design** (#177): `CoverageGuardrailHeuristic` classifies match-line polarity and
treats only `-notmatch … exit` / `-match … $hits++` (require-present) blocks as coverage tokens; a
`-match … exit` (require-absent) block is excluded (SSOT §4.4). The excluded keyword is intentionally
absent from the prompt — that is the point — so a GR2026 warning there would be the #177 false positive.
Do not weaken or delete the negative assertion to silence GR2026; post-#177 there is nothing to silence.
<!-- END ADDED SECTION #176 -->

## 21. Baseline-green (preflight) root — the EXISTING area tests pass on the current code (#181)

The .NET realization of the catalogue's **baseline-green / start-from-green (preflight)** archetype
(catalogue → "Baseline-green / start-from-green (preflight)") — the existing-area-tests-green instance,
the only positive baseline emitted today (the same no-op-root shape extends to build-green / endpoint-up,
none emitted yet). For a **brownfield** plan (it modifies project(s) that already have tests in the
touched area, and the worth-it gate passes), SKILL.md Step 5 inserts a ROOT task
`00-baseline-<area>-tests-green` **per touched test project** (deduped one-per-area): a **TRUE no-op
`exit 0` action** (writes nothing) + **one guardrail** that runs the EXISTING area tests **via
`--filter`** and asserts they PASS on the current code — "never build on red." For a **greenfield** plan
there are no existing area tests; SKIP it (do not author a `dotnet test` over a project with no tests —
it trivially passes and certifies nothing).

**Scope via `--filter`, NEVER a whole-project `dotnet test` at the root.** A whole-project test at the
DAG root hits the **#165/#176 compile-coupling trap**: a mid-TDD project does not compile (its test
project references types later implementation tasks have not produced yet), so the root false-reds with a
compile error no work task can fix, dead-ending the run. The `--filter` selects the existing,
currently-passing tests of the touched area ONLY (excluding any about-to-be-authored category).

`action.ps1` — the verification is the guardrail, not the action:

```powershell
# A no-op: this task does no work. Its guardrail (the EXISTING area tests pass) is the point - it
# gates the DAG root on the touched area being green before any work task runs.
exit 0
```

`guardrails/01-baseline-area-tests-pass.ps1` — run the EXISTING test project(s) covering the area the
plan modifies and assert they ALL pass. **Scope to the AREA** (the test project, or a `--filter` /
category over it), NOT the whole suite. It asserts tests PASS (exit 0 is the pass), so it adopts §4.2's
**capture → emit full log → re-emit failure-signal lines at the END** form so a RED baseline's WHY
reaches the harness retry-feedback tail (#179):

```powershell
# catches: a brownfield plan building on a RED base - the EXISTING tests in the area future tasks will
#          modify are already failing on the starting code. Asserting them green at the DAG root means a
#          later work task's tests-pass failure is attributable to THAT task, not pre-existing breakage,
#          and a new test's red is unambiguous (#181). Re-emits the failure DETAIL at the END so a red
#          baseline's WHY reaches the harness retry tail, not just `[FAIL] <name>` (#179, §4.2).
# Scope to the AREA (the existing test project / a --filter), NOT the whole suite.
$out = dotnet test tests/Inventory.Tests --filter "Category!=Stats" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the existing tests in tests/Inventory.Tests are already failing on the starting code - fix the pre-existing breakage before this plan builds on it (#181)"
    exit 1
}
exit 0
```

`task.json` — the DAG ROOT (`dependsOn: []`); it does no work, so it declares **no `writeScope`**:

```jsonc
{
  "description": "Baseline: the existing tests in the touched area (tests/Inventory.Tests) pass on the starting code - never build on red (#181)",
  "dependsOn": []
}
```

`guardrails/01-baseline-area-tests-pass.json`:

```jsonc
{
  "description": "Existing area tests pass on the current code (baseline-green root, #181)"
}
```

Notes on the scope and the edges:

- **Existing tests ONLY, before the TDD-red tasks.** The baseline runs at the root on the STARTING
  state, BEFORE any inserted `author-tests` task adds its intentionally-failing new tests. If
  `$baselineArea` is a project a later `author-tests` task ALSO adds failing tests into (e.g. it adds a
  `Category=Stats` test class to `tests/Inventory.Tests`), use a `--filter` that **excludes** the
  about-to-be-authored category (`--filter "Category!=Stats"` above) so the baseline can never go red on
  tests that don't exist yet. In worktree mode the root's tree IS the starting state (no new tests), so
  this is natural — the filter just makes the intent explicit and robust if the baseline is ever re-run
  on a later tree.
- **Make every work task transitively depend on it.** Add `00-baseline-<area>-tests-green` to the
  `dependsOn` of the existing roots (the test-author tasks, any seam tasks, the first implementation
  tasks); everything downstream reaches it transitively. Nothing runs against a red base.
- **It is NOT the terminal gate.** The whole-suite §4 terminal `02-all-tests-pass` (a green END on
  EVERYTHING at the sink, LOCAL) is complementary — keep both. The baseline is the green START on the
  EXISTING area at the root.
- **Composes with #174/#182.** The action is a TRUE no-op (`exit 0`, writes nothing), so a RED baseline
  short-circuits to `needsHuman` on the 2nd attempt (no-op-deadlock, SSOT §7) — in BOTH serial and
  worktree mode now (#182), with the actionable re-emitted detail above — the correct fast halt, since a
  no-op cannot fix pre-existing breakage. (A baseline action that touched a file or wrote a fragment
  would DEFEAT this short-circuit and burn the full retry budget — keep the action a genuine no-op.)

## WPF structural checks (#11 F5/F6)

WPF idioms are verified the same way — match the structural attribute/property, scoped to
the XAML or code-behind file the task produces, never a broad grep:

```powershell
# catches: a three-state checkbox claim where IsThreeState was never set
if ((Get-Content "src/Desktop/Views/FilterView.xaml" -Raw) -notmatch 'IsThreeState\s*=\s*"True"') {
    Write-Output "FilterView.xaml CheckBox is missing IsThreeState=\"True\""
    exit 1
}
exit 0
```

Other common WPF structural tokens (assert the construct, scoped to the owning file):
`Grid.Column="N"` / `Grid.Row="N"` (a control actually placed in the grid cell, not just a
`ColumnDefinition` existing), `SelectedItem="{Binding …}"` (a real two-way selection bind),
`Grid.ColumnDefinitions` / `<ColumnDefinition`. As always: match the attribute construct,
scope to the one XAML/`.cs` file, and write one actionable failure line.
