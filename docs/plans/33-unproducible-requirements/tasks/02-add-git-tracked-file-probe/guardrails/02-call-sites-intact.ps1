# catches: a fifth PlanValidator overload that silently breaks or rewrites existing callers. The new
#          probe parameter must arrive with a DEFAULT, so all 73 shipped `new PlanValidator(` call sites
#          keep binding unchanged. A task that "fixes" a call site instead of defaulting the parameter
#          would pass a build check while changing 73 behaviours nobody reviewed - and those sites are
#          outside this task's writeScope, so it cannot legitimately touch them anyway.
#
# Required-present baseline (#478): this is a REGRESSION PIN, so its measured baseline is deliberately
#          NONZERO - the named reason the rule requires. Measured on master @67859c7:
#          `grep -rn "new PlanValidator(" src tests --include=*.cs | wc -l` = 73 (2 in src, 71 in
#          tests). The assertion is that the number does not MOVE, in either direction: fewer means a
#          caller was deleted or rewritten, more means this task added callers it was not asked to add.
$ErrorActionPreference = 'Continue'

$expected = 73

$files = Get-ChildItem -Path 'src', 'tests' -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue
if (-not $files) {
    Write-Output 'PRECONDITION: no .cs files found under src/ or tests/. Every clause below would count zero and pass vacuously.'
    exit 1
}

$count = 0
foreach ($f in $files) {
    $count += ([regex]::Matches((Get-Content -LiteralPath $f.FullName -Raw), [regex]::Escape('new PlanValidator('))).Count
}

$failures = New-Object System.Collections.Generic.List[string]

if ($count -ne $expected) {
    $failures.Add('CALL-SITE COUNT MOVED: found ' + $count + ' `new PlanValidator(` call sites, expected ' + $expected + '. Fewer means an existing caller was deleted or rewritten - the new probe parameter must arrive with a DEFAULT so every shipped arity still binds, and those call sites are outside this task writeScope. More means this task added callers it was not asked to add.')
}

# The two composition roots must keep their signatures. Grep for them rather than trusting a line number.
$probe = 'src/Guardrails.Cli/PlanProbe.cs'
$sched = 'src/Guardrails.Core/Execution/Scheduler.cs'
foreach ($root in @($probe, $sched)) {
    if (-not (Test-Path -LiteralPath $root)) {
        $failures.Add('COMPOSITION ROOT MISSING: ' + $root + ' is not present. GR2060 reaches operators through these two roots and neither may move in this task.')
        continue
    }
    if ((Get-Content -LiteralPath $root -Raw) -notmatch [regex]::Escape('new PlanValidator(')) {
        $failures.Add('COMPOSITION ROOT NO LONGER CONSTRUCTS PlanValidator: ' + $root + ' has lost its `new PlanValidator(` call. Section 11 prohibition 8 forbids changing either composition root signature in this task.')
    }
}

if ($failures.Count -gt 0) {
    Write-Output ('=== PlanValidator call sites were not preserved (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ('All ' + $expected + ' `new PlanValidator(` call sites intact, and both composition roots still construct it.')
exit 0
