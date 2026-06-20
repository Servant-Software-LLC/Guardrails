# catches: the settle implemented wrong - MergeLockAndSettleTests still failing. The B1 four-effect
#          rollback and FF-is-free + trailer are the load-bearing false-green gates; a non-FF union that
#          fails re-verify must reset --hard preHead with no fragment / no mergeSequence consumed.
dotnet test tests/Guardrails.Integration.Tests --filter "FullyQualifiedName~MergeLockAndSettleTests" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Output "MergeLockAndSettleTests failing - the merge lock / FF / non-FF union settle (B1) is not implemented to spec"
    exit 1
}
exit 0
