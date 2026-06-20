# catches: --merge-on-success implemented wrong - MergeOnSuccessTests still failing (no end-of-run
#          delivery, or - the load-bearing case - AI-merge NOT withheld at the user-branch boundary so a
#          conflicting user commit gets auto-resolved instead of halting to needs-human)
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MergeOnSuccessTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "MergeOnSuccessTests failing - --merge-on-success / AI-merge-withheld-at-user-boundary not implemented to spec"
    exit 1
}
exit 0
