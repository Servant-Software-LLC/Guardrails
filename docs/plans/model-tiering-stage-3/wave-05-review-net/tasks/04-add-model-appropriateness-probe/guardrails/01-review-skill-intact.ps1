# catches: an edit to a 200 KB skill that DELETES more than it adds. This task's deliverable is three
#          insertions into a document every agent in this repo loads, delivered through
#          `needsHarnessWrite` - which also offers a full-`content` mode that would replace the whole file
#          with whatever the agent re-emitted. The prompt forbids it; this is the structural backing for
#          that prohibition (#221), and it is cheap enough to run before the expensive one.
#
#          It is deliberately BLUNT and deliberately not clever. Twelve section landmarks span the document
#          from its first heading to its last checklist line, plus the three anchor passages this task's
#          own `edits` entries attach to. A truncation, a mis-anchored replacement, or a full-content
#          re-emit that dropped a section takes at least one of them with it. What it does NOT do is judge
#          the new text - the anchor tests in 02 do that, and duplicating them here as regexes would be the
#          proxy the demotion order (#468) exists to remove.
#
# EVERY CLAUSE IS A REGRESSION CLAUSE, and its nonzero baseline is EXPECTED and NAMED (#478): this is the
# `tests-untouched` exception - the whole point is that these were present before this task ran and must
# still be present after. MEASURED BASELINE 2026-08-24 against the merged wave-4 HEAD, each pattern run
# against this exact file with this script's own operator: all 13 matched exactly 1, and the file was
# 201,740 bytes.
#
# CRLF NOTE, and it is not decoration: `.claude/skills/**` is not `eol=lf`-pinned, so this file is checked
# out with CRLF on Windows. A `(?m)^### 6\. Report$` clause therefore matches ZERO times - `$` sits before
# the `\n` but AFTER the `\r`. The first draft of this file shipped exactly that and the author-time
# measurement caught it; every anchored clause below ends `\s*$` for that reason. Do not "tidy" them.
#
# AUTHOR-TIME PROBE (#302): samples/01-review-skill-intact.probe.ps1 runs the valid half (the real skill,
# expect 0) and a truncation family (expect non-zero for each landmark removed).
$ErrorActionPreference = 'Continue'

$path = '.claude/skills/guardrails-review/SKILL.md'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the subject is gone, so every clause below would scan a null. This is the worst
    # outcome the check exists for, and it deserves its own sentence rather than 13 identical ones.
    # Single-quoted where it matters: a backtick inside a DOUBLE-quoted PowerShell string is the escape
    # character, so "needsHarnessWrite" written with backticks around it would emit a newline mid-message.
    Write-Output ("$path does not exist - the skill file this task edits has been DELETED. A needsHarnessWrite request never removes a file, so this is a merge or a workaround, not an edit.")
    exit 1
}

$text = Get-Content -Raw -Path $path
$failures = @()

$landmarks = @(
    @('(?m)^### 1\. Inventory\s*$', 'section 1 (Inventory)'),
    @('(?m)^### 2\. Adversarial pass per task \(the heart\)\s*$', 'section 2 (the adversarial pass) - the section this task INSERTS INTO'),
    @('(?m)^### 2b\. EXECUTE the guardrails', 'section 2b (execute the guardrails)'),
    @('(?m)^### 3\. DAG soundness\s*$', 'section 3 (DAG soundness)'),
    @('(?m)^### 4\. Missing-insertion check\s*$', 'section 4 (missing-insertion check)'),
    @('(?m)^### 5\. State-contract lint\s*$', 'section 5 (state-contract lint)'),
    @('(?m)^### 6\. Report\s*$', 'section 6 (Report) - the section this task INSERTS INTO'),
    @('(?m)^### 7\. Record the review', 'section 7 (record the review)'),
    @('(?m)^## Quality bar\s*$', 'the Quality bar - the section this task INSERTS INTO'),
    @('Model named but unservable', 'the #224 model-availability probe - this task''s nearest sibling, and the bullet its first insertion follows'),
    @('Missing / malformed positive-baseline \(preflight\) on a brownfield plan', 'the #181 baseline-preflight probe - the bullet this task''s first insertion is anchored BEFORE'),
    @("the model-availability probe's JIT-resolved judge models, deferred to #223;", 'the first item of section 6''s "At minimum:" list - the anchor this task''s second insertion attaches to'),
    @('No fix applied without explicit approval', 'the Quality bar line this task''s third insertion attaches to')
)

foreach ($landmark in $landmarks) {
    # Case-SENSITIVE, like this wave's two wave-root gates and for the reason recorded there: PowerShell's
    # `-match` family is case-INSENSITIVE, which is how a clause ends up satisfied by unrelated prose. Every
    # landmark here is an exact heading or an exact sentence from the document.
    if ($text -cnotmatch $landmark[0]) {
        $failures += "$path no longer contains $($landmark[1]) (/$($landmark[0])/) - this task ADDS three passages and removes nothing, so a missing landmark means an edit replaced content instead of inserting it"
    }
}

# Defence in depth against the one shape the landmarks alone would survive: every heading kept, the bodies
# gone. The floor is well below the measured 201,740 and this task only ever grows the file, so it has no
# path to a false red short of a deletion this large.
$size = (Get-Item $path).Length
if ($size -lt 190000) {
    $failures += "$path is $size bytes - it measured 201,740 on this wave's entry tree and this task only ADDS to it. A file this much smaller has lost content, whatever survived the landmark scan"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== review-skill intactness: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output 'Deliver the three insertions as three needsHarnessWrite "edits" entries in ONE request. Do NOT use full-"content" mode on this file: the harness refuses it above 64 KB anyway, and re-emitting a 200 KB document is how the sections above go missing.'
    exit 1
}
exit 0
