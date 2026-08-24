# The author-time two-sided proof for preflights/01-tier-vocabulary-materialized.ps1 (#302/#468).
#
# It runs the REAL wave-entry gate - not a copy of its clause list. The clauses are LIFTED out of the
# guardrail by parsing its own `$anchors` literals, so the probe cannot go stale against the script: add a
# clause and it is tested on the next run without this file being touched.
#
# Why a probe script rather than a committed `.valid` / `.invalid` fixture pair: the gate reads 11 files
# for 25 clauses plus two directories, so no single fixture file can represent its input, and one fixture
# per clause would be dozens of files that drift the moment a clause is edited.
#
# Cases:
#   valid              -> exit 0   (the real tree, seeded from a checkout or the integration worktree)
#   mutant per clause  -> exit 1   (every occurrence of that one clause's pattern removed from its file)
#   missing file       -> exit 1   (per distinct file, the precondition path)
#   missing directory  -> exit 1   (the two directory clauses, which are not regex clauses)
#
# ALL occurrences are removed rather than the first, deliberately: the gate strips C# comments before
# matching, so removing only the first raw match could delete a doc-comment mention and leave the code
# match standing - which would report a LIVE clause as dead and send the next reader chasing nothing.
#
# Read-only against the repo: everything is copied under %TEMP% and removed in the finally block.
#
#   pwsh -NoProfile -File <this file> [-Repo <path to a checkout or the integration worktree>]
[CmdletBinding()]
param([string]$Repo)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$waveDir = Split-Path -Parent $here
$guardrail = Join-Path $waveDir 'preflights/01-tier-vocabulary-materialized.ps1'

if (-not $Repo) {
    # samples -> wave -> plan -> plans -> docs -> repo root
    $Repo = (Resolve-Path (Join-Path $here '../../../../..')).Path
}

if (-not (Test-Path $guardrail -PathType Leaf)) {
    Write-Output "PROBE PRECONDITION FAILED: $guardrail is missing"
    exit 1
}

# --- lift the clause list out of the guardrail ----------------------------------------------------
$source = Get-Content -Raw -Path $guardrail
$clauses = @()
foreach ($m in [regex]::Matches($source, "@\('([^']+)',\s*'((?:[^']|'')*)',")) {
    $clauses += , @($m.Groups[1].Value, $m.Groups[2].Value.Replace("''", "'"))
}

# The two directory clauses are not triples; lift them by name so a rename here is caught too.
$dirClauses = @()
foreach ($m in [regex]::Matches($source, "^\s*\`$(?:precedent|testFamilyDir)\s*=\s*'([^']+)'", 'Multiline')) {
    $dirClauses += $m.Groups[1].Value
}

if ($clauses.Count -lt 20 -or $dirClauses.Count -ne 2) {
    Write-Output "PROBE PRECONDITION FAILED: lifted $($clauses.Count) regex clause(s) and $($dirClauses.Count) directory clause(s) from the guardrail."
    Write-Output "Expected at least 20 and exactly 2. The guardrail's shape changed and this probe's parser no longer reads it - fix the parser, do not lower the floor."
    exit 1
}

$files = $clauses | ForEach-Object { $_[0] } | Sort-Object -Unique

# --- build the valid tree -------------------------------------------------------------------------
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-w5-entry-probe-" + [guid]::NewGuid().ToString('N'))

function New-Tree {
    param([string]$Workspace, [string]$SkipFile, [string]$SkipDir, [string]$BlankPattern, [string]$BlankIn)

    foreach ($rel in $files) {
        if ($rel -eq $SkipFile) { continue }
        $src = Join-Path $Repo $rel
        if (-not (Test-Path $src -PathType Leaf)) { throw "the seed repo '$Repo' has no $rel - pass -Repo <a tree with wave 1-4 merged>" }

        $dest = Join-Path $Workspace $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        $content = Get-Content -Raw -Path $src
        if ($BlankPattern -and $rel -eq $BlankIn) {
            $content = [regex]::Replace($content, $BlankPattern, '')
        }
        Set-Content -Path $dest -Value $content -NoNewline
    }

    foreach ($dir in $dirClauses) {
        if ($dir -eq $SkipDir) { continue }
        New-Item -ItemType Directory -Path (Join-Path $Workspace $dir) -Force | Out-Null
    }
}

function Invoke-Guardrail {
    param([string]$Workspace)
    Push-Location $Workspace
    try {
        & $guardrail *>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { Pop-Location }
}

$results = @()
try {
    $i = 0

    $ws = Join-Path $root ("case-" + $i++)
    New-Tree -Workspace $ws
    $results += @{ Name = 'valid (real files, both directories present)'; Expected = 0; Actual = (Invoke-Guardrail $ws) }

    foreach ($clause in $clauses) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -BlankPattern $clause[1] -BlankIn $clause[0]
        $results += @{ Name = "mutant: /$($clause[1])/ removed from $($clause[0])"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    foreach ($rel in $files) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -SkipFile $rel
        $results += @{ Name = "mutant: $rel absent"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    foreach ($dir in $dirClauses) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -SkipDir $dir
        $results += @{ Name = "mutant: directory $dir absent"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }
}
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

$bad = @($results | Where-Object { $_.Expected -ne $_.Actual })
foreach ($r in $bad) {
    Write-Output ("FAIL  expected {0}, got {1}  <- {2}" -f $r.Expected, $r.Actual, $r.Name)
}

Write-Output ""
if ($bad.Count -gt 0) {
    Write-Output "$($bad.Count) of $($results.Count) case(s) behaved wrongly. A mutant that exits 0 means that clause is DEAD - it can never fire, however far the anchor moves; a case-insensitive operator or a pattern also present in a comment are the two ways that happens. A valid case that exits 1 means the gate false-REDs the real tree, which halts a correct wave before any task runs."
    exit 1
}

Write-Output "all $($results.Count) case(s) behaved as specified ($($clauses.Count) clause mutants, $($files.Count) missing-file cases, $($dirClauses.Count) missing-directory cases)"
exit 0
