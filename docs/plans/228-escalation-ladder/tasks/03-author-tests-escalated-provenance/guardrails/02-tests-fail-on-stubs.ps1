# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, any assertion that never reaches TierProvenance or
#          the journal serializer). It PASSES on this tree and hides behind its genuinely-failing
#          siblings, so a suite-level non-zero exit certifies the file honest while the DISCRIMINATOR -
#          the one test that separates a capability climb from an escalation - asserts nothing (#375).
#          One entry per enumerated behaviour, each observed in the runner's OWN TRX, never merely
#          discovered by name, which a hollow body satisfies exactly as a comment satisfies a token floor.
#
# ONE DECLARED EXEMPTION - a single row a CORRECT implementation leaves GREEN on this tree, so
# demanding red would demand a correct implementation fail. It asserts Expect='Executed' (it ran, and
# was not [Skip]ped). It is not dropped: an undeclared omission is indistinguishable from an oversight.
#
# NOT exempt, and this is the correction: 'SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated'
# is THE discriminator, and it is RED. Its negative half - a route with Climbed=true and
# EscalatedFrom=null maps to its ORIGIN-derived source, not to Escalated - is true today, which is
# exactly why it cannot stand alone: an Assert.True(true) body satisfies it. The action prompt
# therefore pins it to the MIRROR in the same test method - a route carrying EscalatedFrom="easy" AND
# Climbed=true must be Escalated and still report Climbed - and that half cannot pass until task 04
# adds the Escalated arm. The exemption was unnecessary: the test the prompt describes is red on this
# tree, so Expect='Failed' is the CORRECT reading of it and the census can see a hollow body. This is
# the trap the plan exists to avoid: Climbed is a CAPABILITY fact ("no runner served the requested
# rung"), escalation is "a previous attempt failed its guardrails", and only the mirror proves the two
# are independently readable.
#   * 'Provenance_WritesEscalatedFromOnlyWhenTheAttemptEscalated' covers a pure DATA member whose
#     declaration IS its implementation (Step 2 collapse criterion (c)) - task 03 declares
#     AttemptProvenance.EscalatedFrom with its WhenWritingNull attribute, so correct serialization
#     follows the moment the member exists. There is no stub-vs-real distinction to be red about.
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so unlike 4.3 the guard does
#          not depend on it - keep it anyway so the logged summary is readable.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=EscalationLadder&FullyQualifiedName~EscalatedProvenanceTests'   # SAME string as task 04's forward half

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
# A BARE STRING means Expect='Failed'. A HASHTABLE declares an EXEMPTION (Expect='Executed').
$manifest = [ordered]@{
    'an escalated route is sourced Escalated'                     = 'SourceFor_OnAnEscalatedRoute_IsEscalated'
    'the wire token for Escalated is escalated'                   = 'TierSourceToken_ForEscalated_IsTheEscalatedWireToken'
    'the journal round-trips the escalated token'                 = 'TierSourceConverter_RoundTripsEscalatedThroughTheJournal'
    # THE DISCRIMINATOR - RED, not exempt. Its negative half is true today; the MIRROR the prompt pins
    # into the same test method (EscalatedFrom="easy" AND Climbed=true must be Escalated) is not.
    'DISCRIMINATOR: a capability climb is NOT an escalation (mirror: an escalated climb IS)' = 'SourceFor_OnACapabilityClimbThatDidNotEscalate_IsNotEscalated'
    # THE ONE DECLARED EXEMPTION - a pure data member; the declaration IS the implementation.
    'escalatedFrom is written only when the attempt escalated'    = @{ Name = 'Provenance_WritesEscalatedFromOnlyWhenTheAttemptEscalated'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# No -v q: it is pointless here (nothing is re-emitted) and propagates onto forward checks by cloning (#462).
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT. Falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry
# agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form makes the guard below
# evaluate 1 -lt 1 and NEVER FIRE.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry   = $manifest[$behaviour]
    $name    = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect  = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct implementation leaves it green) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on this tree, not Failed. TierProvenance.SourceFor has no Escalated arm yet and JournalJson throws on an unhandled TierSource, so a test that does not fail here never reaches either - it asserts a tautology and certifies nothing. For the DISCRIMINATOR row specifically: its negative half (Climbed=true, EscalatedFrom=null must NOT be Escalated) is already true today, so it passes on its own - the action prompt pins the MIRROR into the SAME test method (a route with EscalatedFrom='easy' AND Climbed=true must be Escalated and still report Climbed), and that half is what makes the row red. Write both. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven on this tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
