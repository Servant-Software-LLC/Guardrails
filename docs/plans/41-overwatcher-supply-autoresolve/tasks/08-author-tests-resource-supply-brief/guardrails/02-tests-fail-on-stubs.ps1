# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          over a string the test itself built). It PASSES against the NotImplementedException stub and
#          hides behind its genuinely-failing siblings, so a suite-level non-zero exit certifies the file
#          honest (#375). One entry per enumerated behaviour, each observed Failed in the runner's OWN
#          TRX - never stdout, never --list-tests, both of which a hollow body satisfies.
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this.
#
# DECLARED EXEMPTION (one row, with its reason - an exemption nobody can read is indistinguishable from
#          a row somebody quietly deleted):
#   TheGenericDiagnoseBriefIsUnchanged
#     STRUCTURAL REASON: it pins behaviour that must NOT change. The generic '# Overwatch diagnose:'
#     brief is shipped code this task does not touch, so a CORRECT test is GREEN on arrival; demanding
#     Failed would demand a correct implementation fail, and would force a test coupled to a stub instead
#     of to the never-weaker property. It asserts Expect='Executed' (it ran, and was not [Skip]ped) and
#     stays IN the manifest, because a dropped row and an oversight look identical.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Culture pin FIRST. This census reads the TRX (schema tokens, NOT localized) so the guard below does not
# depend on it - keep it anyway so the logged summary is readable and the pair stays copy-pasteable (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The class term is MANDATORY; the plan-wide trait is the optional conjunct (#455). A trait-only filter
# would go green off a SIBLING pair's red tests whether or not this pair's tests fail.
# 'OverwatchResourceSupplyBriefTests' was checked for discrimination against every other test class in
# the solution - notably the existing OverwatchSupplyAutoResolveTests, which it does not match.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~OverwatchResourceSupplyBriefTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed' (the default). A HASHTABLE declares the EXEMPTION above.
$manifest = [ordered]@{
    'the brief''s first line is the PINNED resource-supply heading'   = 'TheBriefsFirstLineIsThePinnedResourceSupplyHeading'
    'harness facts first; the agent question in an UNTRUSTED block'   = 'TheBriefStatesHarnessFactsFirst_AndDelimitsTheAgentsQuestionAsUntrusted'
    'every candidate is tabled with its source sha and branch'        = 'TheBriefTablesEveryCandidateWithItsSourceShaAndBranch'
    'ONLY the resource-supply fix vocabulary is offered'              = 'TheBriefOffersOnlyTheResourceSupplyFixVocabulary'
    'the missing-resource trigger token'                              = 'TheMissingResourceTriggerTokenIsMissingResource'
    # ADDED AT REVIEW. Design line 349 requires Overwatch.FixKindToken to map ResourceSupply to
    # "resource-supply" instead of today's `_ => "unknown"` (Overwatch.cs:722), and NO task in the plan
    # pinned it - measured, zero hits for FixKindToken across every prompt and guardrail. Left unpinned it
    # ships silently: the op records as "unknown" in overwatch.jsonl forever and nothing ever fails.
    # FixKindToken is PRIVATE (Overwatch.cs:716), so a test cannot call it - assert the OBSERVABLE instead,
    # the detail record's fixes[].Kind, which is built through that very method at Overwatch.cs:630.
    'the resource-supply FIX-KIND token (not "unknown")'              = 'TheResourceSupplyFixKindTokenIsResourceSupply'
    'an unparseable verdict is RECORDED as no-verdict, not silence'   = 'AnUnparseableVerdict_IsRecordedAsNoVerdict_NotSilence'
    'REAL SEAM (#382, bucket E): the real ClaudePromptRunner over a fake CLI process' =
        'ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict'
    # DECLARED EXEMPTION - see this file's header for why a correct implementation leaves it green.
    'the GENERIC diagnose brief is unchanged (never-weaker)'          = @{ Name = 'TheGenericDiagnoseBriefIsUnchanged'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr41-census-08-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

# No -v q: pointless here (nothing is re-emitted) and it propagates onto forward checks by cloning (#462).
$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
       --filter $filter --logger "trx;LogFileName=census.trx" --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

try {
    # PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
    # start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
    # The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
    # navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard
    # below evaluate 1 -lt 1 and NEVER FIRE.
    $xml      = [xml](Get-Content $trx.FullName -Raw)
    $recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, is malformed, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # ACCUMULATE: one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
    $failures = @()
    foreach ($behaviour in $manifest.Keys) {
        $entry  = $manifest[$behaviour]
        $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
        $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
        # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "$behaviour -> no test named '$name' ran (absent from the file, not carrying [Trait(""Category"", ""OverwatchSupply"")], or not selected by the filter)"
            continue
        }

        if ($expect -eq 'Executed') {
            # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
            # treated as not-executed - never let a missing value read as satisfied.
            $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
            if ($notRun.Count -gt 0) {
                $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct implementation leaves it green) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
            }
            continue
        }

        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$behaviour -> '$name' is $seen on the STUB tree, not Failed. A test that does not fail against the NotImplementedException stub never invokes the subject, so it asserts a tautology. Drive Overwatch.ProposeResourceSupplyAsync (or OverwatchTriggers.Token) and assert the outcome. ('NotExecuted' = [Fact(Skip=...)].)"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the stubs ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        exit 1
    }

    Write-Output "Red census: $($manifest.Count) enumerated behaviour(s) bound - all pinned rows observed Failed, the declared exemption observed Executed."
    exit 0
}
finally {
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue
}
