# catches: a wave-01 change that compiles in isolation but breaks the whole solution once every branch
#          merges (a cross-project break, an unregistered command, a broken reference). Terminal
#          postcondition for the wave — runs LOCAL (no scope key) so it fires ONCE on the merged wave-01
#          HEAD, not at every intermediate union (#165 — a whole-solution build is not union-safe).
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build on the merged wave-01 HEAD — a cross-project compilation error"
    exit 1
}
exit 0
