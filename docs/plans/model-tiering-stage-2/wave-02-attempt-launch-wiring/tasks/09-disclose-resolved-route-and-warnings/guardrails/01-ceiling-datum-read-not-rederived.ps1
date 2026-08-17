# catches: the D28 ceiling being RE-DERIVED here instead of read off the resolution. The datum is
#          definitionally `DeclaresTier AND NOT ServesTier`, which is a second copy of the candidacy
#          predicate D22a forbids duplicating - and a second copy is not a style complaint: if it
#          drifts from the one GR2048 uses, validate certifies a config the runtime cannot route, or
#          warns about a ceiling that is not binding. TierResolution carries the datum precisely so
#          wave 2 READS it.
#
#          Also enforces the prompt's other prohibition (#221): this task changes what is LOGGED,
#          never what is SELECTED. A costly-floor edit here would be a new path to a costly model
#          with no human in the loop.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$content = Get-Content -Raw $file

# POSITIVE: the datum is read, and the disclosure surface exists.
foreach ($needle in @(
    @{ Pattern = 'CostlyCeilingBound';  What = 'the D28 binding-ceiling flag from TierResolution' },
    @{ Pattern = 'CostlyCeilingBlocks'; What = 'the block NAMES the D28 warning must print (naming them is the whole point - "some stronger block was excluded" is not actionable)' },
    @{ Pattern = 'Climbed';             What = 'the climb datum, which 6.2 says is recorded AND logged' },
    @{ Pattern = 'attempt-route\.log';  What = 'the attempt-route.log disclosure file the conformance clauses read' })) {
    if ($content -notmatch $needle.Pattern) {
        $failures += "TaskExecutor never references $($needle.What) (pattern: $($needle.Pattern))"
    }
}

# NEGATIVE (#176): no second copy of the candidacy predicate, and no touching of the costly floor.
if ($content -match 'DeclaresTier') {
    $failures += 'TaskExecutor calls DeclaresTier - that is the resolver''s half of the D28 computation (DeclaresTier AND NOT ServesTier). Read TierResolution.CostlyCeilingBound / CostlyCeilingBlocks instead; a second copy of the predicate can drift from the one GR2048 uses'
}
if ($content -match '\.Costly\b') {
    $failures += 'TaskExecutor reads PromptRunnerConfig.Costly - the costly floor is enforced ONCE, inside TierResolver.SelectCandidate. This task changes what is LOGGED, never what is SELECTED'
}
if ($content -match 'ServesTier') {
    $failures += 'TaskExecutor calls ServesTier - candidacy is decided ONCE, in the resolver (D22a). The executor consumes the resolution; it does not re-run selection'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== route disclosure: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
