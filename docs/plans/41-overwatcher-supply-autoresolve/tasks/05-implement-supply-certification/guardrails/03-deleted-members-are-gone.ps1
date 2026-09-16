# catches: an implementation that adds Certify and leaves the four retired members standing. Nothing
#          else can see it. Every test passes with Resolve still in the file — the tests were
#          rewritten off it, so it simply has no callers — and dead code that no test names is
#          exactly what #712 was: a capability that exists only in its own tests. Design 41 §3.4
#          deletes these four BECAUSE a second, unreachable copy of a decision is how the wrong one
#          gets wired next time.
#          FAIL-ON-PRESENT (a negative assertion), the mirror of a covers-key-behaviors check.
#
#          Each ban is anchored on the DECLARATION construct, never the bare word, because required
#          and forbidden clauses share a vocabulary in this file (#470): the class that MUST survive
#          is OverwatchSupplyAutoResolve, which contains the substring 'AutoResolve', and the method
#          that MUST exist is Certify. COLLISION CHECKED, clause by clause:
#            - 'static OverwatchDecision Resolve(' cannot match 'static SupplyCertification Certify('
#            - the enum-member ban is line-anchored, so it cannot match a class or type name
#            - neither AutoResolvedPaths nor OverwatchDecisionKind.AutoResolve appears in any
#              required clause
#          The action prompt for this task names all four members in prose. That is unavoidable — it
#          has to say what to delete — and harmless: this script reads the SOURCE file, never the
#          prompt.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/05-implement-supply-certification/samples/03-deleted-members-are-gone.valid.cs';   ./03-deleted-members-are-gone.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/05-implement-supply-certification/samples/03-deleted-members-are-gone.invalid.cs'; ./03-deleted-members-are-gone.ps1  # expect 1
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/OverwatchDecision.cs" }

$failures = @()
if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }   # PRECONDITION - the only early exit

# Variable discipline (the two-variable rule): $raw is NEVER matched against; REQUIRED clauses read
# $code (comments gone, literals readable); FORBIDDEN clauses read $scan (literal CONTENT gone too).
# Neutralize string literals BEFORE stripping comments (#561): this file's ProposedSequenceFor body
# contains literals with '/' in them, so deriving $code straight from $raw risks a forged delimiter.
$raw  = Get-Content $f -Raw
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')

# baseline counts on the untouched tree - MEASURED with Select-String -CaseSensitive (the clauses are
# -cnotmatch), against src/Guardrails.Core/Execution/OverwatchDecision.cs:
#   static\s+SupplyCertification\s+Certify\s*\(   0
#   record\s+SupplyCertification\b                0
#   class\s+OverwatchSupplyAutoResolve\b          1   DECLARED NONZERO, and required: this is a
#       SURVIVAL anchor, not a delivery clause. Its whole job is to stop the three bans below being
#       satisfied by deleting the file, so it is expected to be already-present by construction - the
#       same category as a tests-untouched regression clause. The two clauses above it are the ones
#       that measure delivery, and both are 0.
#   The ancestor check: task 04 writes Certify and the SupplyCertification record into this very file,
#       so on the tree THIS task sees, the first two clauses are ALREADY SATISFIED. That is correct
#       and deliberate - they are anti-vacuity anchors for the bans, and task 04's own red census is
#       what proves the members are stubs rather than implementations.
# The three ban clauses are NOT censused: forbidden-present polarity. All three are RED on arrival by
# design - deleting them is this task's deliverable, which is the ordinary TDD story, not the #470
# collision.

# --- required: the gate survives the deletion -----------------------------------------------------
if ($code -cnotmatch 'class\s+OverwatchSupplyAutoResolve\b') {
    $failures += "$f no longer declares the OverwatchSupplyAutoResolve class — the four members are retired, the TYPE is not. Deleting the file is not how the bans below are satisfied."
}
if ($code -cnotmatch 'static\s+SupplyCertification\s+Certify\s*\(') {
    $failures += "$f declares no 'static SupplyCertification Certify(...)' — design 41 §3.1 replaces Resolve with Certify; removing Resolve without it leaves the overwatcher no gate at all."
}
if ($code -cnotmatch 'record\s+SupplyCertification\b') {
    $failures += "$f declares no SupplyCertification record — it is the gate's verdict type (certified with its supplies, or refused with a reason token)."
}

# --- forbidden: the four members design 41 §3.4 retires -------------------------------------------
if ($scan -cmatch 'static\s+OverwatchDecision\s+Resolve\s*\(') {
    $failures += "$f still declares OverwatchSupplyAutoResolve.Resolve — §3.4 deletes it. Its staged-tree drain and its plan.Workspace target are the #712 defect; Certify replaces it, and a surviving copy is the unreachable second decision that gets wired by mistake next time."
}
if ($scan -cmatch '\bProposedSequenceFor\b') {
    $failures += "$f still declares or calls ProposedSequenceFor — §3.4 deletes it per d41-below-critical. RunCommand's halt text is the SINGLE producer of the supply/reset/run sequence, at every dial; this is a second copy no production path reaches."
}
if ($scan -cmatch '\bAutoResolvedPaths\b') {
    $failures += "$f still names AutoResolvedPaths — §3.4 deletes it. The certified paths live on SupplyCertification.Supplies; nothing in src/ ever read this property."
}
if ($scan -cmatch '(?m)^\s*AutoResolve\s*,?\s*$') {
    $failures += "$f still declares the OverwatchDecisionKind.AutoResolve enum member — §3.4 deletes it. OverwatchDecision is the control-flow signal the TaskExecutor retry loop reads, and a supply is a Scheduler action that never passes through that loop, so no component should act on this kind."
}
if ($scan -cmatch 'OverwatchDecisionKind\s*\.\s*AutoResolve\b') {
    $failures += "$f still USES OverwatchDecisionKind.AutoResolve — §3.4 deletes the member, so every construction of it goes too."
}

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
# ${f}, not "$f:" — in a double-quoted string PowerShell reads '$f:' as a SCOPE-QUALIFIED variable
# reference (the $env:PATH form) and fails to parse the whole script. An unparseable guardrail is a
# dead end no retry can fix (#473): the agent may not edit it, and it can never run to report why.
Write-Output "${f}: Certify and SupplyCertification are declared, the class survives, and all four retired members are gone."
exit 0
