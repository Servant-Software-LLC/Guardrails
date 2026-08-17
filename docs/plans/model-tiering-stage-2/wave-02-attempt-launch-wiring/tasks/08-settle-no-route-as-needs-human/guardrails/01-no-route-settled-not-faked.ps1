# catches: the two cheapest wrong implementations of no-route, both of which can make the behavioural
#          clause pass for the wrong reason or be introduced later without anything noticing.
#          (1) An EXCEPTION instead of a settle. TierResolution's own doc says it is deliberately a
#              RESULT, "not an exception the caller cannot tell from a bug" - throwing would surface
#              as a generic action-failed and burn the retry budget on a config gap no retry fixes.
#          (2) A silent FALLBACK. Turning NoRoute into a launch on promptRunners.<name>.model is
#              exactly the revision-4 reading D30 severed, and it is the single most natural
#              "make the red test go away" edit in this file.
#          Cheap and structural; the sibling 02 guardrail drives the real path.
$ErrorActionPreference = 'Continue'
$file = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist"
    exit 1
}
$content = Get-Content -Raw $file

# POSITIVE: the branch exists and settles through the distinct outcome.
if ($content -notmatch '\bNoRoute\b') {
    $failures += 'TaskExecutor never references NoRoute - the resolution''s no-candidate branch is still unhandled (task 07 left a `// task 08 settles NoRoute` marker for you)'
}
if ($content -notmatch 'AttemptOutcome\.NoRoute') {
    $failures += 'nothing settles AttemptOutcome.NoRoute - 12.4 gives this its OWN attempt outcome precisely so a human (and #9 triage) sees a routing config gap rather than a generic action failure'
}

# POSITIVE: the operator message is actionable and names the rung. 12.4 pins the shape.
if ($content -notmatch '(?i)serving tier') {
    $failures += 'no "register a provider serving tier >= <rung>" style message - 12.4 pins an ACTIONABLE reason naming the rung; "no route" alone tells an operator nothing about what to change'
}

# NEGATIVE (#176/#221): the marker left for the ordinary needs-human path must not be the vehicle.
# The task prompt forbids both wrong implementations; a prose prohibition with no structural backing
# is free to break.
if ($content -match '(?s)NoRoute[^;{}]{0,200}throw\s+new') {
    $failures += 'a NoRoute result appears to be THROWN - TierResolution is a RESULT by design, "not an exception the caller cannot tell from a bug". Settle it as needs-human with the distinct no-route outcome instead'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== no-route settle: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
