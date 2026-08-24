# catches: a merged wave HEAD from which wave 5's work has VANISHED - and, much more likely here, one from
#          which HALF of it has. Both siblings in this folder pass perfectly on the tree this wave STARTED
#          from: the solution builds and both suites are green with none of this work in them. Between them
#          they carry zero positive evidence that anything was delivered. This gate is the additive
#          contribution-present half the catalogue requires on top of them.
#
#          Four clauses earn their place beyond bookkeeping.
#
#          (1) The SKILL clauses. This wave's whole point is prose in a document, and prose is the one
#          deliverable a build cannot see and a suite only sees indirectly. `Model-appropriateness` is the
#          probe itself; `NOTHING AT ALL` is the graceful-skip sentence, which is the requirement most
#          likely to be softened by a well-meaning editor and the one the charter is bluntest about;
#          `MISSING-CLASSIFICATION` is the Quality bar item, without which the probe is prose nobody is
#          checked against.
#
#          (2) The TWO FIXTURE FOLDERS, asserted as directories holding a plan. They are the only
#          deliverable in this wave that is neither code nor prose, and a merge that dropped them leaves
#          TierClassificationAuditTests failing for a reason that reads as a bug in the audit.
#
#          (3) The NEGATIVE clause, and it is the only one that can fail because a task did too much: this
#          wave ships NO src/ change at all. The audit is a reference implementation that lives in tests/
#          precisely because the ruling is that no validate code and no diagnostic code is allocated. No
#          task in this wave has src/ in its writeScope, so the per-task write-scope check already refuses
#          it - but nothing else re-checks the MERGED tree, and a green build with the audit sitting in
#          Guardrails.Core would look like success.
#
#          (4) The three CLASS declarations, comment-stripped. Every prompt in this wave mandates doc
#          comments, so a member dropped by an AI-merge whose doc comment survived would otherwise read
#          here as delivered - the exact defect wave 2's gate probe found live.
#
# LOCAL - no `scope` key (GR2059/#459), like its siblings: a wave-root guardrail runs exactly once, on the
# merged HEAD at its own wave's exit, and the per-union set is the task folders plus the PLAN root.
#
# CASE-SENSITIVE (`-cnotmatch`), and this is not stylistic. The first draft used `-notmatch`, and the
# author-time run against the entry tree found the `NOTHING AT ALL` clause GREEN: `.claude/skills/guardrails-review/SKILL.md`
# already carries the ordinary sentence "reports nothing at all" a thousand lines away, and PowerShell's
# `-match` family is case-INSENSITIVE, so that clause could never have fired however far the graceful-skip
# sentence drifted. It was the one clause of eight that was dead, and it hid behind its seven failing
# siblings - one exit code, many clauses (#478). Every clause here is an exact-case token the wave's own
# tasks write verbatim, so the whole scan is case-sensitive. Do not relax it.
#
# MEASURED BASELINE 2026-08-24 against the merged wave-4 HEAD, each pattern run against the exact file that
# clause scans with this script's own case-SENSITIVE operator: every required clause below is 0, or its
# file/directory is absent entirely. Tree-wide, `TierClassificationAudit`, `ModelAppropriatenessDoctrineAnchorTests`,
# `tier-tags-configured` and `MISSING-CLASSIFICATION` occur in NO file under src/, tests/ or .claude/ - so
# no stale copy anywhere can satisfy a clause. This gate is correctly RED before the wave runs.
#
# AUTHOR-TIME PROBE (#302): samples/03-wave-deliverables-present.probe.ps1 runs both halves.
$ErrorActionPreference = 'Continue'
$failures = @()

$required = @(
    # --- the audit, its tests, and the anchors (01, 02, 03) ---
    @('tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs', 'class\s+TierClassificationAudit\b',
      'the audit is not on the merged HEAD at all - the file is missing or no longer declares the type'),
    @('tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs', 'IsTieringConfigured',
      'the audit has no IsTieringConfigured - that member IS the graceful skip, and without it there is no gate between a legacy plan and a wall of findings'),
    @('tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAuditTests.cs', 'class\s+TierClassificationAuditTests\b',
      'the audit test class is not on the merged HEAD - the suites prove the tests PASS, nothing else proves they EXIST, and a suite with the file deleted is green'),
    @('tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs', 'class\s+ModelAppropriatenessDoctrineAnchorTests\b',
      'the doctrine-anchor class is not on the merged HEAD - it is the ONLY durable regression signal the skill text has, so its loss silently retires the probe'),

    # --- the prose (04) ---
    @('.claude/skills/guardrails-review/SKILL.md', 'Model-appropriateness',
      'the review skill does not carry the model-appropriateness probe - the wave''s central deliverable is missing from the document every reviewing agent loads'),
    @('.claude/skills/guardrails-review/SKILL.md', 'NOTHING AT ALL',
      'the graceful-skip sentence is gone. A plan generated before tiering shipped must produce NO finding, NO unchecked-gap line and NO note saying it was skipped; softening that is how a check ends up firing on every legacy plan, and a check that fires on every legacy plan gets muted within a week'),
    @('.claude/skills/guardrails-review/SKILL.md', 'MISSING-CLASSIFICATION',
      'the Quality bar item is gone - the probe is then prose nobody is checked against, which is the difference between a rule and a suggestion')
)

foreach ($clause in $required) {
    $path = $clause[0]
    if (-not (Test-Path $path -PathType Leaf)) {
        # PRECONDITION for this clause only: the file is gone, so the scan below would read a null. Other
        # clauses still run - this is an accumulating gate, not an exit-1 chain.
        $failures += "$path does not exist on the merged HEAD - a deliverable file of this wave is missing entirely"
        continue
    }
    $text = Get-Content -Raw -Path $path
    # Strip C# comments before a REQUIRED scan. Without it several clauses are satisfied by a comment
    # ALONE - `// TODO: class TierClassificationAudit` matches even the declaration pattern - and that is
    # fatal to this gate specifically, because its whole job is catching a hunk that vanished in the wave
    # merge, and every prompt in this wave MANDATES a doc comment on the member it checks.
    # Comments ONLY, never string literals (#470, the two-level rule); and NOT applied to the markdown
    # skill, where `//` is ordinary prose.
    if ($path -like '*.cs') {
        $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    }
    if ($text -cnotmatch $clause[1]) {
        $failures += "$path does not match /$($clause[1])/ (case-sensitively) - $($clause[2])"
    }
}

# --- the two fixture folders, asserted as directories holding a plan ------------------------------
foreach ($fixture in @('configured', 'untagged')) {
    $dir = "tests/Guardrails.Core.Tests/TestData/tier-tags/$fixture"
    if (-not (Test-Path $dir -PathType Container)) {
        $failures += "$dir does not exist on the merged HEAD - it is half of the committed two-sided fixture pair, and without it TierClassificationAuditTests fails for a reason that reads as a bug in the audit"
    }
    elseif (-not (Test-Path (Join-Path $dir 'guardrails.json') -PathType Leaf)) {
        $failures += "$dir exists but holds no guardrails.json - it is not a loadable plan folder, so the audit has nothing to be run against"
    }
}

# --- the negative clause: this wave ships NO src/ change ------------------------------------------
# The only check in this wave that can fail because a task did TOO MUCH. Forbidden-present, so it is
# correctly GREEN on arrival and needs no baseline count (#478).
$leaked = @(Get-ChildItem -Path 'src' -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Where-Object { (Get-Content -Raw -Path $_.FullName -ErrorAction SilentlyContinue) -match 'TierClassificationAudit' })
if ($leaked.Count -gt 0) {
    $names = ($leaked | ForEach-Object { $_.FullName }) -join '; '
    $failures += "TierClassificationAudit appears under src/ ($names) - this wave ships NO harness change. The audit is a reference implementation living in tests/ precisely because no validate code and no diagnostic code is allocated for a model-quality opinion; a copy in src/ is that ruling being reversed by accident"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-5 deliverables: $($failures.Count) problem(s) on the merged HEAD ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The build and both suites pass on a tree with none of this work in it, so a green from them is not evidence of delivery. Something dropped between the task segments and the wave merge."
    exit 1
}
exit 0
