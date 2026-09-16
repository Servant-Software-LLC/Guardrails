# catches: a rewritten test file that still names a member task 05 DELETES. Task 05 removes Resolve,
#          OverwatchDecisionKind.AutoResolve and OverwatchDecision.AutoResolvedPaths, and its
#          writeScope EXCLUDES every test file — so a surviving reference here becomes a compile error
#          in a file task 05 is forbidden to touch. It would burn every retry and halt needs-human
#          with the work already correct, which is the #155 dead-end this plan is shaped to avoid.
#          Nothing else can see it: the file compiles today, the red census is happy (a row naming
#          Resolve is red against the stubs too), and the failure only appears one task later.
#
#          The bans are anchored on the CONSTRUCT, never the bare word, because the required and
#          forbidden clauses share a vocabulary here (#470): the type the tests MUST call is
#          OverwatchSupplyAutoResolve and the class itself is OverwatchSupplyAutoResolveTests, both of
#          which contain the substring 'AutoResolve'. A bare ban would be unsatisfiable by
#          construction. COLLISION CHECKED: the '.Resolve(' ban cannot match the required '.Certify(',
#          and neither AutoResolvedPaths nor OverwatchDecisionKind.AutoResolve appears in any
#          required clause.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/04-author-tests-supply-certification/samples/03-tests-do-not-pin-deleted-members.valid.cs';   ./03-tests-do-not-pin-deleted-members.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/04-author-tests-supply-certification/samples/03-tests-do-not-pin-deleted-members.invalid.cs'; ./03-tests-do-not-pin-deleted-members.ps1  # expect 1
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs" }

$failures = @()
if (-not (Test-Path $f)) { Write-Output "$f does not exist"; exit 1 }   # PRECONDITION - the only early exit

# Variable discipline (the two-variable rule): $raw is NEVER matched against; REQUIRED clauses read
# $code (comments gone, literals still readable, so the [Trait] attribute value can satisfy a clause);
# FORBIDDEN clauses read $scan (literal CONTENT gone too). Neutralize string literals BEFORE stripping
# comments (#561), so a literal spelling '/*' cannot forge a delimiter that blanks real code.
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
# -cnotmatch), against tests/Guardrails.Core.Tests/Supply/OverwatchSupplyAutoResolveTests.cs:
#   OverwatchSupplyAutoResolve\s*\.\s*Certify\s*\(    0
#   GateThreshold\s*\.\s*Effective\s*\(               0
#   \[Trait\("Category",\s*"OverwatchSupply"\)\]      0   (the file carries "Supply" today; the quoted
#                                                         literal makes "Supply" a non-match, and the
#                                                         retag to "OverwatchSupply" is this task's job)
#   NOTE a bare 'Certify' would have measured 2, NOT 0 - the shipped method name
#   AutoResolve_DoesNotCertifyAnythingUnverified contains it, and so does its <see cref>. The clause is
#   anchored on the CALL for exactly that reason.
#   No ancestor writes these tokens into this subject: this task's ancestors (02, 03) have writeScopes
#   covering only MissingResource* sources and their own two test files.

# --- required: the rewritten file actually exercises the new surface ------------------------------
# Without these, "names none of the deleted members" is satisfied by an empty file.
if ($code -cnotmatch 'OverwatchSupplyAutoResolve\s*\.\s*Certify\s*\(') {
    $failures += "$f never calls OverwatchSupplyAutoResolve.Certify(...) — the rewrite is against the certification gate (design 41 §3.1); a file that names none of the deleted members but calls nothing certifies nothing."
}
if ($code -cnotmatch 'GateThreshold\s*\.\s*Effective\s*\(') {
    $failures += "$f never calls GateThreshold.Effective(...) — the three shared-threshold rows live in this class and the prompt pins them here."
}
if ($code -cnotmatch '\[Trait\("Category",\s*"OverwatchSupply"\)\]') {
    # NOTE the backtick escapes. PowerShell's escape character is the BACKTICK, not the backslash: the
    # first draft of this line used \" inside a double-quoted string, which terminates the string early
    # and leaves the remainder as bare tokens — an unparseable guardrail, which is a dead end NO retry
    # can fix (#473), because the agent may not edit the guardrail and the script can never run.
    $failures += "$f does not carry [Trait(`"Category`", `"OverwatchSupply`")] — it still carries the old `"Supply`" value, so the baseline preflight's exact Category!=OverwatchSupply exclusion will enrol these deliberately-red tests in the baseline."
}

# --- forbidden: the three members task 05 deletes, each anchored on its construct ------------------
if ($scan -cmatch 'OverwatchSupplyAutoResolve\s*\.\s*Resolve\s*\(') {
    $failures += "$f still calls OverwatchSupplyAutoResolve.Resolve(...) — task 05 DELETES that method and cannot edit this file to follow. Rewrite the row against Certify."
}
if ($scan -cmatch 'OverwatchDecisionKind\s*\.\s*AutoResolve\b') {
    $failures += "$f still names OverwatchDecisionKind.AutoResolve — task 05 DELETES that enum member (design 41 §3.4: a supply is a Scheduler action that never passes through the TaskExecutor retry loop, so no component should act on it)."
}
if ($scan -cmatch '\bAutoResolvedPaths\b') {
    $failures += "$f still names AutoResolvedPaths — task 05 DELETES that property; the certified paths live on SupplyCertification.Supplies now."
}

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }
Write-Output "$f exercises Certify and the shared threshold rule, and names none of the three members task 05 deletes."
exit 0
