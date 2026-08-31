# catches: this task leaving RetryPolicy.AppendSalvageSection / AppendHeader `private static`. All
#          three of this task's other guardrails PASS if it does - the escalation path can preserve,
#          the shipped suites can stay green, and the build is fine, because nothing in THIS task's
#          four files needs the wider accessibility. The consumer is task 03's PromptComposer.
#
#          So the failure lands one task downstream, as a CS0122 on RetryPolicy.cs - a file task 03
#          may NOT write. Task 03's own 01-build-passes correctly redirects that to needsHuman rather
#          than letting it widen its scope, so the run HALTS on task 03 with task 02 fully green, and
#          tasks 09 and 10 are blocked behind it. On an unattended overnight run that is the whole
#          night. The defect is here; the diagnosis has to be here too.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is an
#          ACCESSIBILITY MODIFIER. It is invisible at runtime by construction - `internal` and
#          `private` behave identically for every existing caller, and the only thing that can observe
#          the difference is a compiler compiling a DIFFERENT task's file, which does not exist yet.
#          No test in this plan can carry it.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. Both clauses are
#          REQUIRED-present and read $code (comments gone, string literals intact), so a doc comment
#          explaining "this is internal for PromptComposer" cannot satisfy them.
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478):
#          'internal static void AppendSalvageSection' -> 0   (today it reads `private static`, :438)
#          'internal static void AppendHeader'         -> 0   (today it reads `private static`, :983)
#          Both are 0 as they should be for a task that has not run.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'src/Guardrails.Core/Execution/RetryPolicy.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# -cnotmatch on every required clause: C# keywords are case-SENSITIVE, and a case-insensitive
# require-present clause false-GREENS on text C# would never compile (taxonomy entry 3).
$required = @(
    @{ Pattern = 'internal\s+static\s+void\s+AppendSalvageSection'
       Member  = 'AppendSalvageSection'
       Why     = "task 03's PromptComposer.AppendPreviousAttempt calls it to render the PriorAttempt routing block. Plan section 3.3 is explicit that there is ONE owner of that text and never a second copy - which is only possible if this method is reachable from the Prompts namespace. Both types live in Guardrails.Core, so `internal` is exactly enough; do not make it public." },
    @{ Pattern = 'internal\s+static\s+void\s+AppendHeader'
       Member  = 'AppendHeader'
       Why     = "plan section 3.3 moves it in the same breath, and its existing four-way branch gains a fifth for preserved-but-not-rolled-back. Leaving it private strands that branch behind a wall task 03 cannot cross." }
)
foreach ($r in $required) {
    if ($code -cnotmatch $r.Pattern) {
        $current = if ($code -cmatch ('(private|public|protected)\s+static\s+void\s+' + $r.Member)) { " It currently reads '$($Matches[1]) static'." } else { " No 'static void $($r.Member)' declaration was found at all - do not rename or delete it." }
        $failures += "$rel does not declare '$($r.Member)' as 'internal static void'.$current $($r.Why)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== salvage-section accessibility: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This is caught HERE deliberately. Your other three guardrails all pass with these methods left private, because nothing in YOUR four files needs the wider accessibility - the consumer is task 03, whose build would then fail with CS0122 on a file it may not write, halting the run overnight with this task fully green."
    exit 1
}
Write-Output "Accessibility sound: AppendSalvageSection and AppendHeader are internal static, so task 03's PromptComposer can route through the one owner of the salvage text."
exit 0
