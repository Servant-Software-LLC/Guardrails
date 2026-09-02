# catches: the mitigation keyed on the WRONG predicate. This is the single defect the design was
#          rewritten for, so it gets its own check rather than riding on the regression suite.
#          PlanIsClosed is `plan.Waves.All(w => w.Tasks.Count > 0)` - it detects an EMPTY STUB WAVE and
#          returns TRUE for an authored partial prefix, which is exactly the case that breaks.
#          wavePrefixIsIncomplete is ACTUAL KNOWLEDGE of incompleteness, set from a breakdown-intent
#          manifest that still owes folders. A fix keyed on PlanIsClosed would pass the regression tests
#          for the wrong reason on a plan shape they do not cover, and would re-open #501 one code over.
#
# Required-present baselines (#478), measured on master @67859c7 against this exact subject:
#          UnproducibleGateRequirement  0  - expected; task 4 allocates it, task 6 references it here
#          wavePrefixIsIncomplete       6  - NONZERO with a named reason: this is a REGRESSION PIN on an
#                                            existing gate parameter, not a new requirement
#          PlanIsClosed                 0  - forbidden-present, exempt from measurement (a ban green on
#                                            arrival is a correct ban); recorded for the record anyway
$ErrorActionPreference = 'Continue'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/Scheduler.cs' }
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist.')
    exit 1
}

$raw  = Get-Content -LiteralPath $subject -Raw
# Blank comments: the surrounding #501 comment legitimately DISCUSSES both predicates, and a required
# clause satisfied by a comment - or a ban tripped by one - would be exactly backwards.
# ALL // comments, not just line-leading ones. The original anchored '^\s*//', so a TRAILING comment on
# a code line survived into $scan and tripped the PlanIsClosed ban - two implementations with identical
# behaviour got opposite verdicts on comment PLACEMENT, and the red one reported a claim that was false
# about the code. Measured against three synthesized samples: correct impl with a full-line comment
# exit 0, the SAME comment moved to trail the code line exit 1 (false red), wrong impl exit 0.
$scan = [regex]::Replace($raw, '(?m)//.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$failures = New-Object System.Collections.Generic.List[string]

if ($scan -notmatch 'UnproducibleGateRequirement') {
    $failures.Add('GR2060 IS NOT EXCUSED AT THE JIT GATE: Scheduler.cs never references DiagnosticCodes.UnproducibleGateRequirement outside a comment. UnsatisfiableWhileIncomplete must name it, or an ERROR-severity GR2060 on a JIT partial prefix casts a veto and the authored prefix is reverted wholesale - verbatim the defect #501 fixed.')
}

if ($scan -notmatch 'wavePrefixIsIncomplete') {
    $failures.Add('THE EXCUSE IS NOT KEYED ON wavePrefixIsIncomplete in ' + $subject + '. That parameter is the gate actual knowledge that folders are still owed; without it the excuse is unconditional and GR2060 stops blocking complete plans that genuinely cannot produce their own gate requirement.')
}

# The excuse must stay CONDITIONAL. Without this, an implementation that makes the excuse unconditional
# passes: 'wavePrefixIsIncomplete' still measures 5 elsewhere in the file (the parameter, the report
# line), so the presence clause above cannot see the difference - measured as a false green by an
# adversarial pass. Pinned on the ASSIGNMENT at Scheduler.cs:2224, not on the ternary: the '?' that
# shares a line with the name is at :2246 and belongs to the gate-decision REPORT string, which would
# survive an unconditional excuse untouched.
if ($scan -notmatch 'excused\s*=\s*wavePrefixIsIncomplete') {
    $failures.Add('THE EXCUSE IS NO LONGER CONDITIONAL in ' + $subject + ': the excused set is not assigned from wavePrefixIsIncomplete. An unconditional excuse stops GR2060 blocking a COMPLETE plan that genuinely cannot produce its own gate requirement, which is the over-correction that would make the whole diagnostic meaningless. Keep the conditional assignment.')
}

if ($scan -match 'PlanIsClosed') {
    $failures.Add('THE MITIGATION KEYS ON PlanIsClosed in ' + $subject + '. That is the trap this task exists to avoid: PlanIsClosed is Waves.All(w => w.Tasks.Count > 0), so it detects an EMPTY STUB WAVE and returns TRUE for an authored partial prefix - the exact case that breaks. Key the excuse on wavePrefixIsIncomplete instead. PlanIsClosed stays where it is, as GR2060 condition-10 suppressor in PlanValidator; the two suppressions are complementary, not alternatives.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The JIT-gate excuse is keyed wrongly (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'GR2060 is excused at the JIT breakdown gate, keyed on wavePrefixIsIncomplete, and PlanIsClosed is not used here.'
exit 0
