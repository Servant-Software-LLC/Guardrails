# catches: a contract that never reached the SSOT - the "profile":"lite" key undocumented in section 2's
#          guardrails.json block (or documented somewhere else in the file, where a reader of section 2
#          never sees it), GR2090 missing from the diagnostic-code table so the next author allocates it
#          for something else, or the next-free prose still telling that author to take GR2090. Also a
#          contract "recorded" inside an HTML comment, which renders as nothing (#468 doc-target rule).
# DOCUMENTATION target: exempt from the committed .valid/.invalid pair (no meaningful invalid sample of a
#          design doc); the PRECEDENT check applies instead - every demanded form has a sibling precedent
#          in this same file: the one-line `"version": 1, // ...` entry in the section-2 block, the
#          `| `GR2083` | — | RESERVED BY NAME ...` row, and the existing `should take **`GR2090`**` phrase.
# Fenced code blocks are NOT stripped (a fence renders; the section-2 block IS a fence).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Subject override (author-time smoke test, #302): argv[0], then GR_SUBJECT, else the real SSOT.
$subject = if ($args.Count -gt 0 -and $args[0]) { $args[0] } elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/02-schemas-and-contracts.md' }

# PRECONDITION - the only early exit: every clause below needs the file.
if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output "$subject not found - cannot check the Lite profile contract"
    exit 1
}
$raw = Get-Content -LiteralPath $subject -Raw
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
if ($doc.Contains('<!--')) {
    Write-Output "$subject has an unterminated '<!--' - refusing to strip to end of file; close the comment"
    exit 1
}

$failures = @()

# baseline counts on the untouched tree - MEASURED with Select-String / [regex]::Matches, 2026-09-29:
#   (?m)^\s*"profile":\s*"lite"  within section 2      0
#   (?m)^\|\s*`GR2090`\s*\|.*LiteUnsupportedFeature     0
#   should take \*\*`GR2091`\*\*                         0
#   should take \*\*`GR2090`\*\*  (forbidden)            1  - the phrase this task must change
# No ancestor task (this task has none) writes these tokens.

# Section 2 = from the '## 2. ' heading to the '## 3. ' heading.
$s2 = [regex]::Match($doc, '(?ms)^## 2\. .*?(?=^## 3\. )')
if (-not $s2.Success) {
    $failures += "could not find section 2 ('## 2. ' up to '## 3. ') in $subject - the headings this check anchors on have moved"
}
elseif ($s2.Value -cnotmatch '(?m)^\s*"profile":\s*"lite"') {
    $failures += "section 2's guardrails.json block has no line starting with `"profile`": `"lite`" - document the optional Lite marker key there (after `"version`": 1,), one line with its // comment"
}

if ($doc -cnotmatch '(?m)^\|\s*`GR2090`\s*\|[^\n]*LiteUnsupportedFeature') {
    $failures += "no diagnostic-code table row '| ``GR2090`` | ... LiteUnsupportedFeature ...' - add the RESERVED BY NAME row after the GR2089 row"
}

$n91 = [regex]::Matches($doc, 'should take \*\*`GR2091`\*\*').Count
if ($n91 -ne 1) {
    $failures += "the next-free prose must say 'should take **``GR2091``**' exactly once (found $n91)"
}

if ($doc -cmatch 'should take \*\*`GR2090`\*\*') {
    $failures += "the next-free prose still says 'should take **``GR2090``**' - GR2090 is now reserved for Guardrails Lite; point the next author at GR2091"
}

if ($failures.Count -gt 0) {
    Write-Output "=== SSOT does not yet record the Lite profile contract ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "SSOT records the profile key (section 2), the GR2090 reservation and the GR2091 next-free pointer"
exit 0
