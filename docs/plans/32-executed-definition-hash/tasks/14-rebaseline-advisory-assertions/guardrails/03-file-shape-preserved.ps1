# catches: the SECOND re-baseline reaching green by DELETING, SKIPPING or GUTTING a test - and one thing
#          stage 2's version could not yet watch for: the RENAME taking a whole method with it.
#
#          Guardrail 02's census sees none of this. It asserts that the two rows it manifests are RED, and
#          a deleted third test, a [Fact(Skip=...)] on a fourth, or a removed sibling assertion inside a
#          method that is still red are all consistent with a clean census. Guardrail 01 only asks whether
#          the file compiles.
#
#          The specific loss this stage invites is the same one section 15.1 names, and by this point it
#          is even more expensive than it was at stage 2: the tripwire assertion has now survived TWELVE
#          stages, stage 13 has just landed the delivery gate, and AStrayDsStoreMidRun_'s
#          Assert.True(report.AllSucceeded, ...) is the only artifact in the plan that proves the gate
#          stayed QUIET on an editor artifact. An agent rewriting an assertion in a neighbouring method,
#          under retry pressure, is one edit away from taking it. Section 6.7 calls it "the only thing
#          standing between the delivery gate and being muted within a week."
#
#          This stage also RENAMES one method - ARunCarryingOnlyAPlanEditObservation_FastForwardsAnd
#          ExitsZero, whose name stops describing its behaviour once the run halts at exit 2 and does not
#          deliver. A rename and a deletion look identical to every runner-level instrument, so the new
#          name is required present here and the old one required ABSENT: half a rename leaves two methods
#          where there was one, which the fact count catches, or none, which nothing else does.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is "no test in
#          this file was deleted, skipped or narrowed". A test cannot assert that about itself - a deleted
#          test asserts nothing, and a [Fact(Skip=...)] one asserts nothing while still being counted by
#          every runner-level instrument. The nearest runtime proxy is an executed-test COUNT, which
#          dotnet.md forbids as an adequacy floor because a [Theory] row inflates it. Genuinely
#          unobservable at runtime; demotion order's last rung, and it ships with a committed
#          .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code (comments gone, string literals INTACT - this file's assertions carry long messages); the
#          rationale clause is the deliberate exception and reads $raw, because what it protects IS a
#          comment.
#
# MEASURED BASELINES against the SHIPPED file on design/32-executed-definition-hash @1f6d54c, with each
#          clause's own case sensitivity (#478). Note this stage runs on a tree where stage 2 has already
#          landed, so two of these are stated as they will be THEN and marked accordingly:
#            ^\s*\[Fact(\]|\s*\()                          5   EXPECTED, and the invariant itself: a
#                                                               SHIPPED file this stage edits, so the
#                                                               count is 5 before and 5 after. Nonzero
#                                                               with a named reason.
#            Assert\.True\(report\.AllSucceeded              2 shipped / 1 after stage 2   EXPECTED
#                                                               nonzero - a tests-untouched REGRESSION
#                                                               clause. Stage 2 inverted one of the two;
#                                                               the survivor is P16 and the floor is 1.
#            HashText                                        2   EXPECTED nonzero - the :204-205
#                                                               rationale, nowhere else in the file.
#            enumerates                                      1   EXPECTED nonzero - :204, the second half
#                                                               of the same marker. Both are SINGLE WORDS
#                                                               so a comment re-wrap cannot split them,
#                                                               and both are pinned verbatim in stage 2's
#                                                               and this stage's action prompts.
#            HaltsWithExitTwoAndDoesNotDeliver               0   this stage's rename target.
#            FastForwardsAndExitsZero                        1 shipped   EXPECTED nonzero - a
#                                                               forbidden-present clause whose baseline is
#                                                               the BEFORE-state it exists to remove.
#            (?i)\[Fact\s*\(\s*Skip                          0   forbidden-present.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. Without the override a sample run
# scans the real repo instead, both halves see the same untouched bytes, and BOTH exit the same way -
# the ValidHalfFailed / InvalidHalfPassed shape, whose own diagnosis is "the guardrail may not be
# reading the sample at all". Author-time smoke-testing that stages samples into the real paths does NOT
# exercise this; only `guardrails samples verify` does.
#   $env:GR_SUBJECT='<plan>/tasks/14-rebaseline-advisory-assertions/samples/03-file-shape-preserved.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/14-rebaseline-advisory-assertions/samples/03-file-shape-preserved.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Integration.Tests/PlanEditedDuringRunTests.cs' }
# GR_SUBJECT arrives ABSOLUTE; joining it to the workspace would yield a nonsense path and
# PRECONDITION-fail, which reads exactly like a real finding.
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this stage edits in place - if it is gone, it was deleted rather than re-baselined, which section 15.1 forbids outright."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- 1. No test was deleted or added ---------------------------------------------------------------
# '\[Fact(\]|\s*\()' - NOT '\[Fact\]'. Measured: with the bare form a [Fact(Skip = "...")] drops OUT of
# the count, so the skipped-test case trips THIS clause as well as clause 2 and the operator is told the
# file lost a test when it did not. Counting both spellings keeps each clause's diagnosis its own.
$facts = [regex]::Matches($code, '(?m)^\s*\[Fact(\]|\s*\()').Count
if ($facts -ne 5) {
    $failures += "the file declares $facts [Fact] method(s); it shipped with 5 and this stage inverts two ASSERTIONS, not the test set. Section 15.1: 'What stages 2 and 14 must NOT do: delete a test, mark one skipped, or narrow it to its passing half.' If you believe a sixth test is genuinely needed, that is a plan change - escalate with needsHuman rather than moving this number."
}

# --- 2. No test was skipped out of execution -------------------------------------------------------
# Read $code, not $raw: a COMMENT explaining why nothing is skipped must not trip a fail-on-present ban
# (#97/#98). Anchored on the ATTRIBUTE form, not the bare word 'Skip', so an assertion message mentioning
# skipping is invisible to it (#76 - a use, not a mention).
if ($code -cmatch '\[Fact\s*\(\s*Skip') {
    $failures += "a [Fact(Skip = ...)] appears in the file. A skipped test is counted by every runner-level instrument and asserts nothing - it is a deleted test that still shows up in the census. Fix the assertion instead."
}

# --- 3. P16 survives: the stray-artifact run is still asserted GREEN --------------------------------
# THIS IS THE ONE THAT MATTERS MOST. Floor of 1, not 2: :77's Assert.True legitimately becomes
# Assert.False in this stage, so exactly one Assert.True(report.AllSucceeded should remain - :190, the
# P16 assertion section 15.1 calls 'the one assertion that must NOT move'.
$p16 = [regex]::Matches($code, 'Assert\.True\(\s*report\.AllSucceeded').Count
if ($p16 -lt 1) {
    $failures += "no Assert.True(report.AllSucceeded ...) call remains in the file. AStrayDsStoreMidRun_...'s assertion that a mid-run stray .DS_Store leaves the run GREEN AND DELIVERING is P16 (section 6.7) - the plan calls it 'the only thing standing between the delivery gate and being muted within a week', and section 15.1 calls it 'the one assertion that must NOT move'. Row 2's inversion is at :77, in AGuardrailEditedMidRun_...; you inverted the wrong one, or took both."
}

# --- 4. Row 2 actually inverted --------------------------------------------------------------------
if ([regex]::Matches($code, 'Assert\.False\(\s*report\.AllSucceeded').Count -lt 1) {
    $failures += "no Assert.False(report.AllSucceeded ...) call in the file. That is stage 2 row 2 - a mid-run GUARDRAIL-SCRIPT edit is a real definition file, so the gate fires and the run is no longer wholly green. It landed twelve stages ago and this stage must not undo it."
}

# --- 5. The HashText rationale was re-derived, not deleted -----------------------------------------
# Deliberately reads $raw, not $code: the thing being protected IS a comment, so stripping comments here
# would make the clause unsatisfiable by construction. TWO markers, both pinned VERBATIM in the action
# prompt so a correct rewrite cannot miss them and this clause cannot false-red: 'HashText' (measured 2,
# both inside the :204-205 rationale) and 'enumerates' (measured 1, at :204). One alone is too weak - an
# unrelated comment could carry 'HashText' while the reasoning is gone. Both are SINGLE WORDS so an
# innocent comment re-wrap cannot split them. NOT keyed on
# 'ignore list', which appears twice, once at :199 in an unrelated comment, and so would survive the
# rationale's deletion.
foreach ($marker in @('HashText', 'enumerates')) {
    if ($raw -cnotmatch [regex]::Escape($marker)) {
        $failures += "the file no longer contains the phrase '$marker'. The comment at :204-206 is not decoration - it is the SSOT's reasoning for why the ignore list may NEVER move into HashText (doing so would move every recorded definition hash in every plan and turn the next resume of each into a drift halt). Section 15.1 requires it RE-DERIVED alongside the filtered-gate consequence, never deleted with the assertion it used to justify. Your action prompt pins both phrases verbatim - keep them."
    }
}

# --- 6. The rename landed, whole ---------------------------------------------------------------------
# A rename and a deletion are indistinguishable to every runner-level instrument, so BOTH halves are
# asserted: the new name present, the old one gone. Half a rename leaves two methods where there was one
# (which clause 1 catches) or none (which nothing else does).
if ($code -cnotmatch 'ARunCarryingOnlyAPlanEditObservation_HaltsWithExitTwoAndDoesNotDeliver') {
    $failures += "the renamed method ARunCarryingOnlyAPlanEditObservation_HaltsWithExitTwoAndDoesNotDeliver is absent. Section 15.1 renames it because its old name - FastForwardsAndExitsZero - stops describing its behaviour the moment the run halts at exit 2 and does not deliver, and a method whose name contradicts its assertions is the same half-true-message failure this plan exists to remove, one level down. Guardrail 02's census filters on the NEW name."
}
if ($code -cmatch 'ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero') {
    $failures += "the OLD method name ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero is still present. Either the rename did not happen, or it was copied rather than renamed and the file now carries both - which clause 1's fact count would also catch, but this names it directly."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== file shape: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This stage inverts THREE assertions across two methods and renames ONE of them. It deletes nothing, skips nothing, and does not touch AStrayDsStoreMidRun_ - whose AllSucceeded assertion has now survived twelve stages and is what proves the delivery gate stayed quiet on an editor artifact."
    exit 1
}
Write-Output "File shape preserved: 5 facts, none skipped, P16 AllSucceeded assertion intact, the rename complete, and the HashText rationale still present."
exit 0
