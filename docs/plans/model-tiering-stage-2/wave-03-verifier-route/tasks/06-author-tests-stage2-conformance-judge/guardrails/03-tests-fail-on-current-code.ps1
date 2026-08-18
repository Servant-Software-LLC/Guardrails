# catches: judge clauses that PASS against an UNWIRED GuardrailRunner - which is the #382
#          passing-but-blind trap in its purest form. Today GuardrailRunner picks a judge's block
#          from frontmatter-or-default with no tier awareness at all, so a clause that goes green now
#          is either asking the resolver what it WOULD have chosen (proving the resolver, which waves
#          1-2 already did) or asserting nothing about the wiring at all.
# INVERSE guardrail: a NON-zero exit is SUCCESS. Guardrail 02 has established the solution compiles,
# so a non-zero exit now unambiguously means these tests RAN and FAILED (#155).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$tests = @(
    'Judge_ResolvesThroughSameResolver_AtActorsRung',
    'Judge_WeakActor_StrengthBump_NotTierBump',
    'Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds',
    'Judge_PinnedCostlyActor_MayBumpIntoCostly_D29',
    'Judge_VerifierMinTier_RaisesNeverLowers'
)
$filter = ($tests | ForEach-Object { "FullyQualifiedName~Stage2ConformanceTests.$_" }) -join '|'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): a crashed test host also exits non-zero.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 5) {
    Write-Output "only $ran of the five pinned judge clauses executed - the red proof certified nothing. The names are PINNED (see the task prompt); a rename makes this filter select fewer than it should, and the plan terminal gate matches the same names."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every judge clause PASSED against an UNWIRED GuardrailRunner - so they assert nothing about the wiring. GuardrailRunner still resolves a judge's block from frontmatter-or-default with no tier awareness; nothing calls ResolveJudge yet. MOST LIKELY CAUSE: the clauses consult TierResolver directly instead of observing the route through the journal and the captured PromptInvocation - which proves the resolver, not the wire."
    exit 1
}
exit 0
