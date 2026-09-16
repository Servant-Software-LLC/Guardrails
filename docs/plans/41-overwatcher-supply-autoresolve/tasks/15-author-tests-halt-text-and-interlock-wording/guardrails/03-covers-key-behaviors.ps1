# catches: a test file that satisfies the build + red census while never NAMING the headline behaviours
#          the action prompt enumerated - the cheap naming floor (#75) under the per-test census in 02.
#          Scoped to the ONE file this task authors.
#
#          This is a LOWER BOUND by construction (the #99 substance-floor class): a term inside a string
#          literal still matches, so it proves the file NAMES the behaviour, not that it ASSERTS it. The
#          gate that makes it worth having is 02's per-test red census; this one exists because the
#          census can be satisfied by five correctly-failing tests that quietly drop the banner and
#          option-description halves of the task.
#
# OPERATOR: -cnotmatch on every clause. C# identifiers and xUnit trait strings are case-SENSITIVE and
#          PowerShell -match is not, so a case-insensitive require-present clause false-GREENS on text
#          C# would never compile (taxonomy entry 3).
#
# MEASURED BASELINES on this task's branch point, against the SHIPPED
# tests/Guardrails.Integration.Tests/Supply/SuppliedHaltTextTests.cs (the file EXISTS today - this task
# EDITS it), each with its clause's own case sensitivity (Select-String -CaseSensitive, #478):
#   \[Trait\("Category", "OverwatchSupply"\)\]   0   (the file carries [Trait("Category", "Supply")] today;
#                                                    swapping it is a deliberate deliverable, not cosmetic)
#   RenderUndeliveredWorkWarning                 0   the banner seam is not driven by this file today
#   auto-supplied                                0   the new decision token appears nowhere in it
#   node_modules                                 0   the scoped-path case appears nowhere in it
# Every count above is 0 in the RAW file and therefore also 0 in the comment-stripped $code the clauses
# read, so no hit is hiding in a doc comment.
# No ANCESTOR task writes these tokens into this subject: task 15 has no ancestors that touch tests/**
# (dependsOn 03-implement-missing-resource-facts and 07-implement-delivery-interlock, whose writeScopes
# are src/Guardrails.Core/Execution/** only).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'tests/Guardrails.Integration.Tests/Supply/SuppliedHaltTextTests.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the only legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place, so its absence is not a coverage finding - guardrail 01 would have failed first if it were merely broken."
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
    $failures += "$rel does not carry [Trait(`"Category`", `"OverwatchSupply`")]. That is plan 41's plan-wide trait and every task filter in this plan conjoins it. The class carries [Trait(`"Category`", `"Supply`")] today: REPLACE it, do not add the new one alongside - this plan's baseline preflight excludes its own work by an exact --filter `"Category!=OverwatchSupply`" match, and a class carrying both traits is not reliably excluded by that !=."
}

if ($code -cnotmatch '\bRenderUndeliveredWorkWarning\b') {
    $failures += "$rel never calls RunCommand.RenderUndeliveredWorkWarning. Two of the five pinned behaviours are about the #597 delivery-interlock BANNER (design 41 §6 'Where it shows'), and they cannot be asserted without driving that render seam. A file covering only the halt-text half satisfies the red census and still drops half the task."
}

if ($code -cnotmatch 'auto-supplied') {
    $failures += "$rel never mentions the 'auto-supplied' decision token. Design 41 §6 adds it to the ONE shared delivery-interlock token set, and both banner behaviours are specified against a suppressing DecisionEntry carrying it - a banner test built on proceeded-best-guess instead re-tests what already shipped."
}

if ($code -cnotmatch 'node_modules') {
    $failures += "$rel never exercises a scoped node_modules path. MissingResourceHalt_MatchesAScopedNodeModulesPath_Whole is the row that catches a WRONG answer rather than a missing one: today's private regex excludes '@', so it matches a truncated interior substring of node_modules/@scope/x/index.js and prints a supply command pointing at a path that does not exist."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== covers-key-behaviors: $($failures.Count) enumerated behaviour(s) unnamed in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "covers-key-behaviors: the plan-wide trait, the banner seam, the auto-supplied token and the scoped-path case are all named in $rel."
exit 0
