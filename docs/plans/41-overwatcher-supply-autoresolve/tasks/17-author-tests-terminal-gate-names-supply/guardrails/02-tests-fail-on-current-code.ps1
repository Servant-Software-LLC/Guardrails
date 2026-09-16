# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion
#          that never invokes the subject). dotnet test exits non-zero if ANY selected test fails, so a
#          hollow body hides behind its genuinely-failing siblings and the suite-level exit code
#          certifies the whole file honest (#375). One entry per enumerated behaviour, each observed
#          Failed in the runner's OWN TRX - never merely discovered by name, which a hollow body
#          satisfies just as well.
#
#          There is NO STUB FILE for this task: PlanGuardrailPhase.EvaluateAsync is public static and
#          UnauthoredContentNote is a shipped public static class. What is missing is the CALL, not a
#          type - the red is against CURRENT CODE, hence this file's name.
#
# does NOT catch: a test that can NEVER pass (#530). Red is this gate's success condition, so a test red
#          because no implementation could make it green reads here exactly like one red for the right
#          reason. The specific hazard on THIS task is a fixture that cannot run at all (see the
#          never-weaker row below, which is the two-sided control: it drives the SAME fixture through the
#          SAME seam and must come back GREEN, so a fixture broken outright cannot leave every row red
#          and still satisfy this census).
#
# DECLARED EXEMPTIONS (Expect='Executed' - the row RAN and was not [Skip]ped, rather than Failed). Each
# is a behaviour a CORRECT implementation leaves GREEN on the current tree, so demanding red would demand
# that a correct implementation fail. They stay IN the manifest: a dropped row and an oversight look
# identical to a reviewer (catalogue -> "The declared exemption").
#   FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt
#     STRUCTURAL REASON: this is the NEVER-WEAKER half of the decision, and it pulls in the OPPOSITE
#     direction from the four pinned rows. Design 41 §6 requires that "a run that supplied nothing keeps
#     a byte-identical halt", and UnauthoredContentNote.HeadlineSuffix returns null when both sections
#     are empty - so a CORRECT implementation appends nothing and this row is green on the base BY
#     CONSTRUCTION, not despite it. Demanding red here would demand that the disclosure fire on every
#     run, which is the exact defect the row exists to prevent. It is also the fixture's own
#     discriminator: it proves the harness under test actually runs and halts, so the four red rows are
#     red for the right reason.
#   PassingTerminalGate_AfterASupply_WritesNoHaltAtAll
#     STRUCTURAL REASON: a PASSING gate writes no halt record at all, on this tree and after the change.
#     The disclosure rides on the halt; this row pins that it never becomes an unconditional
#     announcement on a green run, and nothing about it can be red today.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
# it - kept anyway so the logged summary is readable and the pair stays copy-pasteable with task 18.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Class-scoped, never the bare plan-wide trait (#455). 'SuppliedTerminalGateHaltTests' is
# DISCRIMINATING: no other test class in either project contains it as a substring (the only other class
# carrying 'TerminalGate' at all is LogSite/TerminalGateVisibilityTests, which this does not match).
# SAME string as task 18's forward half - copy it verbatim, so the two halves cannot drift.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedTerminalGateHaltTests'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed' (the default). A HASHTABLE declares an EXEMPTION, reasons above.
$manifest = [ordered]@{
    'a failed terminal gate names the supplier and the 10-char sha in the halt headline' = 'FailedTerminalGate_AfterASupply_NamesTheSupplierAndShaInTheHaltHeadline'
    'it names a REFRESH too, not only a supply' = 'FailedTerminalGate_AfterARefresh_NamesTheRefreshInTheHaltHeadline'
    'one detail line per unauthored-content record' = 'FailedTerminalGate_WritesOneDetailLinePerUnauthoredRecord'
    'every record oldest-first ACROSS both sections' = 'FailedTerminalGate_ListsEveryRecordOldestFirst_AcrossBothSections'
    'NEVER-WEAKER: no unauthored content keeps a byte-identical halt' = @{ Name = 'FailedTerminalGate_WithNoUnauthoredContent_KeepsAByteIdenticalHalt'; Expect = 'Executed' }
    'a PASSING gate writes no halt at all, so the disclosure is not unconditional' = @{ Name = 'PassingTerminalGate_AfterASupply_WritesNoHaltAtAll'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr41-tg-red-census-" + [guid]::NewGuid().ToString('N'))
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
            # this census - and this file's header claimed the OPPOSITE ("a fixture broken outright cannot
            # leave every row red and still satisfy this census"), which was false until this change. It is
            # true now: the never-weaker row is REQUIRED Passed, so a fixture that cannot run at all reds
            # HERE, instead of handing task 18 a suite nothing can turn green.
            $notRun = @($hits | Where-Object { $_.outcome -ne 'Passed' })
            if ($notRun.Count -gt 0) {
                $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct implementation leaves it green) and its outcome was not 'Passed'. An exemption is green BY CONSTRUCTION, so it is REQUIRED green: 'NotExecuted' means [Fact(Skip=...)], and 'Failed' means the fixture is broken. An exempt row still has to run AND pass - and this particular row is also the control proving the fixture runs at all."
            }
            continue
        }

        $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
        if ($notRed.Count -gt 0) {
            $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $failures += "$behaviour -> '$name' is $seen on the CURRENT tree, not Failed. PlanGuardrailPhase.cs names UnauthoredContentNote NOWHERE today, so a terminal-gate halt cannot already carry the disclosure - a test that passes against it never drove the halt this task is about. Drive PlanGuardrailPhase.EvaluateAsync and read the halt back from run.json. ('NotExecuted' = [Fact(Skip=...)].)"
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
