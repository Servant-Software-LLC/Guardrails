# catches: a review probe that certifies VOCABULARY. An independent review measured the first version of
#          this guardrail: appending the three required literals as loose prose at EOF made it exit 0 -
#          and so did putting all three on ONE line, with no probe at all. #428 has NO mechanical gate by
#          design (design 38 SS6), so this probe is its ONLY enforcement, and a check that cannot tell a
#          probe from a sentence leaves the issue with nothing behind it.
#          So the literals must land INSIDE the adversarial-pass section, in the file's own probe-bullet
#          form. That is not proof of a GOOD probe - it raises the cost from one appended line to a
#          structured insertion, which is the honest ceiling for a documentation target. Said plainly
#          here so the next reviewer does not re-derive it and think it was an oversight.
#          The MEASURED ceiling, named rather than implied: an UNRELATED `- **...(#428)` bullet up to
#          800 flattened chars before the sentence satisfies the shape check. For a documentation
#          target there is no gate short of a human read, so naming the ceiling IS the exemption -
#          not a caveat on it.
# DOCUMENTATION target: exempt from the two-sided sample pair (#468). Compensating controls: the
#          <!-- --> strip (measured to fire correctly - the reviewer could not defeat it), the PRECEDENT
#          check (every literal is pinned verbatim in this task's prompt, so prompt and guardrail cannot
#          drift - GR2026), and the section/shape binding below.
# Measured baseline (#478): all three literals count 0 in this file on the starting tree, and the
#          section markers `### 2. Adversarial pass per task` and `### 2b.` both exist (verified
#          2026-09-06 on master a3f3e977) - so the anchors are real, not hoped for.
$ErrorActionPreference = 'Stop'

# Collapse whitespace before matching a PHRASE (#428, applied to this guardrail itself). A required
# phrase is prose, and prose WRAPS - a markdown document naturally carries a required phrase across a
# line break, and a single-line literal can never match that. Matching the rendered form rather than the
# stored form is precisely the defect this plan exists to document - and the first
# version of this guardrail committed it, false-REDding the honest probe. Normalizing is remedy 2 of the
# three-line rule the task is asked to write down.
function ConvertTo-Flat([string]$text) { return [regex]::Replace($text, '\s+', ' ') }

$problems = New-Object System.Collections.Generic.List[string]
$subject  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { '.claude/skills/guardrails-review/SKILL.md' }

if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - every clause below would be vacuous."
    exit 1
}

# A required-present clause over a .md must strip HTML comments before matching: <!-- ... --> renders as
# NOTHING, so a commented-out line satisfies a naive grep and the clause false-PASSES. Fenced code blocks
# are deliberately NOT stripped - a fence RENDERS, so a rule stated in a fence is legitimate house style.
$raw   = Get-Content -Raw -LiteralPath $subject
$prose = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($prose -match '<!--') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--'. Stripping to EOF would delete the rest of the document, so no clause below can be trusted."
    exit 1
}

# The adversarial-pass section is where a PROBE lives. Loose prose at EOF is not a probe.
$start = [regex]::Match($prose, '(?m)^### 2\. Adversarial pass per task')
$end   = [regex]::Match($prose, '(?m)^### 2b\.')
if (-not $start.Success -or -not $end.Success -or $end.Index -le $start.Index) {
    Write-Output "PRECONDITION: could not locate the '### 2. Adversarial pass per task' ... '### 2b.' section in $subject. This guardrail binds to those headings; if the file was restructured, reconcile the two rather than deleting the binding."
    exit 1
}
$section     = $prose.Substring($start.Index, $end.Index - $start.Index)
$flatProse   = ConvertTo-Flat $prose
$flatSection = ConvertTo-Flat $section

$required = @(
    @{ Text = 'the rendered form is not the stored form'
       Why  = "the #428 probe is absent from the adversarial-pass section. A guardrail that matches a phrase as it READS rather than as it is STORED silently passes when the target is split across source-line literals - a false-PASS, the expensive direction. #428 has no mechanical gate by design, so this probe is its ONLY enforcement." },
    @{ Text = 'ONE source line'
       Why  = "the #428 probe names a trap and offers no remedy. Its first and cheapest fix is 'prefer a distinctive fragment that sits on ONE source line'; without it a reviewer can recognise the defect and not know what to ask for." },
    @{ Text = 'Neutralize string literals BEFORE stripping comments'
       Why  = "this file still implies that stripping comments is the whole of the preparation. A `/*` spelled inside a string literal opens a phantom block comment running to the next `*/` in a later literal, blanking everything between - failing both closed and open. 29 of the 31 committed guardrails that do both operations do them in the defective order (#561)." }
)

foreach ($r in $required) {
    if ($flatProse -notmatch [regex]::Escape((ConvertTo-Flat $r.Text))) {
        $problems.Add("[$subject] does not carry '$($r.Text)' anywhere - $($r.Why)")
    }
    elseif ($flatSection -notmatch [regex]::Escape((ConvertTo-Flat $r.Text))) {
        $problems.Add("[$subject] carries '$($r.Text)' but NOT inside '### 2. Adversarial pass per task'. That section is where a probe a reviewer actually runs lives; the same words elsewhere in the file are prose nobody executes.")
    }
}

# ... and in the file's own probe-bullet shape, so it reads as a probe rather than a stray sentence.
# IgnoreCase deliberately: the clauses above use PowerShell -match, which is case-INSENSITIVE, so a
# case-SENSITIVE anchor here would find nothing the moment the sentence opens a paragraph with a
# capital - skipping the bullet check entirely AND printing the success line that claims it ran.
$anchor = [regex]::Match($flatSection, [regex]::Escape('the rendered form is not the stored form'), 'IgnoreCase')
if ($anchor.Success) {
    $before = $flatSection.Substring([Math]::Max(0, $anchor.Index - 800), [Math]::Min(800, $anchor.Index))
    # Both bullet shapes exist in this file - `- **Name** (#NNN):` and `- **Name (#NNN)**:` - so the
    # code may sit inside or after the bold. Match either, over the FLATTENED section.
    if ($before -notmatch '- \*\*.{0,120}#428') {
        $problems.Add("[$subject] states the #428 rule but not as a PROBE. Every sibling in this section opens with the file's own bullet form - `- **<name>** (#NNN): ...` - and none was found within 800 chars before the rule. A reviewer works the bullet list; a paragraph between bullets is skipped.")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Review probes incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "guardrails-review carries the #428 rendered-vs-stored probe in the adversarial-pass section, in the file's own probe-bullet form, with its one-source-line remedy and the #561 preprocessing-order correction."
exit 0
