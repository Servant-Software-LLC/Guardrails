# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          that never invokes the subject). dotnet test exits non-zero if ANY selected test fails, so a
#          hollow body hides behind its genuinely-failing siblings and the suite-level exit code
#          certifies the whole file honest (#375). One entry per enumerated behaviour, each observed
#          Failed in the runner's OWN TRX - never merely discovered by name, which a hollow body
#          satisfies just as well.
#
#          There is NO STUB FILE for this task: every member the tests drive
#          (RunCommand.RenderNeedsHumanSections / RenderUndeliveredWorkWarning / Create) is already
#          public and shipped. The red is therefore against CURRENT CODE, not against a
#          NotImplementedException tree - hence this file's name.
#
# does NOT catch: a test that can NEVER pass (#530). Red is this gate's success condition, so a test red
#          because no implementation could make it green reads here exactly like one red for the right
#          reason, and the bill arrives on task 16 as a needs-human halt after its retry budget is spent
#          on production code that was already correct. What covers that is the never-weaker row below
#          plus the paired forward census in task 16.
#
# DECLARED EXEMPTIONS (Expect='Executed' - the row RAN and was not [Skip]ped, rather than Failed). Each
# is a behaviour a CORRECT implementation leaves GREEN on the current tree, so demanding red would demand
# that a correct implementation fail. They stay IN the manifest: a dropped row and an oversight look
# identical to a reviewer (catalogue -> "The declared exemption").
#   MissingResourceHalt_ForASinglePlainPath_IsUnchanged
#     STRUCTURAL REASON: it is the NEVER-WEAKER floor. It asserts that an ordinary single-path question
#     renders the SAME three commands it renders today. That is true of the current tree by definition -
#     the row exists to stop task 16 "fixing" the halt text into something new while widening the match.
#   MissingResourceHalt_NamesTheAsymmetry
#   MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence
#   BlockedWorkHalt_AboutAnOverScopedTask_KeepsTheOriginalClosingLine
#   DefectiveGuardrailHalt_MentioningAFile_DoesNotGetTheAsymmetryOrSequence
#     STRUCTURAL REASON: all four are SHIPPED tests (design 40 task 25) that already pass. Design 41 §11
#     row 9 makes "SuppliedHaltTextTests stay green" an acceptance condition of task 16 in those words,
#     so they must keep passing throughout - the opposite of red.
#   UndeliveredWorkBanner_NamesTheAutoSuppliedDecisionAndItsTask
#     STRUCTURAL REASON: the #597 banner already names the suppressing decision by INTERPOLATING
#     suppressing.Decision / suppressing.Subject rather than enumerating tokens, so it names an
#     'auto-supplied' entry correctly on the current tree. The row exists to keep that true once
#     auto-supplied joins the token set (design §6 "Where it shows"), not to be turned red.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
# it - kept anyway so the logged summary is readable and the pair stays copy-pasteable with 03/task 16.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Class-scoped, never the bare plan-wide trait (#455). 'SuppliedHaltTextTests' is DISCRIMINATING: no other
# test class in either project has it as a prefix (the nearest siblings are SuppliedBoundaryWiringTests,
# SuppliedObserverCliForwardingTests, SuppliedProvenanceTests and SuppliedDrainTests).
# SAME string as task 16's forward half - copy it verbatim, so the two halves cannot drift.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedHaltTextTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed' (the default). A HASHTABLE declares an EXEMPTION, reasons above.
$manifest = [ordered]@{
    'names EVERY path the question names, not only the first' = 'MissingResourceHalt_NamesEveryPathTheQuestionNames'
    'normalizes a leading ./ so a root-level file is supplied by its bare path' = 'MissingResourceHalt_NormalizesALeadingDotSlash_ForARootLevelFile'
    'matches a scoped node_modules path WHOLE (@ is a legal segment character)' = 'MissingResourceHalt_MatchesAScopedNodeModulesPath_Whole'
    'the interlock banner does not call a machine decision a best-guess' = 'UndeliveredWorkBanner_ForAMachineDecision_DoesNotCallItABestGuess'
    '--merge-on-success names a machine decision, not only the two tokens' = 'MergeOnSuccessOption_DescribesAMachineDecision_WithoutEnumeratingOnlyTheTwoTokens'
    'NEVER-WEAKER: a single plain path renders the same three commands as today' = @{ Name = 'MissingResourceHalt_ForASinglePlainPath_IsUnchanged'; Expect = 'Executed' }
    'shipped: the halt names the plan-folder/code-artifact asymmetry' = @{ Name = 'MissingResourceHalt_NamesTheAsymmetry'; Expect = 'Executed' }
    'shipped: the copy-pasteable supply/reset/run sequence' = @{ Name = 'MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence'; Expect = 'Executed' }
    'shipped negative control: an over-scoped blocked-work halt is untouched' = @{ Name = 'BlockedWorkHalt_AboutAnOverScopedTask_KeepsTheOriginalClosingLine'; Expect = 'Executed' }
    'shipped negative control: a defective-guardrail halt naming a file is untouched' = @{ Name = 'DefectiveGuardrailHalt_MentioningAFile_DoesNotGetTheAsymmetryOrSequence'; Expect = 'Executed' }
    'the banner names the auto-supplied entry and its task' = @{ Name = 'UndeliveredWorkBanner_NamesTheAutoSuppliedDecisionAndItsTask'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr41-red-census-" + [guid]::NewGuid().ToString('N'))
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

try {
    # No -v q: pointless here (nothing is re-emitted on an inverse check) and it propagates onto forward
    # checks by cloning (#462).
    $out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter $filter --logger "trx;LogFileName=census.trx" --results-directory $resultsDir 2>&1 | Out-String
    Write-Output $out

    # PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
    # start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Falling through would
    # print "every behaviour unbound" - a confident wrong message aimed at the one artifact the retry
    # agent is allowed to edit.
    $trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
    # The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
    # navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard
    # below evaluate 1 -lt 1 and NEVER FIRE.
    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $recorded = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($recorded.Count -lt 1) {
        Write-Output "PRECONDITION: the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
        exit 1
    }

    # ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
    $failures = @()
    foreach ($behaviour in $manifest.Keys) {
        $entry  = $manifest[$behaviour]
        $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
        $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }

        # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits
        # a [Theory] row's appended data without admitting a longer sibling name.
        $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
        $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
        if ($hits.Count -lt 1) {
            $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter). The action prompt PINS this behaviour to that exact method name."
            continue
        }

        if ($expect -eq 'Executed') {
            # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
            # treated as not-executed - never let a missing value read as satisfied.
            # REVIEW FIX: a declared exemption is green BY CONSTRUCTION, so it must be REQUIRED green. The
            # first spelling rejected only NotExecuted/empty, so an exempt row that came back FAILED passed
            # this census. Five of this manifest's six exemptions are SHIPPED tests that pass today, so a
            # change that breaks them has to be caught here - a broken fixture must not read as red-for-
            # the-right-reason and hand task 16 the bill.
            $notRun = @($hits | Where-Object { $_.outcome -ne 'Passed' })
            if ($notRun.Count -gt 0) {
                $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct implementation leaves it green) and its outcome was not 'Passed'. An exemption is green BY CONSTRUCTION, so it is REQUIRED green: 'NotExecuted' means [Fact(Skip=...)], and 'Failed' means the fixture is broken. An exempt row still has to run AND pass; anything else turns the exemption into no coverage at all."
            }
            continue
        }

        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. This behaviour does not exist yet (design 41 §2.1/§6), so a test that passes against it never invokes the changed code path - it asserts a tautology. Drive the real render seam and assert the outcome. ('NotExecuted' = [Fact(Skip=...)].)"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the current tree ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        exit 1
    }

    Write-Output "Red census: all $($manifest.Count) enumerated behaviour(s) bound - the pinned rows observed Failed, the declared exemptions observed Executed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $resultsDir -ErrorAction SilentlyContinue
}
