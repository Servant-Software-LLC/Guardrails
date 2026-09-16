# catches: TWO wrong implementations the red census (02) cannot see, because both are red for the
#          right-looking reason.
#          (a) A row pinned to the right METHOD NAME that asserts the WRONG reason token. The census
#              reads outcomes, never assertions, so `checkout-diverged` spelled `checkout-drifted`
#              is red on the stub and green after task 03 spells it the same wrong way — and the
#              certification gate, which keys on these exact tokens, then never matches.
#          (b) A temp-git fixture that is not Windows-safe. Every one of the four clauses below is a
#              LOGGED halt in this repo: read-only loose objects abort Dispose (#109), autocrlf
#              rewrites a clean file into a "modified" one (which is the modified-in-checkout row's
#              own subject), a machine-global pre-commit hook gates a throwaway repo, and git prunes
#              an emptied parent directory that the next write then cannot reach.
#          These are ALL properties of the authored SOURCE, invisible to any test outcome. The check
#          is a lower bound (a token in a comment still matches) — the census is what makes it worth
#          having, not a substitute for it.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/02-author-tests-missing-resource-facts/samples/03-tokens-and-fixture.valid.cs';   ./03-tokens-and-fixture.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/02-author-tests-missing-resource-facts/samples/03-tokens-and-fixture.invalid.cs'; ./03-tokens-and-fixture.ps1  # expect 1
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Core.Tests/Supply/MissingResourceFactsTests.cs" }

$failures = @()
if (-not (Test-Path $f)) { Write-Output "$f does not exist — this task authors it"; exit 1 }   # PRECONDITION: the only early exit

# Variable discipline (the two-variable rule): $raw is NEVER matched against; REQUIRED clauses read
# $code; the FORBIDDEN clause below reads $code too, for the reason stated at that clause.
# Neutralize string literals BEFORE stripping comments (#561): mapping the delimiter-forming
# characters INSIDE each literal first means the comment strip cannot be fooled by a literal that
# spells '/*' — and a path literal like "vendor/resource.js" in a test fixture spells exactly that
# risk. The map touches only '/' and '*', and NO required token below contains either character, so
# it cannot damage any clause.
$raw  = Get-Content $f -Raw
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim strings
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary strings
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')        # block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # line comments (/// doc comments included)

# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive (the clauses are
# -cnotmatch), not assumed:
#   every pattern below                 n/a - file created by this task
#   no ancestor writes these tokens here: this task's dependsOn is EMPTY, so it has no ancestors.
# Ambient-vocabulary check: the 14 reason tokens are lowercase-hyphenated string literals that no C#
# identifier, using, namespace or base type can supply incidentally; the four fixture clauses are
# anchored on a call or an argument PAIR, not a bare word.

# --- (a) the 14 reason tokens of design 41 §2.2, asserted VERBATIM ---------------------------------
# -cnotmatch, not -notmatch: these are wire tokens compared with ordinal equality in production, so a
# clause that accepts 'Checkout-Diverged' would certify a test that can never match (taxonomy 3).
$tokens = @(
    'checkout-not-on-run-branch', 'checkout-diverged',
    'escapes-workspace', 'protected-path', 'under-plan-folder',
    'plan-scope-incomplete', 'produced-by-another-task',
    'present-on-run-base', 'case-collision', 'deleted-on-run-base',
    'not-committed-in-checkout', 'not-a-blob', 'modified-in-checkout',
    'facts-unavailable'
)
foreach ($t in $tokens) {
    if ($code -cnotmatch [regex]::Escape($t)) {
        $failures += "$f never names the reason token '$t' — design 41 §2.2 pins it, and the certification gate keys on it verbatim. The row for this fact is asserting some other spelling."
    }
}

# --- (b) the Windows-safe git fixture — four logged halts, each anchored on its construct -----------
if ($code -cnotmatch 'SafeDelete\s*\.\s*DeleteDirectory') {
    $failures += "$f does not dispose its temp repo through SafeDelete.DeleteDirectory — git marks .git/objects loose objects READ-ONLY on Windows and a plain Directory.Delete throws UnauthorizedAccessException (#109). Both existing TempGitRepo copies in tests/Guardrails.Core.Tests/Supply/ use it."
}
if ($code -cnotmatch '"core\.autocrlf"\s*,\s*"false"') {
    $failures += "$f does not force core.autocrlf=false on its temp repo — on a host whose global config translates line endings, 'git diff --quiet HEAD' reports a clean file as modified, so the modified-in-checkout row would pass for the wrong reason and every blob size would be host-dependent."
}
if ($code -cnotmatch '"core\.hooksPath"') {
    $failures += "$f does not isolate hooks with core.hooksPath — a machine-global pre-commit scanner reaches into a throwaway fixture repo and gates its commits (a logged failure on this repo's own demo box)."
}
if ($code -cnotmatch 'Directory\s*\.\s*CreateDirectory\(\s*Path\s*\.\s*GetDirectoryName\(') {
    $failures += "$f never recreates a pruned parent before writing (Directory.CreateDirectory(Path.GetDirectoryName(...))) — Git-for-Windows prunes the emptied parent on 'git rm', which the deleted-on-run-base row performs, and the next write into vendor/ then fails."
}

# --- the negative assertion: the rollback that exits 128 ------------------------------------------
# POLARITY NOTE, and it is deliberate: this forbidden-present clause reads $code, NOT $scan. Doctrine
# sends forbidden clauses to $scan because a banned C# CONSTRUCT hiding in a string literal is a false
# positive. Here the banned thing IS a string literal — git argv — so a $scan clause, which erases
# literal content, could NEVER fire (taxonomy 13: a clause that cannot match). $code is the right
# level: comments are already stripped, so a doc comment saying "never use merge --abort" is not a hit.
if ($code -cmatch '"merge"\s*,\s*"--abort"' -or $code -cmatch 'merge\s+--abort') {
    $failures += "$f rolls back with 'git merge --abort' — it exits 128 on a dirtied tracked path (logged as W3). Use 'git reset --hard <commitish>', which is reliable."
}

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
Write-Output "Reason tokens and the Windows-safe fixture: all $($tokens.Count + 5) clause(s) satisfied in $f."
exit 0
