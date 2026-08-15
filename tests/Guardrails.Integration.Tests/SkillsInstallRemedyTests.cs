using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #461 — the remedy a drift warning prints must be the command that writes to the root the warning
/// just named. The pre-#461 line was fixed text (<c>skills install --force</c>), which writes only to
/// <c>~/.claude/skills</c>; for drift found anywhere else, running it verbatim left the warning standing.
/// <para>
/// Root-parameterized on purpose (see <see cref="SkillsInstallRemedy"/>): all three branches are covered
/// with fabricated destinations, so the mapping is proven without reading — or worse, writing — the
/// machine's real <c>~/.claude/skills</c> or depending on the test host's current directory. The wiring to
/// the REAL destinations is covered end-to-end by <see cref="VersionDriftCliTests"/>.
/// </para>
/// </summary>
public sealed class SkillsInstallRemedyTests
{
    private static readonly string UserRoot = Path.Combine("home", "someone", ".claude", "skills");
    private static readonly string ProjectRoot = Path.Combine("work", "some-repo", ".claude", "skills");

    [Fact]
    public void UserLevelRoot_IsThePlainInstall()
    {
        Assert.Equal(
            "guardrails skills install --force",
            SkillsInstallRemedy.CommandFor(UserRoot, UserRoot, ProjectRoot));
    }

    [Fact]
    public void ProjectLevelRoot_IsTheProjectInstall()
    {
        // The root the OLD fixed remedy could never reach: --project is the only flag that writes here.
        Assert.Equal(
            "guardrails skills install --project --force",
            SkillsInstallRemedy.CommandFor(ProjectRoot, UserRoot, ProjectRoot));
    }

    [Fact]
    public void AnyOtherRoot_IsAnExplicitTarget()
    {
        string other = Path.Combine("opt", "shared", "skills");

        Assert.Equal(
            $"guardrails skills install --target {other} --force",
            SkillsInstallRemedy.CommandFor(other, UserRoot, ProjectRoot));
    }

    [Fact]
    public void ATargetWithSpaces_IsQuoted_SoItCanBePastedVerbatim()
    {
        // A remedy the user must re-type or repair is barely better than the wrong remedy: on Windows,
        // "C:\Dev AI\..." is the common case, not an exotic one.
        string spaced = Path.Combine("Dev AI", "Guardrails", ".claude", "skills");

        Assert.Equal(
            $"guardrails skills install --target \"{spaced}\" --force",
            SkillsInstallRemedy.CommandFor(spaced, UserRoot, ProjectRoot));
    }

    [Fact]
    public void ATrailingSeparator_StillMatchesTheSameDirectory()
    {
        // Roots reach the report as strings from several sources; "…/skills" and "…/skills/" are one
        // directory, and a mismatch here would silently downgrade a correct remedy to the --target form.
        Assert.Equal(
            "guardrails skills install --force",
            SkillsInstallRemedy.CommandFor(UserRoot + Path.DirectorySeparatorChar, UserRoot, ProjectRoot));
    }
}
