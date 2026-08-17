# catches: a test file that compiles and fails but exercises only the easy half - typically
#          "action.tier wins" and "default fills in" - leaving the EQUAL-VALUES case unasserted.
#          That case is the entire reason this task exists: a comparison-after-the-fact
#          implementation (tier != config.Tiering?.DefaultTier, which is what PlanValidator.cs does
#          today) passes every other assertion here and fails only that one.
#
# A BEHAVIOUR MANIFEST over DISCOVERED TEST NAMES, not a regex over the file's text (#468). The
# file-text form was measured wrong in three separate ways on this plan - credited by a checklist
# comment, by a one-line vacuous body, and by an ambient type name - and every fix introduced a new
# hole. Discovered names cannot be faked by a comment or a string literal, and the check doubles as
# proof the tests are actually DISCOVERABLE under the trait every downstream guardrail filters on.
# Guardrail 01 has already proven the project builds, so a discovery failure here is real.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$proj   = 'tests/Guardrails.Core.Tests'
$filter = 'Category=TierResolution&FullyQualifiedName~ActionTierProvenanceTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$listExit = $LASTEXITCODE
$text = $listed | Out-String
$names = ($text -split "`r?`n") | Where-Object { $_ -match 'ActionTierProvenanceTests' }

if ($listExit -ne 0) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "dotnet test --list-tests failed (exit $listExit) - discovery could not run, so no behaviour could be checked. See the log above."
    exit 1
}
if ($names.Count -lt 1) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "ZERO ActionTierProvenanceTests discovered. MOST LIKELY CAUSE: the tests are missing the Trait attribute for Category = TierResolution, which is what this filter selects on - adding it IS an in-scope fix to your own test file. Other causes: the class is not named ActionTierProvenanceTests, or the file was never authored."
    exit 1
}

# One clause per REQUIRED behaviour, keyed on a discriminating substring of the pinned method name.
# Deliberately NOT keyed on tokens that are ambient in a test file: 'Equal' would be satisfied by
# every Assert.Equal call, 'Task' by System.Threading.Tasks.Task and by any async signature, 'Default'
# by half the fixture setup. Each marker below appears in exactly one pinned name.
$behaviours = @(
    @{ Marker = 'OriginIsTask';               Id = "(1) action.tier set => TierOrigin.Task" }
    @{ Marker = 'OriginIsPlanDefault';        Id = "(2) tier absent, defaultTier supplies it => TierOrigin.PlanDefault" }
    @{ Marker = 'SameTokenAsDefault';         Id = "(3) action.tier EQUAL to defaultTier => still TierOrigin.Task -- THE case a comparison-after-the-fact implementation gets wrong, and the reason this task exists" }
    @{ Marker = 'OriginIsNone_AndTierIsNull'; Id = "(4) no tier anywhere => TierOrigin.None and Tier null (the untagged/legacy case, Invariant 7)" }
    @{ Marker = 'UnrecognizedDefaultTier';    Id = "(5) an unrecognized defaultTier does not propagate (GR2043) => origin stays None, agreeing with the null Tier" }
    @{ Marker = 'WavedPlan';                  Id = "(6) the default reaches a task inside a wave folder - LoadWaves/LoadWaveTasks is a different call path from flat LoadTasks" }
)

$missing = @()
foreach ($b in $behaviours) {
    # -cmatch: C# identifiers are case-SENSITIVE and PowerShell's -match is not (#455 family).
    if (-not (@($names | Where-Object { $_ -cmatch $b.Marker }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test whose name contains: " + $b.Marker + "]")
    }
}

if ($missing.Count -gt 0) {
    Write-Output "Discovered $($names.Count) ActionTierProvenanceTests test(s), but these required behaviours have no test:"
    $missing | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output ""
    Write-Output "The method names in the task prompt's table are PINNED - this guardrail matches discovered names, so renaming one reads as a missing behaviour. Add more tests freely; do not rename those six."
    Write-Output ""
    Write-Output "Discovered names were:"
    $names | ForEach-Object { Write-Output ("  " + $_.Trim()) }
    exit 1
}
exit 0
