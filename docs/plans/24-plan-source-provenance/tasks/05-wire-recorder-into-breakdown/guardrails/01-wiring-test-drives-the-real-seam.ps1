# catches: a wiring test that INJECTS the seam it claims to verify - one that constructs a
#          PlanSourceRecord (or writes state/plan-source.json) itself and then asserts the file exists.
#          That test is green whether or not InitialBreakdownInvoker.PrepareInvocation was ever changed,
#          which is the unwired-factory failure with extra steps (#120): the feature stays dead from the
#          CLI while the suite reports success. It also catches a wiring test that quietly drops the
#          --fresh survival assertion, which is the one property of this design a later refactor breaks
#          silently (plan of record section 3).
#          This task authors its OWN test with no TDD-red half to prove it, so nothing else in the plan
#          can tell a real composition-root test from a hollow one.
#
# Source-shape check over CODE, and it is NOT demotable to a test (#468): no test can assert what
# ANOTHER test's body does. The property is structural about the test file itself.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/samples/01-wiring-test-drives-the-real-seam.valid.cs';   ./01-...ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/24-plan-source-provenance/tasks/05-wire-recorder-into-breakdown/samples/01-wiring-test-drives-the-real-seam.invalid.cs'; ./01-...ps1  # expect 1
#
# baseline counts on the untouched tree - MEASURED, not assumed:
#   InitialBreakdownInvoker\s*\.\s*PrepareInvocation\s*\(   n/a - file created by this task
#   \bRunReset\s*\.\s*Fresh\s*\(                            n/a - file created by this task
#   (the forbidden self-write clause is NOT censused: a ban green on arrival is a correct ban, #478)
#   No ancestor task's prompt or writeScope writes these tokens into this subject - tasks 02 and 04
#   write only src/Guardrails.Core/Breakdown/*.cs, and this test file is created here.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Core.Tests/PlanSource/PlanSourceWiringTests.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - this task's composition-root test was never written"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments

# DELIBERATE DEVIATION from the $scan rule for the forbidden clause below (dotnet.md 11a), stated so a
# reviewer can re-decide it: the forbidden token is a FILE NAME, so it can only ever appear inside a
# string literal. Stripping literals would make the ban unfirable - the mirror dead-end 11a warns about,
# wearing the other polarity. The clause is kept narrow instead: it fires only when the name appears
# INSIDE a write call's own argument list (no statement separator between them), which no legitimate
# test does - a test writing its plan fixture with File.WriteAllText never passes plan-source.json to it.
$failures = @()

# ANCHORED ON THE CALL, NOT THE NAME (#76 / review 2026-08-29, issue #521). The trailing `\s*\(` is the
# whole rule and it is the half that gets dropped: the earlier form matched the dotted NAME, and
# `nameof(InitialBreakdownInvoker.PrepareInvocation)` is valid C# containing that exact text. MEASURED -
# a mutant whose only references were inside nameof() with ZERO invocations exited 0 against the old
# clause. Both members are METHODS (`RunReset.Fresh(string)`, `InitialBreakdownInvoker.PrepareInvocation(
# ... )`) and every existing test calls them in the `Type.Member(` shape, so requiring the paren cannot
# false-red a correct test. Do NOT relax this back to a bare dotted name.
if ($code -cnotmatch 'InitialBreakdownInvoker\s*\.\s*PrepareInvocation\s*\(') {
    $failures += "$f never CALLS InitialBreakdownInvoker.PrepareInvocation - it does not drive the production entry point, so it cannot prove the recorder is wired into it (a test that builds the record itself passes against an unwired PrepareInvocation). A mention is not a call: nameof(InitialBreakdownInvoker.PrepareInvocation), a comment, or the method's own name in a test name does NOT satisfy this - invoke it."
}

if ($code -cnotmatch '\bRunReset\s*\.\s*Fresh\s*\(') {
    $failures += "$f never CALLS RunReset.Fresh - the plan requires a test proving state/plan-source.json SURVIVES --fresh (plan of record section 7), and that survival is the property a later refactor breaks silently. A mention is not a call: nameof(RunReset.Fresh) does NOT satisfy this - invoke it."
}

if ($code -cmatch '(File\.(?:WriteAll\w+|AppendAll\w+|Copy)|new\s+StreamWriter)\s*\([^;]{0,200}plan-source\.json') {
    $failures += "$f writes plan-source.json ITSELF and then asserts about it - that proves nothing about PrepareInvocation. Let the production call write it; assert on what the production call produced. (Writing the plan.md FIXTURE with File.WriteAllText is expected and does not trip this.)"
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
