# catches: a `samples verify` verb that re-implements pair discovery, the two-way sample binding and the
#          mismatch classification INSIDE the CLI instead of driving the shared SampleVerifier - the
#          invariant guardrail 01's source grep was MEASURED (2026-08-29) to be unable to hold. TWO
#          mutation operators defeat that grep while the policy is inlined: a dead field
#          (`private static readonly SampleVerifier _unused = new SampleVerifier();`) and, surviving this
#          repo's TreatWarningsAsErrors=true because an unused private METHOD raises no diagnostic,
#          `private static object NeverCalled() => SampleVerifier.VerifyAsync(...);`. Guardrail 03's
#          reachability smoke misses it too - a faithful duplicate genuinely WORKS. Two operators green
#          on one source-shape check is the #468 demotion gate firing: the archetype was the finding, so
#          the invariant moved to an AGREEMENT PROPERTY TEST -
#              for a fixture corpus, the findings the VERB REPORTS == the findings
#              SampleVerifier.VerifyAsync RETURNS for that same corpus
#          - which an inlined duplicate passes while it is still faithful and FAILS the moment it drifts.
#          That is the only moment the rule matters, and a drifting duplicate is precisely the failure
#          this feature exists to detect (the verb and the preflight phase disagreeing about a pair).
#          No regex can express that property; guardrail 01 is retained only as the fast pre-filter.
#
# ALSO catches the census-shaped failure a bare `dotnet test` greens: an agreement test that was never
# written, was renamed, or is [Fact(Skip=...)]-ed out. `dotnet test` exits 0 for all three, so the exit
# code alone would certify a task that shipped the verb and none of its binding (#375).
#
# WHAT THIS DOES NOT PROVE, named rather than implied (#375 boundary): the census proves each pinned
# test RAN and PASSED. It cannot prove the assertion inside it is correct - an invoking-then-hollow body
# passes. The two structural clauses below raise that floor (the file must drive BOTH sides of the
# equality, so a body asserting nothing about either is visible) but they are a LOWER BOUND, not a proof.
# There is no stub tree to be red against here: task 03 authors and satisfies these tests in one task, so
# the #155/#375 red half is structurally unavailable. Human review owns the residual.
#
# BASELINE - MEASURED 2026-08-29, not assumed (#478). Every required-present clause below is 0 on the
# untouched tree, and by construction:
#   tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs   does not exist (CREATED by this
#     task; there is no `Commands/` directory under that project today), so all four required tokens
#     measure 0 in the only subject any of them scans.
#   'SamplesCommand' across ALL of src/ and tests/                       = 0
#     -> so #455's discriminating-substring test passes outright: no pre-existing class name contains
#        'SamplesCommandTests', and among the classes THIS plan authors neither 'SampleVerifierTests'
#        (task 01) nor 'SampleVerifierWiringTests' (task 04) contains it, nor is contained by it.
#   'BacklogSlate' across ALL of src/ and tests/                         = 0  (the plan-wide trait is new)
#   Positive control for those zeroes (#500): the same recursive -Force invocation over the same tree for
#     a literal known to be present, 'LockCliTests', returns 1 - so the search reached the test project
#     rather than silently skipping it. (A plain recursive search here is NOT self-evidently sound: this
#     repo keeps every skill under a dot-prefixed .claude/, which several default-configured tools skip.)
#   No ancestor can pre-satisfy any clause: tasks 01 and 02 write only under src/Guardrails.Core/Samples/
#     and tests/Guardrails.Core.Tests/Samples/, never into tests/Guardrails.Integration.Tests/.
#
# FORWARD polarity, so the ordering is exit-code-FIRST and the zero-match guard SECOND (#455): a test
# host that never started exits non-zero with no summary, and guard-first would misreport that as "your
# filter matched nothing" - sending the retry agent to rename a correctly-named class, the one artifact
# it is allowed to edit. NO -v q anywhere on the test command: it suppresses the whole
# Error Message/Expected/Actual/Stack Trace block, leaving only "[FAIL] <name>" for the #179 re-emit to
# find, which defeats the re-emit by the flag alone.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary is LOCALIZED; the TRX schema tokens are not

$testFile = 'tests/Guardrails.Integration.Tests/Commands/SamplesCommandTests.cs'
$proj     = 'tests/Guardrails.Integration.Tests'
$filter   = 'Category=BacklogSlate&FullyQualifiedName~SamplesCommandTests'

# THE MANIFEST: each behaviour -> the test method name this task's ACTION PROMPT PINNED for it.
# Cross-checked BY HAND against tasks/03-add-samples-verify-command/action.prompt.md ("The agreement
# test") - the prompt<->manifest agreement is NOT mechanically enforced, and a manifest token the prompt
# never names is a BLOCKER that fails every attempt (#157/GR2026).
$manifest = [ordered]@{
    'the verb REPORTS exactly what SampleVerifier RETURNS, on a corpus with findings' = 'Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACorpusThatProducesFindings'
    'the same equality on a CLEAN corpus (so "agree" is not "report everything")'     = 'Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACleanCorpus'
    'the exit code follows SampleVerifier''s verdict, not the verb''s own judgement'  = 'Verify_TheVerbsExitCode_FollowsSampleVerifiersVerdict_NotItsOwnJudgement'
}

# PRECONDITION - the only early exit before the clauses: everything below is meaningless without the file.
if (-not (Test-Path $testFile)) {
    Write-Output "$testFile does not exist - the agreement test that binds the verb to the shared SampleVerifier was never written. It is the THIRD deliverable of this task (see 'The agreement test' in the action prompt), not an optional extra: without it the only thing standing behind 'drive the shared verifier instead of re-implementing the policy' is a source grep that two one-line mutations defeat."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once, so ONE attempt learns every gap.
$failures = @()

# ── CHEAP STRUCTURAL CLAUSES — the test file drives BOTH sides of the equality ────────────────────────
# A LOWER BOUND, stated as such. An agreement test that never computes the reference side is not an
# agreement test, whatever it is named - and the census below cannot see that, because a test asserting
# nothing still PASSES. Anchored on the CALL, never a dotted name (#521): a clause ending at the name is
# satisfied by `nameof(SampleVerifier.VerifyAsync)`, which is valid C# and survives the string strip.
$raw  = Get-Content $testFile -Raw                           # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

if ($scan -cnotmatch '(new\s+SampleVerifier\b|\bSampleVerifier\s*\.\s*[A-Za-z_]\w*\s*\()') {
    $failures += "$testFile never CALLS SampleVerifier - so whatever it asserts, it is not the agreement property. The reference side of the equality has to be computed by running SampleVerifier.VerifyAsync on the same fixture corpus the verb is given; comparing the verb against a HAND-CODED list of expected findings would put a second copy of the policy in the test, which drifts exactly like the inlined implementation this test exists to forbid."
}
# Either house shape for driving the verb is accepted: through the real composition root (the
# LockCliTests idiom, which also proves registration) or through the command factory directly. Requiring
# only the first would false-red a legitimate test that drives SamplesCommand.Create(io) (#479).
if ($scan -cnotmatch '(\bCommandFactory\s*\.\s*BuildRootCommand\s*\(|\bSamplesCommand\s*\.\s*Create\s*\()') {
    $failures += "$testFile never drives the VERB - it calls neither CommandFactory.BuildRootCommand( nor SamplesCommand.Create(. An agreement test has to observe what the verb actually REPORTS, not just what SampleVerifier returns. Copy the house idiom in tests/Guardrails.Integration.Tests/LockCliTests.cs: a StringConsoleIo, CommandFactory.BuildRootCommand(io), then root.Parse([...]).InvokeAsync()."
}

# Dump the cheap clauses BEFORE paying for a build-and-test run (the one legitimate cost-stage split).
if ($failures.Count -gt 0) {
    Write-Output "=== the agreement test does not drive both sides of the equality: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

# ── THE COST STAGE — the pinned tests RUN and PASS ────────────────────────────────────────────────────
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-samplescmd-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test $proj --filter $filter --nologo `
       --logger 'trx;LogFileName=agreement.trx' --results-directory $resultsDir 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first, for the attempt's saved output

# EXIT CODE FIRST (#455 forward ordering), with the #179 re-emit so the WHY reaches the ~60-line tail the
# harness feeds back - default dotnet test prints the assertion text mid-run and ends with only
# "[FAIL] <name>" plus a count, so without this the next attempt sees WHAT failed and not WHY.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "SamplesCommandTests failing - the verb's report does not agree with SampleVerifier over the fixture corpus, which is what 'drive the shared verifier' now MEANS. Fix the verb, not the test: if the two genuinely disagree, one of them is a second implementation of the pair-verification policy."
    exit 1
}

# PRECONDITION - no TRX means the run never happened (host failed to start, wrong project path, or a
# malformed --filter, which exits 0 SILENTLY). Diagnose THAT, not the tests.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing also
# exits 0. The `| Where-Object { $_ }` is LOAD-BEARING: a run that executed zero tests emits NO <Results>
# element, so $xml.TestRun.Results.UnitTestResult is $null, and MEASURED on this box `@($null).Count` is
# **1** - i.e. the bare @(...) form makes this guard unable to fire, ever. Do not copy that form. An
# XmlElement is always truthy, so the filter can never drop a genuine result row.
$xml = [xml](Get-Content $trx.FullName -Raw)
$ran = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($ran.Count -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every match is [Skip]ped out of execution. Check it against the tests this task owns: class SamplesCommandTests, trait Category=BacklogSlate, in $testFile."
    exit 1
}

# PER-TEST CENSUS - a suite exit code of 0 is also what an ABSENT, RENAMED or [Skip]ped test produces, so
# bind every behaviour to its pinned name and require that name observed Passed in the runner's OWN TRX
# (never stdout, #248; never --list-tests name discovery, which a hollow body satisfies identically).
$census = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($ran | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $census += "$behaviour -> no test named '$name' ran (absent from $testFile, renamed, or not selected by the filter). The name is pinned in the action prompt precisely so this census can bind to it - rename the TEST, never this manifest."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $census += "$behaviour -> '$name' is $seen, not Passed. ('NotExecuted' = [Fact(Skip=...)] - a skipped agreement test certifies nothing and is not a way to make this guardrail green.)"
    }
}

if ($census.Count -gt 0) {
    Write-Output ""
    Write-Output "=== agreement-test census: $($census.Count) of $($manifest.Count) pinned behaviours did not run green ==="
    $census | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
