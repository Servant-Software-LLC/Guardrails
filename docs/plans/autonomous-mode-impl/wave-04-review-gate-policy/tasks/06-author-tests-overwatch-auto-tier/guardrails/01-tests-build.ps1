# catches: an OverwatchAutoTierTests file (or its Overwatch ctor stub) that does NOT compile — or a stub
#          that made autonomyBlockPresent a REQUIRED param, breaking SchedulerFactory's existing
#          new Overwatch(...) call. Builds the WHOLE solution (not just the test project) so the
#          SchedulerFactory call site is compiled too; a non-compiling test exits dotnet test non-zero
#          identically to a red one, so without this the TDD red is gameable (#155).
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build — the OverwatchAutoTierTests file, or the Overwatch ctor stub (did you add autonomyBlockPresent as a TRAILING OPTIONAL param so SchedulerFactory still compiles?), is not type-correct"
    exit 1
}
exit 0
