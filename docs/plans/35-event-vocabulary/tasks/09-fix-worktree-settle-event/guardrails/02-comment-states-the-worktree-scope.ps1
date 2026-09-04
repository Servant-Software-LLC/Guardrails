# catches: the comment on Bug A's fix claiming the WRONG scope. The plan and the prompt each give this a
#          full paragraph, and the plan says shipping it false "is worse than shipping it uncommented":
#          the worktree needs-human settles (a failed union re-verify, an unresolvable AI-merge, a non-FF
#          integration) build NO AttemptRecord, so they still raise nothing after this fix. A comment
#          claiming "the default mode's only route to this event" tells the next reader that path is
#          covered when it is not - and that residual has its own issue precisely because it is not.
# Measured baseline (#478): 'AttemptFinished(' appears 0 times in Scheduler.cs today - that absence IS
#          Bug A - so the raise clause below is honestly red on arrival. The forbidden-phrase clause is a
#          BAN and is correctly green on arrival; bans are not censused (#478).
$ErrorActionPreference = 'Continue'
$path = 'src/Guardrails.Core/Execution/Scheduler.cs'

if (-not (Test-Path -LiteralPath $path)) {
    Write-Output "PRECONDITION: $path does not exist - every clause below would crash."
    exit 1
}

$raw = Get-Content -LiteralPath $path -Raw
# Strip line comments for the RAISE check only: the raise must be real code, while the wording clauses
# below deliberately read the COMMENT text (two-level stripping, Probe C's rule).
$code = [regex]::Replace($raw, '(?m)^\s*//.*$', '')
$failures = New-Object System.Collections.Generic.List[string]

if ($code -notmatch 'AttemptFinished\s*\(') {
    $failures.Add("Scheduler.cs raises AttemptFinished nowhere - Bug A is unfixed. Every clause below is moot until the raise lands in RecordSucceededSettle.")
}
else {
    if ($raw -match "default mode'?s only route") {
        $failures.Add("The comment claims 'the default mode's only route to this event'. That is FALSE: the worktree needs-human settles build no AttemptRecord and still raise nothing after this fix. Write 'the worktree SUCCESS path's only route to this event' instead.")
    }
    if ($raw -notmatch '(?i)worktree') {
        $failures.Add("The raise carries no comment naming the WORKTREE scope. The SSOT documents this serial-versus-worktree asymmetry at 15.2a; a reader who cannot tell which path this covers cannot know the needs-human residual is still open.")
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Bug A comment scope ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Scheduler.cs raises AttemptFinished, and its comment scopes the claim to the worktree success path."
exit 0
