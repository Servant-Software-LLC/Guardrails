# catches: a HOLLOW test - named for the pin, body a tautology (Assert.True(true), Assert.NotNull on a
#          value the test itself constructed, any assertion that never reaches the settle path). It
#          PASSES on today's tree and hides behind its genuinely-failing sibling, so a suite-level
#          non-zero exit certifies the file honest while proving nothing (#375). One entry per pin,
#          each observed Failed in the runner's OWN TRX - never merely discovered by name, which a
#          hollow body satisfies exactly as a comment satisfies a token floor.
#
#          It also catches the specific weakening this stage invites: P1 restated as "the recorded hash
#          is non-null" or "the recorded hash changed". Both are true with the defect fully intact, and
#          both are GREEN here - which is how the census tells them apart from the equality-against-a-
#          value-captured-before-the-edit the plan actually asks for.
#
# DECLARED EXEMPTIONS - P5 and P8, and the reason is structural rather than convenient:
#   P5 AnUneditedRun_RecordsAHashIdenticalToAPostRunRecompute is section 5.5's NO-OP property: with no
#      mid-run edit, today's settle-time recompute already equals a post-run recompute. A CORRECT test
#      is GREEN on today's tree, and demanding red would demand a correct implementation fail. Its job
#      is to STAY green after stages 3-5, because that is the pin proving no migration wave and no
#      repo-wide drift wave is owed (section 5.5).
#   P8 TaskDefinitionHashCompute_OutputHasNotMoved_OnAPinnedFixtureFolder is the same shape: this plan
#      changes WHEN the hash is computed and never WHAT it is computed over, so the byte-pin is true
#      before and must be true after. It is the tripwire on any later "simplification" of HashText or
#      TaskDefinitionFiles, both of which section 11 forbids this plan from touching at all.
#   Both assert Expect='Executed' (present in the TRX, not [Skip]ped). They stay IN the manifest: a
#   dropped row and an oversight look identical from the outside.
#
#   TWO OF FOUR EXEMPT is a high ratio and it is deliberate, not drift: section 5.8 lists two DEFECT
#   pins (P1, P14) and two REGRESSION pins (P5, P8) for this file, and a regression pin is green on
#   both sides by definition. If a later edit wants a THIRD exemption here, the census has become a
#   forward one wearing the red one's name - that is the signal to re-read section 5.8, not to add
#   another row.
#
# WHAT THIS CENSUS CANNOT SEE: it proves each test is COUPLED to the code path (it fails when the pin
#          is absent), never that its ASSERTION is correct. A test that reaches the settle path and
#          then asserts something hollow is red today, green after, and passes. P14 - the load-time vs
#          attempt-start discriminator - is the one where that residual matters most, because an
#          attempt-start capture is a plausible wrong implementation that passes every other pin in
#          this plan; it stays a human read.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized); keep the pin so the log stays readable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NAMESPACE-QUALIFIED, and it is not decoration (#455 companion (a)): stage 8's class
# 'WaveExecutedDefinitionHashTests' CONTAINS the substring 'ExecutedDefinitionHashTests', so a BARE class
# term would silently widen to select stage 8's file too once it lands. The FQN prefix is what breaks the
# containment - 'Guardrails.Core.Tests.WaveExecutedDefinitionHashTests' does NOT contain
# 'Guardrails.Core.Tests.ExecutedDefinitionHashTests', because the '.' separator sits between the prefix
# and the 'Wave'. Verified against every other class this plan authors and every existing class in the
# project.
#
# THE PREFIX IS 'Guardrails.Core.Tests', NOT 'Guardrails.Core.Tests.Journal' - and that is a CORRECTION
# made after a run halted here with a defective-guardrail escalation. The file lives in the Journal/
# FOLDER, but declaring `namespace Guardrails.Core.Tests.Journal` anywhere in this assembly introduces a
# `Journal` member under `Guardrails.Core.Tests`, which then WINS the enclosing-namespace walk over the
# production `Guardrails.Core.Journal` for every unqualified `Journal.X` reference in the assembly. Three
# files break with CS0234, all outside task 01's write scope: OverwatchNoVerdictTests.cs:355
# (`Journal.TaskStatus.Running`), the shared helper WavePlanBuilder.cs, and - the part that makes this
# unarguable - Journal/JudgeSpendRecordingTests.cs, the sibling task 01's prompt says to MIRROR, whose
# own header comment at :9-14 documents this exact hazard verbatim and names OverwatchNoVerdictTests.cs.
# Folder and namespace are deliberately decoupled in that folder; this filter follows the namespace.
$filter = 'FullyQualifiedName~Guardrails.Core.Tests.ExecutedDefinitionHashTests'

# THE MANIFEST: each pin -> the test method name the ACTION PROMPT PINNED for it. A BARE STRING means
# Expect='Failed'. A HASHTABLE declares an EXEMPTION (see the header for why each one is green on a
# correct implementation).
$manifest = [ordered]@{
    'P1  the recorded hash is the PRE-EDIT pin (serial, W1)'       = 'TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Serial'
    'P14 the pin is captured at LOAD, not at attempt start'        = 'TheRecordedHash_IsTheRunStartValue_WhenTaskJsonIsEditedBetweenAttempts'
    'P5  an unedited run records a byte-identical hash (no-op)'    = @{ Name = 'AnUneditedRun_RecordsAHashIdenticalToAPostRunRecompute'; Expect = 'Executed' }
    'P8  TaskDefinitionHash.Compute output has not moved'          = @{ Name = 'TaskDefinitionHashCompute_OutputHasNotMoved_OnAPinnedFixtureFolder'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr32-exec-hash-census-$PID"
# --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be read as
# THIS attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning a
# sibling file (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (test host failed to
# start, wrong project path, or a MALFORMED --filter, which exits 0 SILENTLY). Diagnose THAT; falling
# through would print "every pin unbound", a confident wrong message aimed at the one artifact a retry
# agent here IS allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
# NOTHING. The Where-Object is load-bearing: with zero tests executed the TRX has no <Results> element,
# the navigation yields $null, and @($null).Count is ONE - so the bare form would make the guard below
# evaluate 1 -lt 1 and never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound pin, so ONE attempt learns every gap.
$failures = @()
foreach ($pin in $manifest.Keys) {
    $entry  = $manifest[$pin]
    $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits a
    # [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$pin -> no test named '$name' ran (absent from the file, or not selected by the filter '$filter')"
        continue
    }

    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$pin -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - this file's header says why a correct test is green on today's tree) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all, and these two are the regression pins the whole no-migration claim rests on."
        }
        continue
    }

    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$pin -> '$name' is $seen on today's tree, not Failed. Today the settle stamps the POST-edit disk hash, so a correct pin MUST fail here: if it passes, the assertion never reached the settle path, or it was weakened to something the defect satisfies ('the hash is non-null', 'the hash changed'). Assert EQUALITY against the value captured BEFORE the edit. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) pins are not proven RED on today's tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Census clean: all $($manifest.Count) pins are bound to a pinned test with the declared outcome (2 Failed, 2 declared-exempt Executed)."
exit 0
