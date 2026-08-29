# catches: a wiring test that INJECTS the seam it claims to verify - one that constructs SampleVerifier
#          itself, runs it over a fixture, and asserts on ITS findings. That test is green whether or not
#          PlanPreflightPhase was ever changed, which is the unwired-factory failure with extra steps
#          (#120): a bad sample pair still costs a full run's tokens while the suite reports success.
#          This task authors its OWN test with no TDD-red half to prove it, so nothing else in the plan
#          can tell a real composition-root test from a hollow one.
#
# Source-shape check over CODE, and it is NOT demotable to a test (#468): no test can assert what
# ANOTHER test's body does. The property is structural about the test file itself. The runtime half -
# that the phase actually halts, including on a plan with no preflights/ folder - is carried by
# guardrail 03's per-test census, so nothing is greped here that a test could prove.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/26-guardrail-quality-gate/tasks/04-wire-verifier-into-preflight/samples/01-wiring-test-drives-the-real-phase.valid.cs';   ./01-...ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/26-guardrail-quality-gate/tasks/04-wire-verifier-into-preflight/samples/01-wiring-test-drives-the-real-phase.invalid.cs'; ./01-...ps1  # expect 1
#
# baseline counts on the untouched tree - MEASURED, not assumed:
#   PlanPreflightPhase\s*\.\s*EvaluateAsync\s*\(   n/a - this test file is CREATED by this task.
#   (the SampleVerifier ban is NOT censused: a ban green on arrival is a correct ban, #478 - and it was
#    measured green, since "SampleVerifier" occurs ZERO times across src/ and tests/ on this tree.)
#   No ancestor task's prompt or writeScope writes these tokens into this subject: tasks 01/02 write
#   only under src/Guardrails.Core/Samples/ and tests/Guardrails.Core.Tests/Samples/, task 03 only
#   under src/Guardrails.Cli/. This file is created here.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Integration.Tests/Samples/SampleVerifierWiringTests.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - this task's composition-root test was never written"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end, so ONE attempt
# learns every gap instead of discovering them one retry at a time.
$failures = @()

# ANCHORED ON THE CALL, NOT THE NAME (issue #521, measured 2026-08-28). A clause ending at the dotted
# NAME is satisfied by `nameof(PlanPreflightPhase.EvaluateAsync)` - valid C# containing that exact text,
# which survives the $scan strip because nameof is not a string literal - and a hollow test whose only
# references were two dead nameof() expressions, with ZERO invocations, was MEASURED to exit 0 against
# exactly that shape. The trailing `\s*\(` is the whole rule. EvaluateAsync is a METHOD
# (`public static Task<bool> EvaluateAsync(...)` in src/Guardrails.Cli/PlanPreflightPhase.cs), and every
# existing caller - RunCommand.cs, Revalidate.cs, PlanPreflightPhaseTests - writes it in the
# `Type.Member(` shape, so requiring the paren cannot false-red a correct test. Do NOT relax this back
# to a bare dotted name, and do NOT add a nameof BAN: requiring the CALL already kills the operator, and
# a ban would false-red a legitimate nameof() inside an assertion message.
if ($scan -cnotmatch 'PlanPreflightPhase\s*\.\s*EvaluateAsync\s*\(') {
    $failures += "$f never CALLS PlanPreflightPhase.EvaluateAsync - it does not drive the production pre-DAG phase, so it cannot prove the sample verifier is wired into it (a test that runs SampleVerifier itself passes against a phase that was never changed). A mention is not a call: nameof(PlanPreflightPhase.EvaluateAsync), a comment, or the method's name inside a test name does NOT satisfy this - invoke it."
}

# THE SEAM-INJECTION BAN. Read from $scan so a mention in a comment or an assertion message cannot trip
# it; anchored on a USE (construction or an INVOKED member) so a `using Guardrails.Core.Samples;` and a
# type name in prose are both fine. Writing the plan FIXTURE - guardrails.json, the task folder, the
# guardrail script, the two sample halves - is expected and does not trip this: none of it names the
# verifier.
if ($scan -cmatch '(new\s+SampleVerifier\b|\bSampleVerifier\s*\.\s*[A-Za-z_]\w*\s*\()') {
    $failures += "$f runs SampleVerifier ITSELF and then asserts about its findings - that proves nothing about PlanPreflightPhase, which is what has to call it. The test is green whether or not the phase was ever wired (#120). Let the PHASE run the verifier; assert on what EvaluateAsync returned and on what it journaled. Building the plan FIXTURE by hand is expected and does not trip this clause."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
