# catches: an anchor test that does not compile. It should be nearly impossible to fail here, because a
#          source-reading anchor test needs no production TYPES at all - only file paths and text - and
#          that is a useful signal in itself: a compile error here usually means the anchor was written
#          as a reflection test over members instead of a text scan over files, which would be a
#          different (and weaker) instrument than section 9 asks for.
#
#          NOTE FOR THE IMPLEMENTER, because it is easy to get wrong from the plan alone: the repo's two
#          existing anchor suites - SeamDoctrineAnchorTests and ModelAppropriatenessDoctrineAnchorTests -
#          read MARKDOWN SKILL TEXT, not src/. No test in this repo reads src/**/*.cs as text today. The
#          IDIOM transfers (repo root from the test file's own CallerFilePath, TheoryData rows, ordinal
#          Contains); the SUBJECT does not.
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). These are builds.
$failures = @()

foreach ($project in @('tests/Guardrails.Core.Tests')) {
    dotnet build $project --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $failures += "$project does not build."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== build: $($failures.Count) project(s) do not compile ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output "The anchor test reads src/ as TEXT and should therefore need almost no types from Core at all. If it does not compile, it is probably reaching for a member rather than reading a file. Follow SeamDoctrineAnchorTests: resolve the repo root from the test file's own CallerFilePath, then File.ReadAllText."
    exit 1
}
exit 0
