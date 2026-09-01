# catches: a re-baseline that reached green by DELETING, SKIPPING or GUTTING a test instead of inverting
#          two assertions. Every one of those is invisible to guardrail 02: the census asserts that the
#          two rows it manifests are RED, and a deleted third test, a [Fact(Skip=...)] on a fourth, or a
#          removed sibling assertion inside a method that is still red are all consistent with a clean
#          census. It is also invisible to guardrail 01, which only asks whether the file compiles.
#
#          The specific loss this stage invites is named in section 15.1 and it is expensive:
#          `Assert.True(report.AllSucceeded, ...)` inside AStrayDsStoreMidRun_... is section 6.7's P16 -
#          "the only thing standing between the delivery gate and being muted within a week." An agent
#          rewriting the assertion three lines below it, under retry pressure, is one edit away from
#          taking it with them. The HashText rationale at :204-206 is the SSOT's own reasoning for why
#          the ignore list may never move into HashText, and section 15.1 requires it re-derived, not
#          deleted along with the assertion it used to justify.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is "no test in
#          this file was deleted, skipped or narrowed". A test cannot assert that about itself - a
#          deleted test asserts nothing, and a [Fact(Skip=...)] one asserts nothing while still being
#          counted by every runner-level instrument. The nearest runtime proxy would be an executed-test
#          COUNT, which dotnet.md forbids as an adequacy floor because a [Theory] row inflates it.
#          Genuinely unobservable at runtime, so this is the demotion order's last rung, and it ships
#          with a committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED
#          clauses read $code (comments gone, string literals INTACT - so an assertion MESSAGE can still
#          satisfy them, which matters because this file's assertions carry long messages). The comment
#          clause is the deliberate exception and reads $raw's comment span explicitly, because the
#          rationale it protects IS a comment.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against the real subject, with each
#          clause's own case sensitivity (#478):
#            ^\s*\[Fact(\]|\s*\()                          5   EXPECTED, and the invariant itself: this
#                                                               is a SHIPPED file this stage EDITS, so
#                                                               the count is 5 before and 5 after. A
#                                                               nonzero with a named reason.
#            Assert\.True\(report\.AllSucceeded              2   EXPECTED nonzero - a tests-untouched
#                                                               REGRESSION clause. One of the two (:77)
#                                                               becomes Assert.False in this stage; the
#                                                               other (:190) is P16 and must survive, so
#                                                               the floor below is 1, not 2.
#            Assert\.False\(report\.AllSucceeded             0   the clause this stage must satisfy.
#            HashText                                        2   EXPECTED nonzero - both occurrences are
#                                                               in the :204-205 rationale and nowhere
#                                                               else in the file.
#            enumerates                                      1   EXPECTED nonzero - :204, and the SECOND
#                                                               half of the rationale marker. One token
#                                                               alone is too weak: 'HashText' could be
#                                                               satisfied by an unrelated comment while
#                                                               the reasoning is deleted. Both markers are
#                                                               pinned VERBATIM in the action prompt, so
#                                                               guardrail and prompt agree by
#                                                               construction rather than by luck (#455's
#                                                               prompt-guardrail agreement rule applied
#                                                               to a coverage token). Both are SINGLE
#                                                               WORDS on purpose: a multi-word phrase is
#                                                               split by an innocent comment re-wrap, and
#                                                               a marker a correct rewrite can break by
#                                                               reflowing is a false red with no remedy.
#                                                               NOT keyed on 'ignore list', which appears
#                                                               twice - once at :199 in an unrelated
#                                                               comment - and so would survive the
#                                                               rationale's deletion.
#            (?i)\[Fact\s*\(\s*Skip                          0   forbidden-present; exempt from the
#                                                               baseline rule anyway (a ban green before
#                                                               its task has run is a healthy ban).
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the verifier runs
# this script with the sample path as argv[0] AND in $env:GR_SUBJECT. Without the override a sample run
# scans the real repo instead, both halves see the same untouched bytes, and BOTH exit the same way -
# the ValidHalfFailed / InvalidHalfPassed shape, whose own diagnosis is "the guardrail may not be
# reading the sample at all". Author-time smoke-testing that stages samples into the real paths does NOT
# exercise this; only `guardrails samples verify` does.
#   $env:GR_SUBJECT='<plan>/tasks/02-rebaseline-plan-edit-hash-assertions/samples/03-file-shape-preserved.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/02-rebaseline-plan-edit-hash-assertions/samples/03-file-shape-preserved.invalid.cs' -> expect 1
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
    $failures += "no Assert.False(report.AllSucceeded ...) call in the file. Section 15.1 row 2: a mid-run GUARDRAIL-SCRIPT edit is a real definition file, so the settle-time divergence gate fires and the run is no longer wholly green. AGuardrailEditedMidRun_...'s Assert.True at :77 becomes Assert.False."
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

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== file shape: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This stage inverts TWO assertion senses and rewrites ONE comment. It deletes nothing, skips nothing and renames nothing (the rename is stage 14's)."
    exit 1
}
Write-Output "File shape preserved: 5 facts, none skipped, P16's AllSucceeded assertion intact, row 2 inverted, HashText rationale still present."
exit 0
