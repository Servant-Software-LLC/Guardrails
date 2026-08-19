# catches: a change that does not COMPILE reaching the next task. This is a WIRING task: its own
#          structural guardrail is a set of regexes over source text, and a regex is perfectly happy
#          with code that will never build. Without this, the first compile check would be the
#          wave-terminal union build - after every remaining task has already run against a broken
#          tree, with the failure attributed to whichever task happened to be last.
# -v q is correct on a BUILD; it is FORBIDDEN on dotnet test (dotnet.md 4/4.2, #462).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
dotnet build Guardrails.sln --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not compile. Read the compile errors above: they name the exact symbol. A likely one here is CS0051 (inconsistent accessibility) - IRunObserver is public and Guardrails.Cli has no InternalsVisibleTo into Guardrails.Core, so an internal type on a public interface signature will not build; declare the event with public or primitive parameter types. If the missing symbol is OUTSIDE your write scope, do not edit that file - emit a needsHuman fragment to the state-out path instead."
    exit 1
}
exit 0
