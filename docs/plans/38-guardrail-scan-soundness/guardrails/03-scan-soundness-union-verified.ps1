# catches: an AI-merge that produced a syntactically-intact union which silently DROPPED or corrupted a
#          contribution - conflict markers left in a file, or a landed contribution present only as a
#          mention rather than a real construct. UNION-SAFE / CONDITIONAL (#125): every check is gated
#          on the artifact being present, so it passes trivially at an intermediate union where the
#          contributing task has not run yet. The conflict-marker scan is what credits GR2028; the
#          contribution-present checks are the ADDITIVE tightening layered on top (#343), never the
#          GR2028-satisfying content on their own.
# Measured baseline (#478): every contribution literal below counts 0 on the plan's starting tree
#          (verified 2026-09-06 on master a3f3e977) - each is gated behind a Test-Path/`-match` presence
#          check, so a zero count is the correct pre-work reading and the clause tightens as work lands.
$problems = New-Object System.Collections.Generic.List[string]

$subjects = @(
    'src/Guardrails.Core/Execution/GuardrailRunner.cs',
    'src/Guardrails.Core/Execution/InterpreterMap.cs',
    'tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs',
    '.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json',
    '.claude/skills/plan-breakdown/references/guardrail-catalogue.md',
    '.claude/skills/plan-breakdown/references/stacks/dotnet.md',
    '.claude/skills/plan-breakdown/SKILL.md',
    '.claude/skills/guardrails-review/SKILL.md',
    'docs/plans/02-schemas-and-contracts.md',
    'docs/plans/35-event-vocabulary/tasks/04-author-tests-event-vocabulary/guardrails/02-tests-fail-on-stubs.ps1'
)

# 1. CONFLICT-MARKER FREEDOM - the GR2028-crediting union-soundness proof. Line-anchored (#187): a real
#    ours/theirs marker writes at column 0, so the anchor is false-positive-free, and a bare '=======' is
#    deliberately NOT checked (it false-fires on a setext underline or an ASCII table rule).
foreach ($rel in $subjects) {
    if (-not (Test-Path -LiteralPath $rel -PathType Leaf)) { continue }   # union-safe: absent is fine
    $content = Get-Content -Raw -LiteralPath $rel
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        $problems.Add("[$rel] contains git conflict markers at column 0 - the union did not cleanly integrate.")
    }
    if ([string]::IsNullOrWhiteSpace($content)) {
        $problems.Add("[$rel] is present but EMPTY - a merge truncated it.")
    }
}

# 2. CONTRIBUTION-PRESENT (additive tightening, #343). Each is CONDITIONAL: it fires only once the
#    contributing task's marker has landed, so it cannot fail before that task has run.
$shimMarker = 'GUARDRAILS-ABORT'
$runner = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
if ((Test-Path -LiteralPath $runner -PathType Leaf)) {
    $c = Get-Content -Raw -LiteralPath $runner
    if ($c -match '97') {
        # the abort exit code landed here; require the VERDICT branch, not merely the number
        if ($c -notmatch 'Passed\s*=\s*false') {
            $problems.Add("[$runner] mentions the abort exit code but declares no Passed = false verdict - the abort branch survived the merge only as a number.")
        }
    }
}

$registry = '.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json'
if (Test-Path -LiteralPath $registry -PathType Leaf) {
    $raw = Get-Content -Raw -LiteralPath $registry
    foreach ($id in @('#608a', '#608b', '#561', '#449')) {
        if ($raw -match [regex]::Escape("`"$id`"")) {
            # the entry landed; require it to be a real entry, not a bare id in a comment
            $idx = $raw.IndexOf("`"$id`"")
            $window = $raw.Substring($idx, [Math]::Min(4000, $raw.Length - $idx))
            if ($window -notmatch 'badPattern' -or $window -notmatch 'mustMatch' -or $window -notmatch 'mustNotMatch') {
                $problems.Add("[$registry] entry '$id' is present but carries no badPattern/mustMatch/mustNotMatch - the merge kept the id and dropped the entry body.")
            }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Union invariant violated ($($problems.Count) problem(s)) ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Union intact: $($subjects.Count) subject(s) checked, conflict-marker-free, every landed contribution is a real construct."
exit 0
