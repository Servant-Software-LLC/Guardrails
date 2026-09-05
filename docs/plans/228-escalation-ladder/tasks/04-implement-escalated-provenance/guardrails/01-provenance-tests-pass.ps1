# catches: an implementation whose behavior deviates from the tests THIS task pair owns - chiefly a
#          mapping that conflates a CAPABILITY climb with an ESCALATION (keying TierSource.Escalated on
#          TierResolution.Climbed instead of on EscalatedFrom, which would source every no-candidate
#          climb as an escalation and make the journal unable to answer the one question this feature
#          exists to answer), and a wire token that round-trips only in one direction. The --filter
#          names this pair's OWN test class, never the plan-wide trait alone - a trait-only filter
#          asserts the state of every test in the plan, so this task could not go green until a task
#          that DEPENDS on it has run (a deadlock validate/graph --check cannot see, #455).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#
# MECHANICAL BACKSTOP (#221). The Climbed prohibition is stated in FOUR of this plan's prompts and was
#          backed by NOTHING mechanical - four prose restatements of a rule are four chances to be
#          persuasive and zero chances to be enforced. The clause below is the enforcement, and it runs
#          FIRST because it is free: an instant regex beats a two-minute dotnet test at telling the
#          agent it wired the wrong field.
# Required-forbidden baseline, MEASURED at authoring time (#478): `Climbed` in TierProvenance.cs = 0
#          hits across the whole file - comments included - so the clause is CLEAN ON ARRIVAL and any
#          hit it reports was introduced by this task.
# It anchors on a USE, not a mention (#470/#76): comments and string literals are stripped FIRST, so a
#          doc comment that explains the distinction ("not to be confused with TierResolution.Climbed")
#          and an exception message that names the field are both legal. What is left after the strip
#          is an identifier reference, which is the only thing that can key a mapping.

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$provenancePath = Join-Path $ws 'src/Guardrails.Core/Prompts/TierProvenance.cs'
if (Test-Path -LiteralPath $provenancePath) {
    $src = Get-Content -Raw -LiteralPath $provenancePath
    # Strip in this order: block comments, then raw/verbatim/regular string literals, then line
    # comments (which covers /// XML docs). Over-stripping can only WEAKEN this check; under-stripping
    # is what produces a false RED, so every construct that can legally carry the word is removed.
    $scan = [regex]::Replace($src,  '(?s)/\*.*?\*/', ' ')
    $scan = [regex]::Replace($scan, '(?s)""".*?"""', ' ')                   # raw string literal
    $scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', ' ')                 # verbatim string
    $scan = [regex]::Replace($scan, '"(?:\\.|[^"\\\r\n])*"', ' ')           # regular / interpolated
    $scan = [regex]::Replace($scan, '(?m)//.*$', ' ')                       # line + /// XML doc
    if ($scan -cmatch '\bClimbed\b') {
        Write-Output "TierProvenance.cs USES TierResolution.Climbed (outside comments and string literals). It must not. Climbed is a CAPABILITY fact - Candidates(RequestedTier) was empty, so the resolver walked up INSIDE ONE ATTEMPT - and escalation is a different fact: a PREVIOUS attempt of this task failed its guardrails. Keying TierSource.Escalated on Climbed sources every no-candidate climb as an escalation, and the journal then cannot answer the one question this feature exists to answer. Key the Escalated arm on EscalatedFrom, and leave Climbed to report itself independently. Naming Climbed in a doc comment or an exception message is fine - this clause strips both before it looks."
        exit 1
    }
}

$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=EscalationLadder&FullyQualifiedName~EscalatedProvenanceTests'   # verbatim from task 03's census
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = @()
    $emit = $false
    foreach ($line in $out) {                              # BLOCK capture, not a line allowlist (#608)
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40            # bound so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    Write-Output "EscalatedProvenanceTests failing - the escalated-provenance mapping is not implemented to spec (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against EscalatedProvenanceTests, the class task 03 authored."
    exit 1
}
exit 0
