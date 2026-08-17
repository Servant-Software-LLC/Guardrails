# catches: a suite that is red but covers ONE shape (say DNS) and skips the negative control - which
#          clears both siblings while leaving task 04 free to classify anything containing "connect"
#          or "resolve" as Transient. That failure is the expensive direction: a genuine logic failure
#          would then ride the pause machinery instead of consuming its retry budget, so the task
#          never surfaces and the run stalls on a wall no backoff can clear.
#          Also enforces the prompt's explicit prohibition (#221): NO new probe enum in v1.
#          Structural, scoped to the ONE file this task owns.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Core.Tests/ModelTiering/ConnectionUnavailabilityClassificationTests.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the test file this task owes was never written"
    exit 1
}
$content = Get-Content -Raw $file

if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
    $failures += 'the class carries no [Trait("Category", "TierResolution")] - required so the plan-root baseline preflight excludes it and this wave''s filters select it'
}
if ($content -notmatch '\[(Fact|Theory)\]') {
    $failures += 'no [Fact]/[Theory] attribute - the file declares no executable test'
}

# The four unavailability shape groups, each evidenced by a REAL error string rather than the phrase
# the classifier greps for. One representative token per group is enough to prove the group is there.
$shapes = @(
    @{ Name = 'DNS resolution failure';   Pattern = '(?i)(getaddrinfo|ENOTFOUND|could not resolve host|name or service not known|EAI_AGAIN)' },
    @{ Name = 'connection refused/reset'; Pattern = '(?i)(ECONNREFUSED|ECONNRESET|connection refused|connection reset)' },
    @{ Name = 'TLS / handshake failure';  Pattern = '(?i)(tls|ssl|handshake|certificate)' },
    @{ Name = 'missing CLI at launch';    Pattern = '(?i)(cannot find the file specified|no such file or directory|command not found|is not recognized as an internal)' }
)
foreach ($shape in $shapes) {
    if ($content -notmatch $shape.Pattern) {
        $failures += "no realistic error text for the '$($shape.Name)' shape - DoR 6.3 names it explicitly, and a shape nobody asserts is a shape that keeps burning the retry budget"
    }
}

# The expected classification, and the NEGATIVE CONTROL. The control is the load-bearing one: without
# it every other row can be satisfied by matching a single substring.
if ($content -notmatch 'PromptFailureKind\.Transient') {
    $failures += 'PromptFailureKind.Transient is never asserted - 6.3 routes connection-level failures to the SHIPPED #115 pause, so Transient is the expected classification for every shape'
}
if ($content -notmatch 'PromptFailureKind\.Error') {
    $failures += 'no negative control asserting PromptFailureKind.Error - without one, task 04 can pass every shape by classifying anything containing "connect"/"resolve" as Transient, and a genuine logic failure would then loop on the pause machinery instead of consuming its budget and surfacing'
}

# NEGATIVE ASSERTION (#176): the prompt FORBIDS inventing a new probe/classification member. 6.3:
# "No new probe enum is introduced in v1". A fail-on-present check is the only thing that stops the
# suite from specifying one into existence, and GR2026 stays correctly silent on it (it flags only
# POSITIVE require-present coverage tokens).
if ($content -match '(?i)\bUnavailable\b') {
    $failures += 'the file references an "Unavailable" classification - DoR 6.3 says NO new probe enum in v1: a connection-level failure is classified with the SHIPPED PromptFailureKind.Transient and rides the existing #115 pause. Remove it'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== ConnectionUnavailabilityClassificationTests coverage: $($failures.Count) gap(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "the authored suite does not yet answer the DoR 6.3 open question across all four connection-level shapes with a negative control. Fix the gaps listed above."
    exit 1
}
exit 0
