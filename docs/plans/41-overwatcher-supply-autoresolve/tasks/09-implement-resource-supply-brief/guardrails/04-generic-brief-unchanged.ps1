# catches: the new missing-resource vocabulary leaking into the GENERIC diagnose brief. Design 41 §2.3
#          says the resource-supply op is offered in the new brief ONLY - the generic brief is unchanged.
#          The cheapest way to make the new brief work is to add the op to the shared verdict block every
#          diagnose already sends, which is invisible to every test in this pair (they assert what the
#          NEW brief contains) and reaches EVERY eager, short-circuit and permission-wall diagnose in
#          every run. OverwatchFixClassifier would route such a proposal to Default (propose-only), so
#          nothing would act on it and nothing would fail - the harness would just start paying for a
#          fix vocabulary no consumer exists for, on every struggling task.
#
# SCOPED TO THE METHOD, not the file: BuildDiagnosePrompt's own body, extracted between its declaration
#          and the next method's (RenderAttemptHistory - measured: each declaration appears exactly once
#          in the comment-stripped source). A file-wide clause would be meaningless once this task adds
#          the new brief, which legitimately contains 'resource-supply'.
#
# DELIBERATE DEVIATION from the $scan rule for the forbidden clause (dotnet.md §11a), stated so a
#          reviewer can re-decide it: the forbidden token lives INSIDE a string literal (the brief text is
#          literals), so stripping literals would make the ban unfirable - the mirror dead-end §11a warns
#          about, wearing the other polarity. Both levels here therefore read $code (comments stripped,
#          literals intact).
#
# Author-time smoke test (#302), re-runnable (#468) - run BOTH halves after ANY edit to this file:
#   $D = 'docs/plans/41-overwatcher-supply-autoresolve/tasks/09-implement-resource-supply-brief/samples'
#   $env:GR_SUBJECT="$D/04-generic-brief-unchanged.valid.cs";   ./04-generic-brief-unchanged.ps1  # expect 0
#   $env:GR_SUBJECT="$D/04-generic-brief-unchanged.invalid.cs"; ./04-generic-brief-unchanged.ps1  # expect 1
#   Remove-Item Env:\GR_SUBJECT
#
# MEASURED BASELINES on the untouched tree, over $code within the extracted region (#478):
#   '# Overwatch diagnose'                    1  <- DECLARED NONZERO: every clause below is a
#   '"classification":"retryable\|doomed"'    1     tests-untouched REGRESSION clause. These are shipped
#   '"kind":"budget"'                         1     bytes that must SURVIVE this task, so "already
#   '"kind":"file-edit"'                      1     satisfied" is the required starting state, not a
#   '"kind":"task-field"'                     1     pre-satisfaction defect.
#   'resource-supply'                         0  <- the forbidden clause, deliberately NOT censused: a ban
#                                                   green on arrival is a CORRECT ban (#478 polarity rule).
#   No ancestor task's prompt or writeScope writes 'resource-supply' into this region: task 08, the only
#   ancestor touching Overwatch.cs, adds a throwing ProposeResourceSupplyAsync and nothing else.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# GR_SUBJECT is the `guardrails samples verify` contract: the verifier runs this script with the sample
# path in $env:GR_SUBJECT. It arrives ABSOLUTE, so joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/Overwatch.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the only early exit: every clause below would read an empty string and report the whole
# generic brief missing, a confident wrong message about code that may be untouched and correct.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. This is NOT a finding about the generic diagnose brief."
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

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# Extract BuildDiagnosePrompt's own body. The end anchor is the NEXT method's declaration, which survives
# comment-stripping - anchoring on '/// <summary>' would not, because the strip has already removed it.
$region = [regex]::Match(
    $code, '(?s)private static string BuildDiagnosePrompt.*?(?=private static string RenderAttemptHistory)')

if (-not $region.Success) {
    $failures += "$rel : could not find BuildDiagnosePrompt's body (expected it to sit between 'private static string BuildDiagnosePrompt' and 'private static string RenderAttemptHistory'). Either the generic diagnose brief was renamed/removed - which is itself the change this guardrail forbids - or the two helpers were reordered. Do not restructure the shipped brief to add a new one beside it."
}
else {
    $r = $region.Value

    # REQUIRED-present (tests-untouched regression clauses). -cnotmatch throughout: these are C# string
    # literals in a case-sensitive language, and a case-insensitive clause false-GREENS on text C# would
    # never compile (taxonomy 3).
    $required = [ordered]@{
        '# Overwatch diagnose'                 = "the generic brief no longer opens with '# Overwatch diagnose:'. That heading is how the §7 proof's fake CLI tells a generic diagnose from a resource-supply brief; changing it breaks the proof's controls silently."
        '"classification":"retryable\|doomed"' = 'the generic brief no longer asks for the retryable/doomed verdict line. Its wire shape is shipped behaviour and OverwatchProposal.TryParse reads it.'
        '"kind":"budget"'                      = 'the generic brief no longer offers the budget op. It is one of the four shipped fix shapes (guidance / budget / file-edit / task-field) and the #94 "a bigger budget would finish" case depends on it.'
        '"kind":"file-edit"'                   = 'the generic brief no longer offers the file-edit op - one of the four shipped fix shapes.'
        '"kind":"task-field"'                  = 'the generic brief no longer offers the task-field op - one of the four shipped fix shapes (classified DENYLIST, so it is routed to a human rather than applied).'
    }
    foreach ($pattern in $required.Keys) {
        if ($r -cnotmatch $pattern) {
            $failures += "$rel BuildDiagnosePrompt: $($required[$pattern])"
        }
    }

    # FORBIDDEN-present: the new vocabulary must not reach the generic brief.
    if ($r -cmatch 'resource-supply') {
        $failures += "$rel BuildDiagnosePrompt offers 'resource-supply'. Design 41 §2.3: that vocabulary belongs to the NEW missing-resource brief ONLY - the generic brief is unchanged. Every eager, short-circuit and permission-wall diagnose sends this brief, so a resource-supply op proposed there has no consumer (OverwatchFixClassifier routes it to Default, propose-only) and is billed on every struggling task. Put the op in ProposeResourceSupplyAsync's own brief."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== generic diagnose brief CHANGED: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "A run that never hits a missing-resource halt must send exactly the bytes it sends today. Add the new brief beside the shipped one; do not widen the shipped one to serve both."
    exit 1
}

Write-Output "Generic diagnose brief unchanged: its heading, verdict line and all four shipped op shapes are intact, and it offers no resource-supply op."
exit 0
