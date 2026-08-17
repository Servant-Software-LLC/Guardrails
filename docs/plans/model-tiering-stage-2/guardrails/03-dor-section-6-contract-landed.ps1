# catches: THE Stage 1 failure - a 9/9 GREEN run that shipped materially different code than the
#          design of record, because every guardrail verified what the TASKS specified and nothing
#          ever compared shipped code against the DoR.
#
# ==> READ THIS BEFORE EDITING. Three designs of this gate failed adversarial review; the reasons are
#     the rationale for the shape below, and re-introducing any of them is a regression:
#
#     v1 could only fail for something PRESENT AND WRONG - every check sat inside "if the resolver
#     file exists", so an ABSENT deliverable passed silently, which is the whole Stage 1 failure mode.
#
#     v2 replaced that with a 14-clause GREP manifest, and was MEASURED to certify DECLARATIONS
#     rather than behaviour: against a tree holding only wave 1's record and NO wiring, 10 of 14
#     clauses went green - a `bool NoRoute` property satisfied "the no-route outcome exists". A grep
#     cannot tell a name from a behaviour. Category limit, not a bug.
#
#     v3 moved behaviour into a contract test but gated it on ONE COUNT (`executed >= 6`). Measured:
#     `dotnet test` counts THEORY DATA ROWS, so a single [Theory] with six [InlineData] rows cleared
#     the floor and the whole behavioural half certified one behaviour. Its structural half also
#     carried two regexes that false-RED correct code (a positional `record` with `bool` members) and
#     missed real bypasses (`bool?`, `async`, `sealed`) - deleted here, not patched, because three
#     rounds of patching that layer produced five regressions and zero convergence.
#
#     v4 (this file):
#       PART 1 - only what a grep proves SOUNDLY: the artifacts exist, and the SSOT delta landed.
#       PART 2 - a BEHAVIOUR MANIFEST over TEST NAMES. Each required behaviour must be present as a
#                discovered test, and the suite must pass. Test names are authored to describe
#                behaviour, so this ratchets automatically: wave 3 lands a judge test and the §6.5
#                clause goes green by itself - nobody has to remember to edit this script.
#
# LOCAL - no scope key (#165): terminal postconditions, false at any partial union by construction.
$ErrorActionPreference = 'Continue'
$failures = @()

# =================================================================================================
# PART 1 - STRUCTURAL. Existence and the schema delta. Nothing about code SHAPE: that was v3's
# mistake, and behaviour below covers what those regexes were reaching for.
# =================================================================================================
foreach ($f in @("src/Guardrails.Core/Prompts/TierResolver.cs")) {
    if (-not (Test-Path $f)) {
        $failures += "[6.1/6.2] $f does not exist - the static resolver (#226) has not landed"
    }
}

# Invariant 4: the schema delta lands in the SSOT in the SAME change as its code. `tierSource` is the
# probe token because it is net-new (the SSOT already carried the string `no-route` in 9.6 prose, so
# a no-route token would have been pre-satisfied). Its INPUT is restored in wave 1 by the
# 05/06 tier-provenance pair (ActionDefinition.TierOrigin); wave 2 maps that to the journal value
# per DoR D31. The "not computable without a PlanLoader change" caveat this line used to carry was
# the finding that PRODUCED that pair, and is resolved.
$ssot = if (Test-Path "docs/plans/02-schemas-and-contracts.md") { Get-Content -Raw "docs/plans/02-schemas-and-contracts.md" } else { "" }
if ($ssot -cnotmatch 'tierSource') {
    $failures += "[12.4/Invariant 4] docs/plans/02-schemas-and-contracts.md does not mention tierSource - every 12.x schema delta must land in the SSOT in the same change as its code, or a claim about the schema lives outside the schema and decays without anything noticing"
}

# =================================================================================================
# PART 2 - BEHAVIOUR MANIFEST. Discover the conformance suite's test NAMES, require one per required
# behaviour, then require the suite to pass. Absence of the suite fails every clause at once, which
# is what keeps "fails for an ABSENT deliverable" true.
# =================================================================================================
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$proj   = 'tests/Guardrails.Integration.Tests'
$filter = 'FullyQualifiedName~Stage2ConformanceTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$names  = ($listed | Out-String) -split "`r?`n" | Where-Object { $_ -match 'Stage2ConformanceTests' }

# Each entry: the DoR clause, and a regex over discovered TEST NAMES that evidences it.
$behaviours = @(
  @{ Id = "6.1/9.3  resolution runs per ATTEMPT and reaches per-attempt provenance"
     Pattern = '(?i)per.?attempt|eachattempt|attempt.*provenance|provenance.*attempt' }
  @{ Id = "6.2      a rung with no non-costly candidate settles no-route / needs-human"
     Pattern = '(?i)no.?route|needshuman|needs.?human' }
  @{ Id = "6.2/D28  a binding costly ceiling is surfaced on re-attempt"
     Pattern = '(?i)ceiling|excludedonly|onlybecausecostly|costbound|boundbycost|blockedbycost' }
  @{ Id = "6.2/D22a the resolver's candidate set agrees with ServesTier (validate vs runtime)"
     Pattern = '(?i)servestier|agree|candidacy|predicate' }
  @{ Id = "3        Invariant 7 - routing-ENABLED config, zero-tag plan, legacy path, no tier activity"
     Pattern = '(?i)invariant7|invariant_7|routingenabled|zerotag|untagged.*legacy|legacy.*untagged' }
  @{ Id = "6.5/D29  the judge resolves through the SAME resolver (strength bump; pinned-costly actor)"
     Pattern = '(?i)judge|verifier|strengthbump|mintier|pinnedactor' }
  # --- added by DoR revision 5 (D30/D31). Same v4 shape; two more required behaviours, no new design.
  @{ Id = "6.1/D30  legacy fires ONLY with no effective tier - a tiered plan with no serving block climbs or no-routes, it never falls back"
     Pattern = '(?i)d30|neverfallsback|neverlegacy|notlegacy|nofallback|tiered.*norout' }
  @{ Id = "12.4/D31 a full pin records tierSource=override with provenance.tier ABSENT; legacy records no tierSource at all"
     Pattern = '(?i)d31|tiersource|pinned.*provenance|provenance.*pin' }
)

foreach ($b in $behaviours) {
    if (-not ($names -match $b.Pattern)) {
        $failures += ("[" + $b.Id + "] no discovered Stage2ConformanceTests test evidences this. The terminal gate proves BEHAVIOUR by requiring one named test per required behaviour - name the test after what it proves. If this whole list is failing, the conformance suite has not been authored at all (wave 2 owes the first five; wave 3 owes the judge clause), and this gate is the only thing standing between an unbuilt deliverable and mergeOnSuccess delivering it.")
    }
}

# Now actually RUN them. A named-but-failing test must not certify anything.
if ($names.Count -gt 0) {
    $out = dotnet test $proj --filter $filter --nologo 2>&1
    $testExit = $LASTEXITCODE
    $out | ForEach-Object { Write-Output $_ }
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40
        Write-Output ""
        Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "[6 behavioural] the Stage 2 real-seam conformance tests FAIL - shipped behaviour does not match the DoR (see the re-emitted failure details above)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== DoR section 6 conformance: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "shipped code does not match the design of record (docs/plans/17-model-tiering.md section 6). The tasks may all be green - that is exactly the Stage 1 failure this gate exists to catch: a green run proves the TASKS were satisfied, not that the DESIGN was implemented."
    exit 1
}
Write-Output "DoR section 6 conformance: artifacts present, SSOT delta landed, and every required behaviour is evidenced by a passing named test."
exit 0
