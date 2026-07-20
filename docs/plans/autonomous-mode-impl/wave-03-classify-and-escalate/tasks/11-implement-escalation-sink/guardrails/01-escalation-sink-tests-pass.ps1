# catches: a FileEscalationSink that deviates from doc 12 §7.1/§7.2 — a seq derived from a directory
#          listing (so a stale answer could bind a reused tuple), a missing decisions[] 'escalated'
#          entry, or a broken journaled counter. Runs the authored EscalationSinkTests AND the existing
#          journal tests (the seq counter must not break run.json). Re-emits failure detail at the END so
#          the WHY reaches the retry tail (#179).
$out = dotnet test tests/Guardrails.Core.Tests --filter "FullyQualifiedName~EscalationSinkTests|FullyQualifiedName~RunJournalTests|FullyQualifiedName~JournalTests" --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "escalation-sink tests failing — the record-to-disk / monotonic-seq / decisions[]-escalated behaviour is not to spec (see details above)"
    exit 1
}
exit 0
