# catches: OVER-DELIVERY - this task implementing Resolve() as well as SelectCandidate. The prompt
#          says to leave Resolve throwing, but prose alone does not stop it (#221), and this task's
#          writeScope covers TierResolver.cs, so it is one plausible agent decision away.
#
#          The damage is DOWNSTREAM and badly misattributed: task 03's TDD-red check would then fail
#          with "the TierResolverPrecedenceTests PASS against the still-throwing Resolve stub - they
#          are tautological", which is FALSE and points the agent at its own tests. Task 03's
#          writeScope is the test file ONLY, so the honest fix is out of its scope and the reachable
#          "fix" is to author deliberately-weakened tests - which then poisons task 04's forward
#          check too. Catching it HERE keeps the failure attributable to the task that caused it.
# Scoped to the ONE file this task owns (grep-scope rule).
$path = "src/Guardrails.Core/Prompts/TierResolver.cs"
if (-not (Test-Path $path)) {
    Write-Output "$path does not exist"
    exit 1
}

$raw    = Get-Content -Raw -Path $path
$noVerb = [regex]::Replace($raw, '@"(?:[^"]|"")*"', '""')
$noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
$code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
$code   = [regex]::Replace($code, '(?m)//.*$', '')

$resolveBody = $null
$block = [regex]::Match($code, '(?ms)^\s*(?:public|internal|private|protected|static|\s)+[\w<>,\[\]\?]+\s+Resolve\s*\([^)]*\)\s*\{(.*?)^\s{0,8}\}')
if ($block.Success) { $resolveBody = $block.Groups[1].Value }
else {
    $expr = [regex]::Match($code, '(?s)\bResolve\s*\([^)]*\)\s*=>(.*?);')
    if ($expr.Success) { $resolveBody = $expr.Groups[1].Value }
}
if ($null -eq $resolveBody) {
    Write-Output "could not locate a Resolve(...) method body in $path - task 01 declared it as a stub and this task must leave it in place for task 03's TDD-red proof. Do not remove or rename it."
    exit 1
}

if ($resolveBody -notmatch 'NotImplementedException') {
    Write-Output "Resolve(...) in $path no longer throws NotImplementedException - this task must implement SelectCandidate ONLY. Task 04 implements precedence, and task 03's TDD-red proof depends on Resolve still throwing when it runs; implementing it here makes task 03 fail with a misleading 'your tests are tautological' message it cannot act on."
    exit 1
}
exit 0
