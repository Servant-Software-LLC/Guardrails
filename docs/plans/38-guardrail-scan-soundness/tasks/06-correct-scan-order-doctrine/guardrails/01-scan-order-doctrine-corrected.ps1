# catches: a doctrine correction that landed in one file and not the others - the failure mode this
#          plan is about. The rule lives in at least six places that currently state it BACKWARDS, and
#          fixing the catalogue while leaving stacks/dotnet.md saying "strip comments first" leaves the
#          next author reading the wrong one. Also catches the generator rule landing without its second
#          preference line, which would abort every inverse TDD-red check on a box where
#          $PSNativeCommandUseErrorActionPreference is $true (design 38 SS4.1a).
# DOCUMENTATION target: exempt from the two-sided sample pair (#468) - no meaningful invalid sample of a
#          prose document exists. The compensating controls are the <!-- --> strip below (a required
#          clause over a .md false-PASSES on a commented-out line, which is the doc-target hole) and the
#          PRECEDENT check: every literal demanded here is a phrase the task prompt pins verbatim, so
#          prompt and guardrail cannot drift (GR2026).
# Measured baseline (#478): every required literal below counts 0 across .claude/skills/ on the starting
#          tree (verified 2026-09-06 on master a3f3e977).
$ErrorActionPreference = 'Stop'

$problems = New-Object System.Collections.Generic.List[string]

$skill     = '.claude/skills/plan-breakdown/SKILL.md'
$catalogue = '.claude/skills/plan-breakdown/references/guardrail-catalogue.md'
$stack     = '.claude/skills/plan-breakdown/references/stacks/dotnet.md'

function Get-Prose([string]$path) {
    # A required-present clause over a .md must strip HTML comments before matching: <!-- ... --> renders
    # as NOTHING, so a commented-out line satisfies a naive grep and the clause false-PASSES (the
    # doc-target hole). Fenced code blocks are deliberately NOT stripped - a fence RENDERS, so a rule
    # stated inside a usage fence is legitimate house style.
    $raw = Get-Content -Raw -LiteralPath $path
    $stripped = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
    if ($stripped -match '<!--') {
        return $null   # an unterminated <!-- : stripping to EOF would delete the rest of the document
    }
    return $stripped
}

$sentence = 'Neutralize string literals BEFORE stripping comments'

foreach ($f in @($skill, $catalogue, $stack)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) {
        $problems.Add("[$f] does not exist - every clause below would be vacuous.")
        continue
    }
    $prose = Get-Prose $f
    if ($null -eq $prose) {
        $problems.Add("[$f] contains an unterminated '<!--'. The HTML-comment strip cannot run without deleting the rest of the document, so no clause over this file can be trusted.")
        continue
    }
    if ($prose -notmatch [regex]::Escape($sentence)) {
        $problems.Add("[$f] does not carry the ordering rule. Every site that states the preprocessing order must carry the sentence '$sentence' verbatim, so the rule is greppable and a reader landing on any one site gets all of it (#561).")
    }
}

# The corrected idiom must neutralize the DELIMITER characters, not just braces - neutralizing braces
# alone leaves `/` and `*` intact, which is exactly how the shipped form fails.
$cprose = Get-Prose $catalogue
if ($cprose) {
    if ($cprose -notmatch [regex]::Escape('the rendered form is not the stored form')) {
        $problems.Add("[$catalogue] carries no rendered-vs-stored anti-pattern (#428). The entry must state 'the rendered form is not the stored form' verbatim - the bare word 'rendered' appears in unrelated prose, so keying on it would certify nothing. A guardrail matching a phrase as it READS rather than as it is STORED silently passes when the target is split across source-line literals - a false-PASS, the expensive direction.")
    }
    if ($cprose -notmatch 'ONE source line') {
        $problems.Add("[$catalogue] states no one-source-line rule. The #428 anti-pattern's first remedy is 'prefer a distinctive fragment that sits on ONE source line'; without it the entry names a trap and offers no way out.")
    }
}

# The generator rule: BOTH preference lines, together. The second is the one that gets dropped.
$sprose = Get-Prose $skill
$kprose = Get-Prose $stack
$generator = @($sprose, $kprose) | Where-Object { $_ }
$hasNative = @($generator | Where-Object { $_ -match [regex]::Escape('PSNativeCommandUseErrorActionPreference') }).Count
# NOT asserted: that "ErrorActionPreference = 'Stop'" appears at all. Measured (#478): it already
# occurs in BOTH files on the starting tree, so a clause requiring it is satisfied before the task
# runs and certifies nothing. What is new is the SECOND line, and the two appearing TOGETHER.
$adjacent = @($generator | Where-Object {
    $_ -match "(?s)ErrorActionPreference = 'Stop'.{0,200}PSNativeCommandUseErrorActionPreference"
}).Count
if ($adjacent -lt 1) {
    $problems.Add("neither $skill nor $stack shows the two preference lines TOGETHER. The emitted guardrail opening is a PAIR: 'Stop' makes an engine error terminate instead of failing open, and the native-command line keeps a non-zero dotnet exit from being read as an error. Shown apart, an author copies one (design 38 SS4.1a).")
}
if ($hasNative -lt 1) {
    $problems.Add("neither $skill nor $stack pins PSNativeCommandUseErrorActionPreference. 'Stop' alone is not the rule: on a box where that preference is `$true, a non-zero `dotnet test` - the SUCCESS condition of every inverse TDD-red check - becomes a terminating error and aborts the check (design 38 SS4.1a).")
}

if ($problems.Count -gt 0) {
    Write-Output "=== Scan-order doctrine incomplete ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Ordering rule present in all three files; rendered-vs-stored anti-pattern present; both generator preference lines pinned."
exit 0
