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
$filter = 'Category=TierResolution&FullyQualifiedName~JudgeProvenanceSchemaTests'

$listed = dotnet test $proj --filter $filter --list-tests --nologo 2>&1
$listExit = $LASTEXITCODE
$names = (($listed | Out-String) -split "`r?`n") | Where-Object { $_ -match 'JudgeProvenanceSchemaTests' }

if ($listExit -ne 0) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "dotnet test --list-tests failed (exit $listExit) - discovery could not run, so no behaviour could be checked. See the log above."
    exit 1
}
if ($names.Count -lt 1) {
    $listed | ForEach-Object { Write-Output $_ }
    Write-Output "ZERO JudgeProvenanceSchemaTests discovered. MOST LIKELY CAUSE: the tests are missing the class-level Trait attribute for Category = TierResolution, which is what this filter selects on - adding it IS an in-scope fix to your own test file. Other causes: the class is not named JudgeProvenanceSchemaTests, or the file was never authored."
    exit 1
}

# One clause per REQUIRED behaviour, keyed on the FULL PINNED METHOD NAME.
#
# FULL names, not discriminating substrings. A substring manifest ('RoundTrip', 'Absent', 'Bumped')
# is satisfiable by ONE test called Judge_RoundTrips_AbsentBumped_Older - three behaviours credited
# to a single method, which is precisely the "exercises only the easy half" failure this guardrail
# exists to catch, wearing a manifest. The names are pinned in the prompt's table, so requiring them
# verbatim costs the author nothing and closes the substring hole.
$behaviours = @(
    @{ Name = 'Judge_RoundTrips_WhenPresent'; Id = "12.4  every judge member survives a serialize/deserialize cycle" }
    @{ Name = 'Judge_AbsentFromJson_WhenNull'; Id = "12.4  the key is ABSENT from the emitted JSON when null - assert on the JSON TEXT; a structural assertion on a deserialized object cannot tell absent from null" }
    @{ Name = 'OlderJournal_WithoutJudge_StillReads'; Id = "12.4  a journal written before this wave still reads, yielding a null member" }
    @{ Name = 'Bumped_RecordsFalse_NotAbsent_WhenNoBumpFired'; Id = "12.4  Bumped records false rather than absent when no bump fired - it is the datum #230-lite reads to answer whether a bumped judge is worth its cost" }
    @{ Name = 'ProvenanceWithoutJudge_SerializesUnchanged'; Id = "12.4  a provenance carrying NO judge serializes exactly as before - the regression a null-emitting member causes, and half of this task's intended RED" }
    @{ Name = 'Advisory_RoundTrips_AndIsAbsentWhenNoFinding'; Id = "6.5/12.4  the advisory finding rides the judge object, optional and independent of Bumped" }
)

$missing = @()
foreach ($b in $behaviours) {
    # -cmatch on the escaped FULL name: C# identifiers are case-SENSITIVE and PowerShell's -match is
    # not (#455 family). Escaped so an underscore-heavy name is matched literally, and anchored on a
    # word boundary so a LONGER name that merely CONTAINS a pinned one does not credit it.
    $pattern = '(^|\.)' + [regex]::Escape($b.Name) + '$'
    if (-not (@($names | Where-Object { ($_.Trim() -split '\s')[0] -cmatch $pattern }).Count -ge 1)) {
        $missing += ($b.Id + "  [expected a discovered test named EXACTLY: " + $b.Name + "]")
    }
}

if ($missing.Count -gt 0) {
    Write-Output "Discovered $($names.Count) JudgeProvenanceSchemaTests test(s), but these required behaviours have no test:"
    $missing | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output ""
    Write-Output "The method names in the task prompt's table are PINNED - this guardrail matches discovered names, so renaming one reads as a missing behaviour. Add more tests freely; do not rename the pinned ones."
    Write-Output ""
    Write-Output "Discovered names were:"
    $names | ForEach-Object { Write-Output ("  " + $_.Trim()) }
    exit 1
}
exit 0
