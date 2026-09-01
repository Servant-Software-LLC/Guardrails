# catches: a SECOND delivery gate. Section 6.5 is explicit that the whole delivery change is ONE added
#          conjunct in ONE expression, and says why in one sentence: "No new delivery path is introduced,
#          which is what keeps the blast radius of a delivery-gate change to one expression - and is the
#          lesson of #457, where a SECOND gate that ran after delivery was the defect."
#
#          A second predicate is invisible to every behavioural pin in this plan. P9 asserts that a
#          DIVERGENCE run does not deliver; a second gate that agrees with AllSucceeded on that case
#          passes it, and diverges from it later, on the case nobody wrote a pin for. Risk 3 is the other
#          half: "AllSucceeded gates delivery, the exit code and the banner for EVERY run. A defect there
#          - a non-null default, an inverted comparison - silently stops the product delivering
#          anything," and P10 is the only thing standing under it.
#
#          It also pins the four EXISTING terms. Losing one is a delivery-gate change nobody asked for,
#          in the file where it would be least visible: a run with definition drift, a halted wave, or an
#          abort would start delivering.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): "there is no SECOND delivery
#          predicate" is unobservable at runtime by construction - a second gate that AGREES today is
#          behaviourally identical today, which is exactly how #457 shipped. There is no input that
#          distinguishes them and therefore no test that can. Demotion order's last rung; it ships with a
#          committed .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code; the BAN reads $scan (literals gone too), so a message string mentioning delivery cannot
#          trip it.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Core/Execution/RunReport.cs, case-SENSITIVE (#478):
#            AllSucceeded =>                       1   EXPECTED nonzero - the single existing declaration.
#                                                      An EQUALITY, not a floor: two would BE the defect.
#            HasDefinitionDrift / HasWaveHalt / Aborted / IsGreen   present   EXPECTED nonzero -
#                                                      tests-untouched REGRESSION clauses on the four
#                                                      terms that must survive.
#            HasExecutedDefinitionDivergence        0   this stage's deliverable.
#            public bool <Deliver|Delivery|Succeeded> =>   2   EXPECTED nonzero, and the reason the ban
#                                                      below carries an ALLOWLIST: the pattern matches
#                                                      'Succeeded' on TaskReport (a per-task outcome
#                                                      test) and 'AllSucceeded' itself. Both are shipped;
#                                                      naming them is what stops the ban false-redding a
#                                                      correct file on arrival. WhollyGreenButUndelivered
#                                                      and DeliveryPendingTerminalGate are auto-properties
#                                                      rather than expression-bodied, so they do not
#                                                      match at all.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/13-add-the-divergence-gate/samples/04-one-delivery-term-no-second-path.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/13-add-the-divergence-gate/samples/04-one-delivery-term-no-second-path.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/RunReport.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: exactly ONE AllSucceeded declaration ------------------------------------------------
# -cmatch throughout: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
$decls = [regex]::Matches($code, 'public\s+bool\s+AllSucceeded\s*=>')
if ($decls.Count -ne 1) {
    $failures += "$rel declares AllSucceeded $($decls.Count) time(s); there must be exactly ONE. It is 'the single predicate that gates delivery, the green summary and the exit code' (section 6.5), and its being single is the entire reason a delivery-gate change has a one-expression blast radius."
}
else {
    # The expression body: from the arrow to the terminating semicolon.
    $expr = [regex]::Match($code, 'public\s+bool\s+AllSucceeded\s*=>([^;]*);')
    if (-not $expr.Success) {
        $failures += "$rel declares AllSucceeded but its expression body could not be read (no terminating semicolon found). Section 6.5 specifies a single expression-bodied predicate with one added conjunct - if it has become a block-bodied member with branching, that is a new delivery PATH, not a new term."
    }
    else {
        $body = $expr.Groups[1].Value

        # --- REQUIRED: the four existing terms survive ------------------------------------------
        foreach ($term in @('HasDefinitionDrift', 'HasWaveHalt', 'Aborted', 'IsGreen')) {
            if ($body -cnotmatch ('\b' + $term + '\b')) {
                $failures += "AllSucceeded no longer includes the term '$term'. Section 6.5 adds ONE conjunct and changes nothing else. Dropping an existing one is a delivery-gate change nobody asked for, in the least visible place it could happen: a run with definition drift, a halted wave, or an abort would start delivering."
            }
        }

        # --- REQUIRED: the new term is there, and it is a NEGATED conjunct -----------------------
        if ($body -cnotmatch 'HasExecutedDefinitionDivergence') {
            $failures += "AllSucceeded does not include HasExecutedDefinitionDivergence. That single term IS the delivery gate (section 6.5): without it the divergence is detected, recorded, and then DELIVERED anyway - which is milestone C's acceptance criterion P9 failing while every other pin passes."
        }
        elseif ($body -cnotmatch '!\s*HasExecutedDefinitionDivergence') {
            $failures += "AllSucceeded mentions HasExecutedDefinitionDivergence but not as a NEGATED conjunct (!HasExecutedDefinitionDivergence). Risk 3: an inverted comparison here silently stops the product delivering ANYTHING, on every run, and the only thing standing under it is P10."
        }
    }
}

# --- FORBIDDEN: a SECOND delivery predicate --------------------------------------------------------
# Anchored on a DECLARATION of a delivery-shaped predicate other than the one above, not on the word
# 'deliver' anywhere (#76 - a use, not a mention). Reads $scan so a message string cannot trip it.
$allowed = @('AllSucceeded', 'Succeeded')
$second  = [regex]::Matches($scan, '(?m)public\s+bool\s+(\w*(?:Deliver|Delivery|Succeeded)\w*)\s*=>')
foreach ($m in $second) {
    $name = $m.Groups[1].Value
    # ALLOWLIST, measured rather than reasoned. On the shipped file this pattern matches TWO
    # expression-bodied predicates besides the gate: 'Succeeded' on TaskReport (a per-task outcome test,
    # nothing to do with delivery) and 'AllSucceeded' itself. Without naming both, this ban false-REDS a
    # correct file on arrival - which the valid half of the sample pair is what exposes, and which is the
    # one direction a ban's own invalid half can never reveal (#468).
    if ($allowed -notcontains $name) {
        $failures += "$rel declares a second delivery-shaped predicate '$name'. Section 6.5 forbids it: 'No new delivery path is introduced, which is what keeps the blast radius of a delivery-gate change to one expression - and is the lesson of #457, where a SECOND gate that ran after delivery was the defect.' A second predicate that AGREES with AllSucceeded today passes every pin in this plan and diverges from it later, on the case nobody wrote a pin for. (If this name is a pre-existing member rather than something you added, say so with needsHuman rather than renaming it.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== delivery gate: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "One added conjunct in one expression. Every one of the seven consumers section 6.5 traces inherits it for free - the merge, the WORK NOT DELIVERED banner, the legacy terminal gate, the CLI terminal plan-guardrail phase, DeliveryPendingTerminalGate, worktree retention, and the exit-code and summary rendering."
    exit 1
}
Write-Output "Delivery gate sound: one AllSucceeded, four existing terms intact, the divergence term added and negated, and no second delivery predicate."
exit 0
