# catches: a plan that ships a new on-disk format and a new CLI verb while the two documents every
#          agent reads to learn this system still describe a harness that has neither. The corpus
#          outlives the run that wrote it, so "which shape is this row" has to be answerable from the
#          SSOT rather than from the implementation - and the undifferentiated bucket in particular is a
#          rule a later reader will otherwise "fix" by guessing, which is the whole defect it exists to
#          prevent.
#          DOCUMENTATION deliverable: exempt from the two-sided sample pair (#468) - no meaningful
#          invalid sample of a design doc exists. The PRECEDENT check is the substitute: every token
#          demanded below is accepted in the form each document already uses for the same kind of thing
#          (a backticked path, a backticked verb), so the check never forces a house style the document
#          does not have.
#          Required-present baselines, MEASURED at authoring time against these exact subjects, case
#          sensitive, as SSOT/SKILL (#478): 'guardrails/telemetry' 0/0, 'telemetry ingest' 0/0,
#          'TelemetryRow' 0/-, 'undifferentiated' 0/-, and the null-rule PAIR 0/-. Zero everywhere,
#          which is the expected answer. It was not free: the null-versus-zero clause first shipped as
#          a bare 'never reported' scan, which measures 1 in the SSOT (JournalTierSpend's own rule) and
#          was therefore GREEN ON ARRIVAL - invisible, because its five failing siblings still exited 1.
#          Measuring each clause separately is what found it; the fix was the CLAUSE, not the comment.
$ErrorActionPreference = 'Continue'

$ssot  = 'docs/plans/02-schemas-and-contracts.md'
$skill = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

$problems = New-Object System.Collections.Generic.List[string]

# PRECONDITION: both subjects must exist, or every clause below reports a vacuous absence.
foreach ($f in @($ssot, $skill)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        Write-Output "PRECONDITION: $f is missing - this task records the telemetry surfaces in it, so there is nothing to check. Fix the path before reading anything below as a finding."
        exit 1
    }
}

$ssotText  = Get-Content -LiteralPath $ssot -Raw
$skillText = Get-Content -LiteralPath $skill -Raw

# Each clause names WHERE the corpus lives, WHAT reads it, and the one rule a reader would otherwise
# invent. Backticks are optional on every token - the precedent check: both documents already write
# paths and verbs both ways.
$clauses = @(
    @{ File = $ssot;  Text = $ssotText;  Token = 'guardrails/telemetry'; What = 'the corpus location - a reader cannot find the rows without it' },
    @{ File = $ssot;  Text = $ssotText;  Token = 'telemetry ingest';     What = 'the verb that fills the corpus' },
    @{ File = $ssot;  Text = $ssotText;  Token = 'TelemetryRow';         What = 'the corpus row type the null-versus-zero rule below must sit beside' },
    @{ File = $ssot;  Text = $ssotText;  Token = 'undifferentiated';     What = 'the bucket a guardrail-failed attempt lands in when its log site is gone - the rule that must never be guessed at' },
    @{ File = $skill; Text = $skillText; Token = 'guardrails/telemetry'; What = 'the corpus location' },
    @{ File = $skill; Text = $skillText; Token = 'telemetry ingest';     What = 'the verb that fills the corpus' }
)

foreach ($c in $clauses) {
    if ($c.Text -notmatch [regex]::Escape($c.Token)) {
        $problems.Add("[$($c.File)] does not mention '$($c.Token)' - $($c.What). This plan ships that surface; the document a reader learns the system from still describes a harness without it.")
    }
}

# The null-versus-zero rule is the one a later implementer is most likely to "simplify" away, so the
# SSOT has to carry it in words, not only in a field list.
# PROXIMITY, not a bare phrase (#478): a bare 'never reported' scan is GREEN ON ARRIVAL - this SSOT
# already contains that phrase once, for JournalTierSpend's own null-versus-zero rule, so the clause
# would have hidden behind its failing siblings and certified nothing. Measured: 'never reported' 1,
# 'TelemetryRow' 0, and the PAIR within 600 characters 0. The pair is what this clause requires.
$nullRule = '(?s)TelemetryRow[\s\S]{0,600}never reported|(?s)never reported[\s\S]{0,600}TelemetryRow'
if ($ssotText -notmatch $nullRule) {
    $problems.Add("[$ssot] records no null-versus-zero rule NEXT TO the corpus row. Null means NEVER REPORTED, which is not the claim zero makes - without that sentence beside TelemetryRow the next reader defaults cost and tokens to 0, and the corpus starts asserting that a costless local run cost nothing. (The phrase alone is not enough: this document already uses it for JournalTierSpend.)")
}

if ($problems.Count -gt 0) {
    Write-Output "=== Telemetry surfaces not recorded ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "Record the corpus location, the verb, the undifferentiated bucket and the null-versus-zero rule in both documents, in whatever form each already uses for the same kind of fact."
    exit 1
}

Write-Output "Telemetry surfaces recorded in both $ssot and $skill."
exit 0
