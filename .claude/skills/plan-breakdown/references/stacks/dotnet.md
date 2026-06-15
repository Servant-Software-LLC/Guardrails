# Stack file — .NET (dotnet)

The **stack-specific** companion to `references/guardrail-catalogue.md`. The catalogue
holds universal doctrine; this file holds the .NET *instantiations* of it — the exact
regex, the canonical build command, the layout-specific traps. SKILL.md Step 0 loads
this file when it detects a .NET workspace (`.slnx` / `.sln` / `.csproj`). On a
JVM/Go/Python project these patterns are wrong or irrelevant — use that stack's file
instead (none ship yet; see "Future stacks" at the foot of SKILL.md Step 0).

Every stack file answers the same five standard questions, in this order, so the files
are mirror-able. Each pattern's PowerShell example follows the catalogue's conventions:
a leading `# catches:` line, one actionable `Write-Output` line on failure, explicit
`exit 1` / `exit 0`. Scope every grep to the one file the task owns.

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

## 4. Canonical build command — how `dotnet build` should appear in guardrails

- **Build a single project** (a code task's `build-passes` guardrail): build THIS task's
  project, not the whole solution — keep failures attributable and the DAG parallel.
  ```powershell
  # catches: code that doesn't compile
  dotnet build PoC/ConformedSources/WorksoftMigrator.Desktop --nologo -v q
  if ($LASTEXITCODE -ne 0) { Write-Output "WorksoftMigrator.Desktop does not build"; exit 1 }
  exit 0
  ```
- **Build the whole solution** belongs to ONE terminal integration task only (catalogue:
  "all tests pass" / whole-suite green is terminal-only):
  ```powershell
  # catches: a project that builds alone but breaks the solution (e.g. unregistered or a broken ref)
  dotnet build PoC/ConformedSources/WorksoftMigrator.slnx -c Release --nologo
  if ($LASTEXITCODE -ne 0) { Write-Output "solution build failed"; exit 1 }
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
