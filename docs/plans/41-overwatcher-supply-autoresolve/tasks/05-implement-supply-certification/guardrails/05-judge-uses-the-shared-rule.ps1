# catches: a GateThreshold.Effective that is ADDED beside CriticalityJudge's private
#          EffectiveThreshold instead of REPLACING it. Every test in the plan passes either way - the
#          judge's own tests exercise its behaviour, not its internals, and Certify's tests exercise
#          the new rule - so the duplication survives silently and design 41 §2.1's whole point ("the
#          rule is spelled twice, and both are replaced") quietly does not happen. Two copies of one
#          threshold rule is how the per-gate override comes to be honoured at one gate and ignored
#          at another, which is precisely the bug control C7 exists to catch at the other end.
#
#          SCOPE: CriticalityJudge.cs only. Scheduler.EffectiveThresholdToken is the OTHER duplicate
#          spelling and it belongs to the wiring task - Scheduler.cs is outside this task's
#          writeScope, so demanding it here would be unsatisfiable by construction.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/CriticalityJudge.cs" }

$failures = @()
if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }   # PRECONDITION - the only early exit

# Variable discipline: $raw is NEVER matched against; the REQUIRED clause reads $code, the FORBIDDEN
# clause reads $scan. Literals neutralized BEFORE comments are stripped (#561).
$raw  = Get-Content $f -Raw
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')

# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive:
#   GateThreshold\s*\.\s*Effective\s*\(   0   in CriticalityJudge.cs (and 0 anywhere in src/ or
#       tests/ - the bare word GateThreshold appears nowhere; the 55 repo-wide hits for that string
#       are all the CONFIG type GateThresholds, plural, which this clause's '.Effective(' cannot match)
#   No ancestor writes this token into this subject: task 04's writeScope covers GateThreshold.cs and
#       OverwatchDecision.cs, never CriticalityJudge.cs.
# The ban clause is NOT censused: forbidden-present polarity. It measured 1 (the declaration at the
# private EffectiveThreshold method), RED on arrival BY DESIGN - deleting it is this task's
# deliverable. Anchored on the DECLARATION, so the call site the task rewrites is not double-reported.
# Checked: the required clause contains no banned token.

if ($code -cnotmatch 'GateThreshold\s*\.\s*Effective\s*\(') {
    $failures += "$f never calls GateThreshold.Effective(...) — design 41 §2.1 makes it the ONE effective-threshold rule, and the judge is one of the two callers that must be collapsed onto it."
}
if ($scan -cmatch 'private\s+EscalationThreshold\s+EffectiveThreshold\s*\(') {
    $failures += "$f still declares its own private EffectiveThreshold — §2.1 replaces it with the shared rule rather than adding a second copy beside it. Two spellings of one threshold rule is how a per-gate override comes to be honoured at one gate and ignored at another."
}

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
# ${f}, not "$f:" — PowerShell reads '$f:' in a double-quoted string as a scope-qualified variable
# reference and fails to parse the script (#473: an unparseable guardrail cannot report anything).
Write-Output "${f}: the judge resolves its threshold through the shared GateThreshold.Effective rule, and its private copy is gone."
exit 0
