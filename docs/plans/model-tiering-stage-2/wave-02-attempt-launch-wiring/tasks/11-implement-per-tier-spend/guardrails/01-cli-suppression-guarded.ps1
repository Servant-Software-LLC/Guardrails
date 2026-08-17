# catches: the CLI half of the Invariant 7 suppression, which the authored unit tests CANNOT reach.
#          PerTierSpendTests pins the AGGREGATOR ("returns null when nothing resolved through
#          routing"); nothing in Core.Tests observes RunCommand's console output. So an aggregator
#          that correctly returns null, printed by a call site that does not check, still emits a
#          blank or header line on EVERY existing single-model user's run - the exact Invariant 7
#          breakage 9.3 names, invisible to every test in this wave.
#          Structural, scoped to the ONE CLI file this task owns. This is an acknowledged
#          second-best: the strong form would be a CLI output test, which is outside this task's
#          area - the wave-2 breakdown reports that limitation rather than hiding it.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Cli/Commands/RunCommand.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$content = Get-Content -Raw $file

if ($content -notmatch 'JournalTierSpend') {
    $failures += 'RunCommand never references JournalTierSpend - the per-tier spend line is not rendered anywhere, so #230-lite produced an aggregator nobody can see. 9.3 calls this the evidence base for every deferred v2 bet'
}
else {
    # The suppression must be a PATTERN-MATCH on "nothing to report", not a string emptiness test on
    # an already-rendered line: the latter silently starts printing the moment a future edit gives the
    # renderer a header.
    if ($content -notmatch 'JournalTierSpend\.\w+\s*\([^)]*\)\s+is\s+\{') {
        $failures += 'the JournalTierSpend call is not guarded by an `is { } ...` pattern-match - suppression must key on the aggregator returning "nothing to report" (the shape PrintTotalCost already uses for JournalCost.Total), never on a rendered string being empty'
    }
}

# The existing total must survive untouched: 9.3 says a tiering-inactive run prints EXACTLY today's
# cost line, and that line is this one.
if ($content -notmatch 'Total prompt cost') {
    $failures += 'the existing "Total prompt cost" line is gone - 9.3 requires a tiering-inactive run to print EXACTLY today''s cost line; the per-tier section is ADDITIVE to it, never a replacement'
}

# NEGATIVE ASSERTION (#176): the bucket 9.3 forbids by name.
if ($content -match '(?i)untiered') {
    $failures += 'RunCommand mentions "untiered" - 9.3 forbids an untiered bucket outright. Attempts that did not resolve through routing are simply not reported per-tier; they are already in the total'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-tier spend CLI wiring: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
