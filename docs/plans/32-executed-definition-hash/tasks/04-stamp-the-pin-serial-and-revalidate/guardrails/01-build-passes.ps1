# catches: an expression-level substitution that does not compile - most likely a type mismatch, since
#          TaskDefinitionHash.Compute returns a non-nullable string while task.DefinitionHashAtLoad is
#          nullable by design (section 5.2). The temptation at a CS8604 here is to silence it with a
#          `!' or a `?? Compute(task)' tail; the first is fine and the second is the defect (guardrail
#          03). The recorders already take `string?' - RunJournal.RecordAttempt / RecordSettle /
#          RecordSettleWithAttempt all declare `string? definitionHash = null' - so no cast is needed at
#          the call site at all.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD; banned only on `dotnet test` (#462). These are builds.
$failures = @()

foreach ($project in @('src/Guardrails.Core', 'tests/Guardrails.Core.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output "If the error is a nullable-reference warning-as-error on the stamped value: the journal recorders already accept a nullable string, so pass the pin through unchanged. Do NOT add a coalescing fallback to satisfy the compiler - that is the exact defect guardrail 03 exists to catch, and section 5.2 calls it the cheapest wrong implementation of this whole plan."
    exit 1
}
exit 0
