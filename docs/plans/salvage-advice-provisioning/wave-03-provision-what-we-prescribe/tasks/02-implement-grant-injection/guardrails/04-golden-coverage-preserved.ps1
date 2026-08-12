# catches: the cheapest wrong implementation for this task (#221) - DELETING the two assertions the
#          injection breaks, or softening Assert.Equal into Assert.Contains, instead of re-pointing them.
#          A tests-pass guardrail can NEVER catch either: a deleted test cannot fail, and a softened one
#          passes. Deletion would also pre-emptively disarm wave 4's full-suite gate.
$f = 'tests/Guardrails.Core.Tests/ClaudePromptRunnerArgsTests.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found - the pre-existing golden was DELETED outright"; exit 1 }
$c = Get-Content -Raw -Path $f

# The seven tests whose names must survive VERBATIM. (The eighth may be renamed - see below.)
$required = @(
    'BaseFlags_ArePresentInOrder',
    'AllowedTools_AreJoinedWithCommas',
    'Model_WhenSet_IsPassed_WhenNull_Omitted',
    'ExtraArgs_AreAppendedVerbatim',
    'Environment_InjectsOutputTokenCap_FromMaxOutputTokens',
    'Environment_DefaultCap_IsAbove32k',
    'Environment_UserEnvPassthrough_WinsLast'
)
$missing = @()
foreach ($n in $required) { if ($c -notmatch [regex]::Escape($n)) { $missing += $n } }
if ($missing.Count -gt 0) {
    Write-Output ("pre-existing tests REMOVED or renamed: " + ($missing -join ', '))
    Write-Output "re-baseline the assertions your change invalidates - never delete the coverage"
    exit 1
}

function Get-FactBody([string]$content, [string]$namePattern) {
    foreach ($chunk in ($content -split '\[Fact\]')) {
        if ($chunk -match ('public\s+void\s+' + $namePattern)) { return $chunk }
    }
    return $null
}

# SSOT 9 quarantines all flag spelling in this class, so AllowedTools_AreJoinedWithCommas is an
# EXACT-EQUALITY pin: re-baseline its expected string, do not weaken the assertion form.
$joined = Get-FactBody $c 'AllowedTools_AreJoinedWithCommas'
if ($null -eq $joined) { Write-Output "AllowedTools_AreJoinedWithCommas is no longer a [Fact]"; exit 1 }
if ($joined -notmatch 'Assert\.Equal\s*\(') {
    Write-Output "AllowedTools_AreJoinedWithCommas no longer uses Assert.Equal - the exact-equality pin was softened away (Assert.Contains is NOT a substitute; update the expected string instead)"
    exit 1
}

# The eighth test MAY be renamed - 'OmitsTheFlag' becomes inaccurate once the flag is always emitted -
# but a NoAllowedTools_* [Fact] must survive, re-pointed to the exact-equality form.
$noTools = Get-FactBody $c 'NoAllowedTools_\w+'
if ($null -eq $noTools) {
    Write-Output "no NoAllowedTools_* [Fact] survives - the empty-declared-list case lost its coverage entirely (a rename must KEEP the NoAllowedTools_ prefix)"
    exit 1
}
if ($noTools -match 'Assert\.DoesNotContain') {
    Write-Output "the NoAllowedTools_* test still asserts DoesNotContain - it was left pinning the OLD behaviour instead of being re-pointed to 'the flag IS emitted, with EXACTLY the injected grant'"
    exit 1
}
if ($noTools -notmatch 'Assert\.Equal\s*\(') {
    Write-Output "the NoAllowedTools_* test asserts no exact value - re-point it to assert the flag's value is EXACTLY the injected grant"
    exit 1
}
exit 0
