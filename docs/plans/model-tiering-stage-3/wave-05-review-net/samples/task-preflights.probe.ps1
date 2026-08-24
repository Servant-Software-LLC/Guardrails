# The author-time two-sided proof for the two TASK-LEVEL preflights (#302/#468):
#   tasks/02-implement-tier-classification-audit/preflights/01-stub-delivered.ps1
#   tasks/04-add-model-appropriateness-probe/preflights/01-anchors-delivered.ps1
#
# Both are the same archetype - a JIT dependency-delivery check asserting an ancestor's contribution
# actually landed in this task's segment - so they share one probe rather than two near-identical ones.
#
# Their subjects do not exist anywhere yet (they are wave 5's own output), which makes this the
# "render or execute the task's own not-yet-authored output" case the author-time gate calls its
# highest-value target: the valid content is hand-written here, and each clause is then removed in turn.
#
# Cases per preflight:
#   valid              -> exit 0
#   mutant per clause  -> exit 1   (every occurrence of that one pattern removed from the subject)
#   comment-only       -> exit 1   (the subject entirely commented out - proves the strip has teeth)
#   missing subject    -> exit 1   (the precondition path)
#
# Read-only against the repo: everything is built under %TEMP% and removed in the finally block.
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$waveDir = Split-Path -Parent $here

$cases = @(
    @{
        Guardrail = Join-Path $waveDir 'tasks/02-implement-tier-classification-audit/preflights/01-stub-delivered.ps1'
        Subject   = 'tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs'
        StripsComments = $true
        Valid     = @'
namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>The deterministic half of the review net.</summary>
public static class TierClassificationAudit
{
    public static bool IsTieringConfigured(object plan) => throw new NotImplementedException();
    public static object Audit(object plan) => throw new NotImplementedException();
    public static object ClassifiableSubjects(object plan) => throw new NotImplementedException();
}

public sealed record TierClassificationFinding(string SubjectId, string Detail);
'@
        Clauses   = @('class\s+TierClassificationAudit\b', 'IsTieringConfigured', 'ClassifiableSubjects', 'record\s+TierClassificationFinding')
    },
    @{
        Guardrail = Join-Path $waveDir 'tasks/04-add-model-appropriateness-probe/preflights/01-anchors-delivered.ps1'
        Subject   = 'tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs'
        StripsComments = $false
        Valid     = @'
namespace Guardrails.Core.Tests.ModelTiering;

public sealed class ModelAppropriatenessDoctrineAnchorTests
{
    private const string ReviewSkill = ".claude/skills/guardrails-review/SKILL.md";
}
'@
        Clauses   = @('class\s+ModelAppropriatenessDoctrineAnchorTests\b', '\.claude/skills/guardrails-review/SKILL\.md')
    }
)

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-w5-taskpre-probe-" + [guid]::NewGuid().ToString('N'))
$results = @()

function Invoke-Guardrail {
    param([string]$Guardrail, [string]$Workspace, [string]$Subject, [string]$Content, [switch]$OmitSubject)

    $dest = Join-Path $Workspace $Subject
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    if (-not $OmitSubject) { Set-Content -Path $dest -Value $Content -NoNewline }

    Push-Location $Workspace
    try {
        & $Guardrail *>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally { Pop-Location }
}

try {
    $i = 0
    foreach ($case in $cases) {
        $label = Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $case.Guardrail))
        if (-not (Test-Path $case.Guardrail -PathType Leaf)) {
            Write-Output "PROBE PRECONDITION FAILED: $($case.Guardrail) is missing"
            exit 1
        }

        # Self-check: the hand-written valid content must satisfy every clause this probe will mutate.
        foreach ($clause in $case.Clauses) {
            if ($case.Valid -cnotmatch $clause) {
                Write-Output "PROBE PRECONDITION FAILED: the valid content for $label does not satisfy /$clause/ - extend it before trusting a FAIL below"
                exit 1
            }
        }

        $ws = Join-Path $root ("case-" + $i++)
        $results += @{ Name = "$label valid"; Expected = 0
                       Actual = (Invoke-Guardrail $case.Guardrail $ws $case.Subject $case.Valid) }

        foreach ($clause in $case.Clauses) {
            $ws = Join-Path $root ("case-" + $i++)
            $results += @{ Name = "$label mutant: /$clause/ removed"; Expected = 1
                           Actual = (Invoke-Guardrail $case.Guardrail $ws $case.Subject ([regex]::Replace($case.Valid, $clause, ''))) }
        }

        if ($case.StripsComments) {
            $ws = Join-Path $root ("case-" + $i++)
            $commented = ($case.Valid -split "`n" | ForEach-Object { '// ' + $_.TrimEnd("`r") }) -join "`n"
            $results += @{ Name = "$label comment-only: the whole subject commented out"; Expected = 1
                           Actual = (Invoke-Guardrail $case.Guardrail $ws $case.Subject $commented) }
        }

        $ws = Join-Path $root ("case-" + $i++)
        $results += @{ Name = "$label precondition: subject absent"; Expected = 1
                       Actual = (Invoke-Guardrail $case.Guardrail $ws $case.Subject '' -OmitSubject) }
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
    Write-Output "$($bad.Count) of $($results.Count) case(s) behaved wrongly. A mutant that exits 0 means that clause is DEAD; a valid case that exits 1 means the preflight blocks a correctly-delivered segment before its attempt loop even starts, which is the most expensive false red a task can carry."
    exit 1
}

Write-Output "all $($results.Count) case(s) behaved as specified"
exit 0
