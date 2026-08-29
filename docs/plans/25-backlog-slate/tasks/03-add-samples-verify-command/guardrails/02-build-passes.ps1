# catches: a verb that does not COMPILE - a SamplesCommand.cs that does not type-check against the
#          System.CommandLine surface, or a CommandFactory registration line that does not bind. It runs
#          BEFORE the reachability smoke (03) so a compile failure reports as a compile failure, rather
#          than surfacing there as a `dotnet run` non-zero exit that is indistinguishable from "the verb
#          ran and rejected the corpus" (#155).
# src/Guardrails.Cli is the smallest scope that covers this task's whole diff (SamplesCommand.cs +
# CommandFactory.cs) and it builds Guardrails.Core transitively, so the SampleVerifier surface the verb binds
# against is compiled too (#176).
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler
# errors. It is NOT carried onto the dotnet run in 03 - there the app's own report IS the evidence.
dotnet build src/Guardrails.Cli --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "src/Guardrails.Cli does not build - SamplesCommand.cs or the CommandFactory registration is not type-correct (see the compiler errors above)"
    exit 1
}
exit 0
