# The author-time two-sided proof for guardrails/03-wave-deliverables-present.ps1 (#302/#468).
#
# It runs the REAL wave-exit gate. The clause list is LIFTED out of the guardrail by parsing its own
# `$required` literals, so the probe cannot go stale against the script; the VALID content is hand-written
# here, because the gate's input does not exist anywhere yet - that is exactly the "renders or executes the
# task's own not-yet-authored output" case the author-time gate calls its highest-value target.
#
# The hand-written content is SELF-CHECKED against the lifted clauses before anything else runs: if a
# clause is added to the guardrail that this probe's valid tree does not satisfy, the probe says so rather
# than reporting a false FAIL and sending the next reader after a clause that is fine.
#
# Cases:
#   valid                  -> exit 0
#   mutant per clause      -> exit 1   (every occurrence of that one pattern removed from its own file)
#   comment-only per .cs   -> exit 1   (the whole file commented out - proves the comment strip has teeth)
#   missing file           -> exit 1
#   missing fixture dir    -> exit 1
#   fixture dir, no plan   -> exit 1   (present but holding no guardrails.json)
#   src/ leak              -> exit 1   (the one clause that fires because a task did TOO MUCH)
#   src/obj leak           -> exit 0   (the bin/obj exclusion, which a naive scan would false-RED on)
#
# The comment-only family is wave 2's lesson carried forward: its gate did not strip comments before its
# required scans, so 7 of 12 clauses were satisfied by a comment alone.
#
# Read-only against the repo: everything is built under %TEMP% and removed in the finally block. Runs from
# anywhere.
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$waveDir = Split-Path -Parent $here
$guardrail = Join-Path $waveDir 'guardrails/03-wave-deliverables-present.ps1'

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
if ($clauses.Count -lt 7) {
    Write-Output "PROBE PRECONDITION FAILED: lifted only $($clauses.Count) clause(s) from the guardrail, expected at least 7. Its shape changed and this parser no longer reads it - fix the parser, do not lower the floor."
    exit 1
}

# --- the hand-written VALID content ---------------------------------------------------------------
$valid = @{
    'tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs' = @'
namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>The deterministic half of the review net.</summary>
public static class TierClassificationAudit
{
    public static bool IsTieringConfigured(object plan) => true;
}
'@
    'tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAuditTests.cs' = @'
namespace Guardrails.Core.Tests.ModelTiering;

public sealed class TierClassificationAuditTests
{
}
'@
    'tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs' = @'
namespace Guardrails.Core.Tests.ModelTiering;

public sealed class ModelAppropriatenessDoctrineAnchorTests
{
}
'@
    '.claude/skills/guardrails-review/SKILL.md' = @'
- **Model-appropriateness - the tag-quality net**:
  On a plan generated before tiering shipped this probe produces NOTHING AT ALL.

## Quality bar
- [ ] ... or is named as an advisory MISSING-CLASSIFICATION finding.
'@
}

# Self-check: every lifted clause must be satisfied by this probe's own valid content.
$uncovered = @()
foreach ($clause in $clauses) {
    if (-not $valid.ContainsKey($clause[0])) {
        $uncovered += "no valid content is defined for $($clause[0])"
    }
    elseif ($valid[$clause[0]] -cnotmatch $clause[1]) {
        $uncovered += "the valid content for $($clause[0]) does not satisfy /$($clause[1])/"
    }
}
if ($uncovered.Count -gt 0) {
    Write-Output "PROBE PRECONDITION FAILED: this probe's valid tree does not satisfy every clause the guardrail declares:"
    $uncovered | ForEach-Object { Write-Output "  - $_" }
    Write-Output "Extend the `$valid table above. Until then this probe cannot distinguish a dead clause from its own gap."
    exit 1
}

$fixtures = @('tests/Guardrails.Core.Tests/TestData/tier-tags/configured',
              'tests/Guardrails.Core.Tests/TestData/tier-tags/untagged')

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-w5-exit-probe-" + [guid]::NewGuid().ToString('N'))

function New-Tree {
    param([string]$Workspace, [string]$SkipFile, [string]$SkipFixture, [switch]$FixtureWithoutPlan,
          [string]$BlankPattern, [string]$BlankIn, [string]$CommentOut, [string]$LeakInto)

    foreach ($rel in $valid.Keys) {
        if ($rel -eq $SkipFile) { continue }
        $dest = Join-Path $Workspace $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        $content = $valid[$rel]
        if ($BlankPattern -and $rel -eq $BlankIn) {
            $content = [regex]::Replace($content, $BlankPattern, '')
        }
        if ($rel -eq $CommentOut) {
            $content = ($content -split "`n" | ForEach-Object { '// ' + $_.TrimEnd("`r") }) -join "`n"
        }
        Set-Content -Path $dest -Value $content -NoNewline
    }

    foreach ($fixture in $fixtures) {
        if ($fixture -eq $SkipFixture) { continue }
        New-Item -ItemType Directory -Path (Join-Path $Workspace $fixture) -Force | Out-Null
        if (-not $FixtureWithoutPlan) {
            Set-Content -Path (Join-Path $Workspace "$fixture/guardrails.json") -Value '{ "version": 1 }' -NoNewline
        }
    }

    # src/ always exists, with a bin/obj sibling, so the exclusion is exercised on every case rather than
    # only on the one that asserts it.
    $src = Join-Path $Workspace 'src/Guardrails.Core'
    New-Item -ItemType Directory -Path $src -Force | Out-Null
    Set-Content -Path (Join-Path $src 'Placeholder.cs') -Value 'namespace Guardrails.Core;' -NoNewline
    if ($LeakInto) {
        $leak = Join-Path $Workspace $LeakInto
        New-Item -ItemType Directory -Path (Split-Path -Parent $leak) -Force | Out-Null
        Set-Content -Path $leak -Value 'public static class TierClassificationAudit { }' -NoNewline
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
    $results += @{ Name = 'valid'; Expected = 0; Actual = (Invoke-Guardrail $ws) }

    foreach ($clause in $clauses) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -BlankPattern $clause[1] -BlankIn $clause[0]
        $results += @{ Name = "mutant: /$($clause[1])/ removed from $($clause[0])"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    foreach ($rel in @($valid.Keys | Where-Object { $_ -like '*.cs' })) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -CommentOut $rel
        $results += @{ Name = "comment-only: $rel entirely commented out"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    foreach ($rel in @($valid.Keys)) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -SkipFile $rel
        $results += @{ Name = "mutant: $rel absent"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    foreach ($fixture in $fixtures) {
        $ws = Join-Path $root ("case-" + $i++)
        New-Tree -Workspace $ws -SkipFixture $fixture
        $results += @{ Name = "mutant: fixture $fixture absent"; Expected = 1; Actual = (Invoke-Guardrail $ws) }
    }

    $ws = Join-Path $root ("case-" + $i++)
    New-Tree -Workspace $ws -FixtureWithoutPlan
    $results += @{ Name = 'mutant: fixture directories hold no guardrails.json'; Expected = 1; Actual = (Invoke-Guardrail $ws) }

    $ws = Join-Path $root ("case-" + $i++)
    New-Tree -Workspace $ws -LeakInto 'src/Guardrails.Core/Loading/TierClassificationAudit.cs'
    $results += @{ Name = 'mutant: the audit leaked into src/'; Expected = 1; Actual = (Invoke-Guardrail $ws) }

    $ws = Join-Path $root ("case-" + $i++)
    New-Tree -Workspace $ws -LeakInto 'src/Guardrails.Core/obj/Debug/Generated.cs'
    $results += @{ Name = 'not a leak: the same token under src/**/obj/ (build output)'; Expected = 0; Actual = (Invoke-Guardrail $ws) }
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
    Write-Output "$($bad.Count) of $($results.Count) case(s) behaved wrongly. A mutant that exits 0 means that clause is DEAD - a case-insensitive operator, a pattern also present in a comment, or a scan that never reached the file. A valid case that exits 1 means the gate false-REDs a correctly delivered wave."
    exit 1
}

Write-Output "all $($results.Count) case(s) behaved as specified"
exit 0
