# catches: a doctrine correction that certifies VOCABULARY instead of work. An independent review
#          measured the first version of this guardrail: appending the six required literals at EOF of
#          the three files made it exit 0 with EIGHTEEN backwards statements still in place - and that
#          outcome is WORSE than not fixing it, because the reader now meets two contradictory rules.
#          So the clause below is not "the sentence appears" but "no IMPERATIVE backwards statement
#          survives uncorrected": every `strip comments first/before` (and `strip first`) must have the
#          ordering sentence within a 1500-character window, i.e. corrected AT ITS OWN SITE.
# DOCUMENTATION target: exempt from the two-sided sample pair (#468) - no meaningful invalid sample of a
#          prose document exists. Compensating controls: the <!-- --> strip below (a required clause over
#          a .md false-PASSES on a commented-out line), the PRECEDENT check (every literal is pinned
#          verbatim in this task's own prompt, so prompt and guardrail cannot drift - GR2026), and the
#          proximity requirement above, which raises the cost from an append to an in-place edit.
# Measured baselines (2026-09-06, master a3f3e977), and note what is NOT asserted:
#          imperative backwards statements (measured by THIS script's own regex, not by eye) -
#          guardrail-catalogue.md 5 (~460, ~494, ~654, ~666, ~688), stacks/dotnet.md 4 (~1504, ~1509,
#          ~1938, ~1959), SKILL.md 0.
#          SKILL.md is therefore NOT required to carry the ordering sentence: it states the rule nowhere,
#          so demanding it there would send the agent to insert into a file with no natural home (design
#          38 SS4.2's citation of SKILL.md:547 was stale - that line is the unrelated .md HTML-comment
#          rule). `ErrorActionPreference = 'Stop'` is likewise NOT asserted on its own: it already occurs
#          in both files, so that clause would be satisfied before the task ran.
$ErrorActionPreference = 'Stop'

# Collapse whitespace before matching a PHRASE (#428, applied to this guardrail itself). A required
# phrase is prose, and prose WRAPS - a markdown document naturally carries a required phrase across a
# line break, and a single-line literal can never match that. Matching the rendered form rather than the
# stored form is precisely the defect this plan exists to document - and the first
# version of this guardrail committed it, false-REDding the honest probe. Normalizing is remedy 2 of the
# three-line rule the task is asked to write down.
function ConvertTo-Flat([string]$text) { return [regex]::Replace($text, '\s+', ' ') }

$problems = New-Object System.Collections.Generic.List[string]

$skill     = '.claude/skills/plan-breakdown/SKILL.md'
$catalogue = '.claude/skills/plan-breakdown/references/guardrail-catalogue.md'
$stack     = '.claude/skills/plan-breakdown/references/stacks/dotnet.md'

function Get-Prose([string]$path) {
    # A required-present clause over a .md must strip HTML comments before matching: <!-- ... --> renders
    # as NOTHING, so a commented-out line satisfies a naive grep and the clause false-PASSES. Fenced code
    # blocks are deliberately NOT stripped - a fence RENDERS, so a rule in a usage fence is house style.
    $raw = Get-Content -Raw -LiteralPath $path
    $stripped = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
    if ($stripped -match '<!--') { return $null }   # unterminated: stripping to EOF would eat the doc
    return $stripped
}

$sentence   = 'Neutralize string literals BEFORE stripping comments'
$imperative = '(?i)strip comments (first|before)|(?i)strip first'
$window     = 1500

# The two files that actually STATE the ordering rule must carry the correction, at every site.
foreach ($f in @($catalogue, $stack)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        $problems.Add("[$f] does not exist - every clause below would be vacuous.")
        continue
    }
    $prose = Get-Prose $f
    if ($null -eq $prose) {
        $problems.Add("[$f] contains an unterminated '<!--'. The HTML-comment strip cannot run without deleting the rest of the document, so no clause over this file can be trusted.")
        continue
    }

    $corrections = @([regex]::Matches($prose, [regex]::Escape($sentence)))
    if ($corrections.Count -lt 1) {
        $problems.Add("[$f] does not carry the ordering rule at all. Every site that states the preprocessing order must carry '$sentence' verbatim (#561).")
        continue
    }

    $uncorrected = @()
    foreach ($m in [regex]::Matches($prose, $imperative)) {
        $near = $false
        foreach ($c in $corrections) {
            if ([Math]::Abs($c.Index - $m.Index) -le $window) { $near = $true; break }
        }
        if (-not $near) {
            $line = 1 + @([regex]::Matches($prose.Substring(0, $m.Index), "`n")).Count
            $uncorrected += "line ~$line ('$($m.Value)')"
        }
    }
    if ($uncorrected.Count -gt 0) {
        $problems.Add("[$f] still states the rule BACKWARDS at $($uncorrected.Count) site(s) with no correction within $window chars: $($uncorrected -join '; '). Adding the ordering sentence somewhere else in the file leaves a reader who lands on one of these meeting two contradictory rules - which is worse than not fixing it. Correct each site in place (#561).")
    }
}

# The generator rule: BOTH preference lines, TOGETHER, in one of the two files that show the emitted
# guardrail opening. NOT asserted: that "ErrorActionPreference = 'Stop'" appears at all - measured, it
# already occurs in both, so that clause would certify nothing (#478).
$generator = @($skill, $stack) | ForEach-Object { if (Test-Path -LiteralPath $_ -PathType Leaf) { Get-Prose $_ } } | Where-Object { $_ }
$adjacent = @($generator | Where-Object {
    $_ -match "(?s)ErrorActionPreference = 'Stop'.{0,200}PSNativeCommandUseErrorActionPreference"
}).Count
if ($adjacent -lt 1) {
    $problems.Add("neither $skill nor $stack shows the two preference lines TOGETHER (within 200 chars). The emitted guardrail opening is a PAIR: 'Stop' makes an engine error terminate instead of failing open, and the native-command line keeps a non-zero dotnet exit - the SUCCESS condition of every inverse TDD-red check - from being read as an error. Shown apart, an author copies one (design 38 SS4.1a).")
}

# The #428 anti-pattern, bound to a pinned sentence rather than the bare word 'rendered'.
$cprose = Get-Prose $catalogue
if ($cprose) {
    if ($cprose -notmatch [regex]::Escape('the rendered form is not the stored form')) {
        $problems.Add("[$catalogue] carries no rendered-vs-stored anti-pattern (#428). The entry must state 'the rendered form is not the stored form' verbatim - the bare word 'rendered' appears in unrelated prose, so keying on it would certify nothing.")
    }
    if ($cprose -notmatch 'ONE source line') {
        $problems.Add("[$catalogue] states no one-source-line rule. The #428 anti-pattern's first remedy is 'prefer a distinctive fragment that sits on ONE source line'; without it the entry names a trap and offers no way out.")
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Scan-order doctrine incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Every imperative backwards statement is corrected in place; the rendered-vs-stored anti-pattern is present; both generator preference lines are shown together."
exit 0
