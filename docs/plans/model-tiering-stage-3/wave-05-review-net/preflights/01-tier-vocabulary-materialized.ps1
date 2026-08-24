# catches: this wave being authored against a tree that no longer matches. Every one of its four tasks
#          names a member of a named file by hand, and the brief is explicit that the tier vocabulary must
#          be read "as MATERIALIZED by Stage 2 and wave 1 - from the tree, not from this brief". This is
#          the WAVE ENTRY gate (SSOT 14.3): waves 1-4 are merged, and what wave 5 builds ON must be present
#          and real - verified ONCE at the boundary rather than discovered by an agent three attempts in.
#
#          Four groups carry more weight than the rest.
#
#          (1) The INSERTION SURFACE. `04-add-model-appropriateness-probe` inserts a probe into
#          guardrails-review/SKILL.md between two named headings and adds one line to section 6 and one to
#          the Quality bar. Four clauses pin those landmarks. If the skill was restructured, that task is
#          told to insert text between anchors that no longer exist, and `needsHarnessWrite` edits fail on a
#          missing anchor - atomically, so the task delivers nothing and burns the attempt.
#
#          (2) The VOCABULARY the probe describes and the audit reads. `ActionDefinition.TierOrigin` is the
#          load-bearing one and the least obvious: the plan-wide `tiering.defaultTier` is resolved AT LOAD
#          and reaches every untagged task, so an audit that read the RESOLVED tier would report every
#          configured plan as fully classified. TierOrigin is the field that survives that collapse
#          (DoR 12.4) and is the ONLY thing that makes the deterministic finding computable at all.
#          `GuardrailDefinition.Tier` is its judge-side twin - the frontmatter site a judge tag is written
#          to - and without it the judge half of the finding has no discharge and would fire on every
#          judge forever.
#
#          (3) The GATE PREDICATE the graceful skip reuses. `NoRoutingGolden.IsUnconfiguredForTiering` is
#          the codified "no routing block and no tiering block" test the Invariant-7 golden already runs.
#          The charter calls this wave's silence "Invariant 7's review-time counterpart", so the audit
#          restates THAT predicate rather than inventing a second spelling of the same fact.
#
#          (4) The PRECEDENTS both chains are told to follow. SeamProofPlacement is the shape of a
#          folder-observable audit that deliberately lives in tests/ and ships no validate code and no GR
#          code - which is exactly this wave's ruling. SeamDoctrineAnchorTests is the shape of a test that
#          reads a skill document, including the reflow-tolerant Normalize the new anchors reuse and the
#          existing row set that already pins clauses in THIS skill file. TestPaths + the seam fixture
#          folder are the shape of the committed fixture pair.
#
# POSITIVE and MONOTONE-SAFE (SKILL.md 9.2): every clause is assert-PRESENT. Nothing here asserts that
# wave 5's own output is absent - a segment only grows, so a "not yet present" clause in an ENTRY gate
# would flip false the moment an unrelated file landed. The absence assertions live in the wave EXIT gate,
# which is their correct home.
#
# CASE-SENSITIVE (`-cnotmatch`), like this wave's exit gate and for the reason recorded there: PowerShell's
# `-match` family is case-INSENSITIVE, which is how a clause ends up satisfied by unrelated prose and dead
# forever. Every anchor here is a C# identifier or an exact heading, so nothing is lost by demanding the
# case the tree actually uses.
#
# MEASURED BASELINE 2026-08-24 against the merged wave-4 HEAD (C:\.a\42519044\_integration), each pattern
# run against the exact file that clause scans, comment-stripped and case-sensitive exactly as below:
# every clause matched.
# That nonzero is EXPECTED and NAMED - a wave ENTRY preflight is one of the two legitimate
# green-on-arrival guardrails (#478). A clause that goes RED means the anchor moved and the task pointed at
# it is authored against a tree that no longer exists.
#
# AUTHOR-TIME PROBE (#302): samples/01-tier-vocabulary-materialized.probe.ps1 runs both halves.
$ErrorActionPreference = 'Continue'
$failures = @()

# FLAT triples, never a path-keyed hashtable of clause lists: PowerShell UNWRAPS a single-element array
# literal, so a file with exactly one clause would iterate as a STRING and `$clause[0]` would become its
# first CHARACTER - silently disabling that clause. Wave 2's entry gate shipped that bug and its
# author-time invalid sample caught it.
$anchors = @(
    # --- (1) the insertion surface `04-add-model-appropriateness-probe` edits ------------------------
    @('.claude/skills/guardrails-review/SKILL.md', '### 2\. Adversarial pass per task \(the heart\)',
      'the section-2 probe list the model-appropriateness probe is inserted INTO. Task 04 is told to add a probe that "reads like the ones already there"; without this heading there is no list to join'),
    @('.claude/skills/guardrails-review/SKILL.md', 'Model named but unservable',
      'the #224 model-availability probe - the nearest sibling in voice, scope and shape, and the bullet task 04 is told to place the new probe beside. It is also the probe whose "do not re-report what validate already says" paragraph the new one mirrors'),
    @('.claude/skills/guardrails-review/SKILL.md', '### 6\. Report',
      'the report section that gains the conditional unchecked-gap line. Its absence would leave task 04 inserting that line nowhere - and the line is the one place the graceful skip could be broken by a well-meant "state what you skipped" note'),
    @('.claude/skills/guardrails-review/SKILL.md', '## Quality bar',
      'the checklist that gains the model-appropriateness item. Task 04 appends to it; a restructured skill means the append target is gone'),

    # --- (2) the tier vocabulary the probe describes and the audit reads -----------------------------
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'enum\s+TierOrigin',
      'TierOrigin itself. The deterministic finding is computable ONLY because this enum records which SITE supplied a tier; without it the plan-wide default collapse (DoR 12.4) is unrecoverable and the audit would have to re-parse task.json by hand'),
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'public\s+TierOrigin\s+TierOrigin\s*\{',
      'ActionDefinition.TierOrigin - the property the audit reads. The enum existing without the property on the resolved action would leave the provenance unreachable from a loaded plan'),
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'public\s+string\?\s+Tier\s*\{',
      'ActionDefinition.Tier - the resolved rung. The audit reports it beside the origin so a finding can say "resolved medium, but from the plan default, and nobody classified this task"'),
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'public\s+string\?\s+Model\s*\{',
      'ActionDefinition.Model - one of the three pins that DISCHARGE the finding (the charter: "neither a difficulty tag nor an explicit action.model / action.effort pin")'),
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'public\s+string\?\s+Effort\s*\{',
      'ActionDefinition.Effort - the second discharging pin, and the one whose nuance the probe must state: effort ALONE is not a routing bypass, but it IS an explicit per-task model-shaping decision'),
    @('src/Guardrails.Core/Model/ActionDefinition.cs', 'public\s+string\?\s+Runner\s*\{',
      'ActionDefinition.Runner - the third discharging pin. DoR 6.1 makes action.runner a FULL pin that bypasses tier resolution entirely, so a task carrying one is not unclassified'),
    @('src/Guardrails.Core/Model/GuardrailDefinition.cs', 'public\s+string\?\s+Tier\s*\{',
      'GuardrailDefinition.Tier - the judge-side tag site (SSOT 4.2 frontmatter). LOAD-BEARING: it is the ONLY discharge a surviving prompt-judge guardrail has, since no plan-wide default stands behind it. If this field is gone, the judge half of the finding is unsatisfiable and would fire on every judge in every plan forever'),
    @('src/Guardrails.Core/Model/TieringConfig.cs', 'class\s+ActionTiers',
      'ActionTiers - the single source of truth for the easy|medium|hard tokens. The probe names the rungs and the audit must not fork their spelling'),
    @('src/Guardrails.Core/Model/TieringConfig.cs', 'public\s+string\?\s+DefaultTier\s*\{',
      'tiering.defaultTier - the plan-wide default whose LOAD-TIME collapse is the exact trap the deterministic finding is written around. No default, no trap, and the fixture pair that proves the audit survives it has nothing to configure'),
    @('src/Guardrails.Core/Model/RunConfig.cs', 'public\s+TieringConfig\?\s+Tiering\s*\{',
      'RunConfig.Tiering - how the audit reaches the tiering block from a loaded PlanDefinition (plan.Config.Tiering). Half of the graceful-skip gate reads it'),
    @('src/Guardrails.Core/Model/PromptRunnerConfig.cs', 'public\s+PromptRunnerRouting\?\s+Routing\s*\{',
      'PromptRunnerConfig.Routing - the OTHER half of the gate, and per DoR 4.2 the authoritative one: tiering is CONFIGURED iff at least one block declares routing'),
    @('src/Guardrails.Core/Model/PromptRunnerConfig.cs', 'public\s+bool\?\s+Costly\s*\{',
      'the costly TRI-STATE (bool?, where null is "not stated" and distinct from false). A standing ruling the brief forbids re-litigating; the probe must not describe it as a boolean'),

    # --- the three warnings wave 1 landed, which the probe must NOT re-report ------------------------
    @('src/Guardrails.Core/Loading/DiagnosticCodes.cs', 'TieringInert\s*=\s*"GR2049"',
      'GR2049 - tags present but no routing block anywhere. It is the diagnostic that already covers the "tagged but inert" plan, so the probe must defer to it rather than emit a second opinion on the same config'),
    @('src/Guardrails.Core/Loading/DiagnosticCodes.cs', 'NonRoutableBlockIsDefault\s*=\s*"GR2051"',
      'GR2051 - wave 1''s first warning. The brief says the probe may cite the wave-1 codes; a code it cites must exist'),
    @('src/Guardrails.Core/Loading/DiagnosticCodes.cs', 'CostlyBlockRoutingInert\s*=\s*"GR2052"',
      'GR2052 - wave 1''s second warning'),
    @('src/Guardrails.Core/Loading/DiagnosticCodes.cs', 'PinAndTierCoexist\s*=\s*"GR2053"',
      'GR2053 - wave 1''s third warning, and the closest neighbour of all: it fires when a full pin and a tier COEXIST, while this probe fires when NEITHER is present. Two codes at opposite ends of the same axis, and the probe must not be written as if it owned both'),

    # --- (3) the gate predicate the graceful skip reuses ---------------------------------------------
    @('tests/Guardrails.Integration.Tests/ModelTiering/NoRoutingGolden.cs', 'IsUnconfiguredForTiering',
      'the codified "no routing and no tiering block" predicate the Invariant-7 golden already runs. The charter calls this wave''s silence Invariant 7''s review-time counterpart, so the audit restates THIS predicate rather than inventing a second spelling of the same fact'),

    # --- (4) the precedents both chains are told to follow -------------------------------------------
    @('tests/Guardrails.Core.Tests/SeamProofPlacement.cs', 'class\s+SeamProofPlacement',
      'the shape of a folder-observable audit that deliberately lives in tests/ and ships NO validate code and NO GR code. That is this wave''s ruling too, and this type is the worked answer to "then where does the rule live?"'),
    @('tests/Guardrails.Core.Tests/SeamDoctrineAnchorTests.cs', 'private static string Normalize',
      'the reflow-tolerant, wording-sensitive normalizer the new doctrine anchors reuse. Without it an anchor breaks on a re-wrap, which is how an anchor set gets deleted for being noisy'),
    @('tests/Guardrails.Core.Tests/SeamDoctrineAnchorTests.cs', 'ReviewSkill = "\.claude/skills/guardrails-review/SKILL\.md"',
      'the existing anchor rows that already pin clauses in THIS skill file. They are the precedent for voice and granularity, and they are also what will catch task 04 if its edit truncates the sections they pin'),
    @('tests/Guardrails.Core.Tests/TestPaths.cs', 'Path\.Combine\(ProjectDir, "TestData", relative\)',
      'TestPaths.Fixture - how a committed fixture is addressed. The new fixture pair is read in place through it, exactly as the seam fixtures are')
)

# One read per distinct file, so a 25-clause sweep is 11 reads and a missing file is reported once.
# C# comments are STRIPPED before the scan, because several anchors are member names that also appear in
# the doc comment right above them - `IsUnconfiguredForTiering` and `TierOrigin` both do - so without the
# strip a deleted member whose comment survived would read here as present. The markdown skill is NOT
# stripped: `//` is ordinary prose there.
$cache = @{}
foreach ($clause in $anchors) {
    $path = $clause[0]
    if (-not $cache.ContainsKey($path)) {
        if (Test-Path $path -PathType Leaf) {
            $raw = Get-Content -Raw -Path $path
            $cache[$path] = if ($path -like '*.cs') {
                ($raw -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
            }
            else { $raw }
        }
        else {
            # PRECONDITION for this file: it is gone, so every clause against it would scan a null.
            # Report the file ONCE and skip its clauses; other files still run.
            $cache[$path] = $null
            $failures += "$path does not exist - a task in this wave is authored against it, so this wave is pointed at a tree that no longer matches"
        }
    }

    $content = $cache[$path]
    if ($null -eq $content) { continue }

    if ($content -cnotmatch $clause[1]) {
        $failures += "$path no longer matches /$($clause[1])/ (case-sensitively) - $($clause[2])"
    }
}

# --- the fixture-shape precedent, asserted as a DIRECTORY ------------------------------------------
# Not a regex clause: what `01-author-tests-tier-classification-audit` is told to copy is a committed
# fixture FOLDER LAYOUT (guardrails.json + tasks/<id>/{task.json, action.*, guardrails/NN.ps1}), and the
# instruction "mirror the seam fixture pair" names nothing if the pair is gone.
$precedent = 'tests/Guardrails.Core.Tests/TestData/seam-proof-at-tstar'
if (-not (Test-Path $precedent -PathType Container)) {
    $failures += "$precedent does not exist - it is the committed two-sided fixture pair the new tier-tags fixtures are modelled on, and the only in-repo example of a plan folder built as test data"
}

# The directory the new test files land in. `dotnet` would create it, but its ABSENCE would mean the
# ModelTiering test family this wave joins was moved or renamed, and both chains name that path by hand.
# NOT named $home: PowerShell's $HOME is a read-only automatic variable and assigning to it throws.
$testFamilyDir = 'tests/Guardrails.Core.Tests/ModelTiering'
if (-not (Test-Path $testFamilyDir -PathType Container)) {
    $failures += "$testFamilyDir does not exist - both of this wave's test-author tasks write into it by name, and it is the family (ActionTierTests, PerTierSpendTests, ModelsUsedSummaryTests) the new tests sit beside"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-5 entry gate: $($failures.Count) precondition(s) this wave is authored against have MOVED ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave's tasks name these members and paths by hand. Re-run /plan-breakdown for wave-05-review-net against the current integration worktree rather than letting an agent rediscover the drift one failed attempt at a time."
    exit 1
}
exit 0
