# catches: a plan that went green task-by-task but leaves the SOLUTION not compiling - a type referenced
#          across projects that only exists in one task's segment, or a merge that unioned two edits into
#          invalid C#. LOCAL (no scope key) on purpose: this is a terminal POSTCONDITION on the merged
#          plan-branch HEAD, and tagging it scope:"integration" would re-run it at every intermediate
#          union, where a downstream task has not landed yet and the build legitimately fails (#125/#165).
#          Debug, not Release: a dogfood run launched from the Release binary locks Guardrails.Core.dll
#          and a Release solution build then fails on a file lock that has nothing to do with the plan.
$out = dotnet build Guardrails.sln -c Debug -v q --nologo 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }
if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern 'error |ERROR|: error' | ForEach-Object { $_.Line } | Select-Object -First 30
    Write-Output ""
    Write-Output "=== Build errors (re-emitted so they land in the halt output) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    Write-Output "Guardrails.sln does not build on the merged plan HEAD - the telemetry work does not compile together even though each task passed on its own."
    exit 1
}
exit 0
