# catches: the drift-accept branch left open for a divergence-originated drift. Section 6.6 is the whole
#          argument and it is short: after a divergence halt the operator re-runs, the resume pre-pass
#          mismatches on exactly the diverged tasks, and the interactive prompt offers [y] / [a] / [N].
#          The [a] branch calls RunJournal.RecordDriftAccepted, which OVERWRITES the recorded hash with
#          current disk and does NOT re-run the task. Reached from a divergence halt that is
#
#              "one keystroke from re-creating precisely the lie #556 is about: a journal saying the task
#               was built against the new definition when it was built against the old one."
#
#          And it is worse than the original defect, because it also UN-CORROBORATES the plan branch - the
#          task's commit still carries the old trailer while the journal now carries the new hash, so
#          SafeSuffixEvaluator's trailer-corroboration rule refuses any later Part C rewind covering that
#          task and steers the operator to a full reset. This plan CREATES the traffic through that
#          branch, so this plan has to close it.
#
# WHY A SOURCE-SHAPE CHECK, AND THE HONEST ANSWER IS NOT "THE DEMOTION ORDER" (#468): the property IS
#          observable - section 9 asks for it to be "asserted on the prompt's rendered options", which is
#          a test. NO STAGE IN THIS PLAN CAN WRITE THAT TEST. ConfirmSafeDriftIfInteractive is
#          `private static` and gated on !Console.IsInputRedirected, so it is unreachable from a test
#          process; making it reachable is a change to RunCommand.cs, which only THIS stage may write, and
#          every test-authoring row is either upstream of that change (so its assertion would not compile)
#          or does not exist. Section 11 forbids implementation stages from writing tests/**, and that is
#          the right rule - the consequence here is that section 9's [a]-refusal bullet has no behavioural
#          pin anywhere in section 15's sixteen rows.
#
#          So this is a COMPENSATING CONTROL, not a demoted check, and the breakdown report says so and
#          flags the gap for the human. It ships with a committed .valid/.invalid pair in ../samples/, and
#          a reviewer who wants the real instrument should add a test-authoring row AFTER this stage.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. REQUIRED clauses read
#          $code (comments gone, string literals INTACT - load-bearing, since the option text the operator
#          reads IS a string literal and this check is partly about that text).
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Cli/Commands/RunCommand.cs, case-SENSITIVE (#478):
#            DefinitionHashAtSettle      0   this stage's deliverable (the condition it branches on)
#            ConfirmSafeDriftIfInteractive  present   EXPECTED nonzero - the member that renders the
#                                            options, and the REGION the condition clause is scoped to.
#                                            A tests-untouched REGRESSION clause: the branch must still be
#                                            there afterwards, refusing rather than deleted.
#            DefinitionHashAtSettle INSIDE
#            that member                 0   this stage's deliverable, and scoped rather than file-wide
#                                            ON PURPOSE (W5): stage 15 has four other deliverables in this
#                                            ~2000-line file, at least two of which plausibly name the
#                                            divergence record, so a file-wide clause would be satisfied
#                                            by a use that has nothing to do with the drift prompt.
#            RecordDriftAccepted         1   EXPECTED nonzero - the [a] handler's call. The refusal is a
#                                            branch AROUND it for one class of task, never its removal:
#                                            ordinary between-runs drift-accept is UNCHANGED (section 12),
#                                            and that trade is already reviewed.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/15-render-the-divergence-halt/samples/03-accept-is-refused-for-divergence.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/15-render-the-divergence-halt/samples/03-accept-is-refused-for-divergence.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Cli/Commands/RunCommand.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- Member regions, so the condition is bound to the BRANCH and not merely to the FILE (W5) --------
# The first version of this clause was a file-wide token check on a ~2000-line file, and stage 15 has FOUR
# other deliverables in it - the halt render, the terminal-gate fix, the delivery reason, the advisory -
# at least two of which plausibly mention the divergence record for their own reasons. A file-wide hit
# would then satisfy a clause about the drift PROMPT while the prompt itself was untouched. Same region
# cutter as stage 5's sibling check, including the reason it cuts at the opening brace: a head window
# matched a CALL inside another member's body there and returned the wrong region entirely.
$declStarts = [regex]::Matches($code, '(?m)^    (?:public|private|internal|protected)\b')
$regions    = @()
for ($i = 0; $i -lt $declStarts.Count; $i++) {
    $start = $declStarts[$i].Index
    $end   = if ($i + 1 -lt $declStarts.Count) { $declStarts[$i + 1].Index } else { $code.Length }
    $regions += ,$code.Substring($start, $end - $start)
}

$promptRegion = $null
foreach ($region in $regions) {
    $brace = [regex]::Match($region, '(?m)^    \{')
    $sig   = if ($brace.Success) { $region.Substring(0, $brace.Index) } else { $region }
    if ($sig -cmatch '\bConfirmSafeDriftIfInteractive\s*[(<]') { $promptRegion = $region; break }
}

# --- REQUIRED: the refusal is CONDITIONED on the divergence record, INSIDE the prompt ---------------
# -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
if ($null -eq $promptRegion) {
    $failures += "$rel declares no ConfirmSafeDriftIfInteractive member. That is the branch rendering the [y] / [a] / [N] options; the refusal is a condition INSIDE it, never its deletion. Removing the prompt would take the [y] rewind and the [N] abort with it - remediations section 7.2 owns and this plan does not touch."
}
elseif ($promptRegion -cnotmatch '\bDefinitionHashAtSettle\b') {
    $failures += "$rel mentions DefinitionHashAtSettle nowhere inside ConfirmSafeDriftIfInteractive. Section 6.6: 'The condition is cheap and needs no new state - a task whose journal entry carries definitionHashAtSettle is BY CONSTRUCTION one that ran a definition it does not match, and accepting its current disk hash is never sound.' A hit ELSEWHERE in this file does not bind the refusal to the branch: stage 15 has four other deliverables here, at least two of which plausibly name the divergence record for their own reasons, so a file-wide check would go green on a prompt that was never touched. Without the condition IN THE BRANCH the refusal is either absent or unconditional, and an unconditional refusal changes [a] for ORDINARY between-runs drift too, which section 12 puts explicitly out of scope."
}

# --- REQUIRED: the accept HANDLER survives, because ordinary drift-accept is unchanged --------------
if ($code -cnotmatch '\bRecordDriftAccepted\b') {
    $failures += "$rel no longer calls RecordDriftAccepted. Section 12 is explicit that changing [a]'s behaviour for an ORDINARY between-runs edit is OUT OF SCOPE: 'the existing trade is already reviewed and is not this plan's to relitigate.' Only the divergence-originated case is refused. Deleting the handler outright is a different, unreviewed change."
}

# --- REQUIRED: the refusal NAMES the remedy ---------------------------------------------------------
# Reads $code with literals intact, because the remedy is operator-facing TEXT. Anchored on the verb the
# plan names, not on prose: section 6.6 says the prompt "drops the [a] option for those tasks and says
# why, naming guardrails reset <folder> <taskId> instead."
if ($code -cnotmatch 'guardrails\s+reset') {
    $failures += "$rel never names 'guardrails reset' in operator-facing text. Section 6.6: the prompt drops [a] for those tasks AND SAYS WHY, naming 'guardrails reset <folder> <taskId>' instead. A refusal with no remedy is the shape this whole plan exists to remove, one level down: the operator is told no and left with nowhere to go."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== drift-accept refusal: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "A branch, not a deletion: [a] stays exactly as it is for an ordinary between-runs edit, and is refused only for a task whose journal entry carries definitionHashAtSettle. That field is stage 12's; stage 13 is what writes it."
    exit 1
}
Write-Output "Drift-accept refusal present: the divergence record is read INSIDE ConfirmSafeDriftIfInteractive, the accept handler is intact for ordinary drift, and the remedy is named."
exit 0
