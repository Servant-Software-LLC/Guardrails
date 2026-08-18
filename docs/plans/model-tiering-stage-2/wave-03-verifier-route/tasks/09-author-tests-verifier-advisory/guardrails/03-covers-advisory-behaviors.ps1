# catches: a suite that asserts the easy half - "a weak judge produces an advisory" - and skips the
#          two halves that carry the design. NEVER-HALTS is the property a later reader is most
#          likely to "improve" into a gate, and the DE-DUPLICATION rule is the only reason three
#          reporting surfaces are tolerable at all. A suite silent on either is silent about the two
#          things most likely to be got wrong.
#
# A BEHAVIOUR MANIFEST over DISCOVERED TEST NAMES, not a regex over file text (#468). Discovered
# names cannot be faked by a comment or a string literal, and the check doubles as proof the tests
# are discoverable under the trait every downstream guardrail filters on. Guardrail 01 has already
# proven the project builds, so a discovery failure here is real.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$proj   = 'tests/Guardrails.Core.Tests'
$filter = 'Category=TierResolution&FullyQualifiedName~VerifierAdvisoryTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$listExit = $LASTEXITCODE
$names = (($listed | Out-String) -split "`r?`n") | Where-Object { $_ -match 'VerifierAdvisoryTests' }

if ($listExit -ne 0) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "dotnet test --list-tests failed (exit $listExit) - discovery could not run, so no behaviour could be checked."
    exit 1
}
if ($names.Count -lt 1) {
    Write-Output "ZERO VerifierAdvisoryTests discovered. MOST LIKELY CAUSE: the class is missing its class-level Trait attribute for Category = TierResolution, which is what this filter selects on - adding it IS an in-scope fix to your own test file. Other causes: the class is not named VerifierAdvisoryTests, or the file was never authored."
    exit 1
}

$behaviours = @(
    @{ Name = 'WeakerJudge_IsAdvisoryCondition'; Id = "6.5  a judge WEAKER than its actor is an advisory condition" }
    @{ Name = 'EqualAndWeak_IsAdvisoryCondition'; Id = "6.5  equal-and-WEAK is also an advisory condition - the half that makes 'weaker' insufficient on its own" }
    @{ Name = 'EqualAndStrong_NoAdvisory'; Id = "6.5  equal-and-STRONG is NOT flagged - Opus judging Opus is a real check, and flagging it trains people to ignore the advisory entirely" }
    @{ Name = 'Advisory_NeverHalts_RunProceeds'; Id = "6.5  the advisory NEVER halts, attended or unattended - the run proceeds. This is the property most likely to be 'improved' into a gate" }
    @{ Name = 'Preflight_EmitsOneLinePerAffectedTask'; Id = "6.5  the run-start surface emits ONE line per affected task (task 13 wires it; this pins the DECISION)" }
    @{ Name = 'Jit_RecordsAdvisoryIntoProvenance_Always'; Id = "6.5  the JIT re-check records the advisory into provenance ALWAYS (task 12 wires it; this pins the DECISION)" }
    @{ Name = 'Jit_LogsOnlyWhenObservedDiffersFromPreflight'; Id = "6.5  the JIT re-check LOGS only when the observed pair DIFFERS from the run-start prediction - the de-duplication rule, and the whole reason two surfaces are tolerable rather than noise" }
)

$missing = @()
foreach ($b in $behaviours) {
    # -cmatch: C# identifiers are case-SENSITIVE and PowerShell's -match is not (#455 family).
    $pattern = '(^|\.)' + [regex]::Escape($b.Name) + '$'
    if (-not (@($names | Where-Object { ($_.Trim() -split '\s')[0] -cmatch $pattern }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test named EXACTLY: " + $b.Name + "]")
    }
}

if ($missing.Count -gt 0) {
    Write-Output "Discovered $($names.Count) VerifierAdvisoryTests test(s), but these required behaviours have no test:"
    $missing | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output ""
    Write-Output "This guardrail matches DISCOVERED names, so a behaviour named only in a comment earns nothing. Name each test after what it proves."
    Write-Output ""
    Write-Output "Discovered names were:"
    $names | ForEach-Object { Write-Output ("  " + $_.Trim()) }
    exit 1
}
exit 0
