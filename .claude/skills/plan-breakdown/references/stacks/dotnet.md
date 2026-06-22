# Stack file — .NET (dotnet)

The **stack-specific** companion to `references/guardrail-catalogue.md`. The catalogue
holds universal doctrine; this file holds the .NET *instantiations* of it — the exact
regex, the canonical build command, the layout-specific traps. SKILL.md Step 0 loads
this file when it detects a .NET workspace (`.slnx` / `.sln` / `.csproj`). On a
JVM/Go/Python project these patterns are wrong or irrelevant — use that stack's file
instead (none ship yet; see "Future stacks" at the foot of SKILL.md Step 0).

Every stack file answers the same six standard questions first (§1–§6), in this order, so
the files are mirror-able; stack-specific extensions for particular project kinds follow
(§7–§8 server/executable wiring + smoke-test, §9 UI-presence, §10 composition-root wiring,
§11 strip-comments-before-forbidden-keyword-scan, then WPF).
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
  "all tests pass" / whole-suite green is terminal-only):
  ```powershell
  # catches: a project that builds alone but breaks the solution (e.g. unregistered or a broken ref)
  dotnet build PoC/ConformedSources/WorksoftMigrator.slnx -c Release --nologo
  if ($LASTEXITCODE -ne 0) {
      Write-Output "solution build failed"
      exit 1
  }
  exit 0
  ```
- **Tests:** filter to THIS task's tests (`dotnet test <proj> --filter "Category=Stats" --nologo`),
  per archetype #4; the whole-suite `dotnet test` (no filter) is the terminal gate.
- Always pass `--nologo` (and `-v q` on builds) so the one actionable failure line isn't
  buried in banner noise. Declare no interpreter for `dotnet` — it's a build tool the
  guardrail invokes, not a script interpreter (those go in `guardrails.json: interpreters`).
- **Remember the solution-build blind spot (pattern 1):** the terminal `dotnet build
  <solution>` does NOT catch an unregistered project — that's why pattern 1's solution-file
  `file-contains` guardrail exists. Don't let the terminal build stand in for it.

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
10a, then 10b. Mark 10a/10b `scope: "integration"` when they drive the whole assembled feature.

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
