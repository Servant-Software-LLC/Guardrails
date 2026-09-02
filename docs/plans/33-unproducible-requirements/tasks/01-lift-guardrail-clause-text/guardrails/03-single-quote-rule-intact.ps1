# catches: a lift that WIDENS PresenceClause to admit a double-quoted pattern operand while moving it.
#          The existing GR2057 suite cannot catch this: widening makes the reader accept MORE, so every
#          shipped test still passes. It is prohibition 4 of the plan's section 11, and a later task in
#          this plan reasons about that restriction being intact - a widened reader silently changes
#          which clauses GR2057 adjudicates.
#
# SOURCE-SHAPE, and why no test carries it: the property is "this regex admits single-quoted operands
#          ONLY". A test can prove the reader accepts a single-quoted clause and rejects a double-quoted
#          one TODAY - but that is behaviour on two chosen inputs, while the restriction is a statement
#          about the pattern's whole alternation. The demotion order (#468) puts a source-shape regex
#          last and demands this line; the honest reading is that a behavioural test is a proxy here and
#          the literal is the property. Both ship: guardrail 02 is the behavioural half.
#
# The subject is a C# STRING LITERAL, so this scan deliberately does NOT strip string literals - the
#          thing being checked lives inside one. Comments ARE blanked, so a doc comment quoting the old
#          pattern cannot satisfy the required clause (#97/#98).
#
# Required-present baseline (#478), measured at author time with this clause's own case sensitivity:
#          the required literal occurs 0 times in GuardrailClauseText.cs (the file does not exist yet)
#          and 1 time in PlanValidator.cs @67859c7, which is the member about to move. Expected 0 on
#          this subject.
$ErrorActionPreference = 'Continue'

# GR_SUBJECT arrives ABSOLUTE from the sample verifier (#559) - use it as given, never Join-Path it onto
# the workspace root. Absent, fall back to the real file this guardrail exists to police.
$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Loading/GuardrailClauseText.cs' }

if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output ('PRECONDITION: ' + $subject + ' does not exist. Task 1 must create GuardrailClauseText.cs and move PresenceClause into it; every clause below would crash without it.')
    exit 1
}

$raw = Get-Content -LiteralPath $subject -Raw
# Blank comments only. NOT string literals: the pattern under inspection IS a string literal.
$scan = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
$scan = [regex]::Replace($scan, '(?s)/\*.*?\*/', '')

$failures = New-Object System.Collections.Generic.List[string]

# REQUIRED: the single-quote-only operand survives verbatim. Written as a doubled single quote inside a
# single-quoted string, so it is the two characters  '  (  and not an escape of anything.
$requiredOperand = '''(?<pat>'
if ($scan -notmatch [regex]::Escape($requiredOperand)) {
    $failures.Add('PresenceClause no longer requires a SINGLE-QUOTED pattern operand in ' + $subject + ' (the required operand form is absent). The lift is a pure refactor - move the regex byte-for-byte. Its own doc comment explains in three bullets why double-quoted and composed operands are deliberately unmatched; widening it to make something work is prohibition 4 of section 11.')
}

# FORBIDDEN: a double-quoted operand admitted, directly or via a character class.
$forbiddenOperand = '"(?<pat>'
if ($scan -match [regex]::Escape($forbiddenOperand)) {
    $failures.Add('PresenceClause has been WIDENED to admit a DOUBLE-QUOTED pattern operand in ' + $subject + '. PowerShell interpolates the dollar sign inside a double-quoted pattern, so the operand is not statically known and the clause polarity becomes undecidable from the text - which is exactly what the original refused to do.')
}
if ($scan -match '\]\s*\(\?<pat>') {
    $failures.Add('PresenceClause quote operand has been widened into a CHARACTER CLASS before the pat group in ' + $subject + '. Single-quoted only: restore the original operand.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== PresenceClause single-quote rule was not preserved (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output ('PresenceClause single-quoted-operand restriction is intact in ' + $subject + '.')
exit 0
