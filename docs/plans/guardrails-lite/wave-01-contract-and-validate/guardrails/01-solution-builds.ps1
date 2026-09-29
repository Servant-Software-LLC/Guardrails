# catches: a wave whose tasks each built their own project green but whose MERGED union does not compile
#          - e.g. two Lite test classes that each declare the same helper type, or a contract-test edit in
#          Guardrails.Core.Tests that no Lite task's filtered build ever compiled. A LOCAL wave-exit
#          postcondition (no scope key): it runs ONCE, on the merged HEAD at this wave's end (SSOT §14.3).
#          A whole-solution build tagged scope:"integration" would red-halt correct intermediate unions
#          (#125/#165), and on a wave root the tag is INERT anyway (GR2059).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# -v q is correct on a BUILD (leaves the compiler errors, strips the chatter); banned only on `dotnet test`.
$out = dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1
$buildExit = $LASTEXITCODE
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    Write-Output ""
    Write-Output "=== compiler errors (re-emitted for the feedback tail) ==="
    $out | Where-Object { "$_" -match 'error [A-Z]+\d+' } | Select-Object -First 30 | ForEach-Object { Write-Output $_ }
    Write-Output "Guardrails.sln does not build on the merged wave-01 HEAD - a cross-task compile conflict between wave-01 tasks (each built green alone). Read the first error's file and find which two tasks both touched it."
    exit 1
}
exit 0
