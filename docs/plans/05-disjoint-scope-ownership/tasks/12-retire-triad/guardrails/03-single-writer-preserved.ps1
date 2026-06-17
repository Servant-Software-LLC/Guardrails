# catches: the M7 removal over-reaching and deleting the #48 single-writer-per-key invariant,
#          which §6/§9 of the design hold is a SEPARATE state-model rule that must stay. Assert the
#          single-writer enforcement is still present in StateManager.cs (the single writer of
#          state/state.json, where the foreign-key rejection lives). Scoped to the one file that owns
#          the invariant (grep-scope rule). Pre-existing repo file → matched against current content.
$stateMgr = "src/Guardrails.Core/State/StateManager.cs"
if (-not (Test-Path $stateMgr)) {
    Write-Output "$stateMgr does not exist - the single-writer-per-key invariant (#48) appears to have been removed; it must stay (§6/§9)."
    exit 1
}
$code = Get-Content $stateMgr -Raw
if ($code -notmatch '(?i)single.?writer' -or $code -notmatch '(?i)ForeignKey') {
    Write-Output "$stateMgr no longer enforces single-writer-per-key (missing the single-writer / ForeignKey rejection) - #48 must remain intact after the triad is retired (it is a separate state-model invariant)."
    exit 1
}
exit 0
