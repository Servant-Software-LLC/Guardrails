# catches: a skill edit that re-authors the shipped datum trace instead of ADDING the step it does not
#          cover, or that adds a vague "check the callee" line with no teeth. The gap is specific: the
#          shipped procedure walks UPSTREAM to the carrier, finds it in scope, and returns "reachable;
#          stop" - it never reads the callee's PARAMETER LIST, which is where the measured defect was.
#          It also catches the interface half going missing: for a call dispatched through an interface
#          the declaring file is the INTERFACE, and a cast to the concrete type compiles, satisfies the
#          clause, and journals nothing.
#
# DOCUMENTATION TARGET: exempt from the two-sided sample pair (#468). The PRECEDENT substitute: both
#          skills already use the words parameter list, writeScope and declaring in this sense, and the
#          clauses below require SUBSTRINGS with alternatives, never one mandated sentence.
#
# Required-present baselines (#478), measured on master @67859c7 with these clauses own case rules:
#          guardrails-review/SKILL.md  : 'parameter list' 0 - honest, and it fires. 'interface' measures
#                                        7 and the clause below WAS standalone despite this line once
#                                        claiming otherwise, so it was green on arrival and task 10 could
#                                        have shipped the callee step with NO interface-vs-concrete trap
#                                        - the very defect Milestone C exists to close. It is now
#                                        WINDOWED off 'parameter list', which measures 0 and is therefore
#                                        a reliable anchor for the newly added text.
#          plan-breakdown/SKILL.md     : 'declaring file' 0
#          The shipped trace heading TRACE THE DATUM is measured at 1 in plan-breakdown - NONZERO with a
#          named reason: it is a REGRESSION PIN asserting the shipped section SURVIVES this addition.
$ErrorActionPreference = 'Continue'

$review = '.claude/skills/guardrails-review/SKILL.md'
$author = '.claude/skills/plan-breakdown/SKILL.md'
foreach ($f in @($review, $author)) {
    if (-not (Test-Path -LiteralPath $f)) {
        Write-Output ('PRECONDITION: ' + $f + ' does not exist.')
        exit 1
    }
}

$r = [regex]::Replace((Get-Content -LiteralPath $review -Raw), '(?s)<!--.*?-->', '')
$a = [regex]::Replace((Get-Content -LiteralPath $author -Raw), '(?s)<!--.*?-->', '')

# PROSE COPIES with inline markdown emphasis REMOVED. A multi-word phrase clause over a MARKDOWN target
# is broken by emphasis BETWEEN its words: a correct paragraph reading "`M`'s **declaring** file goes in
# the writeScope" does not contain the literal "declaring file" at all, because ** sits between them.
# Measured, and it cost a real attempt: task 10's first attempt wrote exactly that sentence - a better
# paragraph than the one this guardrail asked for - and was FALSE-RED'd by this check. Emphasis markers
# are presentation; a reader sees "declaring file". Match what the reader sees.
# Token clauses keep using $r/$a; only the multi-word PHRASE clauses use these.
$rProse = [regex]::Replace($r, '[*_`]', '')
$aProse = [regex]::Replace($a, '[*_`]', '')

$failures = New-Object System.Collections.Generic.List[string]

if ($rProse -notmatch '(?i)parameter list') {
    $failures.Add('THE REVIEW PROBE STILL STOPS AT THE CARRIER: no mention of a parameter list in ' + $review + '. Add the step that opens the CALLEE declaration and asks whether its parameter list already accepts what the clause requires. Without it the probe returns reachable-stop on the exact shape it missed.')
}
$window = [regex]::Match($rProse, '(?is)parameter list[\s\S]{0,1500}')
if (-not $window.Success -or $window.Value -notmatch '(?i)interface') {
    $failures.Add('THE INTERFACE HALF IS MISSING from the added step in ' + $review + '. For a call dispatched through an interface the declaring file is the INTERFACE, not the concrete type - a cast to the concrete type compiles, satisfies the clause, and journals nothing.')
}

if ($aProse -notmatch '(?i)declaring file') {
    $failures.Add('THE AUTHORING TWIN IS MISSING from ' + $author + ': no mention of a declaring file. When a task deliverable is "pass D to M", M declaring file goes in the writeScope - the interface if the call dispatches through one - unless M already accepts D today.')
}

# REGRESSION PIN: the shipped datum trace must SURVIVE. This is an addition, never a rewrite.
# -cnotmatch: PowerShell -match is case-INSENSITIVE, and lowercase prose elsewhere in this skill also
# says "trace the datum" (measured: 2 insensitive, 1 sensitive). A case-insensitive pin would survive
# deletion of the actual heading, which is the only thing it exists to protect.
if ($a -cnotmatch '(?m)^###.*TRACE THE DATUM') {
    $failures.Add('THE SHIPPED DATUM TRACE WAS REMOVED OR RENAMED in ' + $author + '. It shipped at e118b9d and is correct; this task ADDS the step it does not cover. A task that re-authors that section has done the wrong thing even if the result reads well.')
}

if ($failures.Count -gt 0) {
    Write-Output ('=== The callee-parameter-list step is missing or the shipped trace was disturbed (' + $failures.Count + ' problem(s)) ===')
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output 'Both skills carry the callee-parameter-list step, the interface trap is named, and the shipped datum trace survives.'
exit 0
