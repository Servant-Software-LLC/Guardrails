# catches: a pin that names a member stage 2 or stage 3 has not written yet - SalvageFraming,
#          PriorAttemptRef.SalvagePatchPath, PriorAttemptRef.SalvageRefName, or a restrictToScope
#          argument. Plan 31 section 7 makes "every assertion on an observable artifact, naming no new API
#          member" a DELIBERATE CONSTRAINT ON THE TESTS, not an accident: it is the whole reason these
#          tests compile against today's assemblies and fail for the right reason, and it is what lets
#          section 13 stages 2 and 3 legitimately carry no tests/** path. The prompt states the prohibition;
#          this is the structural backing it needs (#221) - an adversarial or merely lazy
#          implementation is free to ignore a prohibition no guardrail enforces.
#
#          The failure it prevents is NOT a compile error (guardrail 01 catches those). It is the
#          quieter one: an agent that names a new member, discovers it does not compile, and then
#          "fixes" it by widening the change into src/** - an out-of-scope edit that burns a retry -
#          or by weakening the pin into something that compiles and proves nothing.
#
# ANCHORED ON A USE, NOT A MENTION (#470/#76): the scan runs over comment- AND string-literal-stripped
#          source, so a comment explaining "we deliberately do not construct a PriorAttemptRef carrying
#          SalvagePatchPath" is fine, and a line of code that does is not. Anything surviving that
#          strip is code, so a bare identifier occurrence there IS a use.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned; there are no
#          required-present clauses here, so only $scan (comments + literals gone) is derived and read.
#          A forbidden-present clause is EXEMPT from the #478 measured-baseline rule - a ban that is
#          green before its task has run is a HEALTHY ban. Measured on master @1490d2a anyway: both
#          target files are absent, so every clause is 0.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$targets = @(
    'tests/Guardrails.Core.Tests/Execution/EscalationSalvageTests.cs',
    'tests/Guardrails.Integration.Tests/EscalationSalvageTests.cs'
)

# Each ban: the identifier, and the sentence a retry agent needs to act on it.
$bans = @(
    @{ Token = 'SalvageFraming'
       Why   = "SalvageFraming is stage 2's deliverable (a defaulted parameter on RetryPolicy.AppendSalvageSection). Naming it here would not compile today. Assert on the composed prompt BYTES instead - the routing text is what a next attempt actually reads." },
    @{ Token = 'SalvagePatchPath'
       Why   = "PriorAttemptRef.SalvagePatchPath is stage 3's deliverable. Do not construct a PriorAttemptRef carrying it: lay down a log directory containing a real prior-attempt.patch and drive DependencyContextBuilder.BuildPriorAttempts, which is what fills the member in production." },
    @{ Token = 'SalvageRefName'
       Why   = "PriorAttemptRef.SalvageRefName is stage 3's deliverable. The ref name is DERIVED (refs/guardrails/<taskId>/attempt-<N>) - assert that string appears in the composed prompt, not that a property holds it." },
    @{ Token = 'restrictToScope'
       Why   = "restrictToScope is the optional parameter stage 2 adds to PreserveAttemptToRef and TryStashFailedAttempt. Calling it here would not compile. Assert the OUT-OF-SCOPE write is absent from the patch bytes and the ref tree instead - that is the observable the filter produces." }
)

# ACCUMULATE (#478): one distinguishable message per violation, dumped once, so ONE attempt learns
# every gap rather than one per attempt.
$failures = @()
$scanned  = 0

foreach ($rel in $targets) {
    $full = Join-Path $ws $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $failures += "PRECONDITION: $rel does not exist. This task authors it; guardrail 01 would have failed first if it were merely broken, so an absent file means the deliverable was not written."
        continue
    }
    $scanned++

    $raw  = Get-Content -Raw -LiteralPath $full          # NEVER matched against, never reassigned
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')      # /* */ block comments
    $code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')      # // and /// line comments
    $scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')     # C# 11 raw strings
    $scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')    # verbatim strings
    $scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')    # ordinary strings

    foreach ($ban in $bans) {
        $token = $ban.Token
        # Word-boundary identifier match over code with comments and literals already gone: anything
        # left is code, so an occurrence here is a USE. (?<!...)/(?!...) keep it from matching a
        # longer identifier that merely contains the token.
        $pattern = '(?<![A-Za-z0-9_])' + [regex]::Escape($token) + '(?![A-Za-z0-9_])'
        if ($scan -cmatch $pattern) {
            $failures += "$rel USES '$token' in code. $($ban.Why)"
        }
    }
}

if ($scanned -lt 1) {
    Write-Output "PRECONDITION: neither target test file exists, so this ban scanned nothing and certified nothing. Write the two test files first."
    exit 1
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== forbidden new-API references: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Do NOT resolve this by editing src/** - that is outside this task's writeScope and fails the task immediately. Rewrite the assertion against an observable artifact (a file on disk, a git ref, or a composed string)."
    exit 1
}
Write-Output "No new-API reference: $scanned file(s) scanned, none uses SalvageFraming, SalvagePatchPath, SalvageRefName or restrictToScope in code."
exit 0
