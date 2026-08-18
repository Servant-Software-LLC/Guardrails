# catches: wiring that compiles and is still wrong - the judge route computed but not executed on,
#          or guardrailOverrides composed against the ACTOR's block instead of the resolved judge's
#          (rule 7). These clauses drive the REAL seam through Stage2PlanHarness, so they are the only
#          thing that can tell a real wire from a plausible one.
#          The --filter names the five clauses THIS task makes green, never the whole class: the other
#          nine are wave 2's and already green, so including them would assert someone else's work.
#          Re-emits the assertion lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$tests = @(
    'Judge_ResolvesThroughSameResolver_AtActorsRung',
    'Judge_WeakActor_StrengthBump_NotTierBump',
    'Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds',
    'Judge_PinnedCostlyActor_MayBumpIntoCostly_D29',
    'Judge_VerifierMinTier_RaisesNeverLowers'
)
$filter = ($tests | ForEach-Object { "FullyQualifiedName~Stage2ConformanceTests.$_" }) -join '|'
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block (#462).
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455).
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "the judge route is not wired to DoR 6.5. If the failure is about permissions, tools or turn budget rather than the model, check rule 7: EffectiveSettings(isGuardrail: true) must be called on the RESOLVED JUDGE block - calling it on the actor's or the frontmatter's block silently mis-profiles every bumped judge and nothing else in the system notices."
    exit 1
}

# ZERO-MATCH GUARD (#455), keyed on the clause count these five names owe.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 5) {
    Write-Output "exit 0 but only $ran of the five pinned judge clauses executed - this guardrail certified less than it should. The names are PINNED and the plan terminal gate matches the same ones."
    exit 1
}
exit 0
