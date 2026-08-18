# catches: a wave-3 branch whose OWN unit suite regressed once merged with its siblings. The three
#          Core.Tests classes this wave authors are made green in three INDEPENDENT segments that
#          never see each other - the judge-resolution pair (01/02), the provenance-schema pair
#          (03/04) and the advisory pair (09/10). Two of them write to overlapping type surfaces
#          (TierResolution / JudgeResolution), so this is the first tree on which all three run
#          together.
#
#          Distinct from the sibling 02 gate, which proves the INTEGRATION seam, and from
#          01-wave-union-builds, which proves only that the union COMPILES - a compiling union can
#          still have an advisory that no longer agrees with the resolver about what "weak" means.
#
# LOCAL - no scope key (#165): a wave terminal postcondition. At an intermediate union inside this
# wave, the classes whose implementing task has not run yet are legitimately RED.
# Re-emits the failure DETAIL at the END so the WHY reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)

# Named alternation over the three classes THIS WAVE owns - never the bare plan-wide trait, which
# would also select waves 1 and 2's classes (already proven by their own exit gates) and anything a
# later wave adds (dotnet.md 4.3, shape 2: parenthesise, bare '|', no backslash).
$classes = @(
    'JudgeResolutionTests',
    'JudgeProvenanceSchemaTests',
    'VerifierAdvisoryTests'
)
$filter = 'Category=TierResolution&(' + (($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|') + ')'

$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
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
    Write-Output "a wave-3 unit suite is not green on the merged wave HEAD. If the advisory suite is the failing one, the likely cause is two definitions of 'weak' that drifted: the advisory must reuse whatever the resolver computes for the bump rather than deriving weakness a second way."
    exit 1
}

# PER-CLASS FLOOR, not a total (#455 family). The previous form summed the executed count across
# the whole union and required 3 - so ONE class contributing 25 tests while the other two contributed
# ZERO passed the gate. That is the exact failure this guard exists to catch, wearing a threshold:
# a suite that was never authored, whose class was renamed, or whose every test is [Skip]ped is
# indistinguishable from a green one when only the total is read.
#
# Discovery is the per-class probe. The run above already proved the union PASSES; --list-tests then
# proves each named class independently CONTRIBUTES to it. Discovery cannot be satisfied by a skip,
# a rename, or an empty file, and it names WHICH class is missing instead of reporting a bare count.
$missingClasses = @()
foreach ($c in $classes) {
    $listed = dotnet test tests/Guardrails.Core.Tests --filter "Category=TierResolution&FullyQualifiedName~$c" --list-tests --nologo 2>&1
    $listExit = $LASTEXITCODE
    $found = @(($listed | Out-String) -split "`r?`n" | Where-Object { $_ -cmatch [regex]::Escape($c) }).Count
    if ($listExit -ne 0 -or $found -lt 1) {
        $missingClasses += "$c (discovery exit $listExit, $found test(s) found)"
    }
}
if ($missingClasses.Count -gt 0) {
    Write-Output ""
    Write-Output "=== the union passed, but $($missingClasses.Count) of $($classes.Count) wave-3 unit classes contributed NOTHING ==="
    $missingClasses | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "A green union proves nothing about a class that is absent from it. Most likely: the class was never authored, it is missing the class-level [Trait(\"Category\", \"TierResolution\")] this filter selects on, it was renamed, or every one of its tests is [Skip]ped."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does not mean the wave is done. Three classes must each
# contribute; keyed on the EXECUTED count (Passed+Failed), since Total: counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt $classes.Count) {
    Write-Output "exit 0 but only $ran test(s) executed across $($classes.Count) classes - this wave gate certified nothing. The --filter is malformed or every matched test is [Skip]ped. (The per-class check above is the real floor; this is the cheap backstop.)"
    exit 1
}
exit 0
