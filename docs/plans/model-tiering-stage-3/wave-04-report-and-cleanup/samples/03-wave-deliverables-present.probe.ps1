# Two-sided sample probe for guardrails/03-wave-deliverables-present.ps1 (#468/#302). See README.md.
#
# The gate's real input does not exist yet - it is the tree wave 4 has not produced - so the VALID half is
# HAND-SYNTHESIZED here: a minimal, representative delivered tree (the shape this wave is supposed to
# leave behind), plus the deletion actually performed and the two over-deletion victims still in place.
# That is exactly the #302 highest-value case: a gate whose first real execution would otherwise be at run
# time, on the one tree nobody can inspect beforehand.
#
#   VALID    - the synthesized delivered tree                      -> expect exit 0.
#   INVALID a - PER CLAUSE: that clause's matches deleted           -> expect exit 1 + its own message.
#   INVALID b - PER .cs CLAUSE: the match present ONLY as a comment -> expect exit 1 + its own message.
#               (Wave 2's gate probe caught this one LIVE: 7 of 12 required clauses were satisfiable by a
#               `// TODO:` line alone, because the comment strip ran before the forbidden scans and not the
#               required ones.)
#   INVALID c - the doomed folder RESTORED                          -> expect exit 1 + its own message.
#   INVALID d - PER KEEP: an over-deletion victim removed           -> expect exit 1 + its own message.
#
# Read-only against the repo: everything happens in a %TEMP% tree built from the literals below.

$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot '..\guardrails\03-wave-deliverables-present.ps1'

$src = Get-Content -Raw -Path $gate
$m = [regex]::Match($src, '(?ms)^\$required = @\((.*?)^\)\s*$')
if (-not $m.Success) { Write-Output 'ABORT: could not lift $required from the guardrail'; exit 9 }
$required = Invoke-Expression ('@(' + $m.Groups[1].Value + ')')

$dm = [regex]::Match($src, "(?m)^\`$doomed = '([^']+)'")
if (-not $dm.Success) { Write-Output 'ABORT: could not lift $doomed from the guardrail'; exit 9 }
$doomed = $dm.Groups[1].Value

# The two over-deletion victims, lifted from the gate's own foreach literal so they cannot drift.
$keeps = @([regex]::Matches($src, "(?m)^\s+@\('(docs/plans/[^']+)',\s*$") | ForEach-Object { $_.Groups[1].Value })
if ($keeps.Count -lt 2) { Write-Output "ABORT: expected 2 keep paths, lifted $($keeps.Count)"; exit 9 }

# --- the HAND-SYNTHESIZED valid tree ----------------------------------------------------------------
# Minimal but representative: each file carries the real construct the clause is pinned to, in the form
# the delivered code will actually use, NOT a copy of the regex.
$synth = @{
    'src/Guardrails.Core/Journal/JournalModelsUsed.cs' = @'
namespace Guardrails.Core.Journal;

/// <summary>Models-used aggregation - the sibling of JournalTierSpend.</summary>
public static class JournalModelsUsed
{
    public static IReadOnlyList<ModelUsage>? Summarize(JournalDocument document) => null;

    public static string? Render(JournalDocument document) => null;
}
'@
    'src/Guardrails.Cli/Commands/RunCommand.cs' = @'
    private static void PrintTotalCost(string planDirectory, TextWriter output)
    {
        JournalDocument document = JournalReader.Read(journalPath);
        if (JournalTierSpend.Render(document) is { } perTier)
        {
            output.WriteLine($"Per-tier spend: {perTier}");
        }

        if (JournalModelsUsed.Render(document) is { } models)
        {
            output.WriteLine($"Models used: {models}");
        }
    }
'@
    'tests/Guardrails.Core.Tests/ModelTiering/ModelsUsedSummaryTests.cs' = @'
namespace Guardrails.Core.Tests.ModelTiering;

[Trait("Category", "ModelTieringStage3")]
public sealed class ModelsUsedSummaryTests
{
}
'@
    'tests/Guardrails.Integration.Tests/ModelTiering/ModelsUsedReportTests.cs' = @'
namespace Guardrails.Integration.Tests.ModelTiering;

[Trait("Category", "ModelTieringStage3")]
public sealed class ModelsUsedReportTests
{
}
'@
    'docs/plans/02-schemas-and-contracts.md' = @'
- **Per-tier spend (model tiering #230-lite, DoR SS9.3).** The `run` summary adds a
  `Per-tier spend: easy: 180k tok / $0` line.
- **Models used (#349, Stage 3).** The `run` summary adds a `Models used: ...` line.
'@
    '.claude/skills/guardrails-domain-knowledge/SKILL.md' = @'
- **`provenance.model` is BEST-KNOWN-ACTUAL** (#349, Stage 3).
- **The run report names them** (#349, Stage 3): a `Models used:` line closes the summary.
'@
}

function New-Tree {
    $t = Join-Path ([System.IO.Path]::GetTempPath()) ('gr-s3w4g-' + [guid]::NewGuid().ToString('N'))
    foreach ($f in $synth.Keys) {
        $dst = Join-Path $t $f
        New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
        Set-Content -Path $dst -Value $synth[$f] -NoNewline
    }
    # The two over-deletion victims must be PRESENT in a valid tree. One is a file already synthesized
    # above; the other is this plan's own directory, which only has to exist.
    foreach ($k in $keeps) {
        $p = Join-Path $t $k
        if (Test-Path $p) { continue }
        New-Item -ItemType Directory -Path $p -Force | Out-Null
    }
    # The doomed folder is asserted ABSENT, so a valid tree deliberately does NOT create it.
    return $t
}

function Invoke-Gate($tree) {
    Push-Location $tree
    $o = & pwsh -NoProfile -File $gate 2>&1
    $e = $LASTEXITCODE
    Pop-Location
    return @{ Exit = $e; Lines = @($o | Where-Object { $_ -match '^\s+- ' }) }
}

$fail = 0
$tree = New-Tree

# --- VALID -----------------------------------------------------------------------------------------
$v = Invoke-Gate $tree
if ($v.Exit -eq 0) { Write-Output "VALID (synthesized delivered tree) -> exit 0, 0 clauses firing   OK" }
else {
    Write-Output "VALID (synthesized delivered tree) -> exit $($v.Exit), $($v.Lines.Count) firing   *** FALSE RED ***"
    $v.Lines | ForEach-Object { Write-Output "    $_" }
    $fail++
}

$originals = @{}
foreach ($f in $synth.Keys) { $originals[$f] = Get-Content -Raw -Path (Join-Path $tree $f) }
$dead = @()

# --- INVALID a: per-clause removal ------------------------------------------------------------------
foreach ($clause in $required) {
    $path = $clause[0]; $pattern = $clause[1]
    $target = Join-Path $tree $path
    $mutated = [regex]::Replace($originals[$path], $pattern, '')
    if ($mutated -eq $originals[$path]) {
        $dead += "a: $path :: /$pattern/ does not match the synthesized VALID file - the VALID half should already be red"
        continue
    }
    Set-Content -Path $target -Value $mutated -NoNewline
    $r = Invoke-Gate $tree
    Set-Content -Path $target -Value $originals[$path] -NoNewline
    if ($r.Exit -eq 0 -or @($r.Lines | Where-Object { $_ -match [regex]::Escape($pattern) }).Count -lt 1) {
        $dead += "a: $path :: /$pattern/ did NOT fire when its own matches were removed (exit $($r.Exit)) - this clause can never fail"
    }
}

# --- INVALID b: comment-only satisfaction (.cs clauses only) -----------------------------------------
# Replace the whole file with the SAME matching text commented out. A gate that does not strip comments
# before its required scans reads a `// TODO:` line as a delivered member - the live defect wave 2 found.
foreach ($clause in $required) {
    $path = $clause[0]; $pattern = $clause[1]
    if ($path -notlike '*.cs') { continue }
    $target = Join-Path $tree $path
    $commented = ($originals[$path] -split "`n" | ForEach-Object { '// ' + $_.TrimEnd() }) -join "`n"
    Set-Content -Path $target -Value $commented -NoNewline
    $r = Invoke-Gate $tree
    Set-Content -Path $target -Value $originals[$path] -NoNewline
    if ($r.Exit -eq 0 -or @($r.Lines | Where-Object { $_ -match [regex]::Escape($pattern) }).Count -lt 1) {
        $dead += "b: $path :: /$pattern/ is satisfied by a COMMENT alone (exit $($r.Exit)) - the required scan does not strip comments, so a member dropped by the merge whose doc comment survived reads here as delivered"
    }
}

# --- INVALID c: the doomed folder restored -----------------------------------------------------------
$doomedPath = Join-Path $tree $doomed
New-Item -ItemType Directory -Path $doomedPath -Force | Out-Null
Set-Content -Path (Join-Path $doomedPath 'guardrails.json') -Value '{}' -NoNewline
$r = Invoke-Gate $tree
Remove-Item $doomedPath -Recurse -Force
if ($r.Exit -eq 0 -or @($r.Lines | Where-Object { $_ -match 'still exists on the merged HEAD' }).Count -lt 1) {
    $dead += "c: $doomed :: the deletion clause did NOT fire with the folder restored (exit $($r.Exit)) - a merge that silently brought the superseded folder back would pass this gate"
}

# --- INVALID d: per-keep removal ---------------------------------------------------------------------
foreach ($k in $keeps) {
    $p = Join-Path $tree $k
    $wasFile = Test-Path $p -PathType Leaf
    $saved = if ($wasFile) { Get-Content -Raw -Path $p } else { $null }
    Remove-Item $p -Recurse -Force
    $r = Invoke-Gate $tree
    if ($wasFile) { Set-Content -Path $p -Value $saved -NoNewline }
    else { New-Item -ItemType Directory -Path $p -Force | Out-Null }
    if ($r.Exit -eq 0 -or @($r.Lines | Where-Object { $_ -match [regex]::Escape($k) }).Count -lt 1) {
        $dead += "d: $k :: the over-deletion clause did NOT fire when it was removed (exit $($r.Exit)) - task 03 could delete it and this gate would agree"
    }
}

$total = $required.Count + @($required | Where-Object { $_[0] -like '*.cs' }).Count + 1 + $keeps.Count
if ($dead.Count -eq 0) { Write-Output "INVALID (4 mutation families) -> all $total mutations fired individually   OK" }
else {
    Write-Output "INVALID (4 mutation families) -> $($dead.Count) DEAD clause(s):"
    $dead | ForEach-Object { Write-Output "    $_" }
    $fail++
}

Remove-Item $tree -Recurse -Force -ErrorAction SilentlyContinue

Write-Output ''
if ($fail -eq 0) { Write-Output "RESULT: both halves behave. $($required.Count) required clauses, 1 deletion and $($keeps.Count) over-deletion clauses, each individually live; no .cs clause is clearable by comment."; exit 0 }
Write-Output "RESULT: $fail half/halves DEFECTIVE."; exit 1
