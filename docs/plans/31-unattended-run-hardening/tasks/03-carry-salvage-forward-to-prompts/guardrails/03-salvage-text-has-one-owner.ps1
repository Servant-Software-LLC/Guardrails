# catches: PromptComposer re-authoring the salvage routing prose instead of calling the ONE owner of
#          it. Plan section 3.3's seam row is explicit - "the same owner, never a second copy" - and stage 2
#          opened AppendSalvageSection from private to internal for exactly this call site. A second
#          copy that is equivalent TODAY passes every behavioural pin (C1-C3 assert the composed bytes,
#          and a faithful copy produces the same bytes) and fails the moment either copy drifts, which
#          is the only moment the rule matters. The drift is silent: one copy gets the #382 correction,
#          the other keeps handing an agent a git verb the harness does not grant.
#
# WHY THIS IS A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked):
#          The ideal form is an AGREEMENT property test - "PromptComposer's output for a
#          patch-carrying prior equals AppendSalvageSection's output for the same input". It is
#          writable in principle (Guardrails.Core carries InternalsVisibleTo for both test
#          assemblies), but not by any task in THIS plan: it would have to live under tests/**, which
#          stage 3's pinned writeScope excludes, and task 01 cannot author it either because naming
#          SalvageFraming is precisely what its own guardrail 03 forbids (the constraint that lets
#          stages 2 and 3 stay test-free). So the property is unreachable by a test HERE, and this
#          regex is the demotion's last rung, shipped with a committed .valid/.invalid sample pair.
#
# TWO-LEVEL STRIP, DELIBERATELY INVERTED FOR THE BAN (section 11a, and say why):
#          The required clause reads $code - comments stripped, string literals INTACT - as usual.
#          The BAN also reads $code rather than $scan, which is the opposite of the normal rule. The
#          normal rule strips literals because a banned token inside one is usually a mention. Here
#          the literal IS the defect: a re-authored routing block is emitted text, so it can only
#          exist AS a string literal. Stripping literals would make the ban unfireable by
#          construction - a guard that reads correctly and can never fire (#455's family).
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478):
#          'AppendSalvageSection(' in PromptComposer.cs .............. 0   (expected: this task adds it)
#          'Prior attempt work is salvageable' in PromptComposer.cs .. 0   (expected: a healthy ban)
#          The obvious ban - 'git show' - was MEASURED AT 7 in this same file and DISCARDED. Those
#          seven are the #382 read-route guidance PromptComposer legitimately owns (lines 24, 426,
#          428, 449, 456, 461, 463); banning it would have red-halted a correct implementation on
#          arrival. The clause was changed, not the comment.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'src/Guardrails.Core/Prompts/PromptComposer.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. Nothing to check."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once, never an exit-1 chain that
# reports one gap per attempt.
$failures = @()

# --- REQUIRED: the shared owner is actually CALLED -------------------------------------------------
# -cmatch, not -match: C# identifiers are case-SENSITIVE, and a case-insensitive require-present clause
# false-GREENS on text C# would never compile (catalogue taxonomy entry 3).
if ($code -cnotmatch 'AppendSalvageSection\s*\(') {
    $failures += "$rel never calls AppendSalvageSection(. Plan section 3.3 requires AppendPreviousAttempt to route a patch-carrying prior through RetryPolicy.AppendSalvageSection(..., SalvageFraming.PriorAttempt) - the SAME owner stage 2 made internal for this call site. If the composed prompt carries the routing text without this call, a second copy of the prose is living in this file and will drift."
}

# --- FORBIDDEN: the block's own heading is not re-authored here ------------------------------------
# Reads $code (literals intact) for the reason in the header: the defect can only exist as a literal.
if ($code -cmatch 'Prior attempt work is salvageable') {
    $failures += "$rel contains the literal salvage heading 'Prior attempt work is salvageable'. That heading is AppendSalvageSection's, and RetrySalvageTests pins it there. Emitting it from this file means a second copy of the routing block - call the shared method instead."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== salvage-text ownership: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "RetryPolicy.cs is OUTSIDE this task's writeScope - the fix is to call into it from PromptComposer.cs, never to move or duplicate its text."
    exit 1
}
Write-Output "One owner: PromptComposer routes through AppendSalvageSection and does not re-author the salvage heading."
exit 0
