# catches: a test file that satisfies the build + red census while never NAMING the headline behaviours
#          the action prompt enumerated - the cheap naming floor (#75) under the per-test census in 02.
#          Scoped to the ONE file this task authors.
#
#          The specific gap it closes: four rows can be red for the wrong reason. A file that drives some
#          OTHER seam, or that seeds only supplied[] and never refreshed[], is red on the current tree
#          just as convincingly as a correct one - and the decision being implemented
#          (d41-terminal-gate-names-supply) is explicitly "supplied AND refreshed content". A reader that
#          handles only one section is the defect UnauthoredContentNote's own doc comment says it exists
#          to prevent, and a test file that only ever constructs a SuppliedRecord cannot catch it.
#
#          This is a LOWER BOUND by construction (the #99 substance-floor class): a term inside a string
#          literal still matches, so it proves the file NAMES the behaviour, not that it ASSERTS it. The
#          gate that makes it worth having is 02's per-test red census.
#
# OPERATOR: -cnotmatch on every clause. C# identifiers and xUnit trait strings are case-SENSITIVE and
#          PowerShell -match is not, so a case-insensitive require-present clause false-GREENS on text
#          C# would never compile (taxonomy entry 3).
#
# MEASURED BASELINES: n/a - tests/Guardrails.Integration.Tests/Supply/SuppliedTerminalGateHaltTests.cs is
# CREATED BY THIS TASK, so there is no baseline to measure and no count is asserted here. (An honest
# "n/a" and a fabricated 0 look identical to a reviewer, so this says which it is, #478.) The
# ambient-vocabulary test still applies, and each token below was chosen to survive it: none is a
# namespace, a using, or a base type name that a file would acquire merely by existing in this project -
# PlanGuardrailPhase lives in Guardrails.Cli (this is a test project), and neither SuppliedRecord nor
# RefreshedRecord appears in any other file under tests/Guardrails.Integration.Tests/Supply/.
# This task has NO ancestors (dependsOn is empty), so no earlier task can pre-satisfy a clause.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'tests/Guardrails.Integration.Tests/Supply/SuppliedTerminalGateHaltTests.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the only legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. This task CREATES it - author the test file at exactly that path (the guardrail filters and task 18's census both key on the class SuppliedTerminalGateHaltTests)."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                 # NEVER matched against, never reassigned
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. Deriving $code by stripping
# comments straight from $raw lets a literal that spells '/*' forge a delimiter and blank real code before
# any literal pass runs. Only / and * INSIDE literals are replaced, so a literal's CONTENT still satisfies
# a required-present clause (a [Trait("Category", ...)] attribute value is the measured case, #470).
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', ' ')      # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')      # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once, so ONE attempt learns every gap.
$failures = @()

if ($code -cnotmatch '\[Trait\("Category", "OverwatchSupply"\)\]') {
    $failures += "$rel does not carry [Trait(`"Category`", `"OverwatchSupply`")]. That is plan 41's plan-wide trait and every task filter in this plan conjoins it; without it the --filter selects nothing, which exits 0 and would certify both halves of this pair on an empty set."
}

if ($code -cnotmatch '\bPlanGuardrailPhase\b') {
    $failures += "$rel never names PlanGuardrailPhase. The terminal gate IS PlanGuardrailPhase.EvaluateAsync - that is the seam task 18 changes and the only one that produces a RunHaltKind.PlanGuardrailFailed halt. A file that drives the Scheduler's WAVE gate instead would be red on this tree for an entirely different reason and would certify nothing about the terminal gate."
}

if ($code -cnotmatch '\bSuppliedRecord\b') {
    $failures += "$rel never constructs a SuppliedRecord. The headline the decision requires - 'supplied by overwatcher at <sha10>' - is rendered from a supplied[] entry, so the journal seeded into run.json must carry one."
}

if ($code -cnotmatch '\bRefreshedRecord\b') {
    $failures += "$rel never constructs a RefreshedRecord. The DECIDED answer to d41-terminal-gate-names-supply is that the terminal gate names supplied AND REFRESHED content, and UnauthoredContentNote reads BOTH sections precisely because a consumer that reads only one is the defect it exists to prevent. A file covering only supplied[] lets exactly that implementation through."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== covers-key-behaviors: $($failures.Count) enumerated behaviour(s) unnamed in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "covers-key-behaviors: the plan-wide trait, the PlanGuardrailPhase seam, and BOTH unauthored-content sections are named in $rel."
exit 0
