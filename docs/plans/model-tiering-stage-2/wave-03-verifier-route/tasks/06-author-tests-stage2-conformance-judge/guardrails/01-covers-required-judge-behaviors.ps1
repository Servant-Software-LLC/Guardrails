# catches: the wave's real-seam suite landing without the clauses the plan TERMINAL GATE reads by
#          NAME. The gate's 6.5/D29 clause is the ONE unsatisfied clause in the whole plan - the
#          reason wave 3 exists - and it matches DISCOVERED test names. A suite that proves the
#          judge rules but names them something else leaves the gate red after every task has run,
#          where there is no retry.
#
# A BEHAVIOUR MANIFEST over DISCOVERED TEST NAMES, not a regex over file text (#468).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$proj   = 'tests/Guardrails.Integration.Tests'
$filter = 'FullyQualifiedName~Stage2ConformanceTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$listExit = $LASTEXITCODE
$names = (($listed | Out-String) -split "`r?`n") | Where-Object { $_ -match 'Stage2ConformanceTests' }

if ($listExit -ne 0) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "dotnet test --list-tests failed (exit $listExit) - discovery could not run, so no behaviour could be checked."
    exit 1
}
if ($names.Count -lt 1) {
    Write-Output "ZERO Stage2ConformanceTests discovered - the suite this wave EXTENDS was not found. It is wave 2's deliverable and should already exist; check the class name and the wave-2 merge."
    exit 1
}

# Wave 2 owes nine facts; wave 3 adds these five. The count floor guards against a rename that
# silently drops one of wave 2's while adding one of wave 3's.
if ($names.Count -lt 14) {
    Write-Output "only $($names.Count) Stage2ConformanceTests discovered - wave 2 owes NINE and wave 3 adds FIVE, so 14 is the floor. A lower count means an existing fact was renamed or dropped rather than extended; wave 2's nine must keep passing."
    exit 1
}

$behaviours = @(
    @{ Marker = 'Judge_ResolvesThroughSameResolver_AtActorsRung';        Id = "6.5 rules 2-3  the judge resolves through the SAME resolver, at the actor's rung" }
    @{ Marker = 'Judge_WeakActor_StrengthBump_NotTierBump';              Id = "D24a  the bump is in STRENGTH, the rung is unchanged - a tier bump satisfies 'stronger' and is exactly what D24a forbids" }
    @{ Marker = 'Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds';   Id = "6.5 rule 5  degrade and PROCEED - the actor halts in the same case, and the run-proceeds half is what distinguishes them" }
    @{ Marker = 'Judge_PinnedCostlyActor_MayBumpIntoCostly_D29';         Id = "D29  a pinned costly actor licenses a costly judge bump; the default pointer does NOT" }
    @{ Marker = 'Judge_VerifierMinTier_RaisesNeverLowers';               Id = "6.5.1  the verifier floor only ever raises" }
)

$missing = @()
foreach ($b in $behaviours) {
    if (-not (@($names | Where-Object { $_ -cmatch $b.Marker }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test named: " + $b.Marker + "]")
    }
}
if ($missing.Count -gt 0) {
    Write-Output "Discovered $($names.Count) Stage2ConformanceTests, but these required judge behaviours have no test:"
    $missing | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output ""
    Write-Output "These five names are PINNED by the task prompt because the PLAN TERMINAL GATE matches discovered names - renaming one leaves the gate red after the whole plan has run."
    exit 1
}
exit 0
