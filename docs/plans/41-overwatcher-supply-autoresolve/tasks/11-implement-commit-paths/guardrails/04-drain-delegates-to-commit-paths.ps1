# catches: a SECOND commit mechanism. The cheapest way to pass this task's tests is to implement
#          CommitPaths beside an untouched Drain and bolt a pathspec onto Drain's own inline commit.
#          Every test goes green, and the file then owns two commit paths that must be kept in step
#          forever — the next change (a trailer, a rollback rule, a --no-verify decision) lands on one
#          of them, and the operator path or the overwatcher path silently loses it. Design 41 §5 says
#          "Drain is refactored to copy the staged files, then call CommitPaths", and no test can
#          assert that a method DELEGATES rather than duplicates.
#          It also checks the rollback exists at all: a CommitPaths that never resets leaves a
#          partly-staged file for a LATER drain to commit under someone else's name, and only the
#          commit-failure test can see that — one test, one clause, both cheap.
#
# LOCAL scope (no `scope` key), deliberately: the path-count clause below is a statement about THIS
# file at THIS task's attempt. Task 14 adds an overwatcher call site elsewhere; promoting this to
# scope:"integration" would make it re-run at unions it was never written for.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/11-implement-commit-paths/samples/04-drain-delegates-to-commit-paths.valid.cs';   ./04-drain-delegates-to-commit-paths.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/11-implement-commit-paths/samples/04-drain-delegates-to-commit-paths.invalid.cs'; ./04-drain-delegates-to-commit-paths.ps1  # expect 1
#
# baseline counts on src/Guardrails.Core/Execution/SuppliedDrain.cs — MEASURED with
# Select-String -CaseSensitive (matching the -cnotmatch/-cmatch operators below), against the UNTOUCHED
# tree (before task 10's stub), then re-read to confirm where each hit lives:
#   public static SuppliedDrainResult CommitPaths   0
#   CommitPaths\s*\(                               0   (floor of 2 below = the declaration + Drain's call)
#   "reset"                                        0
#   "--hard"                                       0
#   NotImplementedException                        1 at this task's BASE, and that is the point — it is
#     task 10's stub, which this task deletes. A forbidden-present clause RED on arrival is a removal
#     deliverable, the one nonzero §478 permits with a named reason. It is NOT censused as a floor.
#   Only tasks 10 and 11 write this subject (measured across all 20 task.json writeScopes), and task 10
#   writes exactly the stub named above.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/SuppliedDrain.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. Deriving $code by stripping
# comments straight from $raw lets a literal that spells '/*' forge a delimiter and blank real code before
# any literal pass runs. Only / and * INSIDE literals are replaced, so a literal's CONTENT still satisfies
# a required-present clause — and this subject is full of git argv literals ("--hard", "reset").
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // and /// line comments

# ACCUMULATE, never exit 1 per clause (#478).
$failures = @()

if ($code -cnotmatch 'public static SuppliedDrainResult CommitPaths\s*\(') {
    $failures += "$f does not declare `public static SuppliedDrainResult CommitPaths(...)` — the member the Scheduler's auto-resolve will call (design 41 §5). A doc-comment mention does not satisfy this: the clause reads the file with comments stripped."
}

# Anchored on the CALL, not the name (#76): `nameof(SuppliedDrain.CommitPaths)` is valid C# containing
# the bare identifier. Two occurrences = the declaration plus at least one invocation; since the only
# thing in this file that can invoke it is Drain, that is the delegation.
$callCount = ([regex]::Matches($code, 'CommitPaths\s*\(')).Count
if ($callCount -lt 2) {
    $failures += "$f mentions CommitPaths( $callCount time(s) — the declaration alone. Drain never CALLS it, so this file now owns TWO commit mechanisms (design 41 §5 says Drain copies the staged files and then calls CommitPaths). The next trailer/rollback change lands on one of them and the other silently loses it."
}

if ($code -cnotmatch '"reset"' -or $code -cnotmatch '"--hard"') {
    $failures += "$f never runs `git reset --hard` — CommitPaths must restore the pre-commit HEAD on ANY failure (design 41 §5), or a failed supply leaves a partly-staged file for a LATER drain to commit under someone else's name. `git merge --abort` is not a substitute: it exits 128 on a dirtied tracked path."
}

if ($code -cmatch 'NotImplementedException') {
    $failures += "$f still throws NotImplementedException — task 10's stub is still standing, so nothing was implemented."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
