# catches: a wave-twin change that does not compile. The likeliest cause is worth naming: section 5.4
#          adds a PINNED fold beside the unchanged disk-reading Compute(wave), and there are EIGHT
#          existing WaveDefinitionHash.Compute call sites across the Scheduler, ReviewMarker and
#          RunCommand. Changing that method's signature - rather than adding a sibling - breaks all of
#          them at once, and the ones in RunCommand are in a different assembly.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
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
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "Four files change in this stage. If the error is in WaveDefinitionHash.cs, check that the disk-reading Compute(WaveNode) still exists with its original signature - section 5.4 adds a pinned form BESIDE it, and eight call sites across Scheduler, ReviewMarker and RunCommand still bind to the original."
    exit 1
}
exit 0
