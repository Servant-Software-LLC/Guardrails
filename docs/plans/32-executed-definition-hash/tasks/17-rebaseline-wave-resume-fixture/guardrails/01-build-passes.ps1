# catches: a fixture edit that does not COMPILE. The likeliest cause is specific and worth naming: the
#          second b.Load() introduces a NEW local, and the auto-policy method then has to build its config
#          clone from THAT local rather than from run 1's `plan`. Leaving one of the two references
#          pointing at the old variable is a silent no-op at best (the config clone would carry run 1's
#          pre-edit nodes again, which is the whole defect) and a CS0136/CS0128 name collision at worst.
#
#          A non-compiling test exits `dotnet test` non-zero IDENTICALLY to one that compiles and fails,
#          so without this the regression clause in guardrail 03 is gameable by garbage (#155).
$ErrorActionPreference = 'Continue'

# -v q IS correct on a BUILD (it strips restore/banner chatter and leaves the compiler errors). It is
# banned only on `dotnet test`, where it deletes the failure detail (#462). This is a build.
dotnet build tests/Guardrails.Core.Tests --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "tests/Guardrails.Core.Tests does not build after the fixture re-baseline. If the error is a name collision or an unassigned local, the second b.Load() was added but one of run 2's references still points at run 1's plan variable - which would also silently restore the defect, since the config clone would carry the pre-edit nodes again. Both the journal and the scheduler in run 2 must take the RELOADED plan."
    exit 1
}
exit 0
