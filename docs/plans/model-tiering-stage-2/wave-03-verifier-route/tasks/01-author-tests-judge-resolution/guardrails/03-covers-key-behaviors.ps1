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
    @{ Marker = 'Pin|Frontmatter'; Id = "6.5 rule 1  an explicit frontmatter tier/runner pin wins outright" }
    @{ Marker = 'Rung'; Id = "6.5 rule 2  the judge's rung is the ACTOR's rung (not the actor's strength)" }
    @{ Marker = 'Strength|Bump'; Id = "6.5 rule 3/D24a  the bump is in STRENGTH at the same rung, never a TIER bump" }
    @{ Marker = 'EqualAndStrong|NoBump'; Id = "6.5 rule 4  equal-and-strong needs NO bump (Opus judging Opus is a real check)" }
    @{ Marker = 'Costly|Degrade'; Id = "6.5 rule 5  the only stronger block being costly DEGRADES and the run PROCEEDS - the actor halts in the same case, and a test that only checks 'no bump' cannot tell degrade from halt" }
    @{ Marker = 'D29|Pinned'; Id = "D29  a PINNED costly actor licenses a costly judge bump; the default pointer does NOT" }
    @{ Marker = 'MinTier|Floor'; Id = "6.5.1  the verifier floor RAISES a too-low result and NEVER lowers a high one - the asymmetry that makes it a floor rather than a default" }
)

$missing = @()
foreach ($b in $behaviours) {
    # -cmatch: C# identifiers are case-SENSITIVE and PowerShell's -match is not (#455 family).
    if (-not (@($names | Where-Object { $_ -cmatch $b.Marker }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test whose name contains: " + $b.Marker + "]")
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
