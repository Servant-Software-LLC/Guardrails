# catches: a suite that compiles and fails but exercises only the easy half, leaving the cases that
#          actually discriminate a correct implementation unasserted.
#
# A BEHAVIOUR MANIFEST over DISCOVERED TEST NAMES, not a regex over the file's text (#468). The
# file-text form was measured wrong three separate ways on this plan - credited by a checklist
# comment, by a one-line vacuous body, and by an ambient type name - and every fix introduced a new
# hole. Discovered names cannot be faked by a comment or a string literal, and the check doubles as
# proof the tests are DISCOVERABLE under the trait every downstream guardrail filters on.
# Guardrail 01 has already proven the project builds, so a discovery failure here is real.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$proj   = 'tests/Guardrails.Core.Tests'
$filter = 'Category=TierResolution&FullyQualifiedName~JudgeResolutionTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$listExit = $LASTEXITCODE
$names = (($listed | Out-String) -split "`r?`n") | Where-Object { $_ -match 'JudgeResolutionTests' }

if ($listExit -ne 0) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "dotnet test --list-tests failed (exit $listExit) - discovery could not run, so no behaviour could be checked. See the log above."
    exit 1
}
if ($names.Count -lt 1) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "ZERO JudgeResolutionTests discovered. MOST LIKELY CAUSE: the tests are missing the class-level Trait attribute for Category = TierResolution, which is what this filter selects on - adding it IS an in-scope fix to your own test file. Other causes: the class is not named JudgeResolutionTests, or the file was never authored."
    exit 1
}

# One clause per REQUIRED behaviour, keyed on a discriminating substring of the pinned method name.
$behaviours = @(
    @{ Name = 'FrontmatterPin_WinsOutright'; Id = "6.5 rule 1  an explicit frontmatter tier/runner pin wins outright" }
    @{ Name = 'JudgeRung_IsActorsRung_NotActorsStrength'; Id = "6.5 rule 2  the judge's rung is the ACTOR's rung (not the actor's strength)" }
    @{ Name = 'WeakActor_StrengthBump_KeepsActorsRung'; Id = "6.5 rule 3/D24a  the bump is in STRENGTH at the same rung, never a TIER bump" }
    @{ Name = 'EqualAndStrong_NoBump'; Id = "6.5 rule 4a  equal-and-strong needs NO bump (Opus judging Opus is a real check)" }
    @{ Name = 'EqualAndWeak_Bumps'; Id = "6.5 rule 4b  equal-and-WEAK does bump - the half that distinguishes rule 4 from 'never bump on equal'" }
    @{ Name = 'Specialization_BreaksTie_AmongEqualCandidates'; Id = "6.5 rule 6  specialization breaks a tie among otherwise-equal candidates - untested, the rule silently never fires" }
    @{ Name = 'OnlyStrongerBlockCostly_DegradesAndProceeds'; Id = "6.5 rule 5  the only stronger block being costly DEGRADES and the run PROCEEDS - the actor HALTS in the same case, and a test that only checks 'no bump' cannot tell degrade from halt" }
    @{ Name = 'D29_PinnedCostlyActor_MayBumpIntoCostly'; Id = "D29a  a PINNED costly actor licenses a costly judge bump" }
    @{ Name = 'D29_DefaultPointer_DoesNotLicenseIt'; Id = "D29b  the default pointer does NOT license it - the half that makes D29 a rule rather than a permission" }
    @{ Name = 'MinTierFloor_RaisesTooLow'; Id = "6.5.1a  the verifier floor RAISES a too-low result" }
    @{ Name = 'MinTierFloor_NeverLowersHigher'; Id = "6.5.1b  and NEVER lowers a high one - the asymmetry that makes it a FLOOR rather than a default (D27)" }
    @{ Name = 'JudgeCandidacy_AgreesWith_ServesTier'; Id = "D22a  every block ResolveJudge selects must satisfy the SHARED ServesTier predicate. Wave 1's property test covers the ACTOR path only; without this the judge path can grow a second, divergent candidacy rule - exactly what D22a exists to forbid, and nothing else in this wave would notice." }
)

$missing = @()
foreach ($b in $behaviours) {
    # -cmatch on the escaped FULL name: C# identifiers are case-SENSITIVE and PowerShell's
    # -match is not (#455 family). FULL names, not discriminating substrings: a substring
    # manifest is satisfiable by ONE omnibus method name crediting many behaviours at once,
    # which is the very 'exercises only the easy half' failure this guardrail exists to catch.
    $pattern = '(^|\.)' + [regex]::Escape($b.Name) + '$'
    if (-not (@($names | Where-Object { ($_.Trim() -split '\s')[0] -cmatch $pattern }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test named EXACTLY: " + $b.Name + "]")
    }
}

if ($missing.Count -gt 0) {
    Write-Output "Discovered $($names.Count) JudgeResolutionTests test(s), but these required behaviours have no test:"
    $missing | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output ""
    Write-Output "The method names in the task prompt's table are PINNED - this guardrail matches discovered names, so renaming one reads as a missing behaviour. Add more tests freely; do not rename the pinned ones."
    Write-Output ""
    Write-Output "Discovered names were:"
    $names | ForEach-Object { Write-Output ("  " + $_.Trim()) }
    exit 1
}
exit 0
