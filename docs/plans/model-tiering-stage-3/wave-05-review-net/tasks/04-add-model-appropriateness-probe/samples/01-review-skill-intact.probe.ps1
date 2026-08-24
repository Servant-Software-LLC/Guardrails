# The author-time two-sided proof for guardrails/01-review-skill-intact.ps1 (#302).
#
# It runs the REAL guardrail script against a throwaway copy of the real skill file, then against one
# mutant per clause. There is no committed `.valid.md` / `.invalid.md` pair here and that is deliberate:
# the subject is a 200 KB DOCUMENTATION deliverable, so a hand-written "representative valid sample" would
# be a 200 KB duplicate that goes stale the first time the skill is edited, and a hand-written invalid one
# would prove nothing the mutants below do not. The skill itself IS the valid sample; the mutants are
# generated from it, one landmark at a time, so no clause can be dead while its siblings carry the exit
# code (#478).
#
# The valid half is the one that pays here. The CRLF trap it caught during authoring is recorded in the
# guardrail's own header: `.claude/skills/**` is not eol=lf-pinned, so an anchored clause written
# `^### 6\. Report$` matches ZERO times against a CRLF checkout and every heading clause would have been
# permanently dead - invisible under the invalid half, where everything is failing anyway.
#
# Cases:
#   valid              -> exit 0   (the real skill, unmodified)
#   mutant per landmark-> exit 1   (13 of them, each removing exactly one landmark line/passage)
#   truncated          -> exit 1   (headings kept, bulk removed - the size floor's own case)
#   missing subject    -> exit 1   (the precondition path)
#
# Read-only against the repo: everything is copied under %TEMP% and removed in the finally block.
#
#   pwsh -NoProfile -File <this file> [-Repo <path to a checkout or the integration worktree>]
[CmdletBinding()]
param([string]$Repo)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$guardrail = Join-Path (Split-Path -Parent $here) 'guardrails/01-review-skill-intact.ps1'
$relative = '.claude/skills/guardrails-review/SKILL.md'

if (-not $Repo) {
    # samples -> task -> tasks -> wave -> plan -> plans -> docs -> repo root
    $Repo = (Resolve-Path (Join-Path $here '../../../../../../..')).Path
}

$source = Join-Path $Repo $relative
if (-not (Test-Path $source -PathType Leaf)) {
    Write-Output "PROBE PRECONDITION FAILED: could not find $relative under '$Repo'."
    Write-Output "Pass -Repo <path> - e.g. the integration worktree the wave runs against."
    exit 1
}
if (-not (Test-Path $guardrail -PathType Leaf)) {
    Write-Output "PROBE PRECONDITION FAILED: $guardrail is missing"
    exit 1
}

$skill = Get-Content -Raw -Path $source

# One mutant per landmark. Each is (label, the literal passage to delete) - the passage is looked up in the
# real file, so a landmark the probe cannot find is itself reported rather than silently skipped.
$landmarks = @(
    '### 1. Inventory',
    '### 2. Adversarial pass per task (the heart)',
    '### 2b. EXECUTE the guardrails',
    '### 3. DAG soundness',
    '### 4. Missing-insertion check',
    '### 5. State-contract lint',
    '### 6. Report',
    '### 7. Record the review',
    '## Quality bar',
    'Model named but unservable',
    'Missing / malformed positive-baseline (preflight) on a brownfield plan',
    "the model-availability probe's JIT-resolved judge models, deferred to #223;",
    'No fix applied without explicit approval'
)

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-w5-skill-probe-" + [guid]::NewGuid().ToString('N'))
$results = @()

function Invoke-Guardrail {
    param([string]$Workspace, [string]$Content, [switch]$OmitSubject)

    $target = Join-Path $Workspace '.claude/skills/guardrails-review'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if (-not $OmitSubject) {
        Set-Content -Path (Join-Path $target 'SKILL.md') -Value $Content -NoNewline
    }

    Push-Location $Workspace
    try {
        & $guardrail *>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

try {
    $i = 0

    $ws = Join-Path $root ("case-" + $i++)
    $results += @{ Name = 'valid (the real skill)'; Expected = 0; Actual = (Invoke-Guardrail -Workspace $ws -Content $skill) }

    foreach ($landmark in $landmarks) {
        if (-not $skill.Contains($landmark)) {
            $results += @{ Name = "mutant: $landmark"; Expected = 1; Actual = -1
                           Note = 'the probe could not find this landmark in the real skill to remove it - the guardrail clause and this probe have drifted apart' }
            continue
        }

        $ws = Join-Path $root ("case-" + $i++)
        $mutated = $skill.Replace($landmark, '')
        $results += @{ Name = "mutant: removed '$landmark'"; Expected = 1
                       Actual = (Invoke-Guardrail -Workspace $ws -Content $mutated) }
    }

    # The size floor's own case: every landmark kept, the bulk of the document gone.
    $ws = Join-Path $root ("case-" + $i++)
    $skeleton = ($landmarks -join "`r`n`r`n")
    $results += @{ Name = 'mutant: truncated to the landmarks alone'; Expected = 1
                   Actual = (Invoke-Guardrail -Workspace $ws -Content $skeleton) }

    $ws = Join-Path $root ("case-" + $i++)
    $results += @{ Name = 'precondition (subject file missing)'; Expected = 1
                   Actual = (Invoke-Guardrail -Workspace $ws -Content '' -OmitSubject) }
}
finally {
    Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
}

$bad = @($results | Where-Object { $_.Expected -ne $_.Actual })
foreach ($r in $results) {
    $verdict = if ($r.Expected -eq $r.Actual) { 'ok  ' } else { 'FAIL' }
    $note = if ($r.ContainsKey('Note')) { "  ($($r.Note))" } else { '' }
    Write-Output ("{0}  expected {1}, got {2}  <- {3}{4}" -f $verdict, $r.Expected, $r.Actual, $r.Name, $note)
}

if ($bad.Count -gt 0) {
    Write-Output ""
    Write-Output "$($bad.Count) of $($results.Count) case(s) behaved wrongly. A mutant that exits 0 means that clause is DEAD - it can never fire, however far the real defect goes; on a CRLF checkout an anchored clause is the likeliest way that happens. A valid case that exits 1 means the guardrail false-REDs the real skill, which dead-ends every attempt at needs-human."
    exit 1
}

Write-Output ""
Write-Output "all $($results.Count) case(s) behaved as specified"
exit 0
