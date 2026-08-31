# catches: an implementation of the escalation-path salvage that does not actually preserve. Every
#          behaviour below was observed FAILED against today's code by task 01's red census; each must
#          now be observed PASSED against yours. It is the forward mirror of that census, per test,
#          read out of the runner's OWN TRX rather than off an exit code.
#
# WHY PER-TEST AND NOT A PLAIN tests-pass: pin I5 (the escalation Context names the ref and the patch)
#          is STAGE 3's deliverable, not yours - it will still be RED when you finish, so the suite
#          exit code is non-zero for a correct implementation of THIS task. A plain filtered
#          `tests-pass` would be unsatisfiable here: the task could not go green until a task that
#          DEPENDS on it had run, which is the #455 forward deadlock that validate and graph --check
#          both pass. Naming the eight behaviours this task owns is what makes the check both
#          satisfiable and precise. This script's exit code therefore deliberately IGNORES the suite
#          exit and reads outcomes only.
#
# I5 IS NOT SILENTLY DROPPED - it is stage 3's row in stage 3's guardrail, and the terminal gate runs
#          the whole suite on the merged HEAD. If you find yourself wanting to add it here, the honest
#          move is the opposite: it belongs downstream, and stage 3 asserts it.
$ErrorActionPreference = 'Continue'

# The census reads TRX schema tokens (not localized), but keep the pin so the logged output is
# readable and this file stays copy-pasteable beside its siblings (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests'
$filter  = 'FullyQualifiedName~EscalationSalvageTests'

# The eight behaviours THIS task owns. I5 is absent by design (see the header).
$manifest = [ordered]@{
    'I1 needsHuman after writing leaves a NON-EMPTY prior-attempt.patch' = 'NeedsHumanAfterWritingFiles_LeavesANonEmptyPriorAttemptPatch'
    'I2 ...and a salvage ref for that attempt'                           = 'NeedsHumanAfterWritingFiles_LeavesASalvageRefForTheAttempt'
    'I3 an OUT-OF-SCOPE write is ABSENT from the patch and the ref tree' = 'NeedsHumanWithAnOutOfScopeWrite_ThatWriteIsAbsentFromThePatchAndTheRefTree'
    'I4 a needsHuman on a FINAL attempt still preserves'                 = 'NeedsHumanOnTheFinalAttempt_StillPreserves'
    'I6 nothing written in scope => no patch, no ref, no section'        = 'NeedsHumanHavingWrittenNothingInScope_LeavesNoPatchNoRefAndNoSalvageSection'
    'I7 serial mode preserves nothing (unchanged)'                       = 'SerialMode_EscalationPathPreservesNothing'
    'I8 repeat escalations: refs capped but NOT empty'                   = 'RepeatEscalations_SalvageRefsAreCappedButNotEmpty'
    'I9 the salvage text says ORPHANED and never claims a rollback'      = 'NeedsHumanEscalation_SalvageTextSaysOrphanedAndNeverClaimsARollback'
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "gr31-preserve-$PID"
# --results-directory is NOT cleared between runs: a stale TRX from a previous attempt would be read
# as THIS attempt's evidence.
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

# NO -v q on a TEST command (#462/#179) - the log below is the only place a failing pin's WHY appears.
$out = & dotnet test $project --nologo --filter $filter `
       --logger 'trx;LogFileName=preserve.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the one legitimate early exit. No TRX means the run never happened (test host failed
# to start, wrong project path, or a MALFORMED --filter, which exits 0 SILENTLY). Diagnose THAT;
# falling through would print "every behaviour unbound", a confident wrong message aimed at the tests.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about your implementation."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is load-bearing: with zero tests executed the TRX carries no <Results> element, the
# navigation yields $null, and @($null).Count is ONE - so the bare form would evaluate 1 -lt 1 and the
# guard below would never fire.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This guardrail certified nothing."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unmet behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not. The (\(|$) tail admits
    # a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran. It is task 01's deliverable and outside your writeScope: if it is genuinely missing, that is a needsHuman, not something to write yourself."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen   = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $detail = (($notGreen | ForEach-Object { $_.Output.ErrorInfo.Message } | Where-Object { $_ } | Select-Object -First 1) -join ' ')
        $failures += "$behaviour -> '$name' is $seen, not Passed. $detail"
    }
}

Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== escalation-preserve: $($failures.Count) of $($manifest.Count) owned behaviours are not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Fix the implementation in your four files. Do NOT edit the tests (outside your writeScope), and do NOT reach for SegmentStaging.cs - the scope filter is a post-stage 'git reset', not a change to the add pathspec (plan 31 section 3.3, and this task's prompt says why)."
    exit 1
}
Write-Output "Escalation preserve green: all $($manifest.Count) owned behaviours Passed. (I5 - the escalation Context - is stage 3's and is expected to still be red here.)"
exit 0
